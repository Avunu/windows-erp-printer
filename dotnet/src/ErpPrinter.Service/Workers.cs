using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Settings;
using ErpPrinter.Updates;
using ErpPrinter.Windows.Hosting;

namespace ErpPrinter.Service;

/// <summary>
/// Base for the three long-running parts. A part that fails is logged and restarted after a pause instead of taking the
/// whole service down, so a bug in the updater cannot stop printing. If the process itself dies, the service's recovery
/// action (restart after 60 s) brings it back.
/// </summary>
public abstract class RestartingWorker(string name, ErpPrinterContext context) : BackgroundService
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);

    protected ErpPrinterContext Context { get; } = context;
    protected IErpLog Log { get; } = context.CreateLog(name.ToLowerInvariant());

    protected abstract Task RunOnceAsync(CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                return; // returned normally: the part finished (for example, another uploader already runs)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loops log their own crash; this line records that it will be restarted.
                Log.Error($"The {name} stopped unexpectedly ({ex.GetType().Name}); restarting in {RestartDelay.TotalSeconds:N0} s.");
                try { await Task.Delay(RestartDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

/// <summary>Hosts the print pipes. Waits for the Print Spooler first: at boot the service can start before it.</summary>
public sealed class ListenerWorker(ErpPrinterContext context, IOutboxSignal signal) : RestartingWorker("listener", context)
{
    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        await WaitForSpoolerAsync(stoppingToken).ConfigureAwait(false);
        var listener = Context.CreateListener(signal, Log);
        await listener.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task WaitForSpoolerAsync(CancellationToken stoppingToken)
    {
        var announced = false;
        while (!Context.Spooler.IsSpoolerRunning())
        {
            if (!announced)
            {
                Log.Warning("The Print Spooler service is not running yet; waiting for it before opening the print pipes.");
                announced = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        if (announced) Log.Information("The Print Spooler service is running.");
    }
}

/// <summary>Delivers outbox documents to the ERP, waking on the listener's signal or every PollIntervalSeconds.</summary>
public sealed class UploaderWorker(ErpPrinterContext context, IOutboxSignal signal) : RestartingWorker("uploader", context)
{
    protected override Task RunOnceAsync(CancellationToken stoppingToken) =>
        Context.CreateUploaderLoop(signal, Log).RunAsync(stoppingToken);
}

/// <summary>Checks for and installs updates once a day (03:00 plus a random delay), catching up after a missed run.</summary>
public sealed class UpdaterWorker(ErpPrinterContext context) : RestartingWorker("updater", context)
{
    private const string LastCheckValue = "LastUpdateCheck";

    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var time = Context.Time;
        var runner = new UpdateRunner(Context.Http, new MsiexecInstaller(), Log, time);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = UpdateSchedule.NextRun(time.GetLocalNow(), ReadLastCheck(), Random.Shared.NextDouble());
            var wait = next - time.GetLocalNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);

            var settings = Context.LoadConfig().Global;
            try
            {
                var result = await runner.RunAsync(settings, checkOnly: false, force: false, stoppingToken).ConfigureAwait(false);
                Log.Information(result.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Exception("Update check failed", ex);
            }
            Context.Registry.SetValue(ConfigStore.RegistryRoot, LastCheckValue, time.GetLocalNow().ToString("o"), RegistryValueType.String);
        }
    }

    private DateTimeOffset? ReadLastCheck() =>
        Context.Registry.GetValues(ConfigStore.RegistryRoot).TryGetValue(LastCheckValue, out var value)
        && DateTimeOffset.TryParse(value?.ToString(), out var parsed) ? parsed : null;
}
