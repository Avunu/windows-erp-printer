using ErpPrinter.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests.Diagnostics;

/// <summary>A clock the tests move by hand. Local time is UTC so that formatted times are the same everywhere.</summary>
internal sealed class DiagClock(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 10, 20, 17, 27, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan span) => _now += span;
}

internal sealed class DiagSpooler : ISpooler
{
    public List<PrinterInfo> Printers { get; } = [];
    public List<string> Ports { get; } = [];
    public List<string> Drivers { get; } = ["Microsoft Print To PDF"];
    public Func<string, IReadOnlyList<PrintJobInfo>> Jobs { get; set; } = _ => [];
    public Exception? PrintersError { get; set; }
    public bool Running { get; set; } = true;

    public bool IsSpoolerRunning() => Running;
    public IReadOnlyList<PrinterInfo> GetPrinters() => PrintersError is null ? Printers : throw PrintersError;
    public IReadOnlyList<string> GetPorts() => Ports;
    public IReadOnlyList<string> GetDrivers() => Drivers;
    public IReadOnlyList<PrintJobInfo> GetJobs(string printerName) => Jobs(printerName);
    public void AddPort(string portName) => throw new NotSupportedException();
    public void RemovePort(string portName) => throw new NotSupportedException();
    public void AddPrinter(string name, string driverName, string portName, string comment) => throw new NotSupportedException();
    public void UpdatePrinter(string name, string driverName, string portName, string comment) => throw new NotSupportedException();
    public void RemovePrinter(string name) => throw new NotSupportedException();
    public void DeleteJob(string printerName, int jobId) => throw new NotSupportedException();
}

internal sealed class DiagPrintServiceLog : IPrintServiceLog
{
    public List<PrintServiceEvent> Operational { get; } = [];
    public List<PrintServiceEvent> Admin { get; } = [];
    public PrintServiceLogState OperationalState { get; set; } = PrintServiceLogState.Enabled;
    public Exception? ReadError { get; set; }

    public Task<PrintServiceLogState> GetStateAsync(PrintServiceLogKind log, CancellationToken cancellationToken = default) =>
        Task.FromResult(log == PrintServiceLogKind.Operational ? OperationalState : PrintServiceLogState.Enabled);

    public Task<IReadOnlyList<PrintServiceEvent>> ReadAsync(
        PrintServiceLogKind log, DateTimeOffset since, IReadOnlyCollection<int>? levels = null, IReadOnlyCollection<int>? ids = null, int max = 50,
        CancellationToken cancellationToken = default)
    {
        if (ReadError is not null) throw ReadError;
        IReadOnlyList<PrintServiceEvent> result = (log == PrintServiceLogKind.Operational ? Operational : Admin)
            .Where(e => e.Time >= since && (levels is null || levels.Contains(e.Level)) && (ids is null || ids.Contains(e.Id)))
            .OrderByDescending(e => e.Time).Take(max).ToList();
        return Task.FromResult(result);
    }

    public static PrintServiceEvent Event(int id, string message, DateTimeOffset time, int level = 4) =>
        new(id, level, level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" }, time, message);
}

internal sealed class DiagSpoolFiles(string? format = null) : ISpoolFileInspector
{
    public string? GetFormat(int jobId) => format;
}

internal sealed class DiagLogReader : ILogReader
{
    public List<LogEntry> Entries { get; } = [];

    public Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int hours, IReadOnlyCollection<ErpLogLevel>? levels = null, string? component = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LogEntry> result = Entries
            .Where(e => (levels is null || levels.Contains(e.Level)) && (component is null || e.Component == component))
            .OrderBy(e => e.Time).ToList();
        return Task.FromResult(result);
    }
}

internal sealed class DiagSystem : ISystemInspector
{
    public string ComputerName { get; set; } = "PC1";
    public string CurrentUser { get; set; } = @"CORP\admin";
    public string OperatingSystem { get; set; } = "Windows 11 Pro, version 10.0.26200";
    public bool IsElevated { get; set; } = true;
    public FolderWriteProbe Probe { get; set; } = new(true);
    public DiskSpace Disk { get; set; } = new(@"C:\", 50L * 1024 * 1024 * 1024);
    public Exception? DiskError { get; set; }
    public int? ProtectedPrintMode { get; set; }
    public Dictionary<string, ServiceStatus> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spooler"] = new("Spooler", "Running", "Automatic"),
        ["ErpPrinter"] = new("ErpPrinter", "Running", "Automatic", new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero)),
    };
    public List<LegacyTaskInfo> LegacyTasks { get; } = [];
    public List<ErpProcessInfo>? Processes { get; set; } = [new ErpProcessInfo(4242, "ErpPrinter.Service.exe", null, new DateTimeOffset(2026, 10, 20, 8, 0, 1, TimeSpan.Zero))];
    public bool? Pipe { get; set; } = true;
    public string? UserProxy { get; set; }

    public Task<FolderWriteProbe> ProbeWritableAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult(Probe);
    public DiskSpace GetFreeSpace(string path) => DiskError is null ? Disk : throw DiskError;
    public int? GetProtectedPrintMode() => ProtectedPrintMode;
    public ServiceStatus? GetService(string name) => Services.GetValueOrDefault(name);
    public IReadOnlyList<LegacyTaskInfo> GetLegacyTasks() => LegacyTasks;
    public IReadOnlyList<ErpProcessInfo>? GetErpProcesses() => Processes;
    public bool? PipeExists(string pipeName) => Pipe;
    public string? GetUserProxy() => UserProxy;
}

internal sealed class DiagScanner : IOutboxScanner
{
    public Func<string, IReadOnlySet<string>, OutboxItem?> Find { get; set; } = (_, _) => null;

    public Task<IReadOnlySet<string>> GetKnownIdsAsync(ProfileFolders folders, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<OutboxItem?> FindTestPageAsync(ProfileFolders folders, string token, IReadOnlySet<string> known, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(token, known));
}

internal sealed class DiagPageSender : ITestPageSender
{
    public Action<TestPageRequest>? OnSend { get; set; }
    public Exception? Error { get; set; }
    public List<TestPageRequest> Sent { get; } = [];

    public Task<string> SendAsync(TestPageRequest request, CancellationToken cancellationToken = default)
    {
        if (Error is not null) throw Error;
        Sent.Add(request);
        OnSend?.Invoke(request);
        return Task.FromResult(request.DocumentName);
    }
}

internal sealed class DiagTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erp-diag-" + Guid.NewGuid().ToString("N"));

    public DiagTempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { /* best effort */ }
    }
}

internal static class DiagData
{
    public const string ProfileName = "Send to Odoo";
    public const string Pipe = "ErpPrinter-Test";
    public static readonly string Port = Naming.PortName(Pipe);

    public static PrinterProfile Profile(bool enabled = true, string name = ProfileName) =>
        PrinterProfile.Create(name, new Dictionary<string, object> { ["PipeName"] = Pipe, ["Enabled"] = enabled, ["ServerUrl"] = "https://erp.example" });

    public static ErpConfig Config(string dataRoot, params PrinterProfile[] profiles) =>
        new(GlobalSettings.Defaults(dataRoot), profiles);

    public static ErpConfig Config(string dataRoot, Action<Dictionary<string, object>> adjustGlobal, params PrinterProfile[] profiles)
    {
        var values = Schema.Global.ToDictionary(s => s.Name, s => s.DefaultFor(null));
        values["DataRoot"] = dataRoot;
        adjustGlobal(values);
        return new ErpConfig(new GlobalSettings(values), profiles);
    }

    public static PrinterInfo Printer(string name = ProfileName, string? port = null, string driver = "Microsoft Print To PDF", PrinterStatusFlags status = PrinterStatusFlags.None, bool workOffline = false) =>
        new(name, driver, port ?? Port, "", status, workOffline);

    public static PrintJobInfo Job(int id, string document, JobStatusFlags status, DateTimeOffset? submitted = null, string printer = ProfileName, long size = 4000, string? datatype = null) =>
        new(id, printer, document, "jdoe", status, 1, 0, size, submitted, datatype);
}
