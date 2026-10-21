using System.Text.RegularExpressions;

namespace ErpPrinter;

public static partial class Naming
{
    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^[\w][\w .()&+-]{0,63}$")]
    private static partial Regex ProfileNamePattern();

    /// <summary>Makes a string safe for use in file, folder and pipe names.</summary>
    public static string ToSafeName(string name)
    {
        var safe = Unsafe().Replace(name, "_").Trim('_', '.');
        return safe.Length == 0 ? "default" : safe;
    }

    public static void AssertProfileName(string name)
    {
        if (!ProfileNamePattern().IsMatch(name))
            throw new ArgumentException($"Invalid printer profile name '{name}'. Use up to 64 letters, digits, spaces and . ( ) & + - _", nameof(name));
    }

    /// <summary>The printer port name for a pipe.</summary>
    public static string PortName(string pipeName) => @"\\.\pipe\" + pipeName;
}
