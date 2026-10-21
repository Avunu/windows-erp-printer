using ErpPrinter.Logging;
using ErpPrinter.Printing;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Setup;

namespace ErpPrinter.Windows.Printing;

/// <summary>A printer queue and port this tool created, as recorded in the <c>ManagedPrinters</c> registry value ("printer|port").</summary>
public sealed record ManagedPrinter(string Printer, string Port)
{
    public string Serialize() => $"{Printer}|{Port}";
}

/// <summary>Thrown at the end of a sync when some printers could not be provisioned; the others were handled.</summary>
public sealed class PrinterProvisioningException(IReadOnlyList<string> errors, IReadOnlyList<Exception> failures)
    : Exception("Some printers could not be provisioned:\n" + string.Join("\n", errors), failures.Count == 1 ? failures[0] : new AggregateException(failures))
{
    /// <summary>One "printer: message" line per failure.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;

    /// <summary>The underlying exceptions, in the same order.</summary>
    public IReadOnlyList<Exception> Failures { get; } = failures;
}

/// <summary>
/// Makes Windows printers match the enabled profiles (port of <c>Sync-ErpPrinterQueue</c> and <c>Set-ErpPrinterQueue</c>): adds or repairs queues on
/// the profile's pipe port and removes queues and ports this tool created for profiles that were deleted, disabled or renamed.
/// </summary>
public sealed class PrinterProvisioner
{
    public const string ManagedPrintersValue = "ManagedPrinters";
    public const string ProductName = "ERP Printer";

    private readonly ISpooler _spooler;
    private readonly IRegistryStore _registry;
    private readonly Func<ErpConfig> _loadConfig;
    private readonly IErpLog _log;
    private readonly Func<bool> _isAdministrator;

    /// <param name="isAdministrator">Defaults to the real elevation check; tests with a fake spooler pass <c>() =&gt; true</c>.</param>
    public PrinterProvisioner(ISpooler spooler, IRegistryStore registry, Func<ErpConfig> loadConfig, IErpLog? log = null, Func<bool>? isAdministrator = null)
    {
        _spooler = spooler;
        _registry = registry;
        _loadConfig = loadConfig;
        _log = log ?? NullLog.Instance;
        _isAdministrator = isAdministrator ?? WindowsAdministrator.IsAdministrator;
    }

    /// <summary>The printers this tool created (<c>Get-ErpManagedPrinters</c>).</summary>
    public IReadOnlyList<ManagedPrinter> GetManagedPrinters()
    {
        var result = new List<ManagedPrinter>();
        if (!_registry.GetValues(ConfigStore.RegistryRoot).TryGetValue(ManagedPrintersValue, out var raw)) return result;
        var lines = raw switch
        {
            string[] array => array,
            string single => [single],
            _ => [],
        };
        foreach (var line in lines)
        {
            if (line is null || !line.Contains('|')) continue;
            var parts = line.Split('|');
            result.Add(new ManagedPrinter(parts[0], parts[1]));
        }
        return result;
    }

    /// <summary>Creates or repairs one printer queue so it uses the PDF driver on the given port (<c>Set-ErpPrinterQueue</c>).</summary>
    public void SetQueue(string printerName, string portName, string profileName)
    {
        _spooler.AddPort(portName);
        var comment = $"Managed by {ProductName} (profile '{profileName}')";
        var printer = _spooler.GetPrinters().FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));
        if (printer is null)
        {
            _spooler.AddPrinter(printerName, PdfDriverInstaller.DriverName, portName, comment);
            _log.Information($"Created printer '{printerName}' on {portName}.");
            return;
        }
        if (!string.Equals(printer.PortName, portName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(printer.DriverName, PdfDriverInstaller.DriverName, StringComparison.OrdinalIgnoreCase))
        {
            _spooler.UpdatePrinter(printer.Name, PdfDriverInstaller.DriverName, portName, comment);
            _log.Information($"Updated printer '{printerName}' to use {portName}.");
        }
    }

    /// <summary>
    /// Reconciles the Windows printers with the enabled profiles. Every printer is handled even if another fails; the failures are
    /// thrown together as a <see cref="PrinterProvisioningException"/> after the managed list has been saved.
    /// </summary>
    public void Sync()
    {
        if (!_isAdministrator()) throw new UnauthorizedAccessException("This operation needs an elevated (Administrator) session.");

        var config = _loadConfig();
        var desired = config.Profiles.Where(p => p.Enabled)
            .Select(p => (Printer: p.PrinterName, Port: Naming.PortName(p.PipeName), Profile: p.Name))
            .ToList();
        var previous = GetManagedPrinters();
        var managed = new List<string>();
        var errors = new List<string>();
        var failures = new List<Exception>();

        void Record(string printer, Exception ex)
        {
            errors.Add($"{printer}: {ex.Message}");
            failures.Add(ex);
        }

        foreach (var entry in desired)
        {
            try
            {
                SetQueue(entry.Printer, entry.Port, entry.Profile);
                managed.Add(new ManagedPrinter(entry.Printer, entry.Port).Serialize());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record(entry.Printer, ex);
            }
        }

        foreach (var old in previous)
        {
            if (desired.Any(d => Same(d.Printer, old.Printer) && Same(d.Port, old.Port))) continue;
            var samePrinterNewPort = desired.Any(d => Same(d.Printer, old.Printer));
            try
            {
                if (!samePrinterNewPort && _spooler.GetPrinters().Any(p => Same(p.Name, old.Printer)))
                {
                    _spooler.RemovePrinter(old.Printer);
                    _log.Information($"Removed printer '{old.Printer}'.");
                }
                _spooler.RemovePort(old.Port);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record(old.Printer, ex);
                managed.Add(old.Serialize());
            }
        }

        _registry.SetValue(ConfigStore.RegistryRoot, ManagedPrintersValue, managed.ToArray(), RegistryValueType.MultiString);
        if (errors.Count > 0) throw new PrinterProvisioningException(errors, failures);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
