using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Diagnostics;

public sealed partial class DiagnosticsRunner
{
    private const string RepairHint = "Click Repair printers (Status tab) or run erpprinter sync.";

    /// <summary>Printer, port, queue, pipe and outbox state for one profile. Port of Get-ErpProfileChecks.</summary>
    private async Task ProfileChecksAsync(ICollection<DiagnosticResult> r, GlobalSettings global, PrinterProfile profile, CancellationToken cancellationToken)
    {
        var category = ProfileCategory(profile);
        if (!profile.Enabled)
        {
            r.Add(DiagnosticResult.Warn(category, "Enabled", "The profile is disabled: it has no printer and nothing is delivered.", "Tick Enabled and save."));
            return;
        }

        var expectedPort = profile.PortName;
        var printer = spooler.GetPrinters().FirstOrDefault(p => string.Equals(p.Name, profile.PrinterName, StringComparison.OrdinalIgnoreCase));
        if (printer is null)
        {
            r.Add(DiagnosticResult.Fail(category, "Windows printer", $"No printer named '{profile.PrinterName}' exists.", RepairHint));
        }
        else
        {
            var problems = new List<string>();
            if (!string.Equals(printer.PortName, expectedPort, StringComparison.OrdinalIgnoreCase))
                problems.Add($"it is on port '{printer.PortName}' instead of '{expectedPort}', so jobs never reach the listener");
            if (!string.Equals(printer.DriverName, PdfDriver, StringComparison.OrdinalIgnoreCase))
                problems.Add($"it uses driver '{printer.DriverName}' instead of '{PdfDriver}', so the output is not a PDF");
            if (problems.Count > 0)
                r.Add(DiagnosticResult.Fail(category, "Windows printer", $"'{printer.Name}': {string.Join("; ", problems)}.", RepairHint));
            else
                r.Add(DiagnosticResult.Pass(category, "Windows printer", $"'{printer.Name}' uses driver '{printer.DriverName}' on port '{printer.PortName}'."));

            var state = PrinterStateAnalyzer.Analyze(printer, _time.GetLocalNow().TimeOfDay);
            if (state.Problem is not null)
                r.Add(DiagnosticResult.Fail(category, "Printer state", state.Problem, state.Hint));
            else
                r.Add(DiagnosticResult.Pass(category, "Printer state", state.Summary));

            if (!spooler.GetPorts().Contains(expectedPort, StringComparer.OrdinalIgnoreCase))
                r.Add(DiagnosticResult.Fail(category, "Printer port", $"The port '{expectedPort}' does not exist.",
                    "Click Repair printers (Status tab); if that fails, restart the Print Spooler service."));
            else
                r.Add(DiagnosticResult.Pass(category, "Printer port", $"'{expectedPort}' exists."));

            await QueueChecksAsync(r, category, printer, cancellationToken).ConfigureAwait(false);
        }

        var pipe = system.PipeExists(profile.PipeName);
        if (pipe is null)
            r.Add(DiagnosticResult.Info(category, "Pipe", $"Could not tell whether '{expectedPort}' exists."));
        else if (pipe.Value)
            r.Add(DiagnosticResult.Pass(category, "Pipe", $"'{expectedPort}' is being hosted, so the listener is ready to receive."));
        else
            r.Add(DiagnosticResult.Fail(category, "Pipe", $"Nothing is hosting '{expectedPort}'. Print jobs sent now would fail or stall in the spooler.",
                "The service is not running, has not opened this pipe yet, or crashed. See Background service and Service history."));

        await DocumentChecksAsync(r, category, global, profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueChecksAsync(ICollection<DiagnosticResult> r, string category, PrinterInfo printer, CancellationToken cancellationToken)
    {
        IReadOnlyList<PrintJobInfo> jobs;
        try { jobs = spooler.GetJobs(printer.Name); }
        catch (SpoolerException) { jobs = []; }

        if (jobs.Count == 0)
        {
            r.Add(DiagnosticResult.Pass(category, "Print queue", "Empty."));
            return;
        }

        var lines = string.Join(Environment.NewLine, jobs.Take(5).Select(j =>
            $"job {j.Id} '{j.DocumentName}' by {j.UserName}: {j.StatusText}, submitted {PrintJobText.Submitted(j)}"));
        var cutoff = _time.GetUtcNow().AddMinutes(-2);
        var stuck = jobs.Where(j => PrintJobText.IsBadStatus(j.Status) || (j.Submitted is { } submitted && submitted < cutoff)).ToList();
        if (stuck.Count > 0)
            r.Add(DiagnosticResult.Fail(category, "Print queue",
                $"{jobs.Count} job(s) are sitting in the queue instead of reaching the listener:{Environment.NewLine}{lines}",
                "A job that stays here did not make it through the spooler; the analysis below says at which stage it stopped. A failed job also holds up every job behind it, so delete it once the cause is fixed."));
        else
            r.Add(DiagnosticResult.Info(category, "Print queue", $"{jobs.Count} job(s) in progress:{Environment.NewLine}{lines}"));

        foreach (var job in stuck.Take(3))
        {
            var analysis = await _jobs.AnalyzeAsync(job, cancellationToken).ConfigureAwait(false);
            r.Add(new DiagnosticResult(category, $"Job {job.Id} analysis", analysis.Failed ? DiagnosticStatus.Fail : DiagnosticStatus.Info, analysis.Detail, analysis.Hint));
        }
    }

    private async Task DocumentChecksAsync(ICollection<DiagnosticResult> r, string category, GlobalSettings global, PrinterProfile profile, CancellationToken cancellationToken)
    {
        if (!FolderAccess.IsReadable(global.DataRoot))
        {
            r.Add(DiagnosticResult.Warn(category, "Documents", "The outbox cannot be read from this session.", "Run elevated."));
            return;
        }

        var folders = ProfileFolders.For(global.DataRoot, profile.Name);
        var pending = await OutboxReader.ReadFolderAsync(folders.Outbox, "outbox", cancellationToken).ConfigureAwait(false);
        var failed = await OutboxReader.ReadFolderAsync(folders.Failed, "failed", cancellationToken).ConfigureAwait(false);
        var stats = await OutboxReader.ReadStatsAsync(folders.Stats, cancellationToken).ConfigureAwait(false);
        var lastSent = stats.LastSentAt is { } sent ? Stamp(sent) : "never";
        r.Add(DiagnosticResult.Info(category, "Documents", $"{pending.Count} waiting, {failed.Count} failed, {stats.Count} delivered (last: {lastSent})."));

        var errored = pending.Concat(failed).Where(i => !string.IsNullOrEmpty(i.Meta.LastError)).ToList();
        if (errored.Count > 0)
        {
            var latest = errored.OrderBy(i => i.Meta.LastAttempt).Last();
            r.Add(DiagnosticResult.Fail(category, "Delivery error", $"'{latest.Meta.Title}': {latest.Meta.LastError}",
                "Fix the cause shown (credentials, URL, model/field names), then use \"Retry failed\" and \"Send now\" in the Status tab."));
        }
        else if (pending.Count > 0)
        {
            var printed = pending.Where(i => i.Meta.Printed is not null).Select(i => i.Meta.Printed!.Value).OrderBy(t => t).ToList();
            if (printed.Count > 0 && printed[0] < _time.GetUtcNow().AddMinutes(-5))
                r.Add(DiagnosticResult.Warn(category, "Delivery",
                    $"The oldest waiting document was printed {TimeZoneInfo.ConvertTime(printed[0], _time.LocalTimeZone):HH:mm:ss} and has no error recorded.",
                    "The uploader (part of the ErpPrinter service) is probably not running or cannot start; see Background service."));
        }
    }

    /// <summary>Proxy and ERP connection of one profile. Port of Get-ErpConnectionChecks.</summary>
    private async Task ConnectionChecksAsync(ICollection<DiagnosticResult> r, GlobalSettings global, PrinterProfile profile)
    {
        var category = ProfileCategory(profile);
        var proxy = global.ProxyUrl;
        var userProxy = system.GetUserProxy();
        if (!string.IsNullOrEmpty(userProxy) && proxy.Length == 0)
            r.Add(DiagnosticResult.Warn(category, "Proxy",
                $"This user reaches the web through proxy '{userProxy}', but the ProxyUrl setting is empty. The service runs as SYSTEM and does not see per-user proxy settings.",
                "Set ProxyUrl (Settings tab) if the ERP is only reachable through that proxy. A passing connection test from this session does not prove the service can connect."));
        else
            r.Add(DiagnosticResult.Info(category, "Proxy", proxy.Length > 0 ? $"ProxyUrl is {proxy}." : "No proxy configured."));

        var (ok, message) = await connectionTester!(profile).ConfigureAwait(false);
        if (ok)
            r.Add(DiagnosticResult.Pass(category, "ERP connection", string.Join(' ', message.Split('\n').Take(2).Select(l => l.TrimEnd('\r')))));
        else
            r.Add(DiagnosticResult.Fail(category, "ERP connection", message,
                "Check the server URL, database, user and API key. The uploader runs as SYSTEM, so also check the machine can reach the server without a user-level proxy or VPN."));
    }
}
