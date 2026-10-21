using ErpPrinter.Logging;
using ErpPrinter.Secrets;

namespace ErpPrinter.Settings;

/// <summary>
/// Configuration lives in the registry:
/// <code>
/// HKLM\SOFTWARE\ErpPrinter                     global settings (GUI, MSI, scripts)
/// HKLM\SOFTWARE\ErpPrinter\Printers\&lt;name&gt;     one key per printer profile
/// HKLM\SOFTWARE\ErpPrinter\Secrets\&lt;name&gt;      DPAPI blobs, SYSTEM + Administrators only
/// HKLM\SOFTWARE\Policies\ErpPrinter[\Printers\&lt;name&gt;]   same layout, pushed by GPO/Intune; wins over the above
/// </code>
/// Effective value = policy, else machine, else schema default. Same layout as the PowerShell product, so
/// existing installations keep their configuration.
/// </summary>
public sealed class ConfigStore(IRegistryStore registry, SecretStore secrets, IErpLog? log = null)
{
    public const string RegistryRoot = @"SOFTWARE\ErpPrinter";
    public const string PolicyRoot = @"SOFTWARE\Policies\ErpPrinter";

    private readonly IErpLog _log = log ?? NullLog.Instance;

    public SecretStore Secrets => secrets;

    public ErpConfig Load() => new(LoadGlobal(), LoadProfiles());

    public GlobalSettings LoadGlobal()
    {
        var (values, managed) = Resolve(Schema.Global, registry.GetValues(RegistryRoot), registry.GetValues(PolicyRoot), null);
        return new GlobalSettings(values, managed);
    }

    public IReadOnlyList<PrinterProfile> LoadProfiles()
    {
        var machine = registry.GetSubKeyNames($@"{RegistryRoot}\Printers");
        var policy = registry.GetSubKeyNames($@"{PolicyRoot}\Printers");
        return machine.Concat(policy).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => LoadProfile(name, machine.Contains(name, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    public PrinterProfile? GetProfile(string name) =>
        LoadProfiles().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private PrinterProfile LoadProfile(string name, bool hasMachineKey)
    {
        var (values, managed) = Resolve(
            Schema.Profile, registry.GetValues($@"{RegistryRoot}\Printers\{name}"), registry.GetValues($@"{PolicyRoot}\Printers\{name}"), name);
        return new PrinterProfile(name, values, managed, policyDefined: !hasMachineKey);
    }

    private (Dictionary<string, object> Values, List<string> Managed) Resolve(
        IReadOnlyList<SettingDefinition> schema,
        IReadOnlyDictionary<string, object> machine,
        IReadOnlyDictionary<string, object> policy,
        string? profileName)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var managed = new List<string>();
        foreach (var setting in schema.Where(s => s.Type != SettingType.Secret))
        {
            var value = setting.DefaultFor(profileName);
            foreach (var (layer, isPolicy) in new[] { (machine, false), (policy, true) })
            {
                if (!layer.TryGetValue(setting.Name, out var raw)) continue;
                try
                {
                    value = SettingValues.Coerce(setting, raw);
                    if (isPolicy) managed.Add(setting.Name);
                }
                catch (SettingValueException ex)
                {
                    _log.Warning($"Ignoring invalid registry value: {ex.Message}");
                }
            }
            result[setting.Name] = value;
        }
        return (result, managed);
    }

    /// <summary>
    /// Writes a global (<paramref name="profileName"/> null) or per-profile setting. Values equal to the default are removed
    /// rather than stored, so defaults can change in later versions. Policy values still win.
    /// </summary>
    public void SetSetting(string? profileName, string name, object? value)
    {
        var scope = profileName is null ? SettingScope.Global : SettingScope.Profile;
        var setting = Schema.Find(scope, name);
        if (setting.Type == SettingType.Secret) throw new InvalidOperationException($"'{name}' is a secret. Use the secret store.");
        var path = PathFor(profileName);
        var typed = SettingValues.Coerce(setting, value);
        if (Equals(typed, setting.DefaultFor(profileName)) && name != "Backend")
        {
            // Keep the profile key itself so an all-defaults profile still exists.
            if (profileName is not null) registry.EnsureKey(path);
            registry.RemoveValue(path, name);
            return;
        }
        switch (setting.Type)
        {
            case SettingType.Bool: registry.SetValue(path, name, (bool)typed ? 1 : 0, RegistryValueType.DWord); break;
            case SettingType.Int: registry.SetValue(path, name, (int)typed, RegistryValueType.DWord); break;
            default: registry.SetValue(path, name, (string)typed, RegistryValueType.String); break;
        }
    }

    /// <summary>Removes an explicit value so the default applies again.</summary>
    public void ClearSetting(string? profileName, string name) => registry.RemoveValue(PathFor(profileName), name);

    private static string PathFor(string? profileName)
    {
        if (profileName is null) return RegistryRoot;
        Naming.AssertProfileName(profileName);
        return $@"{RegistryRoot}\Printers\{profileName}";
    }

    /// <summary>Creates a profile. Run printer provisioning afterwards to create the Windows printer.</summary>
    public void CreateProfile(string name, IReadOnlyDictionary<string, object?>? settings = null, bool force = false)
    {
        Naming.AssertProfileName(name);
        var path = PathFor(name);
        if (registry.KeyExists(path) && !force) throw new InvalidOperationException($"Printer profile '{name}' already exists.");
        registry.EnsureKey(path);
        var all = new Dictionary<string, object?>(settings ?? new Dictionary<string, object?>());
        if (!all.ContainsKey("Backend")) all["Backend"] = "OdooJson2";
        foreach (var (key, value) in all)
        {
            var setting = Schema.Find(SettingScope.Profile, key);
            if (setting.Type == SettingType.Secret)
            {
                if (value is string { Length: > 0 } secret) secrets.Set(name, key, secret);
            }
            else
            {
                SetSetting(name, key, value);
            }
        }
    }

    /// <summary>Deletes a profile and its secrets.</summary>
    public void RemoveProfile(string name)
    {
        registry.RemoveKey($@"{RegistryRoot}\Printers\{name}");
        secrets.RemoveAll(name);
    }
}
