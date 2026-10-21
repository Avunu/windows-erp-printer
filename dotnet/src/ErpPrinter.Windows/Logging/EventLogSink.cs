using System.Diagnostics;
using ErpPrinter.Logging;

namespace ErpPrinter.Windows.Logging;

/// <summary>Where <see cref="ErpLog"/> sends the event-log copy of a line. Tests use <see cref="NullEventLogSink"/>.</summary>
public interface IEventLogSink
{
    void Write(ErpLogLevel level, string message, int eventId);
}

public sealed class NullEventLogSink : IEventLogSink
{
    public static readonly NullEventLogSink Instance = new();
    public void Write(ErpLogLevel level, string message, int eventId) { }
}

/// <summary>
/// The Application event log under source <c>ErpPrinter</c>. Writes only if the source exists (the installer registers it), so an
/// unelevated run never tries to create one. The existence check is cached briefly because it reads the registry.
/// </summary>
public sealed class WindowsEventLogSink : IEventLogSink
{
    public const string DefaultSource = "ErpPrinter";
    private const int MaxMessageLength = 30000;

    private readonly string _source;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _exists;

    public WindowsEventLogSink(string source = DefaultSource, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    private bool SourceExists()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _checkedAt < TimeSpan.FromMinutes(_exists ? 10 : 1)) return _exists;
            try { _exists = EventLog.SourceExists(_source); }
            catch (Exception) { _exists = false; }
            _checkedAt = now;
            return _exists;
        }
    }

    public void Write(ErpLogLevel level, string message, int eventId)
    {
        if (!SourceExists()) return;
        var type = level switch
        {
            ErpLogLevel.Error => EventLogEntryType.Error,
            ErpLogLevel.Warning => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information,
        };
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];
        EventLog.WriteEntry(_source, message, type, eventId);
    }
}
