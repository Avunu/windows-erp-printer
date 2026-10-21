using ErpPrinter.Logging;
using ErpPrinter.Settings;

namespace ErpPrinter.Secrets;

/// <summary>Encrypts and decrypts secrets. The Windows layer uses machine-scope DPAPI so the SYSTEM service and elevated administrators can both read them.</summary>
public interface ISecretProtector
{
    byte[] Protect(string plainText);
    string Unprotect(byte[] cipher);
}

/// <summary>Plain API key and secret of a profile, as a backend needs them.</summary>
public sealed record BackendSecrets(string ApiKey = "", string ApiSecret = "")
{
    public static readonly BackendSecrets Empty = new();
}

public static class SecretNames
{
    public const string ApiKey = "ApiKey";
    public const string ApiSecret = "ApiSecret";
    public static readonly IReadOnlyList<string> All = [ApiKey, ApiSecret];
}

/// <summary>
/// Secrets live as binary values under HKLM\SOFTWARE\ErpPrinter\Secrets\&lt;profile&gt;, a key readable only by SYSTEM and
/// Administrators. Machine-scope DPAPI alone would not stop other local processes, which is why that ACL matters.
/// </summary>
public sealed class SecretStore(IRegistryStore registry, ISecretProtector protector, IErpLog? log = null)
{
    private readonly IErpLog _log = log ?? NullLog.Instance;

    public static string PathFor(string profileName) => $@"{ConfigStore.RegistryRoot}\Secrets\{profileName}";

    public BackendSecrets Get(string profileName)
    {
        var raw = registry.GetValues(PathFor(profileName));

        string Read(string name)
        {
            if (!raw.TryGetValue(name, out var value) || value is not byte[] { Length: > 0 } bytes) return "";
            try { return protector.Unprotect(bytes); }
            catch (Exception ex)
            {
                _log.Warning($"Could not decrypt {name} for profile '{profileName}': {ex.Message}");
                return "";
            }
        }

        return new BackendSecrets(Read(SecretNames.ApiKey), Read(SecretNames.ApiSecret));
    }

    /// <summary>Whether a secret is stored (without decrypting it).</summary>
    public bool Has(string profileName, string secretName) =>
        registry.GetValues(PathFor(profileName)).TryGetValue(secretName, out var v) && v is byte[] { Length: > 0 };

    /// <summary>Stores a secret; an empty value removes it.</summary>
    public void Set(string profileName, string secretName, string plainText)
    {
        Naming.AssertProfileName(profileName);
        if (!SecretNames.All.Contains(secretName)) throw new ArgumentException($"'{secretName}' is not a secret.", nameof(secretName));
        registry.EnsureSecretsKey();
        if (plainText.Length == 0)
        {
            registry.RemoveValue(PathFor(profileName), secretName);
            return;
        }
        registry.SetValue(PathFor(profileName), secretName, protector.Protect(plainText), RegistryValueType.Binary);
    }

    public void RemoveAll(string profileName) => registry.RemoveKey(PathFor(profileName));

    /// <summary>The stored secrets, with non-empty entries of <paramref name="overrides"/> taking precedence (used to test unsaved credentials).</summary>
    public BackendSecrets Effective(string profileName, BackendSecrets? overrides)
    {
        var stored = Get(profileName);
        return new BackendSecrets(
            string.IsNullOrEmpty(overrides?.ApiKey) ? stored.ApiKey : overrides.ApiKey,
            string.IsNullOrEmpty(overrides?.ApiSecret) ? stored.ApiSecret : overrides.ApiSecret);
    }
}
