using ErpPrinter.Outbox;

namespace ErpPrinter.Windows.Hosting;

/// <summary>
/// Only one uploader may run per machine. A named mutex is the natural lock, but a mutex belongs to the thread that took
/// it and the uploader loop is async, so a dedicated thread owns the mutex until the returned handle is disposed.
/// </summary>
public sealed class NamedMutexLock(string name = @"Global\ErpPrinterUploader") : ISingleInstanceLock
{
    public IDisposable? TryAcquire()
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // the previous owner crashed; we own it now
            acquired.SetResult(owned);
            if (!owned) return;
            release.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true, Name = "ErpPrinter uploader lock" };
        thread.Start();
        return acquired.Task.GetAwaiter().GetResult() ? new Handle(release, thread) : null;
    }

    private sealed class Handle(ManualResetEventSlim release, Thread thread) : IDisposable
    {
        public void Dispose()
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
            release.Dispose();
        }
    }
}
