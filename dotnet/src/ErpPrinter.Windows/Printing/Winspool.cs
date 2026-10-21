using System.Runtime.InteropServices;

namespace ErpPrinter.Windows.Printing;

[StructLayout(LayoutKind.Sequential)]
internal struct PrinterDefaults
{
    public nint pDatatype;
    public nint pDevMode;
    public uint DesiredAccess;
}

/// <summary>PRINTER_INFO_2W. String members point into the enumeration buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PrinterInfo2
{
    public nint pServerName;
    public nint pPrinterName;
    public nint pShareName;
    public nint pPortName;
    public nint pDriverName;
    public nint pComment;
    public nint pLocation;
    public nint pDevMode;
    public nint pSepFile;
    public nint pPrintProcessor;
    public nint pDatatype;
    public nint pParameters;
    public nint pSecurityDescriptor;
    public uint Attributes;
    public uint Priority;
    public uint DefaultPriority;
    public uint StartTime;
    public uint UntilTime;
    public uint Status;
    public uint cJobs;
    public uint AveragePPM;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemTime
{
    public ushort Year;
    public ushort Month;
    public ushort DayOfWeek;
    public ushort Day;
    public ushort Hour;
    public ushort Minute;
    public ushort Second;
    public ushort Milliseconds;
}

/// <summary>JOB_INFO_2W.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobInfo2
{
    public uint JobId;
    public nint pPrinterName;
    public nint pMachineName;
    public nint pUserName;
    public nint pDocument;
    public nint pNotifyName;
    public nint pDatatype;
    public nint pPrintProcessor;
    public nint pParameters;
    public nint pDriverName;
    public nint pDevMode;
    public nint pStatus;
    public nint pSecurityDescriptor;
    public uint Status;
    public uint Priority;
    public uint Position;
    public uint StartTime;
    public uint UntilTime;
    public uint TotalPages;
    public uint Size;
    public SystemTime Submitted;
    public uint Time;
    public uint PagesPrinted;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PortInfo1
{
    public nint pName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DriverInfo1
{
    public nint pName;
}

internal static unsafe partial class Winspool
{
    internal const uint PRINTER_ENUM_LOCAL = 0x2;
    internal const uint PRINTER_ENUM_CONNECTIONS = 0x4;

    internal const uint PRINTER_ACCESS_ADMINISTER = 0x4;
    internal const uint PRINTER_ACCESS_USE = 0x8;
    internal const uint PRINTER_ALL_ACCESS = 0x000F000C;
    internal const uint SERVER_ACCESS_ADMINISTER = 0x1;

    internal const uint PRINTER_ATTRIBUTE_LOCAL = 0x40;
    internal const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x400;

    internal const uint JOB_CONTROL_DELETE = 5;

    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenPrinter(string? pPrinterName, out nint phPrinter, PrinterDefaults* pDefault);

    [LibraryImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClosePrinter(nint hPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumPrinters(uint flags, string? name, uint level, byte* pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPrinter(nint hPrinter, uint level, byte* pPrinter, uint cbBuf, out uint pcbNeeded);

    [LibraryImport("winspool.drv", EntryPoint = "SetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPrinter(nint hPrinter, uint level, byte* pPrinter, uint command);

    [LibraryImport("winspool.drv", EntryPoint = "AddPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint AddPrinter(string? pName, uint level, byte* pPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "DeletePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeletePrinter(nint hPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumJobs(nint hPrinter, uint firstJob, uint noJobs, uint level, byte* pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "SetJobW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetJob(nint hPrinter, uint jobId, uint level, byte* pJob, uint command);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPortsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumPorts(string? name, uint level, byte* pPort, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrinterDriversW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumPrinterDrivers(string? name, string? environment, uint level, byte* pDriverInfo, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "XcvDataW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XcvData(nint hXcv, string pszDataName, byte* pInputData, uint cbInputData, byte* pOutputData, uint cbOutputData, out uint pcbOutputNeeded, out uint pdwStatus);

    /// <summary>Installs a driver from a driver package that is already in the driver store (or from <paramref name="infPath"/>). Returns an HRESULT.</summary>
    [LibraryImport("winspool.drv", EntryPoint = "InstallPrinterDriverFromPackageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int InstallPrinterDriverFromPackage(string? server, string? infPath, string? driverName, string? environment, uint flags);
}
