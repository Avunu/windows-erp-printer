using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ErpPrinter.Logging;
using ErpPrinter.Settings;

namespace ErpPrinter.Windows.Logging;

/// <summary>One parsed log line. <see cref="Time"/> is local time, as written.</summary>
public sealed record LogEntry(DateTime Time, ErpLogLevel Level, string Component, string Message);

/// <summary>Parses the files written by <see cref="ErpLog"/> back into entries (port of <c>Get-ErpRecentLogEntries</c>).</summary>
public static partial class LogReader
{
    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<level>\w+)\s*\] \[(?<component>[^\]]*)\] (?<message>.*)$")]
    private static partial Regex LinePattern();

    public static bool TryParse(string line, out LogEntry entry)
    {
        entry = null!;
        var match = LinePattern().Match(line);
        if (!match.Success) return false;
        if (!Enum.TryParse<ErpLogLevel>(match.Groups["level"].Value, ignoreCase: true, out var level) || !Enum.IsDefined(level)) return false;
        if (!DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
        entry = new LogEntry(time, level, match.Groups["component"].Value, match.Groups["message"].Value);
        return true;
    }

    /// <summary>
    /// The entries written in the last <paramref name="hours"/> hours by every component's files (or only <paramref name="component"/>),
    /// oldest first. <paramref name="levels"/> keeps only those levels; null keeps all.
    /// </summary>
    public static IReadOnlyList<LogEntry> Read(string logDirectory, TimeProvider? time = null, int hours = 24, IReadOnlyCollection<ErpLogLevel>? levels = null, string? component = null)
    {
        if (!Directory.Exists(logDirectory)) return [];
        var since = (time ?? TimeProvider.System).GetLocalNow().DateTime.AddHours(-hours);
        var entries = new List<LogEntry>();

        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(logDirectory).EnumerateFiles("*.log").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        foreach (var file in files)
        {
            if (file.LastWriteTime < since) continue;
            if (!string.IsNullOrEmpty(component) && !file.Name.StartsWith(component + "-", StringComparison.OrdinalIgnoreCase)) continue;
            string[] lines;
            try { lines = ReadAllLinesShared(file.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var line in lines)
            {
                if (!TryParse(line, out var entry)) continue;
                if (entry.Time < since) continue;
                if (levels is not null && !levels.Contains(entry.Level)) continue;
                entries.Add(entry);
            }
        }
        return entries.OrderBy(e => e.Time).ToList();
    }

    /// <summary>The log is appended to by running components, so read it without asking for exclusive access.</summary>
    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }
}

public static class LogMaintenance
{
    /// <summary>Deletes <c>*.log</c> files not written for <paramref name="retentionDays"/> days (port of <c>Remove-ErpOldLogs</c>). Returns the deleted file names.</summary>
    public static IReadOnlyList<string> RemoveOldLogs(string logDirectory, int retentionDays, TimeProvider? time = null)
    {
        if (!Directory.Exists(logDirectory)) return [];
        var cutoff = (time ?? TimeProvider.System).GetLocalNow().DateTime.AddDays(-retentionDays);
        var removed = new List<string>();
        foreach (var file in new DirectoryInfo(logDirectory).EnumerateFiles("*.log").ToList())
        {
            if (file.LastWriteTime >= cutoff) continue;
            try
            {
                file.Delete();
                removed.Add(file.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked: it will be picked up by the next run.
            }
        }
        return removed;
    }

    public static IReadOnlyList<string> RemoveOldLogs(GlobalSettings global, TimeProvider? time = null) =>
        RemoveOldLogs(global.LogDirectory, global.LogRetentionDays, time);
}
