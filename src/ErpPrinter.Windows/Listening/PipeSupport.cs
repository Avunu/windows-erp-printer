using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ErpPrinter.Outbox;
using ErpPrinter.Settings;

namespace ErpPrinter.Windows.Listening;

/// <summary>Creates the pipe instances the listener hosts (<c>New-ErpPipeServer</c>).</summary>
public static class PipeServerFactory
{
    /// <summary>
    /// An inbound, asynchronous, single-instance byte pipe with 64 KiB buffers. The spooler may open the port as SYSTEM or while
    /// impersonating the printing user, so SYSTEM and Administrators get full control and authenticated users read/write access.
    /// Accounts are given by SID so this works on localized Windows.
    /// </summary>
    public static NamedPipeServerStream Create(string pipeName)
    {
        var security = new PipeSecurity();
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier("S-1-5-11"), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
    }
}

public static class PipeVisibility
{
    private const string PipeRoot = @"\\.\pipe\";

    /// <summary>
    /// Whether a named pipe is currently hosted by some process. Lists <c>\\.\pipe\</c> rather than opening the pipe: connecting
    /// would consume the listener's only instance. Returns null when the list cannot be read.
    /// </summary>
    public static bool? Exists(string pipeName)
    {
        try
        {
            var wanted = PipeRoot + pipeName;
            return Directory.EnumerateFiles(PipeRoot).Any(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The named event the listener sets after each job so the uploader wakes immediately (<c>Global\ErpPrinterOutbox</c>, auto-reset,
/// created if missing). Both ends open it by name, so they can live in different processes.
/// </summary>
public sealed class NamedOutboxSignal : IOutboxSignal, IDisposable
{
    public const string DefaultName = @"Global\ErpPrinterOutbox";

    private readonly EventWaitHandle _event;

    public NamedOutboxSignal(string name = DefaultName)
    {
        Name = name;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name, out _);
    }

    public string Name { get; }

    public void Set() => _event.Set();

    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var index = WaitHandle.WaitAny([_event, cancellationToken.WaitHandle], timeout);
        if (index == WaitHandle.WaitTimeout) return false;
        if (index == 1) throw new OperationCanceledException(cancellationToken);
        return true;
    }

    public void Dispose() => _event.Dispose();
}

public static class RuntimeSummary
{
    /// <summary>One log line describing the process: who it runs as, which runtime, where data and logs go.</summary>
    public static string Describe(GlobalSettings? global)
    {
        string identity;
        try { identity = WindowsIdentity.GetCurrent().Name; }
        catch (Exception) { identity = "unknown"; }
        return $"Runtime: user {identity}, {RuntimeInformation.FrameworkDescription}, OS {Environment.OSVersion.Version}, " +
               $"data folder '{global?.DataRoot}', log level {global?.LogLevel}.";
    }
}
