using ErpPrinter.Backends;
using ErpPrinter.Diagnostics;
using ErpPrinter.Http;
using ErpPrinter.Logging;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;
using ErpPrinter.Templating;
using ErpPrinter.Windows.Diagnostics;
using ErpPrinter.Windows.Listening;
using ErpPrinter.Windows.Logging;
using ErpPrinter.Windows.Printing;
using ErpPrinter.Windows.Storage;

namespace ErpPrinter.Windows.Hosting;

/// <summary>
/// The composition root: builds the object graph once so the service, the command line and the configuration window
/// all run on identical wiring. Everything is created eagerly except the pieces that need a loaded configuration.
/// </summary>
public sealed class ErpPrinterContext : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    /// <param name="component">Log component for this process: <c>service</c>, <c>cli</c>, <c>install</c>, ...</param>
    /// <param name="registry">Defaults to the real HKLM registry; tests pass <see cref="MemoryRegistryStore"/> or an HKCU-rooted store.</param>
    public ErpPrinterContext(string component, TimeProvider? time = null, IRegistryStore? registry = null, IEventLogSink? events = null, ISpooler? spooler = null)
    {
        Component = component;
        Time = time ?? TimeProvider.System;
        Registry = registry ?? new RegistryStore();
        Events = events ?? new WindowsEventLogSink();
        LogSettings = new ErpLogSettings();
        Log = CreateLog(component);
        Protector = new DpapiSecretProtector();
        Secrets = new SecretStore(Registry, Protector, Log);
        Store = new ConfigStore(Registry, Secrets, Log);
        Spooler = spooler ?? new WinspoolSpooler(Log, Registry, Time);
        Http = Own(new ErpHttp());
        Backends = new BackendRegistry(Http, Time, new ActiveDirectoryIdentity(Log), Log);
        Connections = new ConnectionTester(Backends, Secrets);
        Provisioner = new PrinterProvisioner(Spooler, Registry, LoadConfig, Log);
        PdfDriver = new PdfDriverInstaller(Spooler, Log);
        Status = new StatusReporter(Spooler, Time, Store);
    }

    public string Component { get; }
    public TimeProvider Time { get; }
    public IRegistryStore Registry { get; }
    public IEventLogSink Events { get; }
    public ErpLogSettings LogSettings { get; }

    /// <summary>The log for this process's component.</summary>
    public ErpLog Log { get; }

    public ISecretProtector Protector { get; }
    public SecretStore Secrets { get; }
    public ConfigStore Store { get; }
    public ISpooler Spooler { get; }
    public ErpHttp Http { get; }
    public BackendRegistry Backends { get; }
    public ConnectionTester Connections { get; }
    public PrinterProvisioner Provisioner { get; }
    public PdfDriverInstaller PdfDriver { get; }
    public StatusReporter Status { get; }

    /// <summary>A log for another component that shares this process's level and folder.</summary>
    public ErpLog CreateLog(string component) => new(component, LogSettings, Time, Events);

    /// <summary>Loads the configuration and points the log at its folder and level.</summary>
    public ErpConfig LoadConfig()
    {
        var config = Store.Load();
        LogSettings.Update(config.Global);
        return config;
    }

    public OutboxJobSink CreateJobSink() => new(Log, Time, PdfDriverInstaller.DriverName, Environment.MachineName);

    public OutboxProcessor CreateProcessor(IOutboxSignal? signal = null, IErpLog? log = null) =>
        new(log ?? Log, Backends, Secrets, (profile, meta) => DocumentFileName.Build(profile.FileNameTemplate, meta, Time), Time, signal);

    /// <param name="instanceLock">Only one uploader may run per machine; tests pass their own lock instead of the machine-wide mutex.</param>
    public UploaderLoop CreateUploaderLoop(IOutboxSignal signal, IErpLog? log = null, ISingleInstanceLock? instanceLock = null)
    {
        var uploaderLog = log ?? CreateLog("uploader");
        return new UploaderLoop(
            uploaderLog, CreateProcessor(signal, uploaderLog), new OutboxHousekeeping(uploaderLog, Time), signal, LoadConfig, Time,
            instanceLock ?? new NamedMutexLock(), RuntimeSummary.Describe);
    }

    public PipeListener CreateListener(IOutboxSignal signal, IErpLog? log = null)
    {
        var listenerLog = log ?? CreateLog("listener");
        var sink = new OutboxJobSink(listenerLog, Time, PdfDriverInstaller.DriverName, Environment.MachineName);
        return new PipeListener(
            LoadConfig, Spooler, sink, signal, listenerLog, Time,
            reconcilePrinters: ct => Task.Run(Provisioner.Sync, ct),
            configLoaded: LogSettings.Update);
    }

    public DiagnosticsRunner CreateDiagnostics(ErpConfig config, IErpLog? log = null)
    {
        var tester = new Func<PrinterProfile, Task<(bool Ok, string Message)>>(async profile =>
        {
            var result = await Connections.TestAsync(profile, config.Global).ConfigureAwait(false);
            return (result.Success, result.Message);
        });
        return new DiagnosticsRunner(
            Spooler, new EventLogPrintServiceLog(), new SystemInspector(), new SpoolFileInspector(),
            new LogFileReader(config.Global.LogDirectory, Time), tester, SecretIsPresentOrUnknown, Time);
    }

    /// <summary>
    /// The Secrets key is readable only by SYSTEM and Administrators, and an unreadable key looks exactly like a missing
    /// one. Outside an elevated session "not stored" therefore proves nothing, so it must not be reported as a problem.
    /// </summary>
    private bool SecretIsPresentOrUnknown(string profile, string secret) =>
        !Setup.WindowsAdministrator.IsAdministrator() || Secrets.Has(profile, secret);

    public TestPrintRunner CreateTestPrint(ErpConfig config) =>
        new(Spooler, new GdiTestPageSender(Time), new EventLogPrintServiceLog(), new LogFileReader(config.Global.LogDirectory, Time),
            new SystemInspector(), new FileSystemOutboxScanner(), new SpoolFileInspector(), Time);

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        foreach (var item in _owned) item.Dispose();
        _owned.Clear();
    }
}
