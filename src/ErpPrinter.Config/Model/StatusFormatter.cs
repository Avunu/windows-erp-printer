using System.Globalization;

namespace ErpPrinter.Config.Model;

/// <summary>Text and colour decisions of the Status tab.</summary>
internal static class StatusFormatter
{
    /// <summary>Headers of the status list, in order. <see cref="Row"/> returns the matching cells.</summary>
    public static readonly IReadOnlyList<(string Header, int Width)> Columns =
    [
        ("Printer profile", 140), ("Windows printer", 140), ("Queue", 65), ("Pending", 60),
        ("Failed", 55), ("Sent", 50), ("Last sent", 160), ("Last error", 190),
    ];

    public static string ServiceLine(StatusSnapshot status) => $"Version {status.Version}    Service: {status.ServiceState}";

    /// <summary>"OK", "Missing" (enabled but the printer is gone) or "Disabled".</summary>
    public static string QueueText(ProfileStatus p) => p.PrinterOk ? "OK" : p.Enabled ? "Missing" : "Disabled";

    /// <summary>The short date and time, plus the remote id when the backend returned one; empty if nothing was sent yet.</summary>
    public static string LastSentText(ProfileStatus p) =>
        p.LastSent is not { } sent ? "" : sent.ToString("g", CultureInfo.CurrentCulture) + (string.IsNullOrEmpty(p.LastRemoteId) ? "" : $" (id {p.LastRemoteId})");

    /// <summary>Failed documents, or an enabled profile whose printer is missing: shown in red.</summary>
    public static bool IsProblem(ProfileStatus p) => p.Failed > 0 || (p.Enabled && !p.PrinterOk);

    public static string[] Row(ProfileStatus p) =>
    [
        p.Profile,
        p.PrinterName,
        QueueText(p),
        p.Pending.ToString(CultureInfo.InvariantCulture),
        p.Failed.ToString(CultureInfo.InvariantCulture),
        p.Sent.ToString(CultureInfo.InvariantCulture),
        LastSentText(p),
        p.LastError ?? "",
    ];
}
