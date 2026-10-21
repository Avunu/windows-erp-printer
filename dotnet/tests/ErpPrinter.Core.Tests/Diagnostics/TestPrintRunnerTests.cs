using ErpPrinter.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>Port of TestPrint.Tests.ps1. Time is a fake clock that the polling delay advances, so nothing sleeps.</summary>
public class TestPrintRunnerTests : IDisposable
{
    private const string Token = "abcd1234";
    private readonly DiagTempDir _data = new();
    private readonly DiagClock _clock = new();
    private readonly DiagSpooler _spooler = new();
    private readonly DiagPrintServiceLog _log = new();
    private readonly DiagLogReader _listenerLog = new();
    private readonly DiagSystem _system = new();
    private readonly DiagScanner _scanner = new();
    private readonly DiagPageSender _sender = new();
    private int _ticks;
    private bool _sent;
    private DateTimeOffset _sentAt;

    public TestPrintRunnerTests()
    {
        _spooler.Printers.Add(DiagData.Printer());
        _sender.OnSend = _ =>
        {
            _sent = true;
            _sentAt = _clock.GetUtcNow();
        };
    }

    public void Dispose() => _data.Dispose();

    private static TestPrintOptions Options(int timeout = 60, Action<string>? progress = null) =>
        new(timeout) { StuckAfter = TimeSpan.FromSeconds(1), TokenFactory = () => Token, OnProgress = progress };

    private Task<TestPrintResult> RunAsync(ErpConfig? config = null, string profile = DiagData.ProfileName, TestPrintOptions? options = null)
    {
        var runner = new TestPrintRunner(_spooler, _sender, _log, _listenerLog, _system, _scanner, new DiagSpoolFiles(), _clock,
            (span, _) =>
            {
                _clock.Advance(span);
                _ticks++;
                return Task.CompletedTask;
            });
        return runner.RunAsync(config ?? DiagData.Config(_data.Path, DiagData.Profile()), profile, options ?? Options());
    }

    private static string PageName => $"ERP Printer test page [{Token}]";

    private static OutboxItem Item(string folder, Action<OutboxMeta>? edit = null)
    {
        var meta = new OutboxMeta { Id = "doc1", Profile = DiagData.ProfileName, Title = PageName, RemoteId = "42" };
        edit?.Invoke(meta);
        return new OutboxItem("doc1", folder, meta);
    }

    [Fact]
    public async Task Follows_a_page_from_the_queue_through_the_listener_to_delivery()
    {
        // The job is printing for two polls; the listener queues the document at once and the uploader delivers it at poll 3.
        _spooler.Jobs = _ => _sent && _ticks < 2 ? [DiagData.Job(7, PageName, JobStatusFlags.Printing)] : [];
        _scanner.Find = (_, _) => _sent && _ticks < 3 ? Item("outbox") : null;

        var result = await RunAsync();

        Assert.True(result.Success);
        Assert.Equal(TestPrintStage.Delivered, result.Stage);
        Assert.Contains("Result: SUCCESS", result.Report);
        Assert.Contains("in the print queue", result.Report);
        Assert.Contains("listener received it", result.Report);
        Assert.Equal(Token, result.Token);
        Assert.Equal(PageName, Assert.Single(_sender.Sent).DocumentName);
    }

    [Fact]
    public async Task Reports_delivery_from_the_sent_folder_with_the_remote_id()
    {
        _scanner.Find = (_, _) => _sent ? Item("sent") : null;

        var result = await RunAsync();

        Assert.Equal(TestPrintStage.Delivered, result.Stage);
        Assert.Contains("Delivered to the ERP (remote id 42).", result.Report);
    }

    [Fact]
    public async Task Stops_early_and_names_the_problem_when_the_job_is_stuck_and_nothing_hosts_the_pipe()
    {
        _system.Pipe = false;
        _spooler.Jobs = _ => _sent ? [DiagData.Job(9, PageName, JobStatusFlags.Error | JobStatusFlags.Offline)] : [];
        var started = _clock.GetUtcNow();

        var result = await RunAsync(options: Options(timeout: 60));

        Assert.True(_clock.GetUtcNow() - started < TimeSpan.FromSeconds(30));
        Assert.False(result.Success);
        Assert.Equal(TestPrintStage.InQueue, result.Stage);
        Assert.Contains("stuck as 'Error, Offline'", result.Summary);
        Assert.Contains("Nothing is hosting", result.Summary);
        Assert.Contains("Nothing is hosting", result.Report);
        Assert.Contains("not waiting any longer", result.Report);
        Assert.Contains("Spooler analysis:", result.Report);
    }

    [Fact]
    public async Task Finds_the_older_job_that_blocks_a_page_sitting_at_Normal()
    {
        var old = DiagData.Job(150, "Old report", JobStatusFlags.Error, _clock.GetUtcNow(), size: 10);
        _spooler.Jobs = _ => _sent ? [old, DiagData.Job(151, PageName, JobStatusFlags.None, _clock.GetUtcNow())] : [old];

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.False(result.Success);
        Assert.Equal(TestPrintStage.InQueue, result.Stage);
        Assert.Equal(1, result.Evidence.JobsAhead);
        Assert.False(result.Evidence.JobEverPrinting);
        Assert.Contains("stuck as 'Normal'", result.Summary);
        Assert.Contains("job 150 'Old report'", result.Summary);
        Assert.Contains("print queue already holds 1 job(s)", result.Report);
    }

    [Fact]
    public async Task Reports_a_printer_that_is_paused_when_the_page_sits_at_Normal()
    {
        _spooler.Printers[0] = DiagData.Printer(status: PrinterStatusFlags.Paused);
        _spooler.Jobs = _ => _sent ? [DiagData.Job(5, PageName, JobStatusFlags.None, _clock.GetUtcNow(), size: 1)] : [];

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Contains("printer is not ready: The spooler reports the printer as Paused.", result.Summary);
        Assert.Contains("Pause Printing", result.Hint);
    }

    [Fact]
    public async Task Reports_a_spooler_that_printed_to_the_wrong_port()
    {
        _spooler.Jobs = _ => _sent && _ticks < 3 ? [DiagData.Job(3, PageName, JobStatusFlags.Printing)] : [];
        _log.Operational.Add(DiagPrintServiceLog.Event(307,
            $"Document 3, {PageName} owned by jdoe was printed on Send to Odoo via port FILE:. Size in bytes: 4000. Pages printed: 1. No user action is required.",
            _clock.GetUtcNow()));

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.False(result.Success);
        Assert.Equal(TestPrintStage.Spooled, result.Stage);
        Assert.Equal("FILE:", result.Evidence.SpoolerPort);
        Assert.Contains("printed to 'FILE:'", result.Summary);
        Assert.Contains("not the listener's pipe", result.Summary);
    }

    [Fact]
    public async Task Also_understands_the_through_port_wording_of_event_307()
    {
        _spooler.Jobs = _ => _sent && _ticks < 3 ? [DiagData.Job(3, PageName, JobStatusFlags.Printing)] : [];
        _log.Operational.Add(DiagPrintServiceLog.Event(307,
            $"Document 3, {PageName} owned by jdoe on \\\\PC was printed on Send to Odoo through port PORTPROMPT:. Size in bytes: 4000.", _clock.GetUtcNow()));

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Equal("PORTPROMPT:", result.Evidence.SpoolerPort);
    }

    [Fact]
    public async Task Says_the_printing_side_works_when_the_listener_received_it_but_the_upload_fails()
    {
        _scanner.Find = (_, _) => _sent ? Item("outbox", m =>
        {
            m.Attempts = 1;
            m.LastError = "Could not reach https://erp.example: no such host";
        }) : null;

        var result = await RunAsync();

        Assert.False(result.Success);
        Assert.Equal(TestPrintStage.Received, result.Stage);
        Assert.Contains("no such host", result.Summary);
        Assert.Contains("Delivery attempt 1 failed", result.Report);
    }

    [Fact]
    public async Task Reports_a_document_the_ERP_rejected()
    {
        _scanner.Find = (_, _) => _sent ? Item("failed", m => m.LastError = "HTTP 400: bad field") : null;

        var result = await RunAsync();

        Assert.Equal(TestPrintStage.Received, result.Stage);
        Assert.True(result.Evidence.Failed);
        Assert.Contains("HTTP 400: bad field", result.Summary);
    }

    [Fact]
    public async Task Reports_a_missing_printer_without_printing()
    {
        _spooler.Printers.Clear();
        _sender.Error = new InvalidOperationException("must not print");

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.False(result.Success);
        Assert.Equal(TestPrintStage.None, result.Stage);
        Assert.Contains("does not exist", result.Report);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Reports_a_printer_on_the_wrong_port_before_printing()
    {
        _spooler.Printers[0] = DiagData.Printer(port: "PORTPROMPT:");

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Contains("is on port 'PORTPROMPT:'", result.Report);
    }

    [Fact]
    public async Task Reports_when_the_spooler_refuses_the_page()
    {
        _sender.Error = new InvalidOperationException("Printer is not valid");

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Equal(TestPrintStage.None, result.Stage);
        Assert.Contains("The spooler refused the test page: Printer is not valid", result.Report);
    }

    [Fact]
    public async Task Reports_a_page_that_never_appears_in_the_queue()
    {
        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Equal(TestPrintStage.Submitted, result.Stage);
        Assert.True(result.Evidence.TimedOut);
    }

    [Fact]
    public async Task Uses_the_listener_error_when_the_job_left_the_queue_but_never_arrived()
    {
        _spooler.Jobs = _ => _sent && _ticks < 2 ? [DiagData.Job(3, PageName, JobStatusFlags.Printing)] : [];
        _listenerLog.Entries.Add(new LogEntry(_clock.GetUtcNow().AddSeconds(1), ErpLogLevel.Warning, "listener", "Discarded an empty job."));

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Equal(TestPrintStage.Spooled, result.Stage);
        Assert.Contains("Discarded an empty job.", result.Summary);
        Assert.Contains("Listener log: Discarded an empty job.", result.Report);
    }

    [Fact]
    public async Task Notes_a_listener_without_a_connection_line()
    {
        _spooler.Jobs = _ => _sent && _ticks < 2 ? [DiagData.Job(3, PageName, JobStatusFlags.Printing)] : [];

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.False(result.Evidence.ListenerConnected);
        Assert.Contains("The listener log has no entries since the page was printed.", result.Report);
    }

    [Fact]
    public async Task Warns_when_the_PrintService_log_is_disabled()
    {
        _log.OperationalState = PrintServiceLogState.Disabled;

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Contains("The PrintService Operational log is disabled", result.Report);
    }

    [Fact]
    public async Task Keeps_going_when_the_spooler_log_cannot_be_read()
    {
        _log.ReadError = new UnauthorizedAccessException("Attempted to perform an unauthorized operation.");

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Contains("Could not read the spooler log: Attempted to perform an unauthorized operation.", result.Report);
        Assert.Equal(TestPrintStage.Submitted, result.Stage);
    }

    [Fact]
    public async Task Reports_progress_to_the_callback()
    {
        var messages = new List<string>();
        _scanner.Find = (_, _) => _sent ? Item("sent") : null;

        await RunAsync(options: Options(progress: messages.Add));

        Assert.Contains(messages, m => m.StartsWith("Sent '"));
        Assert.Contains(messages, m => m.StartsWith("Delivered to the ERP"));
    }

    [Fact]
    public async Task A_throwing_progress_callback_does_not_end_the_test()
    {
        _scanner.Find = (_, _) => _sent ? Item("sent") : null;

        var result = await RunAsync(options: Options(progress: _ => throw new InvalidOperationException("ui gone")));

        Assert.Equal(TestPrintStage.Delivered, result.Stage);
    }

    [Fact]
    public async Task Notes_when_the_service_is_not_running()
    {
        _system.Services["ErpPrinter"] = new ServiceStatus("ErpPrinter", "Stopped", "Automatic");
        _system.Pipe = false;

        var result = await RunAsync(options: Options(timeout: 5));

        Assert.Contains("[FAIL]", result.Report);
        Assert.Contains("ErpPrinter service (listener and uploader): Stopped.", result.Report);
    }

    [Fact]
    public async Task Rejects_unknown_and_disabled_profiles()
    {
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(profile: "Nope"));
        Assert.Contains("No printer profile named 'Nope'", unknown.Message);

        var disabled = DiagData.Config(_data.Path, DiagData.Profile(enabled: false));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(disabled));
        Assert.Contains("disabled", ex.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(901)]
    public async Task Rejects_timeouts_outside_the_supported_range(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(options: Options(seconds)));
    }

    [Fact]
    public async Task Cancellation_stops_the_watch_loop()
    {
        _spooler.Jobs = _ => _sent ? [DiagData.Job(3, PageName, JobStatusFlags.Printing)] : [];
        using var cts = new CancellationTokenSource();
        var runner = new TestPrintRunner(_spooler, _sender, _log, _listenerLog, _system, _scanner, new DiagSpoolFiles(), _clock,
            (_, _) =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync(DiagData.Config(_data.Path, DiagData.Profile()), DiagData.ProfileName, Options(), cts.Token));
    }

    [Fact]
    public void Formats_the_report_layout()
    {
        var verdict = new TestPrintVerdict(TestPrintStage.InQueue, false, "Summary line.", "Do this.");
        var report = TestPrintRunner.FormatReport(verdict, [new TestPrintStep(0.5, DiagnosticStatus.Pass, "ok"), new TestPrintStep(12.5, DiagnosticStatus.Fail, "bad")],
            "Send to Odoo", new DateTimeOffset(2026, 10, 20, 17, 30, 0, TimeSpan.Zero), "PC1");

        var nl = Environment.NewLine;
        Assert.Equal(
            "ERP Printer test print - profile 'Send to Odoo'" + nl +
            "2026-10-20 17:30:00 on PC1" + nl + nl +
            "Result: FAILED (reached stage: InQueue)" + nl +
            "Summary line." + nl +
            "Next step: Do this." + nl + nl +
            "What happened:" + nl +
            "    0.5s [PASS] ok" + nl +
            "   12.5s [FAIL] bad" + nl,
            report);
    }
}
