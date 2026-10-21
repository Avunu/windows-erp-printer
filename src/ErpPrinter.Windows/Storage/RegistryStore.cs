using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using ErpPrinter.Settings;
using Microsoft.Win32;

namespace ErpPrinter.Windows.Storage;

/// <summary>
/// The registry as <see cref="IRegistryStore"/>. Paths are relative to <c>HKLM</c> by default; tests pass another root key (and
/// optionally a base path below it) so they never touch the real machine hive. Key and value names are case-insensitive, as in the registry.
/// Read failures caused by missing access (for example a non-elevated process reading the Secrets key) behave like a missing key,
/// as <c>Get-Item -ErrorAction SilentlyContinue</c> did in the PowerShell module.
/// </summary>
public sealed class RegistryStore : IRegistryStore
{
    private readonly RegistryKey _root;
    private readonly string _basePath;

    public RegistryStore(RegistryKey? root = null, string basePath = "")
    {
        _root = root ?? Registry.LocalMachine;
        _basePath = basePath.Trim('\\');
    }

    private string Full(string path)
    {
        var relative = path.Trim('\\');
        if (_basePath.Length == 0) return relative;
        return relative.Length == 0 ? _basePath : _basePath + "\\" + relative;
    }

    private RegistryKey? Open(string path, bool writable)
    {
        try
        {
            var full = Full(path);
            return full.Length == 0 ? _root : _root.OpenSubKey(full, writable);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void CloseUnlessRoot(RegistryKey? key, RegistryKey root)
    {
        if (key is not null && !ReferenceEquals(key, root)) key.Dispose();
    }

    public IReadOnlyDictionary<string, object> GetValues(string path)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var key = Open(path, false);
        try
        {
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                if (key.GetValue(name) is { } value) result[name] = value;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { /* unreadable: report what was read so far */ }
        finally { CloseUnlessRoot(key, _root); }
        return result;
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var key = Open(path, false);
        try { return key?.GetSubKeyNames() ?? []; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return []; }
        finally { CloseUnlessRoot(key, _root); }
    }

    public bool KeyExists(string path)
    {
        var key = Open(path, false);
        var exists = key is not null;
        CloseUnlessRoot(key, _root);
        return exists;
    }

    public void EnsureKey(string path)
    {
        var full = Full(path);
        if (full.Length == 0) return;
        _root.CreateSubKey(full).Dispose();
    }

    public void SetValue(string path, string name, object value, RegistryValueType type)
    {
        var full = Full(path);
        var key = full.Length == 0 ? _root : _root.CreateSubKey(full);
        try
        {
            switch (type)
            {
                case RegistryValueType.String:
                    key.SetValue(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", RegistryValueKind.String);
                    break;
                case RegistryValueType.DWord:
                    key.SetValue(name, value switch
                    {
                        bool b => b ? 1 : 0,
                        uint u => unchecked((int)u),
                        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    }, RegistryValueKind.DWord);
                    break;
                case RegistryValueType.Binary:
                    key.SetValue(name, value as byte[] ?? throw new ArgumentException("A Binary value must be a byte[].", nameof(value)), RegistryValueKind.Binary);
                    break;
                case RegistryValueType.MultiString:
                    key.SetValue(name, value switch
                    {
                        string[] array => array,
                        IEnumerable<string> lines => lines.ToArray(),
                        string single => [single],
                        _ => throw new ArgumentException("A MultiString value must be a string[].", nameof(value)),
                    }, RegistryValueKind.MultiString);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
        finally { CloseUnlessRoot(key, _root); }
    }

    public void RemoveValue(string path, string name)
    {
        var key = Open(path, true);
        try { key?.DeleteValue(name, throwOnMissingValue: false); }
        finally { CloseUnlessRoot(key, _root); }
    }

    public void RemoveKey(string path)
    {
        var full = Full(path);
        if (full.Length == 0) throw new InvalidOperationException("Refusing to delete the registry root.");
        _root.DeleteSubKeyTree(full, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// SYSTEM and Administrators only, no inheritance, by SID so localized Windows works. Exposed for tests.
    /// </summary>
    internal static RegistrySecurity CreateSecretsSecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
        {
            security.AddAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(sid), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    public void EnsureSecretsKey()
    {
        var full = Full($@"{ConfigStore.RegistryRoot}\Secrets");
        var security = CreateSecretsSecurity();

        bool exists;
        try
        {
            using var probe = _root.OpenSubKey(full);
            exists = probe is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            exists = true; // locked already
        }

        // The key is empty until the ACL is applied (secrets are written afterwards), so creating it first is harmless. Creating it
        // with the final ACL in one call would deny the creator the handle it asked for.
        if (!exists) _root.CreateSubKey(full).Dispose();

        // Applied to a key that already exists too (an upgrade from an installation with a looser ACL).
        using var key = _root.OpenSubKey(full, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions)
            ?? throw new InvalidOperationException($"Could not open the secrets key '{full}' to set its permissions.");
        key.SetAccessControl(security);
    }
}
