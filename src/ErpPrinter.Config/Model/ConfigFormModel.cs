using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Model;

internal sealed record SaveResult(bool Saved, IReadOnlyList<string> Problems, string? Warning)
{
    public static SaveResult Invalid(IReadOnlyList<string> problems) => new(false, problems, null);
    public static SaveResult Ok(string? warning) => new(true, [], warning);
}

/// <summary>
/// Everything the configuration form does that is not drawing: the loaded configuration with the user's edits, dirty tracking,
/// adding/removing/renaming profiles, validation and writing the changes through <see cref="ConfigStore"/>.
/// </summary>
internal sealed class ConfigFormModel
{
    public const string InvalidNameMessage = "Use up to 64 letters, digits, spaces and . ( ) & + - _";

    private readonly ConfigStore _store;
    private readonly List<ProfileEdit> _profiles = [];
    private readonly List<string> _deleted = [];

    public ConfigFormModel(ConfigStore store)
    {
        _store = store;
        Global = new GlobalEdit(store.LoadGlobal());
        Reload();
    }

    public GlobalEdit Global { get; private set; }

    public IReadOnlyList<ProfileEdit> Profiles => _profiles;

    /// <summary>The profile shown on the Printers tab, or null.</summary>
    public ProfileEdit? Current { get; private set; }

    /// <summary>Profiles that were removed and will be deleted on the next save.</summary>
    public IReadOnlyList<string> PendingRemovals => _deleted;

    /// <summary>True when there is anything to save. Unlike a sticky flag, it clears when an edit is reverted.</summary>
    public bool Dirty => _deleted.Count > 0 || Global.IsDirty || _profiles.Any(p => p.IsDirty);

    public ProfileEdit? Find(string name) => _profiles.FirstOrDefault(p => Same(p.Name, name));

    /// <summary>Discards all edits and loads the saved configuration, keeping the selection if the profile still exists.</summary>
    public void Reload()
    {
        var keep = Current?.Name;
        Global = new GlobalEdit(_store.LoadGlobal());
        _profiles.Clear();
        _deleted.Clear();
        _profiles.AddRange(_store.LoadProfiles().Select(p => ProfileEdit.FromStore(p, _store.Secrets)));
        Current = keep is null ? null : Find(keep);
    }

    public void Select(string? name) => Current = name is null ? null : Find(name);

    /// <summary>A name for the "Add printer" box.</summary>
    public string SuggestName() => _profiles.Count == 0 ? "Send to ERP" : $"Send to ERP {_profiles.Count + 1}";

    /// <summary>Why <paramref name="name"/> cannot be used for a profile (the same wording as the PowerShell GUI), or null when it can.</summary>
    public string? CheckName(string name)
    {
        if (Find(name) is not null) return $"A printer named '{name}' already exists.";
        try
        {
            Naming.AssertProfileName(name);
            return null;
        }
        catch (ArgumentException)
        {
            return InvalidNameMessage;
        }
    }

    public ProfileEdit AddProfile(string name)
    {
        name = name.Trim();
        if (CheckName(name) is { } problem) throw new InvalidOperationException(problem);
        var profile = ProfileEdit.CreateNew(name);
        _profiles.Add(profile);
        Current = profile;
        return profile;
    }

    public void RemoveProfile(string name)
    {
        var profile = Find(name) ?? throw new InvalidOperationException($"There is no printer named '{name}'.");
        if (profile.PolicyDefined) throw new InvalidOperationException($"'{name}' is defined by Group Policy and cannot be removed here.");
        if (!profile.IsNew && !_deleted.Contains(profile.Name, StringComparer.OrdinalIgnoreCase)) _deleted.Add(profile.Name);
        _profiles.Remove(profile);
        if (Current == profile) Current = null;
    }

    /// <summary>
    /// A profile is its registry key, so renaming means a new profile with the same settings and secrets and removing the old one.
    /// Both happen when the changes are saved.
    /// </summary>
    public ProfileEdit RenameProfile(string oldName, string newName)
    {
        newName = newName.Trim();
        var profile = Find(oldName) ?? throw new InvalidOperationException($"There is no printer named '{oldName}'.");
        if (profile.PolicyDefined) throw new InvalidOperationException($"'{oldName}' is defined by Group Policy and cannot be renamed here.");
        if (newName == profile.Name) return profile;
        if (CheckName(newName) is { } problem && !Same(newName, oldName)) throw new InvalidOperationException(problem);

        var renamed = profile.RenamedCopy(newName, _store.Secrets);
        var index = _profiles.IndexOf(profile);
        if (!profile.IsNew && !_deleted.Contains(profile.Name, StringComparer.OrdinalIgnoreCase)) _deleted.Add(profile.Name);
        _profiles[index] = renamed;
        if (Current == profile) Current = renamed;
        return renamed;
    }

    /// <summary>
    /// What is wrong with the enabled profiles, as "profile: problem" lines (the messages of <see cref="ProfileValidator"/>);
    /// empty when everything can be saved. Disabled profiles are not checked.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var all = _profiles.Select(p => p.ToProfile()).ToList();
        var problems = new List<string>();
        foreach (var edit in _profiles)
        {
            if (!edit.ToProfile().Enabled) continue;
            problems.AddRange(ProfileValidator.Validate(edit.ToProfile(), edit.SecretAvailable, all).Select(p => $"{edit.Name}: {p}"));
        }
        return problems;
    }

    /// <summary>
    /// Validates and, if everything is fine, writes the changes, syncs the Windows printers and restarts the service, then reloads.
    /// A failure in the last two steps does not undo the save; it is returned as <see cref="SaveResult.Warning"/>.
    /// </summary>
    public SaveResult SaveAndApply(IConfigActions actions)
    {
        var problems = Validate();
        if (problems.Count > 0) return SaveResult.Invalid(problems);

        WriteChanges();

        var warnings = new List<string>();
        try { actions.SyncPrinters(); } catch (Exception ex) { warnings.Add(ex.Message); }
        try { actions.RestartService(); } catch (Exception ex) { warnings.Add(ex.Message); }

        Reload();
        return SaveResult.Ok(warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));
    }

    /// <summary>Writes only what changed; values that equal the default are removed from the registry by <see cref="ConfigStore.SetSetting"/>.</summary>
    internal void WriteChanges()
    {
        foreach (var name in _deleted) _store.RemoveProfile(name);
        _deleted.Clear();

        foreach (var profile in _profiles)
        {
            if (profile.IsNew) _store.CreateProfile(profile.Name, new Dictionary<string, object?> { ["Backend"] = profile.Get("Backend") }, force: true);
            // A policy-defined profile without edits has no changed settings, so saving never turns it into a local profile.
            foreach (var name in profile.ChangedSettings()) _store.SetSetting(profile.Name, name, profile.Get(name));
            foreach (var secret in SecretNames.All)
                if (profile.SecretToSave(secret) is { Length: > 0 } text) _store.Secrets.Set(profile.Name, secret, text);
        }

        foreach (var name in Global.ChangedSettings()) _store.SetSetting(null, name, Global.Get(name));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
