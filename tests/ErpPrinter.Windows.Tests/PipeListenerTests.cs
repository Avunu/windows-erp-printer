using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Listening;

namespace ErpPrinter.Windows.Tests;

/// <summary>An <see cref="IJobSink"/> that reads the pipe to the end and remembers what it got.</summary>
public sealed class WinRecordingSink : IJobSink
{
    private readonly object _gate = new();
    private readonly List<(PrinterProfile Profile, PrintJobInfo? Job, byte[] Bytes)> _received = [];
    private int _attempts;

    /// <summary>Throws this for the next job(s) instead of storing them.</summary>
    public Func<int, Exception?>? FaultFor { get; set; }

    public IReadOnlyList<(PrinterProfile Profile, PrintJobInfo? Job, byte[] Bytes)> Received { get { lock (_gate) return _received.ToList(); } }

    public GlobalSettings? LastGlobal { get; private set; }

    public async Task<OutboxMeta?> ReceiveAsync(PrinterProfile profile, GlobalSettings global, Stream source, PrintJobInfo? job, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        LastGlobal = global;
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        if (FaultFor?.Invoke(attempt) is { } fault) throw fault;
        lock (_gate) _received.Add((profile, job, buffer.ToArray()));
        return null;
    }
}

public sealed class PipeListenerTests : IAsyncLifetime
{
    private readonly string _pipe = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
    private readonly MemoryLog _log = new();
    private readonly WinFakeSpooler _spooler = new();
    private readonly WinRecordingSink _sink = new();
    private readonly WinCountingSignal _signal = new();
    private readonly CancellationTokenSource _cts = new();
    private ErpConfig _config = null!;
    private volatile ErpConfig _current = null!;
    private Task? _run;

    public Task InitializeAsync()
    {
        _config = Config(Profile("Test", _pipe, "Test Printer"));
        _current = _config;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
        {
            try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* a crash test already asserted on it */ }
        }
        _cts.Dispose();
    }

    private static PrinterProfile Profile(string name, string pipe, string printer, bool enabled = true) =>
        PrinterProfile.Create(name, new Dictionary<string, object> { ["PipeName"] = pipe, ["PrinterName"] = printer, ["Enabled"] = enabled });

    private static ErpConfig Config(params PrinterProfile[] profiles) => new(GlobalSettings.Defaults(Path.GetTempPath()), profiles);

    private PipeListener StartListener(
        Func<CancellationToken, Task>? reconcile = null, PipeListenerOptions? options = null, Func<ErpConfig>? load = null, Action<GlobalSettings>? loaded = null)
    {
        var listener = new PipeListener(
            load ?? (() => _current), _spooler, _sink, _signal, _log, TimeProvider.System, reconcile, loaded,
            options ?? new PipeListenerOptions { ReloadInterval = TimeSpan.FromMilliseconds(100), PipeRetryDelay = TimeSpan.FromMilliseconds(50) });
        _run = listener.RunAsync(_cts.Token);
        return listener;
    }

    private static async Task SendAsync(string pipe, byte[] bytes, bool close = true)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
        await client.ConnectAsync(10000);
        await client.WriteAsync(bytes);
        if (close) await client.FlushAsync();
    }

    private bool Logged(string text) => _log.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));

    private static PrintJobInfo Job(int id, JobStatusFlags status, int minutesAgo, string title = "doc") =>
        new(id, "Test Printer", title, "alice", status, 1, 0, 100, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    [Fact]
    public async Task A_job_written_to_the_pipe_reaches_the_sink_exactly_and_wakes_the_uploader()
    {
        var payload = new byte[300_000]; // larger than the pipe buffers
        new Random(42).NextBytes(payload);
        payload[0] = (byte)'%';
        _spooler.Jobs["Test Printer"] = [Job(7, JobStatusFlags.Printing, 0, "Invoice 42")];
        StartListener();

        await SendAsync(_pipe, payload);
        await WinWait.UntilAsync(() => _signal.Count == 1, "signal");

        var (profile, job, bytes) = Assert.Single(_sink.Received);
        Assert.Equal(payload, bytes);
        Assert.Equal("Test", profile.Name);
        Assert.Equal(7, job?.Id);
        Assert.Equal("Invoice 42", job?.DocumentName);
        Assert.NotNull(_sink.LastGlobal);
    }

    [Fact]
    public async Task A_small_job_that_was_written_and_closed_before_the_listener_waited_is_still_received()
    {
        // A job that fits in the pipe buffer can arrive while the instance is being set up: the client connects, writes and
        // closes before WaitForConnection runs, which then fails with "The pipe is being closed" although the data is there.
        var payload = "%PDF-1.7 small job"u8.ToArray();
        var first = true;
        NamedPipeServerStream RacingFactory(string name)
        {
            var server = PipeServerFactory.Create(name);
            if (first)
            {
                first = false;
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(5000);
                client.Write(payload);
                client.Flush();
            }
            return server;
        }
        StartListener(options: new PipeListenerOptions
        {
            ReloadInterval = TimeSpan.FromMilliseconds(100), PipeRetryDelay = TimeSpan.FromMilliseconds(50), PipeFactory = RacingFactory,
        });

        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Equal(payload, _sink.Received[0].Bytes);
        Assert.DoesNotContain(_log.Entries, e => e.Message.StartsWith("Failed to receive a job"));
        await WinWait.UntilAsync(() => _signal.Count == 1, "signal");
    }

    [Fact]
    public async Task The_log_tells_the_story_of_a_job()
    {
        StartListener();

        await WinWait.UntilAsync(() => Logged("Waiting for print jobs"), "pipe announced");
        await SendAsync(_pipe, [1, 2, 3]);
        await WinWait.UntilAsync(() => _signal.Count == 1, "signal");

        var messages = _log.Entries.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.StartsWith("Listener started (version ") && m.EndsWith($", PID {Environment.ProcessId})."));
        Assert.Contains(messages, m => m.StartsWith("Runtime: user "));
        Assert.Contains($"Listening on 1 pipe(s): {_pipe}", messages);
        Assert.Contains($@"Waiting for print jobs for 'Test' on \\.\pipe\{_pipe} (printer 'Test Printer'; visible to the spooler).", messages);
        Assert.Contains($"The spooler connected to pipe '{_pipe}'; receiving a job for 'Test'.", messages);
        Assert.Equal(1, messages.Count(m => m.StartsWith("Waiting for print jobs")));
    }

    [Fact]
    public async Task Two_jobs_in_a_row_are_both_received()
    {
        StartListener();

        await SendAsync(_pipe, [1, 1, 1]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "first job");
        await SendAsync(_pipe, [2, 2]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 2, "second job");

        Assert.Equal(new byte[] { 1, 1, 1 }, _sink.Received[0].Bytes);
        Assert.Equal(new byte[] { 2, 2 }, _sink.Received[1].Bytes);
        await WinWait.UntilAsync(() => _signal.Count == 2, "second signal");
    }

    [Fact]
    public async Task The_job_in_the_printing_state_wins_over_older_ones()
    {
        _spooler.Jobs["Test Printer"] =
        [
            Job(1, JobStatusFlags.Spooling, 30, "oldest but not printing"),
            Job(2, JobStatusFlags.Printing | JobStatusFlags.Retained, 5, "the one on the pipe"),
            Job(3, JobStatusFlags.Spooling, 1, "newest"),
        ];
        StartListener();

        await SendAsync(_pipe, [9]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Equal(2, _sink.Received[0].Job?.Id);
    }

    [Fact]
    public async Task Without_a_printing_job_the_oldest_is_used()
    {
        _spooler.Jobs["Test Printer"] = [Job(3, JobStatusFlags.Spooling, 1), Job(1, JobStatusFlags.Spooling, 30), Job(2, JobStatusFlags.Spooling, 10)];
        StartListener();

        await SendAsync(_pipe, [9]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Equal(1, _sink.Received[0].Job?.Id);
    }

    [Fact]
    public async Task When_the_spooler_lists_no_job_the_sink_gets_null()
    {
        StartListener();

        await SendAsync(_pipe, [9]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Null(_sink.Received[0].Job);
    }

    [Fact]
    public async Task A_spooler_query_failure_is_a_warning_and_the_job_is_still_received()
    {
        _spooler.JobsFault = new SpoolerException("OpenPrinter('Test Printer') failed: boom (Win32 error 1801).", 1801);
        StartListener();

        await SendAsync(_pipe, [9]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Null(_sink.Received[0].Job);
        Assert.Contains(_log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("Could not query print jobs for 'Test Printer': OpenPrinter"));
    }

    [Fact]
    public async Task A_faulting_sink_is_logged_and_the_listener_keeps_serving()
    {
        _sink.FaultFor = attempt => attempt == 1 ? new InvalidOperationException("disk full") : null;
        StartListener();

        await SendAsync(_pipe, [1]);
        await WinWait.UntilAsync(() => Logged("Failed to receive a job for 'Test'"), "failure logged");
        await SendAsync(_pipe, [2, 2]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "second job");

        var failure = Assert.Single(_log.Entries, e => e.Message.StartsWith("Failed to receive a job for 'Test'"));
        Assert.Equal(ErpLogLevel.Error, failure.Level);
        Assert.Contains("[InvalidOperationException] disk full", failure.Message);
        Assert.Equal(new byte[] { 2, 2 }, _sink.Received[0].Bytes);
        Assert.Equal(1, _signal.Count); // the failed job did not wake the uploader
    }

    [Fact]
    public async Task Cancellation_stops_the_listener_cleanly_and_releases_the_pipe()
    {
        StartListener();
        await WinWait.UntilAsync(() => PipeVisibility.Exists(_pipe) == true, "pipe visible");

        await _cts.CancelAsync();
        await _run!.WaitAsync(TimeSpan.FromSeconds(10)); // completes normally

        Assert.True(_run.IsCompletedSuccessfully);
        Assert.Equal("Listener stopped.", _log.Entries[^1].Message);
        Assert.False(PipeVisibility.Exists(_pipe));
    }

    [Fact]
    public async Task Cancelling_during_a_job_cancels_the_sink()
    {
        var sink = new BlockingSink();
        var listener = new PipeListener(() => _current, _spooler, sink, _signal, _log, options: new PipeListenerOptions { ReloadInterval = TimeSpan.FromSeconds(30) });
        _run = listener.RunAsync(_cts.Token);

        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
        await client.ConnectAsync(10000);
        await WinWait.UntilAsync(() => sink.Started, "sink started");
        await _cts.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(sink.Cancelled);
        Assert.Equal(0, _signal.Count);
    }

    [Fact]
    public async Task Reconciliation_runs_at_startup_and_pipes_follow_profile_changes()
    {
        var secondPipe = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
        var reconciled = 0;
        StartListener(reconcile: _ =>
        {
            Interlocked.Increment(ref reconciled);
            return Task.CompletedTask;
        });
        await WinWait.UntilAsync(() => reconciled == 1, "startup reconcile");
        await WinWait.UntilAsync(() => PipeVisibility.Exists(_pipe) == true, "first pipe");

        _current = Config(Profile("Second", secondPipe, "Second Printer"));
        await WinWait.UntilAsync(() => reconciled == 2, "reconcile after change");
        await WinWait.UntilAsync(() => PipeVisibility.Exists(secondPipe) == true, "second pipe");
        await WinWait.UntilAsync(() => PipeVisibility.Exists(_pipe) == false, "first pipe removed");

        await SendAsync(secondPipe, [5, 5]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job on the new pipe");
        Assert.Equal("Second", _sink.Received[0].Profile.Name);
        Assert.Contains(_log.Entries, e => e.Message == $"Listening on 1 pipe(s): {secondPipe}");
        Assert.Equal(2, reconciled); // reload ticks without a change do not reconcile again
    }

    [Fact]
    public async Task Unchanged_pipes_keep_running_and_pick_up_new_settings()
    {
        StartListener();
        await WinWait.UntilAsync(() => PipeVisibility.Exists(_pipe) == true, "pipe");

        var changed = PrinterProfile.Create("Test", new Dictionary<string, object> { ["PipeName"] = _pipe, ["PrinterName"] = "Test Printer", ["FileNameTemplate"] = "{user}-{title}" });
        _current = Config(changed);
        await Task.Delay(400); // several reload ticks
        await SendAsync(_pipe, [1]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Equal("{user}-{title}", _sink.Received[0].Profile.FileNameTemplate);
        Assert.Single(_log.Entries, e => e.Message.StartsWith("Listening on"));
    }

    [Fact]
    public async Task A_failing_reconciliation_is_logged_and_the_pipes_are_still_served()
    {
        StartListener(reconcile: _ => throw new InvalidOperationException("no admin"));

        await SendAsync(_pipe, [1]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job");

        Assert.Contains(_log.Entries, e => e.Level == ErpLogLevel.Error && e.Message == "Printer reconciliation failed: no admin");
    }

    [Fact]
    public async Task Without_enabled_profiles_it_warns_and_waits()
    {
        _current = Config(Profile("Off", _pipe, "Test Printer", enabled: false));
        StartListener();

        await WinWait.UntilAsync(() => Logged("No enabled printer profiles"), "warning");

        Assert.Contains(_log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message == "No enabled printer profiles, so nothing is listening. Create or enable a profile in the configuration tool.");
        Assert.Contains(_log.Entries, e => e.Message == "Listening on 0 pipe(s): ");
        Assert.False(PipeVisibility.Exists(_pipe));
    }

    [Fact]
    public async Task The_host_is_told_about_each_loaded_configuration()
    {
        var loaded = 0;
        StartListener(loaded: _ => Interlocked.Increment(ref loaded));

        await WinWait.UntilAsync(() => Volatile.Read(ref loaded) >= 3, "reloads");
    }

    [Fact]
    public async Task A_failing_reload_keeps_the_previous_configuration()
    {
        var calls = 0;
        StartListener(load: () =>
        {
            if (Interlocked.Increment(ref calls) > 2) throw new IOException("registry unavailable");
            return _current;
        });

        await WinWait.UntilAsync(() => Logged("Could not reload the configuration"), "reload failure logged");
        await SendAsync(_pipe, [3]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job still served");
    }

    [Fact]
    public async Task A_heartbeat_reports_pipes_and_jobs()
    {
        StartListener(options: new PipeListenerOptions { ReloadInterval = TimeSpan.FromSeconds(30), HeartbeatInterval = TimeSpan.FromMilliseconds(300) });

        await WinWait.UntilAsync(() => Logged("Listener alive: 1 pipe(s), 0 job(s) received since start, no jobs since start."), "first heartbeat");
        await SendAsync(_pipe, [1]);
        await WinWait.UntilAsync(() => _log.Entries.Any(e => e.Message.StartsWith("Listener alive: 1 pipe(s), 1 job(s) received since start, last job at ")), "heartbeat after a job");
    }

    [Fact]
    public async Task A_crash_is_logged_and_rethrown()
    {
        var listener = new PipeListener(() => throw new InvalidOperationException("registry gone"), _spooler, _sink, _signal, _log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => listener.RunAsync(_cts.Token));

        Assert.Equal("registry gone", ex.Message);
        Assert.Contains(_log.Entries, e => e.Level == ErpLogLevel.Error && e.Message.StartsWith("The listener crashed: [InvalidOperationException] registry gone"));
        Assert.Equal("Listener stopped.", _log.Entries[^1].Message);
    }

    [Fact]
    public async Task A_pipe_that_cannot_be_created_is_retried_and_reported_once()
    {
        var attempts = 0;
        var options = new PipeListenerOptions
        {
            ReloadInterval = TimeSpan.FromSeconds(30),
            PipeRetryDelay = TimeSpan.FromMilliseconds(20),
            PipeFactory = name =>
            {
                if (Interlocked.Increment(ref attempts) <= 3) throw new IOException("All pipe instances are busy.");
                return PipeServerFactory.Create(name);
            },
        };
        StartListener(options: options);

        await WinWait.UntilAsync(() => Logged("recovered after 3 failure(s)"), "recovery");

        Assert.Single(_log.Entries, e => e.Message.StartsWith($"Cannot open pipe '{_pipe}' (failure 1)"));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("(failure 2)"));
        // A failure that heals by itself must not look like an error in the log or in the diagnostics.
        Assert.DoesNotContain(_log.Entries, e => e.Level == ErpPrinter.Logging.ErpLogLevel.Error);
        await SendAsync(_pipe, [1]);
        await WinWait.UntilAsync(() => _sink.Received.Count == 1, "job after recovery");
    }

    [Fact]
    public void PickActiveJob_handles_empty_lists_and_missing_timestamps()
    {
        Assert.Null(PipeListener.PickActiveJob([]));

        var undated = new PrintJobInfo(1, "p", "d", "u", JobStatusFlags.Spooling, 0, 0, 0, null);
        var dated = Job(2, JobStatusFlags.Spooling, 10);
        Assert.Equal(2, PipeListener.PickActiveJob([undated, dated])?.Id);
    }

    private sealed class BlockingSink : IJobSink
    {
        public volatile bool Started;
        public volatile bool Cancelled;

        public async Task<OutboxMeta?> ReceiveAsync(PrinterProfile profile, GlobalSettings global, Stream source, PrintJobInfo? job, CancellationToken cancellationToken)
        {
            Started = true;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            return null;
        }
    }
}

public sealed class PipeSupportTests
{
    [Fact]
    public void PipeVisibility_lists_pipes_without_connecting_to_them()
    {
        var name = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
        Assert.False(PipeVisibility.Exists(name));

        using var server = PipeServerFactory.Create(name);

        Assert.True(PipeVisibility.Exists(name));
        Assert.True(PipeVisibility.Exists(name.ToUpperInvariant())); // pipe names are case-insensitive
        Assert.False(server.IsConnected); // listing did not consume the only instance
    }

    [Fact]
    public async Task The_pipe_server_accepts_a_client_with_the_listener_ACL_and_allows_only_one_instance()
    {
        var name = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
        using var server = PipeServerFactory.Create(name);

        // Max instances = 1: a second instance is refused (Windows reports it as access denied).
        Assert.ThrowsAny<Exception>(() => PipeServerFactory.Create(name).Dispose());

        var waiting = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        await client.ConnectAsync(5000);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await client.WriteAsync(new byte[] { 1, 2, 3 });
        client.Dispose();

        var received = new byte[3];
        Assert.Equal(3, await server.ReadAsync(received));
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }

    [Fact]
    public void Printing_users_get_write_and_delete_on_the_pipe_but_cannot_host_or_reconfigure_it()
    {
        var name = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
        using var server = PipeServerFactory.Create(name);

        var users = server.GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(r => r.IdentityReference.Value == PipeServerFactory.AuthenticatedUsersSid);

        Assert.Equal(AccessControlType.Allow, users.AccessControlType);
        // The spooler opens the port as the printing user with write and DELETE access; without Delete every print from a
        // normal (non-admin) user fails in the spooler and never reaches the listener.
        Assert.True(users.PipeAccessRights.HasFlag(PipeAccessRights.Write));
        Assert.True(users.PipeAccessRights.HasFlag(PipeAccessRights.Delete));
        Assert.False(users.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(users.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(users.PipeAccessRights.HasFlag(PipeAccessRights.TakeOwnership));
    }

    [Fact]
    public void The_outbox_signal_is_a_named_auto_reset_event_shared_by_name()
    {
        var name = "ErpPrinterTest-" + Guid.NewGuid().ToString("N");
        using var listenerSide = new NamedOutboxSignal(name);
        using var uploaderSide = new NamedOutboxSignal(name);

        Assert.False(uploaderSide.Wait(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        listenerSide.Set();

        Assert.True(uploaderSide.Wait(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(uploaderSide.Wait(TimeSpan.FromMilliseconds(50), CancellationToken.None)); // auto-reset
    }

    [Fact]
    public void Waiting_on_the_signal_honours_cancellation()
    {
        using var signal = new NamedOutboxSignal("ErpPrinterTest-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        Assert.Throws<OperationCanceledException>(() => signal.Wait(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public void The_default_signal_name_is_the_one_the_PowerShell_product_uses()
    {
        Assert.Equal(@"Global\ErpPrinterOutbox", NamedOutboxSignal.DefaultName);
    }
}
