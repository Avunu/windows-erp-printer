namespace ErpPrinter.Diagnostics;

public enum PrintServiceLogKind
{
    /// <summary>Microsoft-Windows-PrintService/Operational: the spooler's record of each job (events 800, 801, 805, 842, 307).</summary>
    Operational,

    /// <summary>Microsoft-Windows-PrintService/Admin: warnings and errors such as port failures.</summary>
    Admin,
}

public enum PrintServiceLogState
{
    NotPresent,
    Disabled,
    Enabled,
}

/// <summary>
/// One PrintService event. <see cref="Level"/> follows Windows: 1 critical, 2 error, 3 warning, 4 information, 5 verbose.
/// <see cref="Time"/> is local time; it is formatted as given, never converted.
/// </summary>
public sealed record PrintServiceEvent(int Id, int Level, string LevelDisplayName, DateTimeOffset Time, string Message)
{
    /// <summary>True for critical, error and warning events.</summary>
    public bool IsProblem => Level is >= 1 and <= 3;
}

/// <summary>Reads the Windows PrintService event logs. The Windows implementation is <c>EventLogPrintServiceLog</c>.</summary>
public interface IPrintServiceLog
{
    Task<PrintServiceLogState> GetStateAsync(PrintServiceLogKind log, CancellationToken cancellationToken = default);

    /// <summary>
    /// Newest-first events at or after <paramref name="since"/>. <paramref name="levels"/> and <paramref name="ids"/> restrict the
    /// result when given. Returns an empty list (never throws) when nothing matches or the log has no events.
    /// </summary>
    Task<IReadOnlyList<PrintServiceEvent>> ReadAsync(
        PrintServiceLogKind log,
        DateTimeOffset since,
        IReadOnlyCollection<int>? levels = null,
        IReadOnlyCollection<int>? ids = null,
        int max = 50,
        CancellationToken cancellationToken = default);
}

public static class PrintServiceEventFormatter
{
    /// <summary>One line: <c>2026-10-20 17:27:35 [Warning] id 372: message</c>. Whitespace is collapsed and long text is cut. Port of Format-ErpEventLine.</summary>
    public static string Format(PrintServiceEvent ev, int maxLength = 260)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(ev.Message, @"\s+", " ").Trim();
        if (text.Length > maxLength) text = text[..maxLength] + "...";
        return $"{ev.Time:yyyy-MM-dd HH:mm:ss} [{ev.LevelDisplayName}] id {ev.Id}: {text}";
    }
}
