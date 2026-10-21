using ErpPrinter.Logging;
using ErpPrinter.Settings;

namespace ErpPrinter.Outbox;

/// <summary>Periodic cleanup run by the uploader: old logs, archived sent documents and downloaded update packages. Port of Invoke-ErpHousekeeping.</summary>
public sealed class OutboxHousekeeping(IErpLog log, TimeProvider time)
{
    public static readonly TimeSpan UpdateFileAge = TimeSpan.FromDays(30);

    /// <returns>The number of files deleted.</returns>
    public int Run(ErpConfig config)
    {
        var global = config.Global;
        var now = time.GetUtcNow();
        var deleted = 0;

        // Log files older than LogRetentionDays.
        var logs = Path.Combine(global.DataRoot, "logs");
        deleted += DeleteOlderThan(logs, "*.log", now.AddDays(-global.LogRetentionDays));

        // Archived sent documents older than KeepSentDays (everything when archiving is off).
        var spool = Path.Combine(global.DataRoot, OutboxPaths.SpoolFolderName);
        if (Directory.Exists(spool))
        {
            var cutoff = now.AddDays(-Math.Max(global.KeepSentDays, 0));
            foreach (var profileDir in Directory.EnumerateDirectories(spool))
                deleted += DeleteOlderThan(Path.Combine(profileDir, "sent"), "*", cutoff);
        }

        // Downloaded update packages and installer logs.
        deleted += DeleteOlderThan(Path.Combine(global.DataRoot, "updates"), "*", now - UpdateFileAge);
        return deleted;
    }

    private int DeleteOlderThan(string folder, string pattern, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(folder)) return 0;
        var deleted = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles(pattern))
        {
            if (new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) >= cutoff) continue;
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"Could not delete {file.FullName}: {ex.Message}");
            }
        }
        return deleted;
    }
}
