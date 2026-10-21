namespace ErpPrinter.Settings;

public enum SettingType
{
    String,
    Int,
    Bool,
    Choice,
    /// <summary>Stored encrypted under Secrets\&lt;profile&gt;, never in the settings key.</summary>
    Secret,
}

/// <summary>
/// One configurable setting. The schema is the single source of truth: the registry layer,
/// validation, the configuration GUI and the ADMX policy templates all follow it.
/// </summary>
public sealed class SettingDefinition
{
    public required string Name { get; init; }
    public required SettingType Type { get; init; }
    public required string Label { get; init; }
    public required string Group { get; init; }

    /// <summary>The default for settings whose default does not depend on the profile.</summary>
    public object? Default { get; init; }

    /// <summary>For defaults derived from the profile name (printer name, pipe name) or the machine (data folder).</summary>
    public Func<string?, object>? DefaultFactory { get; init; }

    /// <summary>Human-readable default shown in documentation when it is computed.</summary>
    public string? DefaultText { get; init; }

    public string Help { get; init; } = "";

    /// <summary>Backends the setting applies to; empty means all.</summary>
    public IReadOnlyList<string> Backends { get; init; } = [];

    public IReadOnlyList<string> Choices { get; init; } = [];
    public int Min { get; init; }
    public int Max { get; init; } = int.MaxValue;
    public bool Required { get; init; }

    /// <summary>Resolves the default for a profile (or for the global scope when <paramref name="profileName"/> is null).</summary>
    public object DefaultFor(string? profileName) =>
        DefaultFactory is not null ? DefaultFactory(profileName) : Default ?? DefaultOfType();

    private object DefaultOfType() => Type switch
    {
        SettingType.Bool => false,
        SettingType.Int => 0,
        _ => "",
    };
}
