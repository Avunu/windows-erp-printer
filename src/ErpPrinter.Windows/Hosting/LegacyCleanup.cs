using ErpPrinter.Logging;

namespace ErpPrinter.Windows.Hosting;

/// <summary>
/// Removes what the PowerShell product left behind: the <c>\ERP Printer\</c> scheduled tasks and its Start Menu shortcut.
/// The C# product runs as a Windows service instead, and both running together would fight over the pipe.
/// </summary>
public sealed class LegacyCleanup(IErpLog log)
{
    public const string TaskFolder = "ERP Printer";

    /// <summary>
    /// Deletes the legacy tasks. The listener and uploader are stopped first. The updater is only deleted, never stopped:
    /// when the PowerShell updater started this very installation, that task's process is the one waiting on msiexec.
    /// </summary>
    public void RemoveScheduledTasks()
    {
        dynamic? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return;
            service = Activator.CreateInstance(type);
            if (service is null) return;
            service.Connect();
            dynamic folder;
            try { folder = service.GetFolder("\\" + TaskFolder); }
            catch (System.Runtime.InteropServices.COMException) { return; } // no legacy folder: nothing to do

            foreach (var name in new[] { "Listener", "Uploader", "Updater" })
            {
                try
                {
                    dynamic task = folder.GetTask(name);
                    if (name != "Updater") { try { task.Stop(0); } catch (System.Runtime.InteropServices.COMException) { /* not running */ } }
                    folder.DeleteTask(name, 0);
                    log.Information($"Removed the legacy scheduled task '\\{TaskFolder}\\{name}'.");
                }
                catch (System.Runtime.InteropServices.COMException) { /* task not present */ }
            }
            try { service.GetFolder("\\").DeleteFolder(TaskFolder, 0); }
            catch (System.Runtime.InteropServices.COMException ex) { log.Verbose($"Task folder not removed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            log.Warning($"Could not remove the legacy scheduled tasks: {ex.Message}");
        }
        finally
        {
            if (service is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Removes the PowerShell product's Start Menu shortcut, but only when it still points at powershell.exe: the new
    /// product's shortcut has the same path and name and must survive.
    /// </summary>
    public void RemoveLegacyShortcut()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ERP Printer", "ERP Printer Configuration.lnk");
        try
        {
            if (!File.Exists(path)) return;
            var target = ShortcutTarget(path);
            if (target is null || !Path.GetFileName(target).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(path);
            log.Information("Removed the legacy Start Menu shortcut.");
        }
        catch (Exception ex)
        {
            log.Warning($"Could not remove the legacy Start Menu shortcut: {ex.Message}");
        }
    }

    private static string? ShortcutTarget(string shortcutPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return null;
        dynamic? shell = Activator.CreateInstance(type);
        try { return shell?.CreateShortcut(shortcutPath).TargetPath as string; }
        finally { if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
