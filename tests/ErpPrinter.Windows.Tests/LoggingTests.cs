using ErpPrinter.Logging;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Logging;

namespace ErpPrinter.Windows.Tests;

public sealed class ErpLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ErpPrinterTests", Guid.NewGuid().ToString("N"));
    private readonly WinMemoryEventSink _events = new();
    private readonly WinFixedTime _time = new(new DateTimeOffset(2026, 3, 5, 14, 7, 9, 123, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    private ErpLog Create(string component = "listener", ErpLogLevel level = ErpLogLevel.Information) =>
        new(component, new ErpLogSettings(level, Path.Combine(_dir, "logs")), _time, _events);

    private string ReadLog(string name) => File.ReadAllText(Path.Combine(_dir, "logs", name));

    [Fact]
    public void Lines_have_the_documented_format_and_go_to_the_daily_file()
    {
        var log = Create();

        log.Information("hello world");
        log.Warning("careful");
        log.Error("broken");

        Assert.Equal(
            "2026-03-05 14:07:09.123 [Information] [listener] hello world" + Environment.NewLine +
            "2026-03-05 14:07:09.123 [Warning    ] [listener] careful" + Environment.NewLine +
            "2026-03-05 14:07:09.123 [Error      ] [listener] broken" + Environment.NewLine,
            ReadLog("listener-20260305.log"));
    }

    [Fact]
    public void Each_component_and_day_gets_its_own_file()
    {
        Create("listener").Information("a");
        Create("uploader").Information("b");
        _time.Advance(TimeSpan.FromDays(1));
        Create("listener").Information("c");

        var names = Directory.GetFiles(Path.Combine(_dir, "logs")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["listener-20260305.log", "listener-20260306.log", "uploader-20260305.log"], names);
    }

    [Fact]
    public void The_file_is_utf8_without_a_BOM_and_keeps_non_ascii_text()
    {
        Create().Information("Größe 5 € – ok");

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "logs", "listener-20260305.log"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Größe 5 € – ok", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData(ErpLogLevel.Error, new[] { "e" })]
    [InlineData(ErpLogLevel.Warning, new[] { "e", "w" })]
    [InlineData(ErpLogLevel.Information, new[] { "e", "w", "i" })]
    [InlineData(ErpLogLevel.Verbose, new[] { "e", "w", "i", "v" })]
    public void The_configured_level_filters_lines(ErpLogLevel configured, string[] expected)
    {
        var log = Create(level: configured);

        log.Verbose("v");
        log.Information("i");
        log.Warning("w");
        log.Error("e");

        var written = ReadLog("listener-20260305.log").Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l[^1].ToString()).Order().ToList();
        Assert.Equal(expected.Order().ToList(), written);
    }

    [Fact]
    public void The_level_follows_the_settings_when_they_are_updated()
    {
        var settings = new ErpLogSettings(ErpLogLevel.Error, Path.Combine(_dir, "logs"));
        var log = new ErpLog("cli", settings, _time, _events);

        log.Information("dropped");
        settings.Update(GlobalSettings.Defaults(_dir)); // default level is Information
        log.Information("kept");

        var text = ReadLog("cli-20260305.log");
        Assert.DoesNotContain("dropped", text);
        Assert.Contains("kept", text);
    }

    [Fact]
    public void Settings_parse_the_level_name_and_fall_back_to_Information()
    {
        Assert.Equal(ErpLogLevel.Verbose, ErpLogSettings.ParseLevel("verbose"));
        Assert.Equal(ErpLogLevel.Information, ErpLogSettings.ParseLevel("nonsense"));
        Assert.Equal(ErpLogLevel.Information, ErpLogSettings.ParseLevel("42"));
        Assert.Equal(ErpLogLevel.Information, ErpLogSettings.ParseLevel(null));
    }

    [Fact]
    public void Warnings_errors_and_information_also_go_to_the_event_log_with_default_ids()
    {
        var log = Create(level: ErpLogLevel.Verbose);

        log.Verbose("not in the event log");
        log.Information("i");
        log.Warning("w");
        log.Error("e");

        Assert.Equal(
            [(ErpLogLevel.Information, "[listener] i", 1000), (ErpLogLevel.Warning, "[listener] w", 2000), (ErpLogLevel.Error, "[listener] e", 3000)],
            _events.Events);
    }

    [Fact]
    public void An_explicit_event_id_wins()
    {
        Create().Information("Queued", 1001);

        Assert.Equal([(ErpLogLevel.Information, "[listener] Queued", 1001)], _events.Events);
    }

    [Fact]
    public void Lines_below_the_level_do_not_reach_the_event_log()
    {
        Create(level: ErpLogLevel.Error).Warning("w");

        Assert.Empty(_events.Events);
    }

    [Fact]
    public void An_unwritable_log_folder_is_reported_once_as_event_2001_and_logging_continues()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "a file where the log folder should be");
        var log = new ErpLog("listener", new ErpLogSettings(ErpLogLevel.Information, blocker), _time, _events);

        log.Information("one");
        log.Information("two");

        var failures = _events.Events.Where(e => e.EventId == 2001).ToList();
        var failure = Assert.Single(failures);
        Assert.Equal(ErpLogLevel.Warning, failure.Level);
        Assert.StartsWith("[listener] Could not write to the log folder '" + blocker + "'", failure.Message);
        Assert.Equal(2, _events.Events.Count(e => e.EventId == 1000)); // the normal event-log copies still happen
    }

    [Fact]
    public void Without_a_log_folder_nothing_is_written_to_files()
    {
        var log = new ErpLog("listener", new ErpLogSettings(), _time, _events);

        log.Information("x");

        Assert.False(Directory.Exists(_dir));
        Assert.Single(_events.Events);
    }

    [Fact]
    public void A_throwing_event_sink_never_breaks_the_caller()
    {
        var log = new ErpLog("listener", new ErpLogSettings(ErpLogLevel.Information, Path.Combine(_dir, "logs")), _time, new ThrowingSink());

        log.Error("still logged to the file");

        Assert.Contains("still logged", ReadLog("listener-20260305.log"));
    }

    [Fact]
    public void Exception_helper_logs_type_and_root_cause()
    {
        var log = Create();

        log.Exception("Failed to receive", new InvalidOperationException("outer", new IOException("disk gone")));

        var text = ReadLog("listener-20260305.log");
        Assert.Contains("[Error      ] [listener] Failed to receive: [InvalidOperationException] outer (caused by IOException: disk gone)", text);
    }

    [Fact]
    public void Echo_receives_the_lines_that_pass_the_filter()
    {
        var echoed = new List<string>();
        var log = Create();
        log.Echo = (level, message) => echoed.Add($"{level}:{message}");

        log.Verbose("hidden");
        log.Warning("shown");

        Assert.Equal(["Warning:shown"], echoed);
    }

    [Fact]
    public void Many_threads_can_write_without_losing_lines()
    {
        var log = Create();

        Parallel.For(0, 200, i => log.Information($"line {i}"));

        Assert.Equal(200, ReadLog("listener-20260305.log").Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private sealed class ThrowingSink : IEventLogSink
    {
        public void Write(ErpLogLevel level, string message, int eventId) => throw new InvalidOperationException("no event log");
    }
}

public sealed class LogReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ErpPrinterTests", Guid.NewGuid().ToString("N"));
    private readonly WinFixedTime _time = new(DateTimeOffset.UtcNow, TimeZoneInfo.Local); // the reader compares with the machine's local time

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    private void Write(string component, ErpLogLevel level, string message, TimeSpan ago)
    {
        _time.Set(DateTimeOffset.UtcNow - ago);
        new ErpLog(component, new ErpLogSettings(ErpLogLevel.Verbose, _dir), _time, NullEventLogSink.Instance).Write(level, message);
    }

    [Fact]
    public void Parses_a_line_into_its_parts()
    {
        Assert.True(LogReader.TryParse("2026-03-05 14:07:09.123 [Warning    ] [uploader] Upload failed: HTTP 500", out var entry));

        Assert.Equal(new DateTime(2026, 3, 5, 14, 7, 9, 123), entry.Time);
        Assert.Equal(ErpLogLevel.Warning, entry.Level);
        Assert.Equal("uploader", entry.Component);
        Assert.Equal("Upload failed: HTTP 500", entry.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   at Some.Stack.Frame()")]
    [InlineData("2026-03-05 14:07:09.123 [Bogus      ] [x] y")]
    [InlineData("2026-13-45 14:07:09.123 [Error      ] [x] y")]
    [InlineData("2026-03-05 14:07:09 [Error      ] [x] y")]
    public void Lines_that_are_not_log_lines_are_skipped(string line)
    {
        Assert.False(LogReader.TryParse(line, out _));
    }

    [Fact]
    public void Round_trips_what_ErpLog_writes()
    {
        Write("listener", ErpLogLevel.Error, "boom [with] brackets", TimeSpan.Zero);

        var entry = Assert.Single(LogReader.Read(_dir));

        Assert.Equal(ErpLogLevel.Error, entry.Level);
        Assert.Equal("listener", entry.Component);
        Assert.Equal("boom [with] brackets", entry.Message);
    }

    [Fact]
    public void Entries_come_back_oldest_first_across_components()
    {
        Write("uploader", ErpLogLevel.Information, "second", TimeSpan.FromMinutes(5));
        Write("listener", ErpLogLevel.Information, "first", TimeSpan.FromMinutes(10));
        Write("listener", ErpLogLevel.Information, "third", TimeSpan.FromMinutes(1));

        Assert.Equal(["first", "second", "third"], LogReader.Read(_dir).Select(e => e.Message));
    }

    [Fact]
    public void Filters_by_hours_level_and_component()
    {
        Write("listener", ErpLogLevel.Information, "old", TimeSpan.FromHours(30));
        Write("listener", ErpLogLevel.Warning, "recent warning", TimeSpan.FromHours(1));
        Write("listener", ErpLogLevel.Verbose, "recent verbose", TimeSpan.FromHours(1));
        Write("uploader", ErpLogLevel.Warning, "uploader warning", TimeSpan.FromHours(2));

        Assert.DoesNotContain("old", LogReader.Read(_dir, hours: 24).Select(e => e.Message));
        Assert.Contains("old", LogReader.Read(_dir, hours: 48).Select(e => e.Message));
        Assert.Equal(["uploader warning", "recent warning"],
            LogReader.Read(_dir, levels: [ErpLogLevel.Warning]).Select(e => e.Message));
        Assert.Equal(["recent warning", "recent verbose"],
            LogReader.Read(_dir, component: "listener").Select(e => e.Message));
    }

    [Fact]
    public void A_missing_folder_is_empty_and_a_file_being_written_can_be_read()
    {
        Assert.Empty(LogReader.Read(Path.Combine(_dir, "nope")));

        Write("listener", ErpLogLevel.Information, "x", TimeSpan.Zero);
        using var writer = new FileStream(Directory.GetFiles(_dir)[0], FileMode.Append, FileAccess.Write, FileShare.Read);

        Assert.Single(LogReader.Read(_dir));
    }
}

public sealed class LogMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ErpPrinterTests", Guid.NewGuid().ToString("N"));

    public LogMaintenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    private void Touch(string name, DateTime lastWrite)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, lastWrite);
    }

    [Fact]
    public void Deletes_log_files_older_than_the_retention_and_keeps_everything_else()
    {
        var now = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);
        Touch("listener-20260301.log", new DateTime(2026, 3, 1));
        Touch("uploader-20260305.log", new DateTime(2026, 3, 5, 11, 0, 0));
        Touch("listener-20260310.log", new DateTime(2026, 3, 10));
        Touch("listener-20260320.log", new DateTime(2026, 3, 20));
        Touch("notes.txt", new DateTime(2020, 1, 1));

        var removed = LogMaintenance.RemoveOldLogs(_dir, 14, new WinFixedTime(now));

        Assert.Equal(["listener-20260301.log", "uploader-20260305.log"], removed.Order());
        Assert.Equal(["listener-20260310.log", "listener-20260320.log", "notes.txt"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Uses_the_settings_of_the_global_configuration()
    {
        var global = GlobalSettings.Defaults(Path.GetDirectoryName(_dir)!);
        Assert.Empty(LogMaintenance.RemoveOldLogs(global)); // its logs folder does not exist: nothing to do, no error
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        Assert.Empty(LogMaintenance.RemoveOldLogs(Path.Combine(_dir, "nope"), 1));
    }
}
