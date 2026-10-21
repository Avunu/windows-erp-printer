using System.Globalization;

namespace ErpPrinter.Settings;

/// <summary>
/// Resolved settings of one scope: policy value, else machine value, else schema default.
/// Typed wrappers (<see cref="GlobalSettings"/>, <see cref="PrinterProfile"/>) expose the same values as properties.
/// </summary>
public class SettingsBag
{
    private readonly IReadOnlyDictionary<string, object> _values;

    public SettingsBag(IReadOnlyDictionary<string, object> values, IReadOnlyCollection<string>? managed = null)
    {
        _values = values;
        Managed = new HashSet<string>(managed ?? [], StringComparer.Ordinal);
    }

    /// <summary>Settings whose value comes from Group Policy and is read-only in the GUI.</summary>
    public IReadOnlySet<string> Managed { get; }

    public IReadOnlyDictionary<string, object> Values => _values;

    public object this[string name] => _values.TryGetValue(name, out var v) ? v : throw new KeyNotFoundException($"No setting '{name}'.");

    public string GetString(string name) => _values.TryGetValue(name, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
    public int GetInt(string name) => _values.TryGetValue(name, out var v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
    public bool GetBool(string name) => _values.TryGetValue(name, out var v) && v is bool b && b;
}

public sealed class GlobalSettings(IReadOnlyDictionary<string, object> values, IReadOnlyCollection<string>? managed = null) : SettingsBag(values, managed)
{
    public string DataRoot => GetString("DataRoot");
    public int PollIntervalSeconds => GetInt("PollIntervalSeconds");
    public int MaxAttempts => GetInt("MaxAttempts");
    public int RetryMaxBackoffMinutes => GetInt("RetryMaxBackoffMinutes");
    public int MaxDocumentSizeMB => GetInt("MaxDocumentSizeMB");
    public int HttpTimeoutSeconds => GetInt("HttpTimeoutSeconds");
    public string ProxyUrl => GetString("ProxyUrl");
    public int KeepSentDays => GetInt("KeepSentDays");
    public string LogLevel => GetString("LogLevel");
    public int LogRetentionDays => GetInt("LogRetentionDays");
    public bool AutoUpdate => GetBool("AutoUpdate");
    public string UpdateManifestUrl => GetString("UpdateManifestUrl");
    public bool RequireSignedUpdates => GetBool("RequireSignedUpdates");
    public string UpdateSignerThumbprint => GetString("UpdateSignerThumbprint");

    public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Settings with every schema default, for tests and first-run tools.</summary>
    public static GlobalSettings Defaults(string? dataRoot = null)
    {
        var values = Schema.Global.ToDictionary(s => s.Name, s => s.DefaultFor(null));
        if (dataRoot is not null) values["DataRoot"] = dataRoot;
        return new GlobalSettings(values);
    }
}

public sealed class PrinterProfile(string name, IReadOnlyDictionary<string, object> values, IReadOnlyCollection<string>? managed = null, bool policyDefined = false)
    : SettingsBag(values, managed)
{
    public string Name { get; } = name;

    /// <summary>True when the profile exists only under the policy key (pushed by Group Policy).</summary>
    public bool PolicyDefined { get; } = policyDefined;

    public bool Enabled => GetBool("Enabled");
    public string PrinterName => GetString("PrinterName");
    public string PipeName => GetString("PipeName");
    public string FileNameTemplate => GetString("FileNameTemplate");
    public string Backend => GetString("Backend");
    public string ServerUrl => GetString("ServerUrl");
    public string Database => GetString("Database");
    public string Username => GetString("Username");
    public string OdooModel => GetString("OdooModel");
    public string OdooFileField => GetString("OdooFileField");
    public string OdooNameField => GetString("OdooNameField");
    public string OdooDefaultValues => GetString("OdooDefaultValues");
    public bool UserMapping => GetBool("UserMapping");
    public string OdooUserField => GetString("OdooUserField");
    public string UserEmailDomain => GetString("UserEmailDomain");
    public string ERPNextFolder => GetString("ERPNextFolder");
    public string ERPNextDoctype => GetString("ERPNextDoctype");
    public string ERPNextDocname => GetString("ERPNextDocname");
    public bool ERPNextIsPrivate => GetBool("ERPNextIsPrivate");
    public string WebhookFormat => GetString("WebhookFormat");
    public string WebhookFileField => GetString("WebhookFileField");
    public string WebhookAuthHeader => GetString("WebhookAuthHeader");

    public string PortName => Naming.PortName(PipeName);

    /// <summary>A profile with schema defaults overridden by <paramref name="overrides"/>; for tests and tools.</summary>
    public static PrinterProfile Create(string name, IReadOnlyDictionary<string, object>? overrides = null)
    {
        var values = Schema.Profile.Where(s => s.Type != SettingType.Secret).ToDictionary(s => s.Name, s => s.DefaultFor(name));
        if (overrides is not null)
            foreach (var (key, value) in overrides) values[key] = value;
        return new PrinterProfile(name, values);
    }
}

public sealed record ErpConfig(GlobalSettings Global, IReadOnlyList<PrinterProfile> Profiles);
