using System.Runtime.InteropServices;
using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Diagnostics;

public sealed record DiagnosticsOptions(string? ProfileName = null, bool SkipConnection = false, bool SaveReport = true);

public sealed record DiagnosticsReport(IReadOnlyList<DiagnosticResult> Results, string Report, string? ReportPath)
{
    public int Failures => Results.Count(r => r.Status == DiagnosticStatus.Fail);
    public int Warnings => Results.Count(r => r.Status == DiagnosticStatus.Warn);
}

/// <summary>
/// Checks every link between "Print" and the ERP without printing anything and reports what is wrong. Every check runs isolated
/// (<see cref="DiagnosticStep"/>), so one that cannot run becomes a warning and never hides the others. Port of
/// Invoke-ErpPrinterDiagnostics and Private/Diagnostics.ps1, with the two scheduled tasks replaced by the single Windows service.
/// </summary>
/// <param name="spooler">The print spooler (printers, ports, drivers, queue).</param>
/// <param name="printServiceLog">The PrintService event logs.</param>
/// <param name="system">Operating system facts.</param>
/// <param name="spoolFiles">Spool shadow file reader, for the job analysis.</param>
/// <param name="logs">Reads back the service's own log files.</param>
/// <param name="connectionTester">Tests the ERP connection of a profile; null skips the connection checks.</param>
/// <param name="hasSecret">Whether a profile has a stored secret (profile name, secret name); null assumes yes.</param>
public sealed partial class DiagnosticsRunner(
    ISpooler spooler,
    IPrintServiceLog printServiceLog,
    ISystemInspector system,
    ISpoolFileInspector spoolFiles,
    ILogReader logs,
    Func<PrinterProfile, Task<(bool Ok, string Message)>>? connectionTester = null,
    Func<string, string, bool>? hasSecret = null,
    TimeProvider? time = null)
{
    public const string PdfDriver = "Microsoft Print To PDF";
    public const string ServiceName = "ErpPrinter";

    private const string ServiceCategory = "Background service";
    private const string HistoryCategory = "Service history (24 h)";
    private const string PrintingCategory = "Windows printing";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly JobAnalyzer _jobs = new(printServiceLog, spoolFiles, time);

    /// <exception cref="InvalidOperationException">A specific profile was requested and does not exist.</exception>
    public async Task<DiagnosticsReport> RunAsync(ErpConfig config, DiagnosticsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DiagnosticsOptions();
        var global = config.Global;
        var results = new List<DiagnosticResult>();

        async Task Step(string category, string name, Func<ICollection<DiagnosticResult>, Task> check) =>
            results.AddRange(await DiagnosticStep.RunAsync(category, name, check).ConfigureAwait(false));

        var profiles = config.Profiles
            .Where(p => options.ProfileName is null || string.Equals(p.Name, options.ProfileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.ProfileName is not null && profiles.Count == 0)
            throw new InvalidOperationException($"No printer profile named '{options.ProfileName}'.");

        results.AddRange(DiagnosticStep.Run("System", "System checks", r => SystemChecks(r)));
        await Step("Storage and logs", "Storage checks", r => StorageChecksAsync(r, global, cancellationToken)).ConfigureAwait(false);
        await Step(ServiceCategory, "Service checks", r => { ServiceChecks(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await Step(HistoryCategory, "Log history", r => LogHistoryChecksAsync(r, global, cancellationToken)).ConfigureAwait(false);
        await SpoolerChecksAsync(results, cancellationToken).ConfigureAwait(false);

        if (profiles.Count == 0)
            results.Add(DiagnosticResult.Fail("Profiles", "Printer profiles", "No printer profiles are configured.", "Add a printer in the configuration tool."));

        foreach (var profile in profiles)
        {
            var category = ProfileCategory(profile);
            await Step(category, "Printer checks", r => ProfileChecksAsync(r, global, profile, cancellationToken)).ConfigureAwait(false);

            var missing = ProfileValidator.Validate(profile, secret => hasSecret?.Invoke(profile.Name, secret) ?? true, config.Profiles);
            if (missing.Count > 0)
                results.Add(DiagnosticResult.Fail(category, "Settings", string.Join(Environment.NewLine, missing), "Complete the profile in the Printers tab."));
            else if (!options.SkipConnection && connectionTester is not null)
                await Step(category, "Connection checks", r => ConnectionChecksAsync(r, global, profile)).ConfigureAwait(false);
        }

        var now = _time.GetLocalNow();
        var report = DiagnosticReportFormatter.Format(results, now, system.ComputerName);
        string? path = null;
        if (options.SaveReport) path = await DiagnosticReportFormatter.SaveAsync(report, global.DataRoot, now, cancellationToken).ConfigureAwait(false);
        return new DiagnosticsReport(results, report, path);
    }

    internal static string ProfileCategory(PrinterProfile profile) => $"Profile '{profile.Name}'";

    private string Stamp(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm:ss");

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    #region System, storage, service

    private void SystemChecks(ICollection<DiagnosticResult> r)
    {
        const string category = "System";
        r.Add(DiagnosticResult.Info(category, "ERP Printer", $"Version {BuildInfo.Version}, build {BuildInfo.Commit}; computer {system.ComputerName}"));
        r.Add(DiagnosticResult.Info(category, "Operating system", system.OperatingSystem));
        r.Add(DiagnosticResult.Info(category, "Runtime", $"{RuntimeInformation.FrameworkDescription}, running as {system.CurrentUser}"));
        if (!system.IsElevated)
            r.Add(DiagnosticResult.Warn(category, "Elevation", "This session is not elevated, so process, service and storage checks are limited.",
                "Run again from an elevated prompt (or the configuration tool as Administrator)."));
    }

    private async Task StorageChecksAsync(ICollection<DiagnosticResult> r, GlobalSettings global, CancellationToken cancellationToken)
    {
        const string category = "Storage and logs";
        var dataRoot = global.DataRoot;
        if (!Directory.Exists(dataRoot))
        {
            r.Add(DiagnosticResult.Fail(category, "Data folder", $"'{dataRoot}' does not exist.",
                "Run the installer repair or erpprinter install; the data folder is created at install time."));
            return;
        }

        var probe = await system.ProbeWritableAsync(dataRoot, cancellationToken).ConfigureAwait(false);
        if (probe.Writable)
            r.Add(DiagnosticResult.Pass(category, "Data folder", $"'{dataRoot}' is writable by this session."));
        else
            r.Add(new DiagnosticResult(category, "Data folder", system.IsElevated ? DiagnosticStatus.Fail : DiagnosticStatus.Warn,
                $"'{dataRoot}' is not writable by this session: {probe.Error}",
                "The folder is limited to SYSTEM and Administrators. If this session is elevated, check the folder ACL (icacls) and antivirus exclusions."));

        try
        {
            var disk = system.GetFreeSpace(dataRoot);
            var freeMb = Math.Round(disk.FreeBytes / 1048576.0);
            var status = freeMb < 200 ? DiagnosticStatus.Fail : freeMb < 1024 ? DiagnosticStatus.Warn : DiagnosticStatus.Pass;
            r.Add(new DiagnosticResult(category, "Free disk space", status, $"{freeMb:0} MB free on {disk.Volume}",
                status == DiagnosticStatus.Pass ? "" : "Print jobs are spooled to disk before upload; free some space."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r.Add(DiagnosticResult.Info(category, "Free disk space", $"Not checked ({ex.Message})"));
        }

        var level = global.LogLevel;
        if (level is "Error" or "Warning")
            r.Add(DiagnosticResult.Warn(category, "Log level", $"LogLevel is {level}, which hides the 'queued' and 'delivered' messages.",
                "Set LogLevel to Information (or Verbose while troubleshooting) in the Settings tab."));
        else
            r.Add(DiagnosticResult.Pass(category, "Log level", level));

        var logDir = global.LogDirectory;
        var files = new List<FileInfo>();
        try { files = new DirectoryInfo(logDir).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* reported below */ }

        if (!FolderAccess.IsReadable(dataRoot))
            r.Add(DiagnosticResult.Warn(category, "Log files", $"'{logDir}' cannot be read from this session.", "Run elevated to read the logs."));
        else if (files.Count == 0)
            r.Add(DiagnosticResult.Warn(category, "Log files", $"No log files in '{logDir}'.",
                "Neither the listener nor the uploader has ever written a line here. Check that the ErpPrinter service runs (see Background service) and that SYSTEM can write to this folder."));
        else
        {
            var latest = string.Join("; ", files.Take(4).Select(f =>
                $"{f.Name} ({Stamp(f.LastWriteTimeUtc)}, {Math.Max(1, Math.Round(f.Length / 1024.0)):0} KB)"));
            r.Add(DiagnosticResult.Info(category, "Log files", $"In '{logDir}': {latest}"));
        }
    }

    private void ServiceChecks(ICollection<DiagnosticResult> r)
    {
        var service = system.GetService(ServiceName);
        if (service is null)
        {
            r.Add(DiagnosticResult.Fail(ServiceCategory, "ErpPrinter service", "The Windows service 'ErpPrinter' is not installed.",
                "Run the installer repair, or: erpprinter install."));
        }
        else
        {
            var lastStart = service.LastStart is { } t ? Stamp(t) : "unknown";
            var detail = $"State {service.State}; start type {service.StartType}; last start {lastStart}.";
            if (service.IsDisabled)
                r.Add(DiagnosticResult.Fail(ServiceCategory, "ErpPrinter service", detail,
                    $"Enable it: sc.exe config {ServiceName} start= auto, then Start-Service {ServiceName}."));
            else if (!service.IsRunning)
                r.Add(DiagnosticResult.Fail(ServiceCategory, "ErpPrinter service", $"{detail} It should always be running.",
                    $"Start it with Start-Service {ServiceName}. If it stops again, the newest service-*.log, listener-*.log and uploader-*.log files in the log folder and the Application event log (source ErpPrinter) say why."));
            else if (service.StartType.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                r.Add(DiagnosticResult.Warn(ServiceCategory, "ErpPrinter service", $"{detail} It will not start again after a reboot.",
                    $"sc.exe config {ServiceName} start= auto"));
            else
                r.Add(DiagnosticResult.Pass(ServiceCategory, "ErpPrinter service", detail));
        }

        var processes = system.GetErpProcesses();
        if (processes is null)
        {
            r.Add(DiagnosticResult.Info(ServiceCategory, "Service process", "Process command lines are not readable from this session (run elevated to check)."));
        }
        else
        {
            var running = processes.Where(p => p.IsService).ToList();
            if (running.Count == 0)
                r.Add(new DiagnosticResult(ServiceCategory, "Service process", service?.IsRunning == true ? DiagnosticStatus.Warn : DiagnosticStatus.Fail,
                    "No ErpPrinter.Service.exe process is running.", $"Start-Service {ServiceName}; check the log folder for a crash message."));
            else if (running.Count > 1)
                r.Add(DiagnosticResult.Warn(ServiceCategory, "Service process",
                    $"{running.Count} processes are running it (PIDs {string.Join(", ", running.Select(p => p.Id))}).",
                    $"Extra copies compete for the same pipe or outbox. Restart-Service {ServiceName} and end any extra ErpPrinter.Service.exe in Task Manager."));
            else
                r.Add(DiagnosticResult.Pass(ServiceCategory, "Service process",
                    $"Running as PID {running[0].Id}{(running[0].Started is { } started ? ", started " + Stamp(started) : "")}."));
        }

        // The PowerShell version's tasks would compete with the service for the pipe and the outbox.
        var tasks = system.GetLegacyTasks();
        var legacy = (processes ?? []).Where(p => p.LegacyCommandName is not null).ToList();
        if (tasks.Count == 0 && legacy.Count == 0)
        {
            r.Add(DiagnosticResult.Pass(ServiceCategory, "Legacy scheduled tasks", "None installed."));
            return;
        }
        var lines = tasks.Select(t => t.Describe())
            .Concat(legacy.Select(p => $"PowerShell process PID {p.Id} runs ErpPrinter.ps1 -Command {p.LegacyCommandName}"));
        var active = tasks.Any(t => t.IsRunning) || legacy.Count > 0;
        r.Add(new DiagnosticResult(ServiceCategory, "Legacy scheduled tasks", active ? DiagnosticStatus.Fail : DiagnosticStatus.Warn,
            "The scheduled tasks of the PowerShell version are still installed" + (active ? " and running" : "") + ":" + Environment.NewLine +
            string.Join(Environment.NewLine, lines),
            $"They compete with the service for the pipe and the outbox. The installer removes them on upgrade; to remove them by hand: schtasks /Delete /TN \"\\ERP Printer\\Listener\" /F (and Uploader, Updater), then Restart-Service {ServiceName}."));
    }

    #endregion

    #region Service history

    private async Task LogHistoryChecksAsync(ICollection<DiagnosticResult> r, GlobalSettings global, CancellationToken cancellationToken)
    {
        if (!FolderAccess.IsReadable(global.DataRoot))
        {
            r.Add(DiagnosticResult.Warn(HistoryCategory, "Service logs", "The logs cannot be read from this session, so what the service did in the last day is unknown.", "Run elevated."));
            return;
        }

        var entries = await logs.ReadRecentAsync(24, null, null, cancellationToken).ConfigureAwait(false);
        var listener = entries.Where(e => e.Component == "listener").ToList();
        var uploader = entries.Where(e => e.Component == "uploader").ToList();

        var starts = listener.Where(e => e.Message.StartsWith("Listener started", StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count == 0)
        {
            var why = global.LogLevel is "Error" or "Warning" ? "LogLevel hides Information messages." : "The listener has not started, or cannot write its log.";
            r.Add(DiagnosticResult.Warn(HistoryCategory, "Listener start-up", $"No 'Listener started' line in the last 24 hours. {why}",
                "See Background service above, and the Application event log (source ErpPrinter)."));
        }
        else
        {
            var lastStart = starts[^1];
            var opened = listener.Where(e => e.Time >= lastStart.Time && e.Message.StartsWith("Waiting for print jobs", StringComparison.OrdinalIgnoreCase)).ToList();
            if (starts.Count >= 5)
                r.Add(DiagnosticResult.Warn(HistoryCategory, "Listener restarts",
                    $"The listener started {starts.Count} times in 24 hours (latest {lastStart.Time:HH:mm:ss}).",
                    "Restarts are normal after an update, a settings change or a reboot. If none of those happened, it is probably crashing: look for a crash or error line next to the start lines in the listener log."));
            if (opened.Count == 0)
                r.Add(DiagnosticResult.Fail(HistoryCategory, "Listener pipes", $"The listener started at {lastStart.Time:HH:mm:ss} but never reported a pipe ready.",
                    "Either no profile is enabled or opening the pipe fails; the errors below say which."));
            else
                r.Add(DiagnosticResult.Pass(HistoryCategory, "Listener pipes",
                    $"{opened.Count} pipe(s) opened after the last start: {string.Join(" | ", opened.Select(e => e.Message.Replace("Waiting for print jobs for ", "")).Take(3))}"));
        }

        var connections = listener.Where(e => e.Message.StartsWith("The spooler connected", StringComparison.OrdinalIgnoreCase)).ToList();
        var queued = listener.Count(e => e.Message.StartsWith("Queued ", StringComparison.OrdinalIgnoreCase));
        var delivered = uploader.Count(e => e.Message.StartsWith("Delivered ", StringComparison.OrdinalIgnoreCase));
        // Info results do not print their hint, so the advice for "no connections" is part of the detail.
        if (connections.Count == 0)
            r.Add(DiagnosticResult.Info(HistoryCategory, "Spooler connections",
                "The spooler has not connected to a pipe in the last 24 hours. If you printed in that time, the job never left the spooler: run erpprinter testprint to see where it stops."));
        else
            r.Add(DiagnosticResult.Info(HistoryCategory, "Spooler connections",
                $"{connections.Count} connection(s), {queued} job(s) queued, {delivered} delivered; latest connection {connections[^1].Time:yyyy-MM-dd HH:mm:ss}."));

        var problems = entries.Where(e => e.Level is ErpLogLevel.Error or ErpLogLevel.Warning).ToList();
        if (problems.Count > 0)
        {
            var recent = string.Join(Environment.NewLine, problems.TakeLast(6).Select(e => $"{e.Time:HH:mm:ss} {e.Level} [{e.Component}] {Limit(e.Message, 300)}"));
            var hasError = problems.Any(e => e.Level == ErpLogLevel.Error);
            r.Add(new DiagnosticResult(HistoryCategory, "Logged problems", hasError ? DiagnosticStatus.Fail : DiagnosticStatus.Warn,
                $"{problems.Count} warning/error line(s); the latest:{Environment.NewLine}{recent}"));
        }
        else
        {
            r.Add(DiagnosticResult.Pass(HistoryCategory, "Logged problems", "No warnings or errors in the last 24 hours."));
        }
    }

    #endregion

    #region Windows printing

    private async Task SpoolerChecksAsync(List<DiagnosticResult> results, CancellationToken cancellationToken)
    {
        async Task Step(string name, Func<ICollection<DiagnosticResult>, Task> check) =>
            results.AddRange(await DiagnosticStep.RunAsync(PrintingCategory, name, check).ConfigureAwait(false));

        await Step("Print Spooler service", r =>
        {
            var service = system.GetService("Spooler");
            if (service is null)
                r.Add(DiagnosticResult.Fail(PrintingCategory, "Print Spooler service", "The Print Spooler service does not exist."));
            else if (!service.IsRunning)
                r.Add(DiagnosticResult.Fail(PrintingCategory, "Print Spooler service", $"Status {service.State}, start type {service.StartType}.",
                    "Start-Service Spooler (and set it to Automatic)."));
            else
                r.Add(DiagnosticResult.Pass(PrintingCategory, "Print Spooler service", "Running"));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Step("PDF driver", r =>
        {
            if (spooler.GetDrivers().Any(d => string.Equals(d, PdfDriver, StringComparison.OrdinalIgnoreCase)))
                r.Add(DiagnosticResult.Pass(PrintingCategory, "PDF driver", $"'{PdfDriver}' is installed."));
            else
                r.Add(DiagnosticResult.Fail(PrintingCategory, "PDF driver", $"'{PdfDriver}' is not installed, so jobs cannot be rendered to PDF.",
                    "Repair printers in the Status tab, or enable the 'Microsoft Print to PDF' Windows feature (Printing-PrintToPDFServices-Features)."));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // Windows protected print mode removes third-party port monitors and drivers.
        await Step("Windows protected print mode", r =>
        {
            if (system.GetProtectedPrintMode() == 1)
                r.Add(DiagnosticResult.Fail(PrintingCategory, "Windows protected print mode", "Windows protected print mode is on.",
                    "It blocks local ports and classic drivers, so ERP Printer queues cannot print. Turn the policy off for these machines."));
            else
                r.Add(DiagnosticResult.Pass(PrintingCategory, "Windows protected print mode", "Not enabled."));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await Step("PrintService Operational log", async r =>
        {
            var state = await printServiceLog.GetStateAsync(PrintServiceLogKind.Operational, cancellationToken).ConfigureAwait(false);
            switch (state)
            {
                case PrintServiceLogState.NotPresent:
                    r.Add(DiagnosticResult.Info(PrintingCategory, "PrintService Operational log", "The log is not present on this system."));
                    break;
                case PrintServiceLogState.Disabled:
                    r.Add(DiagnosticResult.Warn(PrintingCategory, "PrintService Operational log", "The log is disabled, so the spooler leaves no record of what happened to a job.",
                        "wevtutil sl Microsoft-Windows-PrintService/Operational /e:true   (the installer normally does this; a policy may have switched it back.)"));
                    break;
                default:
                    r.Add(DiagnosticResult.Pass(PrintingCategory, "PrintService Operational log", "Enabled."));
                    break;
            }
        }).ConfigureAwait(false);

        var since = _time.GetLocalNow().AddHours(-24);
        foreach (var kind in new[] { PrintServiceLogKind.Admin, PrintServiceLogKind.Operational })
        {
            await Step($"Spooler {kind} log", async r =>
            {
                var events = await printServiceLog.ReadAsync(kind, since, [1, 2, 3], null, 8, cancellationToken).ConfigureAwait(false);
                if (events.Count > 0)
                {
                    var lines = string.Join(Environment.NewLine, events.Take(5).Select(e => PrintServiceEventFormatter.Format(e)));
                    r.Add(DiagnosticResult.Warn(PrintingCategory, $"Spooler {kind} log",
                        $"{events.Count} warning/error event(s) in the last 24 hours:{Environment.NewLine}{lines}",
                        "Look for your printer or port name in these messages: they are the spooler explaining why a job failed."));
                }
                else
                {
                    r.Add(DiagnosticResult.Pass(PrintingCategory, $"Spooler {kind} log", "No warnings or errors in the last 24 hours."));
                }
            }).ConfigureAwait(false);
        }
    }

    #endregion
}
