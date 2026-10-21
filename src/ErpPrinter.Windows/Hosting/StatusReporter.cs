using System.ServiceProcess;
using ErpPrinter.Outbox;
using ErpPrinter.Printing;
using ErpPrinter.Settings;

namespace ErpPrinter.Windows.Hosting;

public sealed record ProfileStatus(
    string Profile,
    bool Enabled,
    string Backend,
    string Printer,
    bool PrinterOk,
    int Pending,
    int Failed,
    int Sent,
    DateTimeOffset? LastSent,
    string? LastRemoteId,
    string? LastError,
    string OutboxPath);

public sealed record PrinterStatus(string Version, string Service, string DataRoot, IReadOnlyList<ProfileStatus> Profiles);

/// <summary>Summarises the service, printer queues and outbox contents per profile. Port of Get-ErpPrinterStatus.</summary>
public sealed class StatusReporter(ISpooler spooler, TimeProvider time, ConfigStore store)
{
    public const string ServiceName = "ErpPrinter";

    public PrinterStatus Get()
    {
        var config = store.Load();
        IReadOnlyList<PrinterInfo> printers;
        try { printers = spooler.GetPrinters(); }
        catch (SpoolerException) { printers = []; }

        var outbox = new OutboxStore(ErpPrinter.Logging.NullLog.Instance, time);
        var profiles = new List<ProfileStatus>();
        foreach (var profile in config.Profiles)
        {
            var paths = OutboxPaths.For(config.Global.DataRoot, profile.Name);
            var queue = printers.FirstOrDefault(p => string.Equals(p.Name, profile.PrinterName, StringComparison.OrdinalIgnoreCase));
            var pending = outbox.List(paths.Outbox);
            var failed = outbox.List(paths.Failed);
            var stats = outbox.GetStats(paths);
            var lastError = pending.Concat(failed).Where(i => !string.IsNullOrEmpty(i.Meta.LastError)).Select(i => i.Meta.LastError).LastOrDefault();
            profiles.Add(new ProfileStatus(
                profile.Name, profile.Enabled, profile.Backend, profile.PrinterName,
                PrinterOk: queue is not null && string.Equals(queue.PortName, profile.PortName, StringComparison.OrdinalIgnoreCase),
                pending.Count, failed.Count, stats.Count, stats.LastSentAt?.ToLocalTime(), stats.LastRemoteId, lastError, paths.Root));
        }
        return new PrinterStatus(BuildInfo.Version, ServiceState(), config.Global.DataRoot, profiles);
    }

    /// <summary>The Windows service state ("Running", "Stopped", ...), or "Not installed".</summary>
    public static string ServiceState()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "Not installed";
        }
    }
}
