namespace ErpPrinter.Logging;

public enum ErpLogLevel
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Verbose = 4,
}

/// <summary>
/// Where components write their log lines. The Windows layer implements it with the daily log files
/// (<c>&lt;data folder&gt;\logs\&lt;component&gt;-yyyyMMdd.log</c>) and the Application event log.
/// Event ids follow the PowerShell product: 1001 queued, 1002 delivered, 2002 retry, 3002 failed.
/// </summary>
public interface IErpLog
{
    void Write(ErpLogLevel level, string message, int eventId = 0);
}

public static class ErpLogExtensions
{
    public static void Information(this IErpLog log, string message, int eventId = 0) => log.Write(ErpLogLevel.Information, message, eventId);
    public static void Warning(this IErpLog log, string message, int eventId = 0) => log.Write(ErpLogLevel.Warning, message, eventId);
    public static void Error(this IErpLog log, string message, int eventId = 0) => log.Write(ErpLogLevel.Error, message, eventId);
    public static void Verbose(this IErpLog log, string message) => log.Write(ErpLogLevel.Verbose, message);

    /// <summary>Logs an exception with its type and root cause; the stack trace goes to Verbose.</summary>
    public static void Exception(this IErpLog log, string message, Exception ex, int eventId = 0)
    {
        var root = ex.GetBaseException();
        var detail = ex.Message;
        if (!ReferenceEquals(root, ex)) detail += $" (caused by {root.GetType().Name}: {root.Message})";
        log.Write(ErpLogLevel.Error, $"{message}: [{ex.GetType().Name}] {detail}", eventId);
        if (ex.StackTrace is { Length: > 0 } stack)
            log.Write(ErpLogLevel.Verbose, "Stack trace: " + stack.ReplaceLineEndings(" | "));
    }
}

public sealed class NullLog : IErpLog
{
    public static readonly NullLog Instance = new();
    public void Write(ErpLogLevel level, string message, int eventId = 0) { }
}

/// <summary>Collects log lines in memory. Used by tests and by diagnostics that want to show what a run logged.</summary>
public sealed class MemoryLog : IErpLog
{
    private readonly List<(ErpLogLevel Level, string Message, int EventId)> _entries = [];
    public IReadOnlyList<(ErpLogLevel Level, string Message, int EventId)> Entries { get { lock (_entries) return _entries.ToArray(); } }
    public void Write(ErpLogLevel level, string message, int eventId = 0) { lock (_entries) _entries.Add((level, message, eventId)); }
}
