using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ErpPrinter.Logging;

namespace ErpPrinter.Diagnostics;

/// <summary>One parsed line of an ERP Printer log file. <see cref="Time"/> is local time.</summary>
public sealed record LogEntry(DateTimeOffset Time, ErpLogLevel Level, string Component, string Message);

/// <summary>Reads back what the components logged, for the log-history checks and the test print.</summary>
public interface ILogReader
{
    /// <summary>
    /// Entries of the last <paramref name="hours"/> hours, oldest first. <paramref name="levels"/> keeps only those levels (all when null),
    /// <paramref name="component"/> only that component's files. Returns an empty list when the log folder does not exist.
    /// </summary>
    Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int hours, IReadOnlyCollection<ErpLogLevel>? levels = null, string? component = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Parses the daily log files (<c>&lt;component&gt;-yyyyMMdd.log</c>, lines <c>yyyy-MM-dd HH:mm:ss.fff [Level      ] [component] message</c>).
/// Port of Get-ErpRecentLogEntries. Files held open by the writer are read with shared access.
/// </summary>
public sealed partial class LogFileReader(string logDirectory, TimeProvider? time = null) : ILogReader
{
    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<level>\w+)\s*\] \[(?<component>[^\]]*)\] (?<message>.*)$")]
    private static partial Regex LinePattern();

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int hours, IReadOnlyCollection<ErpLogLevel>? levels = null, string? component = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(logDirectory)) return [];
        var now = _time.GetLocalNow();
        var since = now.AddHours(-hours);
        var zone = _time.LocalTimeZone;
        var entries = new List<LogEntry>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(logDirectory, "*.log").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (component is not null && !name.StartsWith(component + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) < since.ToUniversalTime()) continue;
            string[] lines;
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                lines = text.Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var raw in lines)
            {
                var match = LinePattern().Match(raw.TrimEnd('\r'));
                if (!match.Success) continue;
                if (!Enum.TryParse<ErpLogLevel>(match.Groups["level"].Value, true, out var level)) continue;
                if (levels is not null && !levels.Contains(level)) continue;
                var local = DateTime.ParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var stamp = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (stamp < since) continue;
                entries.Add(new LogEntry(stamp, level, match.Groups["component"].Value, match.Groups["message"].Value));
            }
        }
        return entries.OrderBy(e => e.Time).ToList();
    }
}
