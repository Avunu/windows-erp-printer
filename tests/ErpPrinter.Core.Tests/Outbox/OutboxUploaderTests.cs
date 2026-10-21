using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

/// <summary>Housekeeping and the uploader loop (Invoke-ErpHousekeeping, Start-ErpPrinterUploader).</summary>
public sealed class OutboxUploaderTests : IDisposable
{
    private readonly OutboxFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static void Age(string path, DateTimeOffset reference, TimeSpan age) => File.SetLastWriteTimeUtc(path, reference.UtcDateTime - age);

    // --- Housekeeping ------------------------------------------------------------------------

    [Fact]
    public void Housekeeping_deletes_old_logs_archived_documents_and_update_files()
    {
        _f.Configure(new() { ["LogRetentionDays"] = 14, ["KeepSentDays"] = 7 });
        var logs = Directory.CreateDirectory(Path.Combine(_f.Root, "logs")).FullName;
        var updates = Directory.CreateDirectory(Path.Combine(_f.Root, "updates")).FullName;
        string Make(string folder, string name, TimeSpan age)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            Age(path, _f.Clock.Now, age);
            return path;
        }

        var oldLog = Make(logs, "listener-20260101.log", TimeSpan.FromDays(20));
        var newLog = Make(logs, "listener-20260228.log", TimeSpan.FromDays(2));
        var otherFile = Make(logs, "notes.txt", TimeSpan.FromDays(60));
        var oldSent = Make(_f.Paths.Sent, "a.pdf", TimeSpan.FromDays(8));
        var newSent = Make(_f.Paths.Sent, "b.pdf", TimeSpan.FromDays(6));
        var oldUpdate = Make(updates, "ErpPrinter-1.0.0.msi", TimeSpan.FromDays(31));
        var newUpdate = Make(updates, "ErpPrinter-2.0.0.msi", TimeSpan.FromDays(29));
        var failed = Make(_f.Paths.Failed, "keep.pdf", TimeSpan.FromDays(400));

        var deleted = new OutboxHousekeeping(_f.Log, _f.Clock).Run(_f.Config);

        Assert.Equal(3, deleted);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(otherFile));
        Assert.False(File.Exists(oldSent));
        Assert.True(File.Exists(newSent));
        Assert.False(File.Exists(oldUpdate));
        Assert.True(File.Exists(newUpdate));
        Assert.True(File.Exists(failed));
    }

    [Fact]
    public void Housekeeping_clears_the_sent_folder_when_archiving_is_off()
    {
        var path = Path.Combine(_f.Paths.Sent, "a.pdf");
        File.WriteAllText(path, "x");
        Age(path, _f.Clock.Now, TimeSpan.FromMinutes(1));
        Assert.Equal(0, _f.Global.KeepSentDays);
        new OutboxHousekeeping(_f.Log, _f.Clock).Run(_f.Config);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Housekeeping_tolerates_missing_folders()
    {
        var empty = new OutboxFixture();
        try
        {
            Directory.Delete(empty.Root, recursive: true);
            Assert.Equal(0, new OutboxHousekeeping(empty.Log, empty.Clock).Run(empty.Config));
        }
        finally { empty.Dispose(); }
    }

    // --- Uploader loop -------------------------------------------------------------------------

    private sealed class ScriptedSignal(Func<int, bool> onWait) : IOutboxSignal
    {
        public List<TimeSpan> Timeouts { get; } = [];
        public void Set() { }

        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Timeouts.Add(timeout);
            var signalled = onWait(Timeouts.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return signalled;
        }
    }

    private sealed class FakeLock(bool available) : ISingleInstanceLock
    {
        public bool Released { get; private set; }
        public int Attempts { get; private set; }

        public IDisposable? TryAcquire()
        {
            Attempts++;
            return available ? new Handle(this) : null;
        }

        private sealed class Handle(FakeLock owner) : IDisposable
        {
            public void Dispose() => owner.Released = true;
        }
    }

    private UploaderLoop MakeLoop(IOutboxSignal signal, Func<ErpConfig> load, ISingleInstanceLock? instanceLock = null, Func<GlobalSettings, string>? summary = null) =>
        new(_f.Log, _f.Processor, new OutboxHousekeeping(_f.Log, _f.Clock), signal, load, _f.Clock, instanceLock, summary);

    [Fact]
    public async Task Runs_passes_until_cancelled_and_logs_start_and_stop()
    {
        using var cts = new CancellationTokenSource();
        var signal = new ScriptedSignal(n => { if (n == 3) cts.Cancel(); return false; });
        var loads = 0;
        var loop = MakeLoop(signal, () => { loads++; return _f.Config; }, summary: g => $"Runtime summary (poll {g.PollIntervalSeconds}s)");
        _f.AddItem();

        await loop.RunAsync(cts.Token);

        Assert.Single(_f.Backend.Calls);
        Assert.Equal(3, signal.Timeouts.Count);
        Assert.All(signal.Timeouts, t => Assert.Equal(TimeSpan.FromSeconds(15), t));
        Assert.Equal(4, loads); // once for the start message, then before each of the three passes
        var messages = _f.Log.Entries.Select(e => e.Message).ToList();
        Assert.StartsWith("Uploader started (version ", messages[0]);
        Assert.Equal("Runtime summary (poll 15s)", messages[1]);
        Assert.Equal("Uploader stopped.", messages[^1]);
    }

    [Fact]
    public async Task Delivers_a_job_queued_while_waiting_when_the_signal_fires()
    {
        using var cts = new CancellationTokenSource();
        var signal = new ScriptedSignal(n =>
        {
            if (n == 1) _f.AddItem();
            if (n == 2) cts.Cancel();
            return n == 1;
        });
        await MakeLoop(signal, () => _f.Config).RunAsync(cts.Token);
        Assert.Single(_f.Backend.Calls);
        Assert.Equal(0, OutboxFixture.Count(_f.Paths.Outbox));
    }

    [Fact]
    public async Task Rereads_the_settings_on_every_pass()
    {
        using var cts = new CancellationTokenSource();
        var configs = new Queue<ErpConfig>([_f.Config, _f.Config, new ErpConfig(_f.MakeGlobal(new() { ["PollIntervalSeconds"] = 99 }), [_f.Profile])]);
        var signal = new ScriptedSignal(n => { if (n == 2) cts.Cancel(); return false; });
        await MakeLoop(signal, () => configs.Count > 1 ? configs.Dequeue() : configs.Peek()).RunAsync(cts.Token);
        Assert.Equal([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(99)], signal.Timeouts);
    }

    [Fact]
    public async Task A_failing_pass_is_logged_and_the_loop_carries_on()
    {
        using var cts = new CancellationTokenSource();
        var loads = 0;
        var signal = new ScriptedSignal(n => { if (n == 2) cts.Cancel(); return false; });
        var loop = MakeLoop(signal, () => ++loads == 2 ? throw new InvalidOperationException("registry unavailable") : _f.Config);
        await loop.RunAsync(cts.Token);
        Assert.Equal(2, signal.Timeouts.Count);
        Assert.Contains(_f.Log.Entries, e => e.Level == ErpLogLevel.Error && e.Message.StartsWith("Upload pass failed: [InvalidOperationException] registry unavailable"));
        Assert.Equal("Uploader stopped.", _f.Log.Entries[^1].Message);
    }

    [Fact]
    public async Task Housekeeping_runs_on_the_first_pass_and_then_hourly()
    {
        using var cts = new CancellationTokenSource();
        var logs = Directory.CreateDirectory(Path.Combine(_f.Root, "logs")).FullName;
        string StaleLog(string name)
        {
            var path = Path.Combine(logs, name);
            File.WriteAllText(path, "x");
            Age(path, _f.Clock.Now, TimeSpan.FromDays(30));
            return path;
        }

        var first = StaleLog("first.log");
        string? second = null, third = null;
        var signal = new ScriptedSignal(n =>
        {
            switch (n)
            {
                case 1: // pass 1 ran housekeeping; less than an hour later nothing happens
                    Assert.False(File.Exists(first));
                    second = StaleLog("second.log");
                    _f.Clock.Advance(TimeSpan.FromMinutes(30));
                    break;
                case 2: // pass 2 skipped it; an hour later it runs again
                    Assert.True(File.Exists(second));
                    _f.Clock.Advance(TimeSpan.FromMinutes(31));
                    break;
                case 3:
                    third = StaleLog("third.log");
                    cts.Cancel();
                    break;
            }
            return false;
        });

        await MakeLoop(signal, () => _f.Config).RunAsync(cts.Token);

        Assert.False(File.Exists(second));
        Assert.True(File.Exists(third)); // the loop was cancelled before the next housekeeping run
    }

    [Fact]
    public async Task Exits_with_a_warning_when_another_uploader_is_running()
    {
        var instanceLock = new FakeLock(available: false);
        var loads = 0;
        var signal = new ScriptedSignal(_ => false);
        await MakeLoop(signal, () => { loads++; return _f.Config; }, instanceLock).RunAsync(CancellationToken.None);

        Assert.Equal(0, loads);
        Assert.Empty(signal.Timeouts);
        var entry = Assert.Single(_f.Log.Entries);
        Assert.Equal(ErpLogLevel.Warning, entry.Level);
        Assert.Equal($"Another uploader is already running; this one (PID {Environment.ProcessId}) is exiting.", entry.Message);
    }

    [Fact]
    public async Task Releases_the_single_instance_lock_when_it_stops()
    {
        using var cts = new CancellationTokenSource();
        var instanceLock = new FakeLock(available: true);
        var signal = new ScriptedSignal(_ => { cts.Cancel(); return false; });
        await MakeLoop(signal, () => _f.Config, instanceLock).RunAsync(cts.Token);
        Assert.Equal(1, instanceLock.Attempts);
        Assert.True(instanceLock.Released);
    }

    [Fact]
    public async Task Stops_promptly_when_cancelled_while_waiting_on_the_real_signal()
    {
        using var cts = new CancellationTokenSource();
        var loop = MakeLoop(new InProcessOutboxSignal(), () => _f.Config);
        var run = loop.RunAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Uploader stopped.", _f.Log.Entries[^1].Message);
    }
}
