using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Storage;

namespace ErpPrinter.Windows.Printing;

/// <summary>
/// <see cref="ISpooler"/> over winspool.drv. Listing (printers, ports, drivers, jobs, spooler state) works without elevation;
/// adding and removing printers and ports needs Administrator rights. Failures are <see cref="SpoolerException"/>s that carry the Win32 error.
/// </summary>
public sealed unsafe class WinspoolSpooler : ISpooler
{
    public const string SpoolerServiceName = "Spooler";
    private const string PortsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Ports";
    private const string LocalPortMonitor = ",XcvMonitor Local Port";

    private readonly IErpLog _log;
    private readonly IRegistryStore _registry;
    private readonly TimeProvider _time;

    /// <param name="log">Receives the warnings of the registry fallbacks for ports.</param>
    /// <param name="registry">HKLM-relative registry, for the Ports-key fallback; defaults to the real one.</param>
    public WinspoolSpooler(IErpLog? log = null, IRegistryStore? registry = null, TimeProvider? time = null)
    {
        _log = log ?? NullLog.Instance;
        _registry = registry ?? new RegistryStore();
        _time = time ?? TimeProvider.System;
    }

    // ---- errors and handles ----------------------------------------------------------------------------------------------

    private static SpoolerException Fail(string operation, int? error = null)
    {
        var code = error ?? Marshal.GetLastPInvokeError();
        return new SpoolerException($"{operation} failed: {new Win32Exception(code).Message} (Win32 error {code}).", code);
    }

    private sealed class PrinterHandle(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() => Winspool.ClosePrinter(Handle);
    }

    private static PrinterHandle Open(string? name, uint access = 0)
    {
        nint handle;
        bool ok;
        if (access == 0)
        {
            ok = Winspool.OpenPrinter(name, out handle, null);
        }
        else
        {
            var defaults = new PrinterDefaults { DesiredAccess = access };
            ok = Winspool.OpenPrinter(name, out handle, &defaults);
        }
        return ok ? new PrinterHandle(handle) : throw Fail($"OpenPrinter('{name}')");
    }

    private static string Str(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUni(pointer) ?? "";

    // ---- enumeration -----------------------------------------------------------------------------------------------------

    private delegate bool EnumCall(byte* buffer, uint size, out uint needed, out uint returned);

    /// <summary>Owns the buffer filled by an Enum* call; the structs and strings inside are valid until it is disposed.</summary>
    private sealed class EnumResult(nint buffer, uint count) : IDisposable
    {
        public nint Buffer { get; } = buffer;
        public uint Count { get; } = count;
        public void Dispose()
        {
            if (Buffer != 0) Marshal.FreeHGlobal(Buffer);
        }
    }

    private static EnumResult Enumerate(string operation, EnumCall call)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = size == 0 ? 0 : Marshal.AllocHGlobal((nint)size);
            var handedOver = false;
            try
            {
                if (call((byte*)buffer, size, out var needed, out var returned))
                {
                    handedOver = true;
                    return new EnumResult(buffer, returned);
                }
                var error = Marshal.GetLastPInvokeError();
                // The list can grow between the sizing call and the real call, hence a few attempts.
                if (error == Winspool.ERROR_INSUFFICIENT_BUFFER && needed > size)
                {
                    size = needed;
                    continue;
                }
                throw Fail(operation, error);
            }
            finally
            {
                if (!handedOver && buffer != 0) Marshal.FreeHGlobal(buffer);
            }
        }
        throw new SpoolerException($"{operation} failed: the list kept changing while it was read.");
    }

    private static T Item<T>(EnumResult result, int index) where T : unmanaged =>
        *(T*)(result.Buffer + (nint)(index * sizeof(T)));

    // ---- queries ---------------------------------------------------------------------------------------------------------

    public bool IsSpoolerRunning()
    {
        try
        {
            using var service = new ServiceController(SpoolerServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<PrinterInfo> GetPrinters()
    {
        using var result = Enumerate("EnumPrinters", (byte* b, uint s, out uint n, out uint c) =>
            Winspool.EnumPrinters(Winspool.PRINTER_ENUM_LOCAL | Winspool.PRINTER_ENUM_CONNECTIONS, null, 2, b, s, out n, out c));
        var offset = TimeZoneInfo.Local.GetUtcOffset(_time.GetUtcNow());
        var printers = new List<PrinterInfo>((int)result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            var p = Item<PrinterInfo2>(result, i);
            int? from = null, until = null;
            if (p.StartTime != p.UntilTime)
            {
                // The spooler stores the window in minutes after midnight UTC; people think in local time.
                from = ToLocalMinutes(p.StartTime, offset);
                until = ToLocalMinutes(p.UntilTime, offset);
            }
            printers.Add(new PrinterInfo(
                Str(p.pPrinterName), Str(p.pDriverName), Str(p.pPortName), Str(p.pComment),
                (PrinterStatusFlags)p.Status, (p.Attributes & Winspool.PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0,
                NullIfEmpty(Str(p.pDatatype)), NullIfEmpty(Str(p.pPrintProcessor)), from, until));
        }
        return printers;
    }

    private static int ToLocalMinutes(uint utcMinutes, TimeSpan offset) =>
        (int)(((long)utcMinutes + (long)offset.TotalMinutes) % 1440 + 1440) % 1440;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public IReadOnlyList<string> GetPorts()
    {
        using var result = Enumerate("EnumPorts", (byte* b, uint s, out uint n, out uint c) => Winspool.EnumPorts(null, 1, b, s, out n, out c));
        var names = new List<string>((int)result.Count);
        for (var i = 0; i < result.Count; i++) names.Add(Str(Item<PortInfo1>(result, i).pName));
        return names;
    }

    public IReadOnlyList<string> GetDrivers()
    {
        using var result = Enumerate("EnumPrinterDrivers", (byte* b, uint s, out uint n, out uint c) =>
            Winspool.EnumPrinterDrivers(null, null, 1, b, s, out n, out c));
        var names = new List<string>((int)result.Count);
        for (var i = 0; i < result.Count; i++) names.Add(Str(Item<DriverInfo1>(result, i).pName));
        // The same driver can be listed once per environment.
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<PrintJobInfo> GetJobs(string printerName)
    {
        using var printer = Open(printerName);
        using var result = Enumerate("EnumJobs", (byte* b, uint s, out uint n, out uint c) =>
            Winspool.EnumJobs(printer.Handle, 0, uint.MaxValue, 2, b, s, out n, out c));
        var jobs = new List<PrintJobInfo>((int)result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            var j = Item<JobInfo2>(result, i);
            jobs.Add(new PrintJobInfo(
                (int)j.JobId, printerName, Str(j.pDocument), Str(j.pUserName), (JobStatusFlags)j.Status,
                (int)j.TotalPages, (int)j.PagesPrinted, j.Size, ToDateTimeOffset(j.Submitted),
                NullIfEmpty(Str(j.pDatatype)), NullIfEmpty(Str(j.pPrintProcessor))));
        }
        return jobs;
    }

    /// <summary>The spooler stamps jobs in UTC.</summary>
    private static DateTimeOffset? ToDateTimeOffset(SystemTime t)
    {
        if (t.Year == 0) return null;
        try
        {
            return new DateTimeOffset(new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // ---- printers --------------------------------------------------------------------------------------------------------

    public void AddPrinter(string name, string driverName, string portName, string comment)
    {
        var strings = new List<nint>();
        try
        {
            var info = new PrinterInfo2
            {
                pPrinterName = Alloc(strings, name),
                pPortName = Alloc(strings, portName),
                pDriverName = Alloc(strings, driverName),
                pComment = Alloc(strings, comment),
                pPrintProcessor = Alloc(strings, "winprint"),
                pDatatype = Alloc(strings, "RAW"),
                Attributes = Winspool.PRINTER_ATTRIBUTE_LOCAL,
            };
            var handle = Winspool.AddPrinter(null, 2, (byte*)&info);
            if (handle == 0) throw Fail($"AddPrinter('{name}')");
            Winspool.ClosePrinter(handle);
        }
        finally { Free(strings); }
    }

    public void UpdatePrinter(string name, string driverName, string portName, string comment)
    {
        using var printer = Open(name, Winspool.PRINTER_ALL_ACCESS);
        var strings = new List<nint>();
        nint buffer = 0;
        try
        {
            uint size = 0;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
                buffer = size == 0 ? 0 : Marshal.AllocHGlobal((nint)size);
                if (Winspool.GetPrinter(printer.Handle, 2, (byte*)buffer, size, out var needed)) break;
                var error = Marshal.GetLastPInvokeError();
                if (error != Winspool.ERROR_INSUFFICIENT_BUFFER || needed <= size) throw Fail($"GetPrinter('{name}')", error);
                size = needed;
                if (attempt == 5) throw new SpoolerException($"GetPrinter('{name}') failed: the printer kept changing while it was read.");
            }

            var info = *(PrinterInfo2*)buffer;
            // A DEVMODE belongs to a driver; keeping the old one with a different driver makes SetPrinter fail.
            if (!string.Equals(Str(info.pDriverName), driverName, StringComparison.OrdinalIgnoreCase)) info.pDevMode = 0;
            info.pDriverName = Alloc(strings, driverName);
            info.pPortName = Alloc(strings, portName);
            info.pComment = Alloc(strings, comment);
            info.pSecurityDescriptor = 0;
            if (!Winspool.SetPrinter(printer.Handle, 2, (byte*)&info, 0)) throw Fail($"SetPrinter('{name}')");
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            Free(strings);
        }
    }

    public void RemovePrinter(string name)
    {
        using var printer = Open(name, Winspool.PRINTER_ALL_ACCESS);
        if (!Winspool.DeletePrinter(printer.Handle)) throw Fail($"DeletePrinter('{name}')");
    }

    public void DeleteJob(string printerName, int jobId)
    {
        // Deleting your own job needs only use access; other users' jobs need administrator access.
        Exception? denied = null;
        foreach (var access in new[] { Winspool.PRINTER_ACCESS_USE, Winspool.PRINTER_ALL_ACCESS })
        {
            using var printer = Open(printerName, access);
            if (Winspool.SetJob(printer.Handle, (uint)jobId, 0, null, Winspool.JOB_CONTROL_DELETE)) return;
            var error = Marshal.GetLastPInvokeError();
            var failure = Fail($"Deleting job {jobId} on '{printerName}'", error);
            if (error != Winspool.ERROR_ACCESS_DENIED) throw failure;
            denied = failure;
        }
        throw denied!;
    }

    private static nint Alloc(List<nint> strings, string value)
    {
        var pointer = Marshal.StringToHGlobalUni(value);
        strings.Add(pointer);
        return pointer;
    }

    private static void Free(List<nint> strings)
    {
        foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        strings.Clear();
    }

    // ---- ports -----------------------------------------------------------------------------------------------------------

    public void AddPort(string portName)
    {
        if (GetPorts().Contains(portName, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            Xcv("AddPort", portName);
        }
        catch (SpoolerException ex)
        {
            // Some builds refuse pipe names through the API; the Local Port monitor reads this key on start.
            _log.Warning($"AddPort refused '{portName}' ({ex.Message}); registering it in the Ports key and restarting the spooler.");
            _registry.SetValue(PortsKey, portName, "", RegistryValueType.String);
            RestartSpooler();
            Thread.Sleep(TimeSpan.FromSeconds(3)); // the Local Port monitor reads the Ports key when the spooler starts; give it a moment
            if (!GetPorts().Contains(portName, StringComparer.OrdinalIgnoreCase))
                throw new SpoolerException($"Could not create printer port '{portName}'.", ex.Win32Error, ex);
        }
    }

    /// <summary>Removes a port unless a printer still uses it. Falls back to the registry if the monitor refuses.</summary>
    public void RemovePort(string portName)
    {
        if (GetPrinters().Any(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))) return;
        if (!GetPorts().Contains(portName, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            Xcv("DeletePort", portName);
        }
        catch (SpoolerException)
        {
            _registry.RemoveValue(PortsKey, portName);
            _log.Warning($"Removed port '{portName}' from the registry; it disappears after the next spooler restart.");
        }
    }

    /// <summary>Sends AddPort/DeletePort to the Local Port monitor (the same call Add-PrinterPort makes).</summary>
    private static void Xcv(string command, string portName)
    {
        using var monitor = Open(LocalPortMonitor, Winspool.SERVER_ACCESS_ADMINISTER);
        var input = System.Text.Encoding.Unicode.GetBytes(portName + "\0");
        var output = new byte[16];
        bool ok;
        uint status;
        fixed (byte* pIn = input)
        fixed (byte* pOut = output)
        {
            ok = Winspool.XcvData(monitor.Handle, command, pIn, (uint)input.Length, pOut, (uint)output.Length, out _, out status);
        }
        if (!ok) throw Fail($"XcvData({command}, '{portName}')");
        if (status != 0) throw Fail($"{command} '{portName}'", unchecked((int)status));
    }

    // ---- spooler service ---------------------------------------------------------------------------------------------------

    /// <summary>Restarts the spooler and the services that depend on it (what <c>Restart-Service -Force</c> did).</summary>
    public void RestartSpooler()
    {
        try
        {
            using var spooler = new ServiceController(SpoolerServiceName);
            var dependents = spooler.DependentServices.Where(s => s.Status == ServiceControllerStatus.Running).Select(s => s.ServiceName).ToList();
            var timeout = TimeSpan.FromSeconds(60);
            if (spooler.Status != ServiceControllerStatus.Stopped)
            {
                foreach (var dependent in spooler.DependentServices.Where(s => s.Status == ServiceControllerStatus.Running))
                {
                    dependent.Stop();
                    dependent.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                }
                spooler.Stop();
                spooler.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            spooler.Start();
            spooler.WaitForStatus(ServiceControllerStatus.Running, timeout);
            foreach (var name in dependents)
            {
                using var dependent = new ServiceController(name);
                try
                {
                    dependent.Start();
                    dependent.WaitForStatus(ServiceControllerStatus.Running, timeout);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
                {
                    _log.Warning($"Could not restart service '{name}' after the spooler restart: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            throw new SpoolerException($"Could not restart the print spooler: {ex.Message}", (ex.InnerException as Win32Exception)?.NativeErrorCode ?? 0, ex);
        }
    }
}
