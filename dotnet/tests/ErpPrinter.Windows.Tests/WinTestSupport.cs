using System.Security.AccessControl;
using System.Security.Principal;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Logging;
using Microsoft.Win32;

namespace ErpPrinter.Windows.Tests;

/// <summary>
/// A test that needs Administrator rights or changes printers, ports or HKLM. Skipped unless the environment variable
/// <c>ERP_PRINTER_ADMIN_TESTS=1</c> is set (and then it must run in an elevated shell).
/// </summary>
public sealed class WinAdminFactAttribute : FactAttribute
{
    public const string Variable = "ERP_PRINTER_ADMIN_TESTS";

    public WinAdminFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Needs Administrator rights and changes the real spooler; set {Variable}=1 and run elevated to enable.";
    }
}

/// <summary>A clock tests move by hand. Local time is UTC so expected log lines do not depend on the machine's zone.</summary>
public sealed class WinFixedTime(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.Utc;
    public void Set(DateTimeOffset value) => _now = value;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A throwaway registry root under HKCU, deleted on dispose (even if a test locked it down).</summary>
public sealed class WinTempRegistry : IDisposable
{
    private readonly string _path = @"Software\ErpPrinterTests\" + Guid.NewGuid().ToString("N");

    public WinTempRegistry() => Root = Registry.CurrentUser.CreateSubKey(_path);

    public RegistryKey Root { get; }

    public void Dispose()
    {
        Root.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // A test locked a key down (the secrets key): give the user access back, then delete.
            try
            {
                RestoreAccess(_path);
                Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                // Best effort: it is only a test key under HKCU.
            }
        }
    }

    /// <summary>Gives the current user full control of every key below <paramref name="path"/> that a test restricted, so it can be deleted.</summary>
    private static void RestoreAccess(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        using (var key = Registry.CurrentUser.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
        {
            if (key is null) return;
            Grant(key, me);
        }
        using var readable = Registry.CurrentUser.OpenSubKey(path);
        if (readable is null) return;
        foreach (var name in readable.GetSubKeyNames()) RestoreAccess(path + "\\" + name);
    }

    private static void Grant(RegistryKey key, SecurityIdentifier user)
    {
        var security = new RegistrySecurity();
        security.AddAccessRule(new RegistryAccessRule(user, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(security);
    }
}

/// <summary>An event-log sink that remembers what it was given.</summary>
public sealed class WinMemoryEventSink : IEventLogSink
{
    private readonly List<(ErpLogLevel Level, string Message, int EventId)> _events = [];
    public IReadOnlyList<(ErpLogLevel Level, string Message, int EventId)> Events { get { lock (_events) return _events.ToArray(); } }
    public void Write(ErpLogLevel level, string message, int eventId) { lock (_events) _events.Add((level, message, eventId)); }
}

/// <summary>An in-memory spooler that records the changes made to it.</summary>
public sealed class WinFakeSpooler : ISpooler
{
    private readonly object _gate = new();

    public List<PrinterInfo> Printers { get; } = [];
    public List<string> Ports { get; } = ["COM1:", "PORTPROMPT:"];
    public List<string> Drivers { get; } = ["Microsoft Print To PDF"];
    public Dictionary<string, List<PrintJobInfo>> Jobs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    /// <summary>When set, returns an exception to throw for a call such as "AddPrinter:Name".</summary>
    public Func<string, Exception?>? Fault { get; set; }

    public Exception? JobsFault { get; set; }

    private void Record(string call)
    {
        lock (_gate) Calls.Add(call);
        if (Fault?.Invoke(call) is { } ex) throw ex;
    }

    public bool IsSpoolerRunning() => true;
    public IReadOnlyList<PrinterInfo> GetPrinters() { lock (_gate) return Printers.ToList(); }
    public IReadOnlyList<string> GetPorts() { lock (_gate) return Ports.ToList(); }
    public IReadOnlyList<string> GetDrivers() { lock (_gate) return Drivers.ToList(); }

    public IReadOnlyList<PrintJobInfo> GetJobs(string printerName)
    {
        if (JobsFault is not null) throw JobsFault;
        lock (_gate) return Jobs.TryGetValue(printerName, out var jobs) ? jobs.ToList() : [];
    }

    public void AddPort(string portName)
    {
        Record($"AddPort:{portName}");
        lock (_gate) if (!Ports.Contains(portName, StringComparer.OrdinalIgnoreCase)) Ports.Add(portName);
    }

    public void RemovePort(string portName)
    {
        Record($"RemovePort:{portName}");
        lock (_gate)
        {
            if (Printers.Any(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))) return;
            Ports.RemoveAll(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void AddPrinter(string name, string driverName, string portName, string comment)
    {
        Record($"AddPrinter:{name}");
        lock (_gate) Printers.Add(new PrinterInfo(name, driverName, portName, comment, PrinterStatusFlags.None, false));
    }

    public void UpdatePrinter(string name, string driverName, string portName, string comment)
    {
        Record($"UpdatePrinter:{name}");
        lock (_gate)
        {
            var index = Printers.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            Printers[index] = Printers[index] with { DriverName = driverName, PortName = portName, Comment = comment };
        }
    }

    public void RemovePrinter(string name)
    {
        Record($"RemovePrinter:{name}");
        lock (_gate) Printers.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteJob(string printerName, int jobId) => Record($"DeleteJob:{printerName}:{jobId}");
}

public static class WinWait
{
    public static async Task UntilAsync(Func<bool> condition, string description, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(20);
        }
    }
}

/// <summary>An outbox signal that counts the times it was set.</summary>
public sealed class WinCountingSignal : IOutboxSignal
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Set() => Interlocked.Increment(ref _count);
    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
}
