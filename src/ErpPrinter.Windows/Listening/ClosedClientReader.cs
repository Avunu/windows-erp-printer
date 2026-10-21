using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ErpPrinter.Windows.Listening;

/// <summary>
/// Reads what a client left in a pipe instance it already closed. When a client connects, writes and closes before the server
/// starts waiting (a job small enough to fit in the pipe buffer, arriving while the instance is being set up), ConnectNamedPipe
/// fails with ERROR_NO_DATA although the data is still in the pipe. <see cref="NamedPipeServerStream"/> then refuses to read,
/// and its handle is already bound to the thread pool, so this reads with plain overlapped ReadFile calls whose event handle has
/// the low bit set: that keeps the completions off the thread pool's I/O completion port.
/// </summary>
internal static partial class ClosedClientReader
{
    /// <summary>HRESULT of ERROR_NO_DATA ("The pipe is being closed"), as thrown by WaitForConnection.</summary>
    public const int ErrorNoDataHResult = unchecked((int)0x800700E8);

    private const int ErrorIoPending = 997;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorHandleEof = 38;

    /// <summary>Everything the client wrote, up to the end of the pipe. Blocks only on data that is already in the pipe.</summary>
    public static unsafe MemoryStream ReadAll(NamedPipeServerStream pipe)
    {
        var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        using var done = new ManualResetEvent(false);
        fixed (byte* data = buffer)
        {
            while (true)
            {
                done.Reset();
                var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() | 1 };
                uint read;
                if (!ReadFile(pipe.SafePipeHandle, data, (uint)buffer.Length, null, &overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is ErrorBrokenPipe or ErrorHandleEof) break;
                    if (error != ErrorIoPending) throw new IOException($"Reading the closed client's data failed (Win32 error {error}).", error);
                }
                if (!GetOverlappedResult(pipe.SafePipeHandle, &overlapped, &read, true))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is ErrorBrokenPipe or ErrorHandleEof) break;
                    throw new IOException($"Reading the closed client's data failed (Win32 error {error}).", error);
                }
                if (read == 0) break;
                result.Write(buffer, 0, (int)read);
            }
        }
        result.Position = 0;
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(SafePipeHandle handle, byte* buffer, uint count, uint* read, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(SafePipeHandle handle, NativeOverlapped* overlapped, uint* transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
