using ErpPrinter.Logging;
using ErpPrinter.Settings;

namespace ErpPrinter.Outbox;

/// <summary>
/// Guards "only one uploader per machine". The Windows implementation holds the named mutex <c>Global\ErpPrinterUploader</c>
/// (a mutex is thread-affine, so it cannot live in the async loop itself).
/// </summary>
public interface ISingleInstanceLock
{
    /// <summary>Returns a handle to dispose when done, or null when another instance holds the lock.</summary>
    IDisposable? TryAcquire();
}

/// <summary>
/// Delivers outbox documents until cancelled: wakes when the listener signals a new job or every <c>PollIntervalSeconds</c>,
/// runs a pass, and runs housekeeping hourly. Port of Start-ErpPrinterUploader.
/// </summary>
/// <param name="loadConfig">Called before every pass so settings changes (including new profiles) apply without a restart.</param>
/// <param name="instanceLock">Optional single-instance guard; when it reports another instance, the loop logs a warning and returns.</param>
/// <param name="runtimeSummary">Optional one-line description of the runtime, logged after the start message (Get-ErpRuntimeSummary).</param>
public sealed class UploaderLoop(
    IErpLog log,
    OutboxProcessor processor,
    OutboxHousekeeping housekeeping,
    IOutboxSignal signal,
    Func<ErpConfig> loadConfig,
    TimeProvider time,
    ISingleInstanceLock? instanceLock = null,
    Func<GlobalSettings, string>? runtimeSummary = null)
{
    public static readonly TimeSpan HousekeepingInterval = TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var guard = AcquireGuard(out var acquired);
        if (!acquired)
        {
            log.Warning($"Another uploader is already running; this one (PID {Environment.ProcessId}) is exiting.");
            return;
        }

        try
        {
            var config = loadConfig();
            log.Information($"Uploader started (version {BuildInfo.Version}, PID {Environment.ProcessId}).");
            if (runtimeSummary is not null) log.Information(runtimeSummary(config.Global));

            var nextHousekeeping = DateTimeOffset.MinValue;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    config = loadConfig();
                    await processor.RunPassAsync(config, ignoreBackoff: false, cancellationToken).ConfigureAwait(false);
                    if (time.GetUtcNow() >= nextHousekeeping)
                    {
                        housekeeping.Run(config);
                        nextHousekeeping = time.GetUtcNow() + HousekeepingInterval;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Exception("Upload pass failed", ex);
                }

                try
                {
                    var timeout = TimeSpan.FromSeconds(config.Global.PollIntervalSeconds);
                    await Task.Run(() => signal.Wait(timeout, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Exception("The uploader crashed", ex);
            throw;
        }
        finally
        {
            log.Information("Uploader stopped.");
        }
    }

    private IDisposable AcquireGuard(out bool acquired)
    {
        if (instanceLock is null)
        {
            acquired = true;
            return NoopDisposable.Instance;
        }
        var handle = instanceLock.TryAcquire();
        acquired = handle is not null;
        return handle ?? NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
