using System.Globalization;
using System.Text;
using ErpPrinter.Logging;
using ErpPrinter.Settings;

namespace ErpPrinter.Windows.Logging;

/// <summary>
/// Level and folder of the log files, shared by every <see cref="ErpLog"/> of a process. Updated when the configuration is reloaded
/// (the PowerShell module did this with <c>Set-ErpLogContext</c>). With no <see cref="LogDirectory"/> nothing is written to files.
/// </summary>
public sealed class ErpLogSettings
{
    private volatile ErpLogLevel _level;
    private volatile string? _logDirectory;
    private int _fileFailureReported;

    public ErpLogSettings(ErpLogLevel level = ErpLogLevel.Information, string? logDirectory = null)
    {
        _level = level;
        _logDirectory = logDirectory;
    }

    public ErpLogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    public string? LogDirectory
    {
        get => _logDirectory;
        set => _logDirectory = value;
    }

    public static ErpLogSettings From(GlobalSettings global)
    {
        var settings = new ErpLogSettings();
        settings.Update(global);
        return settings;
    }

    public void Update(GlobalSettings global)
    {
        Level = ParseLevel(global.LogLevel);
        LogDirectory = global.DataRoot.Length == 0 ? null : global.LogDirectory;
    }

    public static ErpLogLevel ParseLevel(string? text) =>
        Enum.TryParse<ErpLogLevel>(text, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : ErpLogLevel.Information;

    /// <summary>True exactly once per settings object: the "log file cannot be written" event is reported once per process.</summary>
    internal bool TryMarkFileFailureReported() => Interlocked.Exchange(ref _fileFailureReported, 1) == 0;
}

/// <summary>
/// Writes <c>yyyy-MM-dd HH:mm:ss.fff [Level      ] [component] message</c> lines to <c>&lt;logs&gt;\&lt;component&gt;-yyyyMMdd.log</c>
/// (UTF-8) and mirrors warnings, errors and information to the Application event log. Never throws: a log that fails must not take the
/// caller down, but the first file failure is reported to the event log as event 2001.
/// </summary>
public sealed class ErpLog : IErpLog
{
    private static readonly object FileLock = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ErpLogSettings _settings;
    private readonly TimeProvider _time;
    private readonly IEventLogSink _events;

    public ErpLog(string component, ErpLogSettings settings, TimeProvider? time = null, IEventLogSink? events = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        Component = component;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _events = events ?? new WindowsEventLogSink();
    }

    public string Component { get; }

    /// <summary>Called with every line that passes the level filter (the CLI prints it to the console).</summary>
    public Action<ErpLogLevel, string>? Echo { get; set; }

    public static string FileNameFor(string component, DateTimeOffset localNow) =>
        string.Create(CultureInfo.InvariantCulture, $"{component}-{localNow:yyyyMMdd}.log");

    public static string FormatLine(DateTimeOffset localNow, ErpLogLevel level, string component, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"{localNow:yyyy-MM-dd HH:mm:ss.fff} [{level,-11}] [{component}] {message}");

    public void Write(ErpLogLevel level, string message, int eventId = 0)
    {
        if (level > _settings.Level) return;
        var now = _time.GetLocalNow();

        try { Echo?.Invoke(level, message); }
        catch (Exception) { /* a broken console must not break logging */ }

        if (_settings.LogDirectory is { Length: > 0 } directory)
        {
            try
            {
                var line = FormatLine(now, level, Component, message);
                lock (FileLock)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, FileNameFor(Component, now)), line + Environment.NewLine, Utf8);
                }
            }
            catch (Exception ex)
            {
                // A log that silently stops growing is the worst failure mode, so say so once in the event log.
                ReportFileFailure($"Could not write to the log folder '{directory}': {ex.Message}");
            }
        }

        if (level == ErpLogLevel.Verbose) return;
        try
        {
            if (eventId == 0) eventId = DefaultEventId(level);
            _events.Write(level, $"[{Component}] {message}", eventId);
        }
        catch (Exception) { /* the event log is best effort */ }
    }

    public static int DefaultEventId(ErpLogLevel level) => level switch
    {
        ErpLogLevel.Error => 3000,
        ErpLogLevel.Warning => 2000,
        _ => 1000,
    };

    private void ReportFileFailure(string message)
    {
        if (!_settings.TryMarkFileFailureReported()) return;
        try { _events.Write(ErpLogLevel.Warning, $"[{Component}] {message}", 2001); }
        catch (Exception) { /* nothing left to try */ }
    }
}
