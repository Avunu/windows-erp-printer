using System.Globalization;
using System.Text.RegularExpressions;

namespace ErpPrinter.Settings;

public sealed class SettingValueException(string message) : Exception(message);

/// <summary>Coerces raw registry or user values to a setting's type. Port of ConvertTo-ErpSettingValue.</summary>
public static partial class SettingValues
{
    [GeneratedRegex(@"^(1|true|yes|on)$", RegexOptions.IgnoreCase)]
    private static partial Regex TruePattern();

    [GeneratedRegex(@"^(0|false|no|off)$", RegexOptions.IgnoreCase)]
    private static partial Regex FalsePattern();

    public static object Coerce(SettingDefinition setting, object? value)
    {
        var text = value switch
        {
            null => "",
            bool b => b ? "True" : "False",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        switch (setting.Type)
        {
            case SettingType.Bool:
                if (value is bool flag) return flag;
                if (text.Length == 0) return false;
                if (TruePattern().IsMatch(text)) return true;
                if (FalsePattern().IsMatch(text)) return false;
                throw new SettingValueException($"Setting '{setting.Name}' expects a boolean, got '{text}'.");

            case SettingType.Int:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    throw new SettingValueException($"Setting '{setting.Name}' expects a whole number, got '{text}'.");
                if (n < setting.Min || n > setting.Max)
                    throw new SettingValueException($"Setting '{setting.Name}' must be between {setting.Min} and {setting.Max}.");
                return n;

            case SettingType.Choice:
                var match = setting.Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                return match ?? throw new SettingValueException($"Setting '{setting.Name}' must be one of: {string.Join(", ", setting.Choices)}.");

            default:
                return text;
        }
    }
}
