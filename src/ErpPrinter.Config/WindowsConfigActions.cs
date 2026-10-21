using System.ServiceProcess;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;
using ErpPrinter.Updates;
using ErpPrinter.Windows.Hosting;
using ErpPrinter.Windows.Listening;

namespace ErpPrinter.Config;

/// <summary>
/// The configuration window's actions on the real machine: the same composition root, runners and services the command
/// line uses. <see cref="Composition.TryCreateWindows"/> finds <see cref="Create"/> by name.
/// </summary>
internal sealed class WindowsConfigActions(ErpPrinterContext context) : IConfigActions, IDisposable
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);

    public static ConfigComposition Create()
    {
        var context = new ErpPrinterContext("gui");
        context.LoadConfig();
        return new ConfigComposition(context.Store, new WindowsConfigActions(context));
    }

    public string DataRoot => context.LoadConfig().Global.DataRoot;

    public string LogsPath => context.LoadConfig().Global.LogDirectory;

    public Task<(bool Success, string Message)> TestConnectionAsync(PrinterProfile profile, BackendSecrets unsavedSecrets) =>
        TestAsync(profile, unsavedSecrets);

    private async Task<(bool Success, string Message)> TestAsync(PrinterProfile profile, BackendSecrets unsavedSecrets)
    {
        var result = await context.Connections.TestAsync(profile, context.LoadConfig().Global, unsavedSecrets).ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public async Task<TestPrintResult> TestPrintAsync(string profileName, Action<string> progress)
    {
        var config = context.LoadConfig();
        var options = new Diagnostics.TestPrintOptions { OnProgress = progress };
        var result = await context.CreateTestPrint(config).RunAsync(config, profileName, options).ConfigureAwait(false);
        return new TestPrintResult(result.Success, result.Report);
    }

    public async Task<DiagnosticsResult> RunDiagnosticsAsync()
    {
        var config = context.LoadConfig();
        var report = await context.CreateDiagnostics(config).RunAsync(config).ConfigureAwait(false);
        return new DiagnosticsResult(report.Report, report.ReportPath);
    }

    public StatusSnapshot GetStatus()
    {
        var status = context.Status.Get();
        return new StatusSnapshot(status.Service, status.Version, status.Profiles.Select(p => new ProfileStatus(
            p.Profile, p.Enabled, p.Backend, p.Printer, p.PrinterOk, p.Pending, p.Failed, p.Sent,
            p.LastSent?.LocalDateTime, p.LastRemoteId, p.LastError ?? "", p.OutboxPath)).ToList());
    }

    public async Task SendNowAsync() =>
        await context.CreateProcessor().RunPassAsync(context.LoadConfig(), ignoreBackoff: true, CancellationToken.None).ConfigureAwait(false);

    public int RetryFailed()
    {
        // Wake the service's uploader as well, so the documents leave without waiting for the next poll.
        using var signal = new NamedOutboxSignal();
        return context.CreateProcessor(signal).RetryFailed(context.LoadConfig());
    }

    public void RestartService()
    {
        ServiceController service;
        try
        {
            service = new ServiceController(StatusReporter.ServiceName);
            _ = service.Status;
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("The ERP Printer service is not installed. Run the installer repair or 'erpprinter install'.");
        }
        using (service)
        {
            if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
        }
    }

    public void SyncPrinters()
    {
        context.PdfDriver.EnsureInstalled();
        context.Provisioner.Sync();
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var result = await Updater().RunAsync(context.LoadConfig().Global, checkOnly: true, force: false, CancellationToken.None).ConfigureAwait(false);
        return new UpdateCheckResult(result.UpdateAvailable, result.Message, result.AvailableVersion, result.CurrentVersion);
    }

    public async Task<string> InstallUpdateAsync()
    {
        var result = await Updater().RunAsync(context.LoadConfig().Global, checkOnly: false, force: false, CancellationToken.None).ConfigureAwait(false);
        return result.Message;
    }

    private UpdateRunner Updater() => new(context.Http, new MsiexecInstaller(), context.Log, context.Time);

    public void Dispose() => context.Dispose();
}
