using System.Text.Json;
using ErpPrinter.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>The checks of Diagnostics.ps1 over fake spooler, event log, log reader and system facts.</summary>
public class DiagnosticsRunnerTests : IDisposable
{
    private readonly DiagTempDir _data = new();
    private readonly DiagClock _clock = new();
    private readonly DiagSpooler _spooler = new();
    private readonly DiagPrintServiceLog _events = new();
    private readonly DiagLogReader _logs = new();
    private readonly DiagSystem _system = new();
    private Func<PrinterProfile, Task<(bool Ok, string Message)>>? _connection = _ => Task.FromResult((true, "Connected\nOdoo 19.0\nthird line"));
    private Func<string, string, bool>? _hasSecret = (_, _) => true;

    public DiagnosticsRunnerTests()
    {
        _spooler.Printers.Add(DiagData.Printer());
        _spooler.Ports.Add(DiagData.Port);
        var logDir = Path.Combine(_data.Path, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "listener-20261020.log"), "x");
        var morning = _clock.GetUtcNow().AddHours(-9);
        _logs.Entries.Add(new LogEntry(morning, ErpLogLevel.Information, "listener", "Listener started (version 0.4.0, PID 4242)."));
        _logs.Entries.Add(new LogEntry(morning.AddSeconds(1), ErpLogLevel.Information, "listener", @"Waiting for print jobs for \\.\pipe\ErpPrinter-Test."));
        _logs.Entries.Add(new LogEntry(morning.AddHours(1), ErpLogLevel.Information, "listener", "The spooler connected to pipe x."));
        _logs.Entries.Add(new LogEntry(morning.AddHours(1), ErpLogLevel.Information, "listener", "Queued 'Invoice' (1 page)."));
        _logs.Entries.Add(new LogEntry(morning.AddHours(1), ErpLogLevel.Information, "uploader", "Delivered 'Invoice'."));
    }

    public void Dispose() => _data.Dispose();

    private DiagnosticsRunner Runner() => new(_spooler, _events, _system, new DiagSpoolFiles(), _logs, _connection, _hasSecret, _clock);

    private async Task<DiagnosticsReport> RunAsync(DiagnosticsOptions? options = null, ErpConfig? config = null) =>
        await Runner().RunAsync(config ?? DiagData.Config(_data.Path, DiagData.Profile()), options ?? new DiagnosticsOptions(SaveReport: false));

    private static DiagnosticResult Find(DiagnosticsReport report, string name) =>
        report.Results.Single(r => r.Name == name);

    private static string ProfileCategory => $"Profile '{DiagData.ProfileName}'";

    private ProfileFolders Folders => ProfileFolders.For(_data.Path, DiagData.ProfileName);

    private static void WriteSidecar(string folder, OutboxMeta meta)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, meta.Id + ".json"), JsonSerializer.Serialize(meta));
    }

    // --- Whole run ----------------------------------------------------------------------------

    [Fact]
    public async Task A_healthy_machine_has_no_failures_or_warnings()
    {
        var report = await RunAsync();

        Assert.DoesNotContain(report.Results, r => r.Status is DiagnosticStatus.Fail or DiagnosticStatus.Warn);
        Assert.Equal(0, report.Failures);
        Assert.Equal(0, report.Warnings);
        Assert.Equal("Empty.", Find(report, "Print queue").Detail);
        Assert.Contains("1 connection(s), 1 job(s) queued, 1 delivered", Find(report, "Spooler connections").Detail);
        Assert.Equal("Connected Odoo 19.0", Find(report, "ERP connection").Detail);
        Assert.Contains("Version ", Find(report, "ERP Printer").Detail);
        Assert.Contains("0 fail, 0 warn", report.Report);
        Assert.Null(report.ReportPath);
    }

    [Fact]
    public async Task Saves_the_report_in_the_log_folder()
    {
        var report = await RunAsync(new DiagnosticsOptions());

        Assert.NotNull(report.ReportPath);
        Assert.Equal(Path.Combine(_data.Path, "logs"), Path.GetDirectoryName(report.ReportPath));
        Assert.Equal(report.Report, await File.ReadAllTextAsync(report.ReportPath!));
    }

    [Fact]
    public async Task Rejects_an_unknown_profile_name()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new DiagnosticsOptions("Nope", SaveReport: false)));
        Assert.Contains("No printer profile named 'Nope'", ex.Message);
    }

    [Fact]
    public async Task Limits_the_per_printer_checks_to_the_requested_profile()
    {
        var other = DiagData.Profile(name: "Other");
        var report = await Runner().RunAsync(DiagData.Config(_data.Path, DiagData.Profile(), other), new DiagnosticsOptions(DiagData.ProfileName, SaveReport: false));

        Assert.DoesNotContain(report.Results, r => r.Category == "Profile 'Other'");
        Assert.Contains(report.Results, r => r.Category == ProfileCategory);
    }

    [Fact]
    public async Task Fails_when_no_profiles_are_configured()
    {
        var report = await RunAsync(config: DiagData.Config(_data.Path));

        var r = Find(report, "Printer profiles");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Equal("No printer profiles are configured.", r.Detail);
    }

    [Fact]
    public async Task A_check_that_throws_becomes_a_warning_and_the_others_still_run()
    {
        _spooler.PrintersError = new SpoolerException("OpenPrinter failed (5)");

        var report = await RunAsync();

        var broken = Find(report, "Printer checks");
        Assert.Equal(DiagnosticStatus.Warn, broken.Status);
        Assert.Contains("OpenPrinter failed (5)", broken.Detail);
        Assert.Equal(DiagnosticStatus.Pass, Find(report, "Print Spooler service").Status);
        Assert.Equal(DiagnosticStatus.Pass, Find(report, "ERP connection").Status);
    }

    // --- Regression: the queue holds a job ---------------------------------------------------

    [Fact]
    public async Task Analyses_a_failed_job_in_the_queue_instead_of_throwing()
    {
        // Regression from the PowerShell version: -or after a command call made the profile check throw when the queue held a job.
        var now = _clock.GetUtcNow();
        _spooler.Jobs = _ => [DiagData.Job(4, "Edge page", JobStatusFlags.Error | JobStatusFlags.Printing, now)];
        _spooler.Printers[0] = DiagData.Printer(status: PrinterStatusFlags.Error);
        _events.Operational.Add(DiagPrintServiceLog.Event(800, "Spooling job 4.", now));
        _events.Operational.Add(DiagPrintServiceLog.Event(801, "Printing job 4.", now.AddSeconds(2)));

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Print queue").Status);
        var job = Find(report, "Job 4 analysis");
        Assert.Equal(DiagnosticStatus.Fail, job.Status);
        Assert.Contains("job 4 'Edge page'", job.Detail);
        Assert.Contains("17:27:00 spooled -> 17:27:02 printing started", job.Detail);
        Assert.Contains("Microsoft Print to PDF", job.Hint);
        Assert.DoesNotContain(report.Results, r => r.Name == "Printer checks");
        Assert.Contains($"Start with: {ProfileCategory} / Job 4 analysis", report.Report);
    }

    [Fact]
    public async Task A_fresh_healthy_job_is_only_information()
    {
        var now = _clock.GetUtcNow();
        _spooler.Jobs = _ => [DiagData.Job(5, "Invoice", JobStatusFlags.Spooling, now.AddSeconds(-20))];

        var report = await RunAsync();

        var queue = Find(report, "Print queue");
        Assert.Equal(DiagnosticStatus.Info, queue.Status);
        Assert.StartsWith("1 job(s) in progress:", queue.Detail);
        Assert.DoesNotContain(report.Results, r => r.Name == "Job 5 analysis");
    }

    [Fact]
    public async Task A_job_older_than_two_minutes_counts_as_stuck_even_with_a_normal_status()
    {
        var now = _clock.GetUtcNow();
        _spooler.Jobs = _ => [DiagData.Job(6, "Invoice", JobStatusFlags.None, now.AddMinutes(-3))];

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Print queue").Status);
        // The spooler logged nothing for this job, so the analysis can only say so.
        Assert.Equal(DiagnosticStatus.Info, Find(report, "Job 6 analysis").Status);
    }

    [Fact]
    public async Task Analyses_at_most_three_stuck_jobs()
    {
        var now = _clock.GetUtcNow();
        _spooler.Jobs = _ => Enumerable.Range(1, 5).Select(i => DiagData.Job(i, "Doc " + i, JobStatusFlags.Error, now)).ToList();

        var report = await RunAsync();

        Assert.Equal(3, report.Results.Count(r => r.Name.EndsWith(" analysis", StringComparison.Ordinal)));
    }

    // --- System and storage -------------------------------------------------------------------

    [Fact]
    public async Task Warns_when_the_session_is_not_elevated()
    {
        _system.IsElevated = false;

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Warn, Find(report, "Elevation").Status);
    }

    [Fact]
    public async Task Fails_when_the_data_folder_does_not_exist_and_skips_the_other_storage_checks()
    {
        var missing = Path.Combine(_data.Path, "nowhere");

        var report = await RunAsync(config: DiagData.Config(missing, DiagData.Profile()));

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Data folder").Status);
        Assert.DoesNotContain(report.Results, r => r.Name is "Free disk space" or "Log level" or "Log files");
    }

    [Theory]
    [InlineData(true, DiagnosticStatus.Fail)]
    [InlineData(false, DiagnosticStatus.Warn)]
    public async Task A_data_folder_that_is_not_writable_fails_only_when_elevated(bool elevated, DiagnosticStatus expected)
    {
        _system.IsElevated = elevated;
        _system.Probe = new FolderWriteProbe(false, "Access denied");

        var report = await RunAsync();

        var r = Find(report, "Data folder");
        Assert.Equal(expected, r.Status);
        Assert.Contains("Access denied", r.Detail);
    }

    [Theory]
    [InlineData(100, DiagnosticStatus.Fail)]
    [InlineData(500, DiagnosticStatus.Warn)]
    [InlineData(5000, DiagnosticStatus.Pass)]
    public async Task Grades_free_disk_space(long megabytes, DiagnosticStatus expected)
    {
        _system.Disk = new DiskSpace(@"D:\", megabytes * 1024 * 1024);

        var report = await RunAsync();

        var r = Find(report, "Free disk space");
        Assert.Equal(expected, r.Status);
        Assert.Equal($"{megabytes} MB free on D:\\", r.Detail);
        Assert.Equal(expected == DiagnosticStatus.Pass, r.Hint.Length == 0);
    }

    [Fact]
    public async Task Reports_disk_space_that_cannot_be_read_as_information()
    {
        _system.DiskError = new IOException("drive not ready");

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Info, Find(report, "Free disk space").Status);
        Assert.Contains("drive not ready", Find(report, "Free disk space").Detail);
    }

    [Fact]
    public async Task Warns_when_the_log_level_hides_the_queued_and_delivered_messages()
    {
        var config = DiagData.Config(_data.Path, v => v["LogLevel"] = "Warning", DiagData.Profile());

        var report = await RunAsync(config: config);

        Assert.Equal(DiagnosticStatus.Warn, Find(report, "Log level").Status);
    }

    [Fact]
    public async Task Warns_when_there_are_no_log_files()
    {
        File.Delete(Path.Combine(_data.Path, "logs", "listener-20261020.log"));

        var report = await RunAsync();

        var r = Find(report, "Log files");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("No log files", r.Detail);
    }

    // --- Background service -------------------------------------------------------------------

    [Fact]
    public async Task Fails_when_the_service_is_not_installed()
    {
        _system.Services.Remove("ErpPrinter");
        _system.Processes = [];

        var report = await RunAsync();

        var r = Find(report, "ErpPrinter service");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Contains("not installed", r.Detail);
        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Service process").Status);
    }

    [Fact]
    public async Task Fails_when_the_service_is_stopped()
    {
        _system.Services["ErpPrinter"] = new ServiceStatus("ErpPrinter", "Stopped", "Automatic");
        _system.Processes = [];

        var report = await RunAsync();

        var r = Find(report, "ErpPrinter service");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Contains("It should always be running", r.Detail);
        Assert.Contains("Start-Service ErpPrinter", r.Hint);
    }

    [Fact]
    public async Task Fails_when_the_service_is_disabled()
    {
        _system.Services["ErpPrinter"] = new ServiceStatus("ErpPrinter", "Stopped", "Disabled");

        var report = await RunAsync();

        Assert.Contains("start= auto", Find(report, "ErpPrinter service").Hint);
    }

    [Fact]
    public async Task Warns_when_the_service_will_not_survive_a_reboot()
    {
        _system.Services["ErpPrinter"] = new ServiceStatus("ErpPrinter", "Running", "Manual");

        var report = await RunAsync();

        var r = Find(report, "ErpPrinter service");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("after a reboot", r.Detail);
    }

    [Fact]
    public async Task Warns_about_a_running_service_without_a_process_and_about_duplicates()
    {
        _system.Processes = [];
        var none = await RunAsync();
        Assert.Equal(DiagnosticStatus.Warn, Find(none, "Service process").Status);

        _system.Processes =
        [
            new ErpProcessInfo(10, "ErpPrinter.Service.exe", null, null),
            new ErpProcessInfo(11, "ErpPrinter.Service.exe", null, null),
        ];
        var two = await RunAsync();
        var r = Find(two, "Service process");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("2 processes", r.Detail);
        Assert.Contains("PIDs 10, 11", r.Detail);
    }

    [Fact]
    public async Task Says_when_process_command_lines_cannot_be_read()
    {
        _system.Processes = null;

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Info, Find(report, "Service process").Status);
    }

    [Fact]
    public async Task Flags_a_lingering_legacy_scheduled_task_installation()
    {
        _system.LegacyTasks.Add(new LegacyTaskInfo("Listener", "Ready", 267011));
        _system.LegacyTasks.Add(new LegacyTaskInfo("Uploader", "Disabled", 0, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)));

        var report = await RunAsync();

        var r = Find(report, "Legacy scheduled tasks");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("Listener: state Ready; last run never; last result 0x00041303 (has not run yet)", r.Detail);
        Assert.Contains("Uploader: state Disabled; last run 2026-10-01 08:00:00; last result 0x00000000 (completed successfully)", r.Detail);
        Assert.Contains("schtasks /Delete", r.Hint);
    }

    [Fact]
    public async Task A_running_legacy_task_or_PowerShell_listener_is_a_failure()
    {
        _system.LegacyTasks.Add(new LegacyTaskInfo("Listener", "Running", 267009));
        var task = await RunAsync();
        Assert.Equal(DiagnosticStatus.Fail, Find(task, "Legacy scheduled tasks").Status);

        _system.LegacyTasks.Clear();
        _system.Processes =
        [
            new ErpProcessInfo(4242, "ErpPrinter.Service.exe", null, null),
            new ErpProcessInfo(99, "powershell.exe", @"powershell.exe -File ""C:\Program Files\ERP Printer\ErpPrinter.ps1"" -Command Listen", null),
        ];
        var process = await RunAsync();
        var r = Find(process, "Legacy scheduled tasks");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Contains("PowerShell process PID 99 runs ErpPrinter.ps1 -Command Listen", r.Detail);
        Assert.Equal(DiagnosticStatus.Pass, Find(process, "Service process").Status);
    }

    // --- Service history ----------------------------------------------------------------------

    [Fact]
    public async Task Warns_when_the_listener_never_started_and_says_why_when_the_log_level_hides_it()
    {
        _logs.Entries.Clear();
        var config = DiagData.Config(_data.Path, v => v["LogLevel"] = "Error", DiagData.Profile());

        var report = await RunAsync(config: config);

        var r = Find(report, "Listener start-up");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("LogLevel hides Information messages.", r.Detail);
        Assert.StartsWith("The spooler has not connected to a pipe in the last 24 hours.", Find(report, "Spooler connections").Detail);
    }

    [Fact]
    public async Task Fails_when_the_listener_started_but_never_opened_a_pipe()
    {
        _logs.Entries.RemoveAll(e => e.Message.StartsWith("Waiting", StringComparison.Ordinal));

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Listener pipes").Status);
    }

    [Fact]
    public async Task Warns_when_the_listener_restarted_five_times_in_a_day()
    {
        for (var i = 1; i <= 4; i++)
            _logs.Entries.Add(new LogEntry(_clock.GetUtcNow().AddHours(-4).AddMinutes(i), ErpLogLevel.Information, "listener", "Listener started (version 0.4.0)."));

        var report = await RunAsync();

        var r = Find(report, "Listener restarts");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("started 5 times in 24 hours", r.Detail);
    }

    [Fact]
    public async Task Summarises_logged_problems_and_fails_when_one_is_an_error()
    {
        _logs.Entries.Add(new LogEntry(_clock.GetUtcNow().AddHours(-2), ErpLogLevel.Warning, "uploader", "Retry later."));
        var warnings = await RunAsync();
        Assert.Equal(DiagnosticStatus.Warn, Find(warnings, "Logged problems").Status);

        _logs.Entries.Add(new LogEntry(_clock.GetUtcNow().AddHours(-1), ErpLogLevel.Error, "listener", "The listener crashed: " + new string('x', 400)));
        var errors = await RunAsync();
        var r = Find(errors, "Logged problems");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.StartsWith("2 warning/error line(s); the latest:", r.Detail);
        Assert.Contains("16:27:00 Error [listener] The listener crashed:", r.Detail);
        Assert.Contains("...", r.Detail);
    }

    // --- Windows printing ---------------------------------------------------------------------

    [Fact]
    public async Task Fails_when_the_Print_Spooler_service_is_stopped_or_missing()
    {
        _system.Services["Spooler"] = new ServiceStatus("Spooler", "Stopped", "Manual");
        var stopped = Find(await RunAsync(), "Print Spooler service");
        Assert.Equal(DiagnosticStatus.Fail, stopped.Status);
        Assert.Equal("Status Stopped, start type Manual.", stopped.Detail);

        _system.Services.Remove("Spooler");
        Assert.Contains("does not exist", Find(await RunAsync(), "Print Spooler service").Detail);
    }

    [Fact]
    public async Task Fails_when_the_PDF_driver_is_missing()
    {
        _spooler.Drivers.Clear();

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "PDF driver").Status);
    }

    [Theory]
    [InlineData(1, DiagnosticStatus.Fail)]
    [InlineData(0, DiagnosticStatus.Pass)]
    public async Task Detects_Windows_protected_print_mode(int mode, DiagnosticStatus expected)
    {
        _system.ProtectedPrintMode = mode;

        Assert.Equal(expected, Find(await RunAsync(), "Windows protected print mode").Status);
    }

    [Theory]
    [InlineData(PrintServiceLogState.NotPresent, DiagnosticStatus.Info)]
    [InlineData(PrintServiceLogState.Disabled, DiagnosticStatus.Warn)]
    [InlineData(PrintServiceLogState.Enabled, DiagnosticStatus.Pass)]
    public async Task Reports_the_state_of_the_PrintService_Operational_log(PrintServiceLogState state, DiagnosticStatus expected)
    {
        _events.OperationalState = state;

        Assert.Equal(expected, Find(await RunAsync(), "PrintService Operational log").Status);
    }

    [Fact]
    public async Task Lists_spooler_warnings_of_the_last_day()
    {
        _events.Admin.Add(DiagPrintServiceLog.Event(372, "The document failed to print on port \\\\.\\pipe\\x.", _clock.GetUtcNow().AddHours(-1), level: 2));
        _events.Admin.Add(DiagPrintServiceLog.Event(999, "Old", _clock.GetUtcNow().AddDays(-2), level: 2));

        var report = await RunAsync();

        var admin = Find(report, "Spooler Admin log");
        Assert.Equal(DiagnosticStatus.Warn, admin.Status);
        Assert.StartsWith("1 warning/error event(s) in the last 24 hours:", admin.Detail);
        Assert.Contains("[Error] id 372", admin.Detail);
        Assert.Equal(DiagnosticStatus.Pass, Find(report, "Spooler Operational log").Status);
    }

    // --- Profile ------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_profile_is_reported_and_not_checked_further()
    {
        var report = await RunAsync(config: DiagData.Config(_data.Path, DiagData.Profile(enabled: false)));

        Assert.Equal(DiagnosticStatus.Warn, Find(report, "Enabled").Status);
        Assert.DoesNotContain(report.Results, r => r.Name is "Windows printer" or "Pipe");
    }

    [Fact]
    public async Task Fails_when_the_printer_does_not_exist_but_still_checks_the_pipe()
    {
        _spooler.Printers.Clear();

        var report = await RunAsync();

        Assert.Equal(DiagnosticStatus.Fail, Find(report, "Windows printer").Status);
        Assert.Contains("No printer named 'Send to Odoo' exists.", Find(report, "Windows printer").Detail);
        Assert.Equal(DiagnosticStatus.Pass, Find(report, "Pipe").Status);
        Assert.DoesNotContain(report.Results, r => r.Name is "Printer state" or "Print queue");
    }

    [Fact]
    public async Task Fails_a_printer_on_the_wrong_port_and_driver()
    {
        _spooler.Printers[0] = DiagData.Printer(port: "PORTPROMPT:", driver: "Generic / Text Only");

        var report = await RunAsync();

        var r = Find(report, "Windows printer");
        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Contains("it is on port 'PORTPROMPT:' instead of '" + DiagData.Port + "', so jobs never reach the listener", r.Detail);
        Assert.Contains("it uses driver 'Generic / Text Only' instead of 'Microsoft Print To PDF', so the output is not a PDF", r.Detail);
    }

    [Fact]
    public async Task Fails_when_the_port_does_not_exist()
    {
        _spooler.Ports.Clear();

        Assert.Equal(DiagnosticStatus.Fail, Find(await RunAsync(), "Printer port").Status);
    }

    [Fact]
    public async Task Fails_a_paused_printer_with_the_state_hint()
    {
        _spooler.Printers[0] = DiagData.Printer(status: PrinterStatusFlags.Paused);

        var r = Find(await RunAsync(), "Printer state");

        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Equal("The spooler reports the printer as Paused.", r.Detail);
    }

    [Theory]
    [InlineData(false, DiagnosticStatus.Fail)]
    [InlineData(null, DiagnosticStatus.Info)]
    public async Task Reports_a_pipe_that_nothing_hosts_or_that_cannot_be_listed(bool? exists, DiagnosticStatus expected)
    {
        _system.Pipe = exists;

        Assert.Equal(expected, Find(await RunAsync(), "Pipe").Status);
    }

    // --- Documents ----------------------------------------------------------------------------

    [Fact]
    public async Task Counts_documents_and_reads_the_delivery_statistics()
    {
        WriteSidecar(Folders.Outbox, new OutboxMeta { Id = "a", Title = "A", Printed = _clock.GetUtcNow().AddMinutes(-1) });
        WriteSidecar(Folders.Failed, new OutboxMeta { Id = "b", Title = "B", LastError = "HTTP 400", LastAttempt = _clock.GetUtcNow() });
        await File.WriteAllTextAsync(Folders.Stats, """{"count":3,"lastSentAt":"2026-10-20T17:00:00+00:00","lastTitle":"x","lastRemoteId":"9"}""");

        var report = await RunAsync();

        Assert.Equal("1 waiting, 1 failed, 3 delivered (last: 2026-10-20 17:00:00).", Find(report, "Documents").Detail);
        var error = Find(report, "Delivery error");
        Assert.Equal(DiagnosticStatus.Fail, error.Status);
        Assert.Equal("'B': HTTP 400", error.Detail);
    }

    [Fact]
    public async Task Reports_the_latest_delivery_error()
    {
        var now = _clock.GetUtcNow();
        WriteSidecar(Folders.Outbox, new OutboxMeta { Id = "a", Title = "Old", LastError = "old failure", LastAttempt = now.AddMinutes(-30) });
        WriteSidecar(Folders.Outbox, new OutboxMeta { Id = "b", Title = "New", LastError = "new failure", LastAttempt = now.AddMinutes(-1) });

        var report = await RunAsync();

        Assert.Equal("'New': new failure", Find(report, "Delivery error").Detail);
    }

    [Fact]
    public async Task Warns_about_an_old_waiting_document_without_an_error()
    {
        WriteSidecar(Folders.Outbox, new OutboxMeta { Id = "a", Title = "A", Printed = _clock.GetUtcNow().AddMinutes(-10) });

        var report = await RunAsync();

        var r = Find(report, "Delivery");
        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("printed 17:17:00 and has no error recorded", r.Detail);
    }

    [Fact]
    public async Task A_recent_waiting_document_is_fine()
    {
        WriteSidecar(Folders.Outbox, new OutboxMeta { Id = "a", Title = "A", Printed = _clock.GetUtcNow().AddMinutes(-1) });

        var report = await RunAsync();

        Assert.DoesNotContain(report.Results, r => r.Name == "Delivery");
        Assert.StartsWith("1 waiting, 0 failed, 0 delivered (last: never)", Find(report, "Documents").Detail);
    }

    [Fact]
    public async Task Skips_unreadable_sidecars()
    {
        Directory.CreateDirectory(Folders.Outbox);
        await File.WriteAllTextAsync(Path.Combine(Folders.Outbox, "half.json"), "{ \"id\": ");

        var report = await RunAsync();

        Assert.StartsWith("0 waiting", Find(report, "Documents").Detail);
    }

    // --- Connection ---------------------------------------------------------------------------

    [Fact]
    public async Task Warns_about_a_per_user_proxy_when_ProxyUrl_is_empty()
    {
        _system.UserProxy = "proxy.corp:8080";

        var r = Find(await RunAsync(), "Proxy");

        Assert.Equal(DiagnosticStatus.Warn, r.Status);
        Assert.Contains("proxy 'proxy.corp:8080'", r.Detail);
    }

    [Fact]
    public async Task Does_not_warn_when_ProxyUrl_is_set()
    {
        _system.UserProxy = "proxy.corp:8080";
        var config = DiagData.Config(_data.Path, v => v["ProxyUrl"] = "http://proxy.corp:8080", DiagData.Profile());

        var r = Find(await RunAsync(config: config), "Proxy");

        Assert.Equal(DiagnosticStatus.Info, r.Status);
        Assert.Equal("ProxyUrl is http://proxy.corp:8080.", r.Detail);
    }

    [Fact]
    public async Task Fails_a_connection_that_does_not_work()
    {
        _connection = _ => Task.FromResult((false, "HTTP 401 Unauthorized"));

        var r = Find(await RunAsync(), "ERP connection");

        Assert.Equal(DiagnosticStatus.Fail, r.Status);
        Assert.Equal("HTTP 401 Unauthorized", r.Detail);
    }

    [Fact]
    public async Task A_throwing_connection_test_becomes_a_warning()
    {
        _connection = _ => throw new HttpRequestException("no route");

        var report = await RunAsync();

        Assert.Contains("no route", Find(report, "Connection checks").Detail);
    }

    [Fact]
    public async Task Reports_missing_settings_instead_of_testing_the_connection()
    {
        _hasSecret = (_, _) => false;

        var report = await RunAsync();

        var settings = Find(report, "Settings");
        Assert.Equal(DiagnosticStatus.Fail, settings.Status);
        Assert.Contains("An API key is required.", settings.Detail);
        Assert.DoesNotContain(report.Results, r => r.Name == "ERP connection");
    }

    [Fact]
    public async Task Skips_the_connection_checks_on_request()
    {
        var report = await RunAsync(new DiagnosticsOptions(SkipConnection: true, SaveReport: false));

        Assert.DoesNotContain(report.Results, r => r.Name is "ERP connection" or "Proxy");
    }
}
