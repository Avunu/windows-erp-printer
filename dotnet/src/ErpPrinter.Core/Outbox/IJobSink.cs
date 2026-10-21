using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Outbox;

/// <summary>
/// Where the pipe listener puts a received print job. The implementation streams the PDF into the profile's outbox
/// (<c>.tmp</c> then rename), checks it, writes the sidecar last and logs what happened; see <c>OutboxJobSink</c> in Core.
/// </summary>
public interface IJobSink
{
    /// <param name="profile">The profile whose printer received the job.</param>
    /// <param name="source">The pipe; read to the end (one connection is exactly one job).</param>
    /// <param name="job">The spooler's record of the job (user, title, id, pages), or null when the spooler lists none.</param>
    /// <returns>The queued sidecar, or null when the job was discarded (for example, it was empty).</returns>
    Task<OutboxMeta?> ReceiveAsync(PrinterProfile profile, GlobalSettings global, Stream source, PrintJobInfo? job, CancellationToken cancellationToken);
}

/// <summary>Wakes the uploader as soon as the listener has queued a job. The Windows implementation is the named event <c>Global\ErpPrinterOutbox</c>.</summary>
public interface IOutboxSignal
{
    void Set();

    /// <summary>Waits until <see cref="Set"/> is called, the timeout elapses (returns false) or cancellation is requested (throws).</summary>
    bool Wait(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>An in-process signal for tests and for hosting listener and uploader in one process.</summary>
public sealed class InProcessOutboxSignal : IOutboxSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) => _semaphore.Wait(timeout, cancellationToken);
}
