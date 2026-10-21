using System.Text;
using System.Text.RegularExpressions;
using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Diagnostics;

/// <summary>What a test page is called and where it goes.</summary>
public sealed record TestPageRequest(string PrinterName, string ProfileName, string Token, string DocumentName);

/// <summary>Prints a one-page document through the Windows printing API, as any application would (GDI, like the PowerShell version).</summary>
public interface ITestPageSender
{
    /// <summary>Sends the page and returns the document name it used. Throws when the printer cannot be opened.</summary>
    Task<string> SendAsync(TestPageRequest request, CancellationToken cancellationToken = default);
}

public sealed record TestPrintOptions(int TimeoutSeconds = 60)
{
    /// <summary>How long a job may sit in an error state before the test gives up waiting.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Called with a one-line status message as things happen.</summary>
    public Action<string>? OnProgress { get; init; }

    /// <summary>Makes the 8-hex-digit token in the page title; tests replace it.</summary>
    public Func<string>? TokenFactory { get; init; }
}

public sealed record TestPrintStep(double Seconds, DiagnosticStatus Status, string Message);

public sealed record TestPrintResult(
    bool Success,
    TestPrintStage Stage,
    string Summary,
    string Hint,
    string Token,
    IReadOnlyList<TestPrintStep> Steps,
    TestPrintEvidence Evidence,
    string Report);

/// <summary>
/// Prints a test page through the real printer queue and reports where it stops. Sending a page to the spooler only proves the
/// spooler accepted it, so this keeps watching: the job in the print queue, the spooler's own log, the listener's log, the outbox
/// and finally delivery to the ERP. Port of Invoke-ErpPrinterTestPrint.
/// </summary>
public sealed partial class TestPrintRunner(
    ISpooler spooler,
    ITestPageSender sender,
    IPrintServiceLog printServiceLog,
    ILogReader logs,
    ISystemInspector system,
    IOutboxScanner outbox,
    ISpoolFileInspector spoolFiles,
    TimeProvider? time = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    [GeneratedRegex(@"(?:via|through) port (?<port>.+?)\.\s")]
    private static partial Regex PortInEvent();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, time ?? TimeProvider.System, ct));
    private readonly JobAnalyzer _analyzer = new(printServiceLog, spoolFiles, time);

    /// <exception cref="InvalidOperationException">The profile does not exist or is disabled.</exception>
    public async Task<TestPrintResult> RunAsync(ErpConfig config, string profileName, TestPrintOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TestPrintOptions();
        if (options.TimeoutSeconds is < 5 or > 900)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeoutSeconds must be between 5 and 900.");
        var profile = config.Profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"No printer profile named '{profileName}'.");
        if (!profile.Enabled) throw new InvalidOperationException($"Profile '{profileName}' is disabled; enable it first.");

        var global = config.Global;
        var start = _time.GetLocalNow();
        var steps = new List<TestPrintStep>();

        void Progress(string message)
        {
            if (options.OnProgress is null) return;
            try { options.OnProgress(message); }
            catch (Exception) { /* a broken callback must not end the test */ }
        }

        void Note(DiagnosticStatus status, string message)
        {
            steps.Add(new TestPrintStep(Math.Round((_time.GetLocalNow() - start).TotalSeconds, 1), status, message));
            Progress(message);
        }

        var printerName = profile.PrinterName;
        var expectedPort = profile.PortName;
        var folders = ProfileFolders.For(global.DataRoot, profile.Name);
        var token = options.TokenFactory?.Invoke() ?? Guid.NewGuid().ToString("N")[..8];
        var documentName = $"ERP Printer test page [{token}]";
        var evidence = new TestPrintEvidence { ExpectedPort = expectedPort };

        // --- Before printing: what should already be true -------------------------------------
        var printer = FindPrinter(printerName);
        if (printer is null)
            Note(DiagnosticStatus.Fail, $"Windows printer '{printerName}' does not exist.");
        else if (!string.Equals(printer.PortName, expectedPort, StringComparison.OrdinalIgnoreCase))
            Note(DiagnosticStatus.Fail, $"Printer '{printerName}' is on port '{printer.PortName}', not '{expectedPort}'. The page will not reach the listener.");
        else
            Note(DiagnosticStatus.Pass, $"Printer '{printerName}' is on port '{expectedPort}' with driver '{printer.DriverName}'.");

        var spoolerService = system.GetService("Spooler");
        if (spoolerService is null || !spoolerService.IsRunning)
            Note(DiagnosticStatus.Fail, $"Print Spooler service: {spoolerService?.State ?? "missing"}.");

        if (printer is not null)
        {
            var state = PrinterStateAnalyzer.Analyze(printer, _time.GetLocalNow().TimeOfDay);
            if (state.Problem is not null)
            {
                evidence.PrinterProblem = state.Problem;
                evidence.PrinterHint = state.Hint;
                Note(DiagnosticStatus.Fail, state.Problem);
            }
            else
            {
                Note(DiagnosticStatus.Pass, $"Printer state: {state.Summary}.");
            }

            // The spooler works through a queue one job at a time, so an older stuck job blocks the new page.
            var ahead = GetJobs(printerName);
            evidence.JobsAhead = ahead.Count;
            if (ahead.Count > 0)
            {
                evidence.JobsAheadText = PrintJobText.FormatList(ahead);
                Note(DiagnosticStatus.Warn, $"The print queue already holds {ahead.Count} job(s): {evidence.JobsAheadText}.");
            }
            else
            {
                Note(DiagnosticStatus.Pass, "The print queue is empty.");
            }
        }

        var service = system.GetService(DiagnosticsRunner.ServiceName);
        Note(service is { IsRunning: true } ? DiagnosticStatus.Pass : DiagnosticStatus.Fail, $"ErpPrinter service (listener and uploader): {service?.State ?? "Not installed"}.");
        evidence.PipeExists = system.PipeExists(profile.PipeName);
        if (evidence.PipeExists == true) Note(DiagnosticStatus.Pass, $"The listener is hosting {expectedPort}.");
        else if (evidence.PipeExists == false) Note(DiagnosticStatus.Fail, $"Nothing is hosting {expectedPort}; the spooler has nowhere to send the page.");

        evidence.DataFolderReadable = FolderAccess.IsReadable(global.DataRoot);
        if (!evidence.DataFolderReadable)
            Note(DiagnosticStatus.Warn, $"The data folder '{global.DataRoot}' cannot be read from this session, so the outbox cannot be watched. Run elevated.");
        // The spooler opens the port as the printing user; an administrator's rights hide problems ordinary users would hit.
        if (system.IsElevated)
            Note(DiagnosticStatus.Info, "This page is printed as an administrator. Users' prints go through the same pipe with their own rights; to check those, print from an unelevated session.");
        var known = await outbox.GetKnownIdsAsync(folders, cancellationToken).ConfigureAwait(false);

        // --- Print ----------------------------------------------------------------------------
        if (printer is not null)
        {
            try
            {
                var sent = await sender.SendAsync(new TestPageRequest(printerName, profile.Name, token, documentName), cancellationToken).ConfigureAwait(false);
                evidence.Submitted = true;
                Note(DiagnosticStatus.Pass, $"Sent '{sent}' to the spooler for '{printerName}'.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Note(DiagnosticStatus.Fail, $"The spooler refused the test page: {ex.Message}");
            }
        }

        // --- Watch it travel ------------------------------------------------------------------
        var deadline = start.AddSeconds(options.TimeoutSeconds);
        var seenJob = false;
        var jobGone = false;
        var lastStatus = "";
        var everPrinting = false;
        PrintJobInfo? lastJob = null;
        DateTimeOffset? badSince = null;
        string? receivedId = null;
        var lastProgress = _time.GetLocalNow();

        while (evidence.Submitted && _time.GetLocalNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = GetJobs(printerName).FirstOrDefault(j => j.DocumentName.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (job is not null)
            {
                var status = job.StatusText;
                if (!seenJob)
                {
                    seenJob = true;
                    Note(DiagnosticStatus.Info, $"Job {job.Id} is in the print queue (status '{status}', {job.SizeBytes} bytes, submitted {PrintJobText.Submitted(job)}).");
                }
                else if (status != lastStatus)
                {
                    Note(DiagnosticStatus.Info, $"Job {job.Id} status changed from '{lastStatus}' to '{status}'.");
                }
                lastStatus = status;
                lastJob = job;
                if (status.Contains("Printing", StringComparison.Ordinal)) everPrinting = true;
                if (PrintJobText.IsBadStatus(status)) badSince ??= _time.GetLocalNow();
                else badSince = null;
            }
            else if (seenJob && !jobGone)
            {
                jobGone = true;
                Note(DiagnosticStatus.Info, "The job left the print queue: the spooler is done with it.");
            }

            var found = await outbox.FindTestPageAsync(folders, token, known, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                if (receivedId is null)
                {
                    receivedId = found.Id;
                    evidence.Received = true;
                    Note(DiagnosticStatus.Pass, $"The listener received it and queued document {found.Id} ({found.Folder}).");
                }
                if (found.Folder == "sent")
                {
                    evidence.Delivered = true;
                    Note(DiagnosticStatus.Pass, $"Delivered to the ERP (remote id {found.Meta.RemoteId}).");
                    break;
                }
                if (found.Folder == "failed")
                {
                    evidence.Failed = true;
                    evidence.FailedError = found.Meta.LastError ?? "";
                    Note(DiagnosticStatus.Fail, $"The ERP rejected it: {found.Meta.LastError}");
                    break;
                }
                if (!string.IsNullOrEmpty(found.Meta.LastError))
                {
                    evidence.LastDeliveryError = found.Meta.LastError;
                    Note(DiagnosticStatus.Fail, $"Delivery attempt {found.Meta.Attempts} failed: {found.Meta.LastError}");
                    break;
                }
            }
            else if (receivedId is not null)
            {
                // Gone from every folder: delivered, and KeepSentDays is 0 so nothing was archived.
                evidence.Delivered = true;
                Note(DiagnosticStatus.Pass, "Delivered to the ERP (the document was removed from the outbox).");
                break;
            }

            if (badSince is { } since && _time.GetLocalNow() - since >= options.StuckAfter)
            {
                Note(DiagnosticStatus.Fail, $"Job stayed in state '{lastStatus}' for {options.StuckAfter.TotalSeconds:0.#} seconds; not waiting any longer.");
                break;
            }

            if ((_time.GetLocalNow() - lastProgress).TotalSeconds >= 3)
            {
                lastProgress = _time.GetLocalNow();
                Progress($"Waiting for the page to arrive... {(_time.GetLocalNow() - start).TotalSeconds:N0}s");
            }
            await _delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // --- Collect what the spooler and the listener say about it --------------------------
        if (evidence.Submitted)
        {
            evidence.JobSeen = seenJob;
            evidence.JobGone = jobGone;
            evidence.LastJobStatus = lastStatus;
            evidence.JobStuck = seenJob && !jobGone && !evidence.Received && !evidence.Delivered;
            evidence.TimedOut = !(evidence.Delivered || evidence.Failed || !string.IsNullOrEmpty(evidence.LastDeliveryError));
            evidence.JobEverPrinting = everPrinting;

            if (evidence.JobStuck)
            {
                if (lastJob is not null)
                {
                    try
                    {
                        var analysis = await _analyzer.AnalyzeAsync(lastJob, cancellationToken).ConfigureAwait(false);
                        Note(analysis.Failed ? DiagnosticStatus.Fail : DiagnosticStatus.Info, "Spooler analysis: " + LineBreak().Replace(analysis.Detail, " | "));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Note(DiagnosticStatus.Warn, $"Could not analyse the job: {ex.Message}");
                    }
                }
                var queueNow = GetJobs(printerName);
                Note(DiagnosticStatus.Info, $"Print queue at the end: {(queueNow.Count > 0 ? PrintJobText.FormatList(queueNow) : "empty")}.");
                if (printer is not null && FindPrinter(printerName) is { } printerNow)
                {
                    var stateNow = PrinterStateAnalyzer.Analyze(printerNow, _time.GetLocalNow().TimeOfDay);
                    Note(stateNow.Problem is not null ? DiagnosticStatus.Fail : DiagnosticStatus.Info,
                        $"Printer state at the end: {stateNow.Problem ?? stateNow.Summary}.");
                    if (stateNow.Problem is not null && string.IsNullOrEmpty(evidence.PrinterProblem))
                    {
                        evidence.PrinterProblem = stateNow.Problem;
                        evidence.PrinterHint = stateNow.Hint;
                    }
                }
            }

            await CollectSpoolerLogsAsync(evidence, start, token, printerName, expectedPort, Note, cancellationToken).ConfigureAwait(false);
            await CollectListenerLogAsync(evidence, start, Note, cancellationToken).ConfigureAwait(false);

            if (evidence.Received && !evidence.Delivered)
            {
                var uploader = system.GetService(DiagnosticsRunner.ServiceName);
                Note(uploader is { IsRunning: true } ? DiagnosticStatus.Info : DiagnosticStatus.Fail, $"ErpPrinter service: {uploader?.State ?? "Not installed"}.");
            }
        }

        var verdict = TestPrintVerdict.Evaluate(evidence);
        var report = FormatReport(verdict, steps, profile.Name, _time.GetLocalNow(), system.ComputerName);
        return new TestPrintResult(verdict.Success, verdict.Stage, verdict.Summary, verdict.Hint, token, steps, evidence, report);
    }

    private async Task CollectSpoolerLogsAsync(
        TestPrintEvidence evidence, DateTimeOffset start, string token, string printerName, string expectedPort,
        Action<DiagnosticStatus, string> note, CancellationToken cancellationToken)
    {
        try
        {
            var state = await printServiceLog.GetStateAsync(PrintServiceLogKind.Operational, cancellationToken).ConfigureAwait(false);
            if (state == PrintServiceLogState.Disabled)
                note(DiagnosticStatus.Warn, "The PrintService Operational log is disabled, so the spooler cannot report what it did with the job (wevtutil sl Microsoft-Windows-PrintService/Operational /e:true).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown state: the event reads below report their own failure.
        }

        try
        {
            var read = await printServiceLog.ReadAsync(PrintServiceLogKind.Operational, start.AddSeconds(-2), null, null, 60, cancellationToken).ConfigureAwait(false);
            var events = read
                .Where(e => Mentions(e.Message, token) || Mentions(e.Message, printerName) || Mentions(e.Message, expectedPort) || e.IsProblem)
                .OrderBy(e => e.Time).ToList();
            foreach (var ev in events)
            {
                note(ev.IsProblem ? DiagnosticStatus.Warn : DiagnosticStatus.Info, "Spooler log: " + PrintServiceEventFormatter.Format(ev));
                if (Mentions(ev.Message, token) && ev.Id == 307)
                {
                    evidence.SpoolerPrinted = true;
                    if (Mentions(ev.Message, expectedPort)) evidence.SpoolerPort = expectedPort;
                    else if (PortInEvent().Match(ev.Message) is { Success: true } m) evidence.SpoolerPort = m.Groups["port"].Value;
                }
            }
            evidence.ErrorEvents = events.Count(e => e.IsProblem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note(DiagnosticStatus.Warn, $"Could not read the spooler log: {ex.Message}");
        }

        try
        {
            foreach (var ev in await printServiceLog.ReadAsync(PrintServiceLogKind.Admin, start.AddSeconds(-2), [1, 2, 3], null, 10, cancellationToken).ConfigureAwait(false))
                note(DiagnosticStatus.Warn, "Spooler Admin log: " + PrintServiceEventFormatter.Format(ev));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note(DiagnosticStatus.Warn, $"Could not read the spooler Admin log: {ex.Message}");
        }
    }

    private async Task CollectListenerLogAsync(TestPrintEvidence evidence, DateTimeOffset start, Action<DiagnosticStatus, string> note, CancellationToken cancellationToken)
    {
        IReadOnlyList<LogEntry> lines;
        try
        {
            lines = (await logs.ReadRecentAsync(1, null, "listener", cancellationToken).ConfigureAwait(false))
                .Where(l => l.Time >= start.AddSeconds(-2)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            note(DiagnosticStatus.Warn, $"Could not read the listener log: {ex.Message}");
            return;
        }

        evidence.ListenerConnected = lines.Any(l => l.Message.StartsWith("The spooler connected", StringComparison.OrdinalIgnoreCase));
        foreach (var line in lines)
        {
            var message = line.Message.Length > 300 ? line.Message[..300] + "..." : line.Message;
            note(line.Level is ErpLogLevel.Error or ErpLogLevel.Warning ? DiagnosticStatus.Warn : DiagnosticStatus.Info, "Listener log: " + message);
        }
        var failure = lines.FirstOrDefault(l => l.Level is ErpLogLevel.Error or ErpLogLevel.Warning);
        if (failure is not null && !evidence.Received) evidence.ListenerError = failure.Message;
        if (lines.Count == 0) note(DiagnosticStatus.Info, "The listener log has no entries since the page was printed.");
    }

    private static bool Mentions(string text, string value) => value.Length > 0 && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private PrinterInfo? FindPrinter(string printerName)
    {
        try { return spooler.GetPrinters().FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase)); }
        catch (SpoolerException) { return null; }
    }

    private IReadOnlyList<PrintJobInfo> GetJobs(string printerName)
    {
        try { return spooler.GetJobs(printerName); }
        catch (SpoolerException) { return []; }
    }

    public static string FormatReport(TestPrintVerdict verdict, IReadOnlyList<TestPrintStep> steps, string profileName, DateTimeOffset generated, string computerName)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ERP Printer test print - profile '{profileName}'");
        builder.AppendLine($"{generated:yyyy-MM-dd HH:mm:ss} on {computerName}");
        builder.AppendLine();
        builder.AppendLine($"Result: {(verdict.Success ? "SUCCESS" : "FAILED")} (reached stage: {verdict.Stage})");
        builder.AppendLine(verdict.Summary);
        if (verdict.Hint.Length > 0) builder.AppendLine($"Next step: {verdict.Hint}");
        builder.AppendLine();
        builder.AppendLine("What happened:");
        foreach (var step in steps)
            builder.AppendLine($"  {step.Seconds,5:N1}s [{step.Status.ToString().ToUpperInvariant(),-4}] {step.Message}");
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
