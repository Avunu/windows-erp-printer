using System.Text;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Model;

/// <summary>View model of one setting: what the generated label and control show, and where an edit goes.</summary>
internal sealed class SettingEditor(SettingDefinition definition, SettingsEdit owner)
{
    public SettingDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public SettingType Type => Definition.Type;
    public string Group => Definition.Group;

    /// <summary>Managed by Group Policy: the control is read-only.</summary>
    public bool ReadOnly => owner.IsManaged(Name);

    public string Label => ReadOnly ? Definition.Label + " (policy)" : Definition.Label;

    /// <summary>Help text wrapped for a tooltip, with a note for policy-managed settings; empty when there is nothing to say.</summary>
    public string ToolTip
    {
        get
        {
            var text = Wrap(Definition.Help, 80);
            if (!ReadOnly) return text;
            const string note = "This setting is managed by Group Policy.";
            return text.Length == 0 ? note : text + "\n\n" + note;
        }
    }

    /// <summary>
    /// The value shown in the control: the typed value (bool, int or string). For a secret this is only what was typed in this
    /// session, never the stored secret, so it is empty until the user types.
    /// </summary>
    public object Value
    {
        get => Type == SettingType.Secret ? owner.EnteredSecret(Name) : owner.Get(Name);
        set => owner.Set(Name, value);
    }

    /// <summary>Placeholder of a secret box ("Stored - leave empty to keep" or "Not set"); null for other types.</summary>
    public string? SecretCue =>
        Type != SettingType.Secret ? null : owner.SecretStored(Name) ? "Stored - leave empty to keep" : "Not set";

    /// <summary>Greedy word wrap on spaces; existing line breaks are kept.</summary>
    internal static string Wrap(string text, int width)
    {
        if (text.Length == 0) return "";
        var result = new StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line > 0 && line + 1 + word.Length > width)
                {
                    result.Append('\n');
                    line = 0;
                }
                else if (line > 0)
                {
                    result.Append(' ');
                    line++;
                }
                result.Append(word);
                line += word.Length;
            }
            result.Append('\n');
        }
        return result.ToString().TrimEnd('\n');
    }
}
