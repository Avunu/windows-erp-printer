using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Model;

/// <summary>
/// The edits made to one scope (the global settings, or one printer profile): the loaded values, the current values and the
/// secrets typed in. It is the model behind the generated controls and knows nothing about Windows Forms.
/// </summary>
/// <remarks>
/// Secrets are write-only: the stored secret is never read into the editor, so the control shows only whether one is stored.
/// </remarks>
internal abstract class SettingsEdit
{
    private readonly Dictionary<string, object> _original;
    private readonly Dictionary<string, object> _current;
    private readonly Dictionary<string, string> _entered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _carried = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stored;

    protected SettingsEdit(
        IReadOnlyList<SettingDefinition> schema,
        IReadOnlyDictionary<string, object> original,
        IReadOnlyDictionary<string, object> current,
        IReadOnlyCollection<string> managed,
        IEnumerable<string> storedSecrets)
    {
        Schema = schema;
        _original = new Dictionary<string, object>(original, StringComparer.Ordinal);
        _current = new Dictionary<string, object>(current, StringComparer.Ordinal);
        Managed = new HashSet<string>(managed, StringComparer.Ordinal);
        _stored = new HashSet<string>(storedSecrets, StringComparer.Ordinal);
    }

    public IReadOnlyList<SettingDefinition> Schema { get; }

    /// <summary>Settings pushed by Group Policy; read-only.</summary>
    public IReadOnlySet<string> Managed { get; }

    public bool IsManaged(string name) => Managed.Contains(name);

    /// <summary>The settings that apply right now (for a profile: those of the selected backend).</summary>
    public virtual IEnumerable<SettingDefinition> VisibleSettings => Schema;

    public object Get(string name) =>
        _current.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"No setting '{name}'.");

    public string GetString(string name) => Convert.ToString(Get(name), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>Sets a value. Managed settings are ignored; text is trimmed. Secrets set the pending entry, never a stored value.</summary>
    public void Set(string name, object value)
    {
        var setting = Find(name);
        if (IsManaged(name)) return;
        if (setting.Type == SettingType.Secret)
        {
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (text.Length == 0) _entered.Remove(name);
            else _entered[name] = text;
            return;
        }
        var typed = SettingValues.Coerce(setting, value);
        _current[name] = typed is string s ? s.Trim() : typed;
    }

    /// <summary>The secret text typed in the editor and not saved yet; empty when nothing was typed. This is not the stored secret.</summary>
    public string EnteredSecret(string name) => _entered.GetValueOrDefault(name, "");

    /// <summary>Whether a secret is stored, or entered, or carried over from a renamed profile (so the form can say "stored" without showing it).</summary>
    public bool SecretAvailable(string name) => _entered.ContainsKey(name) || _carried.ContainsKey(name) || _stored.Contains(name);

    /// <summary>True when a secret is already stored (not merely typed).</summary>
    public bool SecretStored(string name) => _carried.ContainsKey(name) || _stored.Contains(name);

    /// <summary>Keeps a secret read from the store so that a renamed profile can write it under its new name. It is never shown.</summary>
    public void CarrySecret(string name, string plainText)
    {
        if (plainText.Length > 0) _carried[name] = plainText;
    }

    /// <summary>The value to write for a secret on save: what was typed, else what was carried over, else null (leave the stored one alone).</summary>
    public string? SecretToSave(string name) =>
        _entered.TryGetValue(name, out var typed) ? typed : _carried.GetValueOrDefault(name);

    /// <summary>The secrets typed in, as the backend wants them, for "Test connection" with unsaved credentials.</summary>
    public BackendSecrets EnteredSecrets() => new(EnteredSecret(SecretNames.ApiKey), EnteredSecret(SecretNames.ApiSecret));

    /// <summary>Non-secret settings whose current value differs from the loaded one, in schema order.</summary>
    public IReadOnlyList<string> ChangedSettings() =>
        Schema.Where(s => s.Type != SettingType.Secret && !IsManaged(s.Name) && !Equals(_original.GetValueOrDefault(s.Name), _current.GetValueOrDefault(s.Name)))
            .Select(s => s.Name).ToList();

    public virtual bool IsDirty => ChangedSettings().Count > 0 || _entered.Count > 0;

    public SettingDefinition Find(string name) =>
        Schema.FirstOrDefault(s => s.Name == name) ?? throw new KeyNotFoundException($"No setting '{name}'.");

    /// <summary>
    /// One editor per visible setting for generating controls: in schema order, but with the settings of a group together
    /// (the global schema lists some groups in two places), groups in order of first appearance.
    /// </summary>
    public IReadOnlyList<SettingEditor> Editors() =>
        VisibleSettings.GroupBy(s => s.Group).SelectMany(g => g).Select(s => new SettingEditor(s, this)).ToList();

    /// <summary>The labels of every setting of the scope, visible or not, so a form can size its label column once for all backends.</summary>
    public IReadOnlyList<string> AllLabels() => Schema.Select(s => new SettingEditor(s, this).Label).ToList();

    internal IReadOnlyDictionary<string, object> CurrentValues => _current;
}

internal sealed class GlobalEdit : SettingsEdit
{
    public GlobalEdit(GlobalSettings source)
        : base(Settings.Schema.Global, source.Values, source.Values, source.Managed, [])
    {
    }
}

internal sealed class ProfileEdit : SettingsEdit
{
    private ProfileEdit(
        string name,
        IReadOnlyDictionary<string, object> original,
        IReadOnlyDictionary<string, object> current,
        IReadOnlyCollection<string> managed,
        IEnumerable<string> storedSecrets,
        bool isNew,
        bool policyDefined)
        : base(Settings.Schema.Profile, original, current, managed, storedSecrets)
    {
        Name = name;
        IsNew = isNew;
        PolicyDefined = policyDefined;
    }

    public string Name { get; }

    /// <summary>Added in this session and not saved yet; everything that differs from the defaults is written when it is saved.</summary>
    public bool IsNew { get; }

    /// <summary>Exists only in the policy key (pushed by Group Policy): it cannot be removed or renamed.</summary>
    public bool PolicyDefined { get; }

    public string Backend => GetString("Backend");

    public override IEnumerable<SettingDefinition> VisibleSettings =>
        Schema.Where(s => s.Backends.Count == 0 || s.Backends.Contains(Backend));

    public override bool IsDirty => IsNew || base.IsDirty;

    /// <summary>The line under the tab bar, or empty.</summary>
    public string HeaderText =>
        PolicyDefined ? $"'{Name}' is defined by Group Policy." : IsNew ? $"'{Name}' is new and not saved yet." : "";

    /// <summary>The profile as currently edited (without secrets), for validation and "Test connection".</summary>
    public PrinterProfile ToProfile() => new(Name, new Dictionary<string, object>(CurrentValues), Managed, PolicyDefined);

    public static ProfileEdit FromStore(PrinterProfile profile, SecretStore secrets) =>
        new(profile.Name, profile.Values, profile.Values, profile.Managed,
            SecretNames.All.Where(n => secrets.Has(profile.Name, n)), isNew: false, profile.PolicyDefined);

    public static ProfileEdit CreateNew(string name)
    {
        var defaults = DefaultsFor(name);
        return new ProfileEdit(name, defaults, defaults, [], [], isNew: true, policyDefined: false);
    }

    /// <summary>A new, unsaved profile named <paramref name="newName"/> with the settings and secrets of this one.</summary>
    public ProfileEdit RenamedCopy(string newName, SecretStore secrets)
    {
        var oldDefaults = DefaultsFor(Name);
        var newDefaults = DefaultsFor(newName);
        var values = new Dictionary<string, object>(CurrentValues);
        // Names that were derived from the old profile name follow the new one; deliberate overrides stay.
        foreach (var derived in new[] { "PrinterName", "PipeName" })
            if (Equals(values[derived], oldDefaults[derived])) values[derived] = newDefaults[derived];

        var copy = new ProfileEdit(newName, newDefaults, values, [], [], isNew: true, policyDefined: false);
        var stored = IsNew ? BackendSecrets.Empty : secrets.Get(Name);
        foreach (var secret in SecretNames.All)
        {
            var text = EnteredSecret(secret);
            if (text.Length == 0) text = SecretToSave(secret) ?? (secret == SecretNames.ApiKey ? stored.ApiKey : stored.ApiSecret);
            copy.CarrySecret(secret, text);
        }
        return copy;
    }

    private static Dictionary<string, object> DefaultsFor(string name) =>
        Settings.Schema.Profile.Where(s => s.Type != SettingType.Secret).ToDictionary(s => s.Name, s => s.DefaultFor(name));
}
