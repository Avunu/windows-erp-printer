using System.Globalization;
using System.Text.RegularExpressions;
using ErpPrinter.Outbox;

namespace ErpPrinter.Templating;

/// <summary>The values a template can use for one document. Port of Get-ErpTemplateTokens.</summary>
public static partial class TemplateTokens
{
    [GeneratedRegex(@"^(?<d>[^\\]+)\\(?<u>.+)$")]
    private static partial Regex DomainBackslashUser();

    [GeneratedRegex(@"^(?<u>[^@]+)@(?<d>.+)$")]
    private static partial Regex UserAtDomain();

    /// <summary>
    /// Tokens: title user username domain computer date time datetime id jobid profile (keys are lower case).
    /// The date tokens use the print time in the local time zone, or "now" when the sidecar has none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Create(OutboxMeta meta, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var user = meta.User ?? "";
        var username = user;
        var domain = "";
        if (DomainBackslashUser().Match(user) is { Success: true } m1)
        {
            domain = m1.Groups["d"].Value;
            username = m1.Groups["u"].Value;
        }
        else if (UserAtDomain().Match(user) is { Success: true } m2)
        {
            domain = m2.Groups["d"].Value;
            username = m2.Groups["u"].Value;
        }

        var printed = meta.Printed is { } p
            ? TimeZoneInfo.ConvertTime(p, time.LocalTimeZone)
            : time.GetLocalNow();
        var inv = CultureInfo.InvariantCulture;

        return new Dictionary<string, string>
        {
            ["title"] = Template.CleanTitle(meta.Title),
            ["user"] = user,
            ["username"] = username,
            ["domain"] = domain,
            ["computer"] = meta.Computer ?? "",
            ["date"] = printed.ToString("yyyy-MM-dd", inv),
            ["time"] = printed.ToString("HHmmss", inv),
            ["datetime"] = printed.ToString("yyyy-MM-dd HHmmss", inv),
            ["id"] = meta.Id ?? "",
            ["jobid"] = meta.JobId ?? "",
            ["profile"] = meta.Profile ?? "",
        };
    }
}
