using System.Diagnostics.Eventing.Reader;
using ErpPrinter.Diagnostics;

namespace ErpPrinter.Windows.Diagnostics;

/// <summary>
/// Reads <c>Microsoft-Windows-PrintService/Operational</c> and <c>/Admin</c> with the event log API. A missing log or a log without
/// matching events reads as empty (like Get-WinEvent's "no events were found"); access problems throw.
/// </summary>
public sealed class EventLogPrintServiceLog : IPrintServiceLog
{
    private static string LogName(PrintServiceLogKind kind) => $"Microsoft-Windows-PrintService/{kind}";

    public Task<PrintServiceLogState> GetStateAsync(PrintServiceLogKind log, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                using var configuration = new EventLogConfiguration(LogName(log));
                return configuration.IsEnabled ? PrintServiceLogState.Enabled : PrintServiceLogState.Disabled;
            }
            catch (EventLogNotFoundException)
            {
                return PrintServiceLogState.NotPresent;
            }
        }, cancellationToken);

    public Task<IReadOnlyList<PrintServiceEvent>> ReadAsync(
        PrintServiceLogKind log,
        DateTimeOffset since,
        IReadOnlyCollection<int>? levels = null,
        IReadOnlyCollection<int>? ids = null,
        int max = 50,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(log, since, levels, ids, max, cancellationToken), cancellationToken);

    internal static string BuildQuery(DateTimeOffset since, IReadOnlyCollection<int>? levels, IReadOnlyCollection<int>? ids)
    {
        var conditions = new List<string>();
        if (levels is { Count: > 0 }) conditions.Add("(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")");
        if (ids is { Count: > 0 }) conditions.Add("(" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")");
        conditions.Add($"TimeCreated[@SystemTime>='{since.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}']");
        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    private static IReadOnlyList<PrintServiceEvent> Read(
        PrintServiceLogKind log, DateTimeOffset since, IReadOnlyCollection<int>? levels, IReadOnlyCollection<int>? ids, int max, CancellationToken cancellationToken)
    {
        var events = new List<PrintServiceEvent>();
        try
        {
            var query = new EventLogQuery(LogName(log), PathType.LogName, BuildQuery(since, levels, ids)) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            while (events.Count < max)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (record.TimeCreated is not { } created) continue;
                var level = record.Level ?? 0;
                events.Add(new PrintServiceEvent(record.Id, level, LevelName(level), new DateTimeOffset(created), Describe(record)));
            }
        }
        catch (EventLogNotFoundException)
        {
            return [];
        }
        return events;
    }

    private static string Describe(EventRecord record)
    {
        try
        {
            if (record.FormatDescription() is { Length: > 0 } text) return text;
        }
        catch (EventLogException)
        {
            // The provider's message file is not available; fall back to the raw insertion strings.
        }
        return string.Join(" ", record.Properties.Select(p => p.Value?.ToString()).Where(v => !string.IsNullOrEmpty(v)));
    }

    private static string LevelName(int level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        _ => "Unknown",
    };
}
