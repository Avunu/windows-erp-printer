namespace ErpPrinter.Updates;

public static class VersionComparer
{
    /// <summary>
    /// Returns 1 if <paramref name="available"/> is newer than <paramref name="current"/>, 0 if equal, -1 if older.
    /// A leading "v" and any "-prerelease" or "+build" suffix are ignored (so 1.0.1-beta.1 counts as 1.0.1).
    /// </summary>
    /// <exception cref="FormatException">A version is not of the form major.minor[.build[.revision]].</exception>
    public static int Compare(string current, string available) => Math.Sign(Parse(available).CompareTo(Parse(current)));

    internal static Version Parse(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];
        text = text.Trim();
        if (!Version.TryParse(text, out var version)) throw new FormatException($"'{value}' is not a valid version.");
        return version;
    }
}
