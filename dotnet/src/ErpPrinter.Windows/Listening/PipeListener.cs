using System.Globalization;
using System.IO.Pipes;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Windows.Listening;

public sealed record PipeListenerOptions
{
    /// <summary>How often the configuration is re-read.</summary>
    public TimeSpan ReloadInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long a pipe waits before it tries again after it could not be created (or its wait failed).</summary>
    public TimeSpan PipeRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public Func<string, NamedPipeServerStream> PipeFactory { get; init; } = PipeServerFactory.Create;
}

/// <summary>
/// Hosts one named pipe per enabled profile and hands each connection (exactly one print job) to the <see cref="IJobSink"/>
/// (port of <c>Start-ErpPrinterListener</c>). Profiles are re-read every <see cref="PipeListenerOptions.ReloadInterval"/>; when the set of
/// enabled profiles changes (including at startup) the printers are reconciled through the injected callback and the pipes are
/// rebuilt: pipes of unchanged profiles keep running. <see cref="RunAsync"/> ends normally when cancelled and rethrows crashes after logging them.
/// </summary>
public sealed class PipeListener
{
    private readonly Func<ErpConfig> _loadConfig;
    private readonly ISpooler _spooler;
    private readonly IJobSink _sink;
    private readonly IOutboxSignal _signal;
    private readonly IErpLog _log;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task>? _reconcilePrinters;
    private readonly Action<GlobalSettings>? _configLoaded;
    private readonly PipeListenerOptions _options;

    private GlobalSettings _global = GlobalSettings.Defaults();
    private long _jobsReceived;
    private long _lastJobTicks;

    /// <param name="loadConfig">Reads the current configuration (normally <c>ConfigStore.Load</c>).</param>
    /// <param name="reconcilePrinters">Called when the enabled profiles change; the service passes <c>PrinterProvisioner.Sync</c>. Failures are logged, not fatal.</param>
    /// <param name="configLoaded">Called with the global settings after every (re)load, so the host can update the log level and folder.</param>
    public PipeListener(
        Func<ErpConfig> loadConfig,
        ISpooler spooler,
        IJobSink sink,
        IOutboxSignal signal,
        IErpLog log,
        TimeProvider? time = null,
        Func<CancellationToken, Task>? reconcilePrinters = null,
        Action<GlobalSettings>? configLoaded = null,
        PipeListenerOptions? options = null)
    {
        _loadConfig = loadConfig;
        _spooler = spooler;
        _sink = sink;
        _signal = signal;
        _log = log;
        _time = time ?? TimeProvider.System;
        _reconcilePrinters = reconcilePrinters;
        _configLoaded = configLoaded;
        _options = options ?? new PipeListenerOptions();
    }

    /// <summary>Jobs received since start (including empty ones the sink discarded).</summary>
    public long JobsReceived => Interlocked.Read(ref _jobsReceived);

    private sealed class PipeWorker(string key, PrinterProfile profile, CancellationTokenSource cancellation)
    {
        public string Key { get; } = key;
        public volatile PrinterProfile Profile = profile;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = new Dictionary<string, PipeWorker>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var config = _loadConfig();
            Apply(config);
            _log.Information($"Listener started (version {BuildInfo.Version}, PID {Environment.ProcessId}).");
            _log.Information(RuntimeSummary.Describe(config.Global));

            string? stamp = null;
            var firstPass = true;
            var nextReload = DateTimeOffset.MinValue;
            var nextHeartbeat = _time.GetUtcNow() + _options.HeartbeatInterval;

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                if (now >= nextReload)
                {
                    if (!firstPass)
                    {
                        try
                        {
                            config = _loadConfig();
                            Apply(config);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Keep serving with the last good configuration rather than dropping every printer on a transient registry error.
                            _log.Exception("Could not reload the configuration; keeping the previous one", ex);
                        }
                    }
                    firstPass = false;
                    stamp = await ApplyProfilesAsync(config, workers, stamp, cancellationToken).ConfigureAwait(false);
                    nextReload = _time.GetUtcNow() + _options.ReloadInterval;
                }

                if (now >= nextHeartbeat)
                {
                    var last = Interlocked.Read(ref _lastJobTicks) is var ticks and > 0
                        ? "last job at " + new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : "no jobs since start";
                    _log.Information($"Listener alive: {workers.Count} pipe(s), {JobsReceived} job(s) received since start, {last}.");
                    nextHeartbeat = _time.GetUtcNow() + _options.HeartbeatInterval;
                }

                var wake = nextReload < nextHeartbeat ? nextReload : nextHeartbeat;
                var delay = wake - _time.GetUtcNow();
                if (delay < TimeSpan.FromMilliseconds(10)) delay = TimeSpan.FromMilliseconds(10);
                if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Without this a crash only shows up as "Listener stopped" with no reason, or not at all.
            _log.Exception("The listener crashed", ex);
            throw;
        }
        finally
        {
            foreach (var worker in workers.Values.ToList()) await StopAsync(worker).ConfigureAwait(false);
            workers.Clear();
            _log.Information("Listener stopped.");
        }
    }

    private void Apply(ErpConfig config)
    {
        _global = config.Global;
        _configLoaded?.Invoke(config.Global);
    }

    private async Task<string?> ApplyProfilesAsync(ErpConfig config, Dictionary<string, PipeWorker> workers, string? stamp, CancellationToken cancellationToken)
    {
        var enabled = config.Profiles.Where(p => p.Enabled).ToList();
        var newStamp = string.Join(';', enabled.Select(KeyOf));
        if (newStamp == stamp)
        {
            // Refresh settings of unchanged pipes (for example a new FileNameTemplate).
            foreach (var profile in enabled)
                if (workers.TryGetValue(profile.PipeName, out var worker)) worker.Profile = profile;
            return stamp;
        }

        if (_reconcilePrinters is not null)
        {
            try { await _reconcilePrinters(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"Printer reconciliation failed: {ex.Message}");
            }
        }

        var desired = new Dictionary<string, PrinterProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in enabled) desired[profile.PipeName] = profile;

        foreach (var (pipe, worker) in workers.ToList())
        {
            if (desired.TryGetValue(pipe, out var profile) && worker.Key == KeyOf(profile)) continue;
            await StopAsync(worker).ConfigureAwait(false);
            workers.Remove(pipe);
        }
        foreach (var (pipe, profile) in desired)
        {
            if (workers.TryGetValue(pipe, out var existing))
            {
                existing.Profile = profile;
                continue;
            }
            var worker = new PipeWorker(KeyOf(profile), profile, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            worker.Task = Task.Run(() => RunPipeAsync(worker, cancellationToken), CancellationToken.None);
            workers[pipe] = worker;
        }

        _log.Information($"Listening on {workers.Count} pipe(s): {string.Join(", ", workers.Keys.Order(StringComparer.OrdinalIgnoreCase))}");
        if (workers.Count == 0)
            _log.Warning("No enabled printer profiles, so nothing is listening. Create or enable a profile in the configuration tool.");
        return newStamp;
    }

    private static string KeyOf(PrinterProfile profile) => $"{profile.Name}|{profile.PipeName}|{profile.PrinterName}";

    private static async Task StopAsync(PipeWorker worker)
    {
        worker.Cancellation.Cancel();
        try { await worker.Task.ConfigureAwait(false); }
        catch (Exception) { /* the worker logs its own failures */ }
        worker.Cancellation.Dispose();
    }

    /// <summary>One pipe: wait for the spooler, receive one job, make a new pipe instance, repeat. Never throws.</summary>
    private async Task RunPipeAsync(PipeWorker worker, CancellationToken stopping)
    {
        var token = worker.Cancellation.Token;
        var failures = 0;
        var announced = false;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var connected = false;
            try
            {
                var profile = worker.Profile;
                try
                {
                    pipe = _options.PipeFactory(profile.PipeName);
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures == 1 || failures % 60 == 0)
                        _log.Exception($"Cannot open pipe '{profile.PipeName}' (failure {failures})", ex);
                    await Task.Delay(_options.PipeRetryDelay, _time, token).ConfigureAwait(false);
                    continue;
                }

                if (failures > 0) _log.Information($"Pipe '{profile.PipeName}' recovered after {failures} failure(s).");
                failures = 0;
                if (!announced)
                {
                    announced = true;
                    var visible = PipeVisibility.Exists(profile.PipeName) switch
                    {
                        null => "visibility not checked",
                        true => "visible to the spooler",
                        false => @"NOT visible in \\.\pipe\",
                    };
                    _log.Information($"Waiting for print jobs for '{profile.Name}' on {Naming.PortName(profile.PipeName)} (printer '{profile.PrinterName}'; {visible}).");
                }

                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                connected = true;
                await ReceiveAsync(worker, pipe, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Exception($"Failed to receive a job for '{worker.Profile.Name}'", ex);
                if (!connected)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(1), _time, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ReceiveAsync(PipeWorker worker, NamedPipeServerStream pipe, CancellationToken stopping)
    {
        var profile = worker.Profile;
        _log.Information($"The spooler connected to pipe '{profile.PipeName}'; receiving a job for '{profile.Name}'.");

        // Look the job up while the spooler still shows it as printing.
        var job = LookupJob(profile);
        await _sink.ReceiveAsync(profile, _global, pipe, job, stopping).ConfigureAwait(false);

        Interlocked.Increment(ref _jobsReceived);
        Interlocked.Exchange(ref _lastJobTicks, _time.GetUtcNow().UtcTicks);
        _signal.Set();
    }

    private PrintJobInfo? LookupJob(PrinterProfile profile)
    {
        try
        {
            return PickActiveJob(_spooler.GetJobs(profile.PrinterName));
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not query print jobs for '{profile.PrinterName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The job the spooler is currently sending to the port. A queue processes one job at a time, so the job in the Printing state is the
    /// one on the pipe; falls back to the oldest job.
    /// </summary>
    public static PrintJobInfo? PickActiveJob(IReadOnlyList<PrintJobInfo> jobs)
    {
        var ordered = jobs.OrderBy(j => j.Submitted ?? DateTimeOffset.MaxValue).ThenBy(j => j.Id).ToList();
        return ordered.FirstOrDefault(j => j.Status.HasFlag(JobStatusFlags.Printing)) ?? ordered.FirstOrDefault();
    }
}
