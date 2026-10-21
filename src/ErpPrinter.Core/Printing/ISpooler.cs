namespace ErpPrinter.Printing;

/// <summary>PRINTER_STATUS_* bits from winspool.h.</summary>
[Flags]
public enum PrinterStatusFlags : uint
{
    None = 0,
    Paused = 0x1,
    Error = 0x2,
    PendingDeletion = 0x4,
    PaperJam = 0x8,
    PaperOut = 0x10,
    ManualFeed = 0x20,
    PaperProblem = 0x40,
    Offline = 0x80,
    IoActive = 0x100,
    Busy = 0x200,
    Printing = 0x400,
    OutputBinFull = 0x800,
    NotAvailable = 0x1000,
    Waiting = 0x2000,
    Processing = 0x4000,
    Initializing = 0x8000,
    WarmingUp = 0x10000,
    TonerLow = 0x20000,
    NoToner = 0x40000,
    PagePunt = 0x80000,
    UserIntervention = 0x100000,
    OutOfMemory = 0x200000,
    DoorOpen = 0x400000,
    ServerUnknown = 0x800000,
    PowerSave = 0x1000000,
}

/// <summary>JOB_STATUS_* bits from winspool.h. A failed XPS job seen in the field was 0x2012: Error | Printing | Retained.</summary>
[Flags]
public enum JobStatusFlags : uint
{
    None = 0,
    Paused = 0x1,
    Error = 0x2,
    Deleting = 0x4,
    Spooling = 0x8,
    Printing = 0x10,
    Offline = 0x20,
    PaperOut = 0x40,
    Printed = 0x80,
    Deleted = 0x100,
    BlockedDeviceQueue = 0x200,
    UserIntervention = 0x400,
    Restart = 0x800,
    Complete = 0x1000,
    Retained = 0x2000,
    RenderingLocally = 0x4000,
}

public sealed record PrinterInfo(
    string Name,
    string DriverName,
    string PortName,
    string Comment,
    PrinterStatusFlags Status,
    /// <summary>"Use Printer Offline" (PRINTER_ATTRIBUTE_WORK_OFFLINE): the spooler holds every job.</summary>
    bool WorkOffline,
    string? Datatype = null,
    string? PrintProcessor = null,
    /// <summary>Availability window (minutes after midnight), or null when the printer is always available.</summary>
    int? AvailableFromMinutes = null,
    int? AvailableUntilMinutes = null);

public sealed record PrintJobInfo(
    int Id,
    string PrinterName,
    string DocumentName,
    string UserName,
    JobStatusFlags Status,
    int TotalPages,
    int PagesPrinted,
    long SizeBytes,
    DateTimeOffset? Submitted,
    string? Datatype = null,
    string? PrintProcessor = null)
{
    /// <summary>Space-separated flag names for logs and reports ("Error, Printing, Retained").</summary>
    public string StatusText => Status == JobStatusFlags.None ? "Normal" : Status.ToString();
}

/// <summary>
/// The Windows print spooler as the product needs it. The real implementation (<c>ErpPrinter.Windows</c>) calls winspool.drv;
/// tests and the diagnostics logic use fakes. Methods throw <see cref="SpoolerException"/> with the Win32 error in the message.
/// </summary>
public interface ISpooler
{
    bool IsSpoolerRunning();
    IReadOnlyList<PrinterInfo> GetPrinters();
    IReadOnlyList<string> GetPorts();
    IReadOnlyList<string> GetDrivers();
    IReadOnlyList<PrintJobInfo> GetJobs(string printerName);

    void AddPort(string portName);
    void RemovePort(string portName);
    void AddPrinter(string name, string driverName, string portName, string comment);
    void UpdatePrinter(string name, string driverName, string portName, string comment);
    void RemovePrinter(string name);
    void DeleteJob(string printerName, int jobId);
}

public sealed class SpoolerException(string message, int win32Error = 0, Exception? inner = null) : Exception(message, inner)
{
    public int Win32Error { get; } = win32Error;
}
