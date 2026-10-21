using System.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Windows.Logging;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Printing;
using ErpPrinter.Windows.Setup;

namespace ErpPrinter.Windows.Hosting;

/// <summary>Values the MSI passes to <c>erpprinter install</c> (empty strings mean "not given").</summary>
public sealed record InstallOptions(
    string InstallDir,
    string ProfileName = "Send to ERP",
    string Backend = "",
    string ServerUrl = "",
    string Database = "",
    string Username = "",
    string ApiKey = "",
    string ApiSecret = "",
    string AutoUpdate = "",
    string UpdateManifestUrl = "");

/// <summary>
/// Provisioning, as run by the MSI (and by administrators). Port of Install-ErpPrinter and Uninstall-ErpPrinter.
/// Both are idempotent. Neither starts or stops the Windows service: the MSI owns the service lifecycle.
/// </summary>
public sealed class InstallCommands(ErpPrinterContext context)
{
    private readonly ErpLog _log = context.Log;

    public void Install(InstallOptions options)
    {
        WindowsAdministrator.Assert();
        var store = context.Store;

        EnsureEventSource();
        context.Registry.SetValue(ConfigStore.RegistryRoot, "InstallDir", options.InstallDir, RegistryValueType.String);
        context.Registry.SetValue(ConfigStore.RegistryRoot, "InstalledVersion", BuildInfo.Version, RegistryValueType.String);
        context.Registry.EnsureSecretsKey();

        if (options.AutoUpdate.Length > 0) store.SetSetting(null, "AutoUpdate", options.AutoUpdate);
        if (options.UpdateManifestUrl.Length > 0) store.SetSetting(null, "UpdateManifestUrl", options.UpdateManifestUrl);

        var config = context.LoadConfig();
        DataRootInitializer.Initialize(config.Global.DataRoot);

        if (options.ServerUrl.Length > 0)
        {
            var settings = new Dictionary<string, object?> { ["ServerUrl"] = options.ServerUrl };
            if (options.Backend.Length > 0) settings["Backend"] = options.Backend;
            if (options.Database.Length > 0) settings["Database"] = options.Database;
            if (options.Username.Length > 0) settings["Username"] = options.Username;
            if (options.ApiKey.Length > 0) settings["ApiKey"] = options.ApiKey;
            if (options.ApiSecret.Length > 0) settings["ApiSecret"] = options.ApiSecret;
            store.CreateProfile(options.ProfileName, settings, force: true);
            _log.Information($"Seeded printer profile '{options.ProfileName}' ({settings.GetValueOrDefault("Backend") ?? "OdooJson2"}) from installer properties.");
        }

        // The spooler's per-job audit trail (event 307) is what explains a job that vanished.
        try { new ProcessRunner().Run("wevtutil.exe", "sl Microsoft-Windows-PrintService/Operational /e:true", TimeSpan.FromSeconds(30)); }
        catch (Exception ex) { _log.Warning($"Could not enable the PrintService operational log: {ex.Message}"); }

        try
        {
            context.PdfDriver.EnsureInstalled();
            context.Provisioner.Sync();
        }
        catch (Exception ex)
        {
            // Do not fail the whole installation; the configuration window and `erpprinter status` surface this.
            _log.Error($"Printer provisioning failed: {ex.Message}");
        }

        var legacy = new LegacyCleanup(_log);
        legacy.RemoveScheduledTasks();
        legacy.RemoveLegacyShortcut();
        _log.Information($"ERP Printer {BuildInfo.Version} installed in {options.InstallDir}.");
    }

    /// <param name="purge">Also delete configuration, secrets and the data folder, including undelivered documents.</param>
    public void Uninstall(bool purge)
    {
        WindowsAdministrator.Assert();
        var config = context.LoadConfig();

        new LegacyCleanup(_log).RemoveScheduledTasks();
        var existing = context.Spooler.GetPrinters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.Provisioner.GetManagedPrinters())
        {
            try
            {
                if (existing.Contains(entry.Printer)) context.Spooler.RemovePrinter(entry.Printer);
                context.Spooler.RemovePort(entry.Port);
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not remove printer '{entry.Printer}': {ex.Message}");
            }
        }
        foreach (var name in new[] { "ManagedPrinters", "InstalledVersion", "InstallDir" })
            context.Registry.RemoveValue(ConfigStore.RegistryRoot, name);

        var spool = Path.Combine(config.Global.DataRoot, "spool");
        var pending = Directory.Exists(spool)
            ? Directory.EnumerateFiles(spool, "*.json", SearchOption.AllDirectories).Count(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "outbox", StringComparison.OrdinalIgnoreCase))
            : 0;
        if (purge)
        {
            if (pending > 0) _log.Warning($"Purging {pending} undelivered document(s).");
            TryDelete(config.Global.DataRoot);
            context.Registry.RemoveKey(ConfigStore.RegistryRoot);
            try { if (EventLog.SourceExists(EventSource)) EventLog.DeleteEventSource(EventSource); }
            catch (Exception ex) { Debug.WriteLine($"Event source not removed: {ex.Message}"); }
        }
        else if (pending > 0)
        {
            _log.Warning($"{pending} undelivered document(s) remain in {config.Global.DataRoot}.");
        }
    }

    private const string EventSource = "ErpPrinter";

    private static void EnsureEventSource()
    {
        if (!EventLog.SourceExists(EventSource)) EventLog.CreateEventSource(EventSource, "Application");
    }

    private void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { _log.Warning($"Could not delete '{folder}': {ex.Message}"); }
    }
}
