using System.Text;
using ErpPrinter.Secrets;

namespace ErpPrinter.Settings;

/// <summary>
/// The scripting surface of the product: list, create and remove printer profiles, change settings and store API keys.
/// <c>erpprinter profile|set|secret</c> are thin shells over these methods, which are the replacement for the
/// PowerShell module's New-/Set-/Remove-ErpPrinterProfile, Set-ErpPrinterSetting and Set-ErpPrinterSecret.
/// Creating a profile does not create the Windows printer: the service reconciles printers when the configuration
/// changes, and <c>erpprinter sync</c> does it on demand.
/// </summary>
public sealed class ConfigCommands(ConfigStore store)
{
    public string ListProfiles()
    {
        var profiles = store.LoadProfiles();
        if (profiles.Count == 0) return "No printer profiles.";
        var builder = new StringBuilder();
        foreach (var p in profiles)
            builder.AppendLine($"{p.Name}  [{(p.Enabled ? "enabled" : "disabled")}, {p.Backend}, printer '{p.PrinterName}'{(p.PolicyDefined ? ", set by policy" : "")}]");
        return builder.ToString().TrimEnd();
    }

    /// <summary>The effective settings of a profile. Secrets are never shown, only whether they are stored.</summary>
    public string ShowProfile(string name)
    {
        var profile = store.GetProfile(name) ?? throw new ArgumentException($"No printer profile named '{name}'.");
        var builder = new StringBuilder();
        builder.AppendLine($"Profile: {profile.Name}{(profile.PolicyDefined ? " (defined by Group Policy)" : "")}");
        foreach (var setting in Schema.Profile)
        {
            if (setting.Type == SettingType.Secret)
            {
                builder.AppendLine($"  {setting.Name} = {(store.Secrets.Has(name, setting.Name) ? "(stored)" : "(not set)")}");
                continue;
            }
            if (setting.Backends.Count > 0 && !setting.Backends.Contains(profile.Backend)) continue;
            var managed = profile.Managed.Contains(setting.Name) ? "  [policy]" : "";
            builder.AppendLine($"  {setting.Name} = {Display(profile[setting.Name])}{managed}");
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Creates a profile from <c>Name=Value</c> pairs. <c>ApiKey</c> and <c>ApiSecret</c> are stored encrypted.</summary>
    public void AddProfile(string name, IReadOnlyDictionary<string, string> pairs, bool force)
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) settings[Canonical(SettingScope.Profile, key)] = value;
        store.CreateProfile(name, settings, force);
    }

    public void RemoveProfile(string name)
    {
        if (store.GetProfile(name) is null) throw new ArgumentException($"No printer profile named '{name}'.");
        store.RemoveProfile(name);
    }

    /// <summary>Sets global settings, or settings of <paramref name="profileName"/>. Values equal to the default are not stored.</summary>
    public void Set(string? profileName, IReadOnlyDictionary<string, string> pairs)
    {
        if (profileName is not null && store.GetProfile(profileName) is null) throw new ArgumentException($"No printer profile named '{profileName}'.");
        var scope = profileName is null ? SettingScope.Global : SettingScope.Profile;
        foreach (var (key, value) in pairs) store.SetSetting(profileName, Canonical(scope, key), value);
    }

    /// <summary>Removes explicit values so the defaults apply again.</summary>
    public void Clear(string? profileName, IEnumerable<string> names)
    {
        var scope = profileName is null ? SettingScope.Global : SettingScope.Profile;
        foreach (var name in names) store.ClearSetting(profileName, Canonical(scope, name));
    }

    public void SetSecret(string profileName, string secretName, string value)
    {
        if (store.GetProfile(profileName) is null) throw new ArgumentException($"No printer profile named '{profileName}'.");
        var canonical = SecretNames.All.FirstOrDefault(n => n.Equals(secretName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"'{secretName}' is not a secret. The secrets are: {string.Join(", ", SecretNames.All)}.");
        store.Secrets.Set(profileName, canonical, value);
    }

    public string SecretStatus(string profileName) =>
        string.Join(Environment.NewLine, SecretNames.All.Select(n => $"{n}: {(store.Secrets.Has(profileName, n) ? "stored" : "not set")}"));

    /// <summary>Resolves a setting name case-insensitively to the schema's spelling, or explains what is valid.</summary>
    private static string Canonical(SettingScope scope, string name)
    {
        var schema = Schema.For(scope);
        var match = schema.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Name;
        var kind = scope == SettingScope.Global ? "global" : "profile";
        throw new ArgumentException($"Unknown {kind} setting '{name}'. Valid names: {string.Join(", ", schema.Select(s => s.Name))}.");
    }

    private static string Display(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string { Length: 0 } => "(empty)",
        _ => value.ToString() ?? "",
    };
}
