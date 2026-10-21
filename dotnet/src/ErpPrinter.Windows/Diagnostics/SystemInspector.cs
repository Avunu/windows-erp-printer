using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using ErpPrinter.Diagnostics;
using Microsoft.Win32;

namespace ErpPrinter.Windows.Diagnostics;

/// <summary>
/// Windows facts for the diagnostics. It only reads: it never connects to a named pipe (that would take the listener's only
/// instance) and never starts or stops anything.
/// </summary>
public sealed class SystemInspector : ISystemInspector
{
    private static readonly string[] ProtectedPrintKeys =
    [
        @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\WPP",
        @"SYSTEM\CurrentControlSet\Control\Print\WPP",
    ];

    private static readonly string[] ProtectedPrintValues = ["EnableWindowsProtectedPrint", "WindowsProtectedPrintMode"];

    public string ComputerName => Environment.MachineName;

    public string CurrentUser
    {
        get
        {
            try { return WindowsIdentity.GetCurrent().Name; }
            catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException) { return "unknown"; }
        }
    }

    public string OperatingSystem
    {
        get
        {
            string caption;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                caption = searcher.Get().Cast<ManagementBaseObject>().Select(o => o["Caption"]?.ToString()).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "Windows";
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                caption = "Windows";
            }
            return $"{caption}, version {Environment.OSVersion.Version}";
        }
    }

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<FolderWriteProbe> ProbeWritableAsync(string folder, CancellationToken cancellationToken = default)
    {
        var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(probe, "x", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
            return new FolderWriteProbe(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderWriteProbe(false, ex.Message);
        }
    }

    public DiskSpace GetFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new ArgumentException($"'{path}' has no volume.", nameof(path));
        var drive = new DriveInfo(root);
        return new DiskSpace(drive.Name, drive.AvailableFreeSpace);
    }

    public int? GetProtectedPrintMode()
    {
        foreach (var keyPath in ProtectedPrintKeys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) continue;
            foreach (var name in ProtectedPrintValues)
            {
                if (key.GetValue(name) is { } value && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var mode)) return mode;
            }
        }
        return null;
    }

    public ServiceStatus? GetService(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            var state = controller.Status.ToString();
            var startType = controller.StartType.ToString();
            return new ServiceStatus(name, state, startType, controller.Status == ServiceControllerStatus.Running ? GetServiceStart(name) : null);
        }
        catch (InvalidOperationException)
        {
            // No service of that name (the inner exception is a Win32 "service does not exist").
            return null;
        }
    }

    private static DateTimeOffset? GetServiceStart(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Service WHERE Name = '{name.Replace("'", "''")}'");
            foreach (var service in searcher.Get().Cast<ManagementBaseObject>())
            {
                if (service["ProcessId"] is uint pid and not 0)
                {
                    using var process = Process.GetProcessById((int)pid);
                    return new DateTimeOffset(process.StartTime);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // Best effort: the start time is only shown when available.
        }
        return null;
    }

    public IReadOnlyList<LegacyTaskInfo> GetLegacyTasks()
    {
        try
        {
            return QueryTaskScheduler();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NullReferenceException)
        {
            return QuerySchtasks();
        }
    }

    private static List<LegacyTaskInfo> QueryTaskScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("The Task Scheduler COM class is not registered.");
        dynamic service = Activator.CreateInstance(type) ?? throw new COMException("Could not create the Task Scheduler COM object.");
        service.Connect();
        dynamic folder;
        try
        {
            folder = service.GetFolder(@"\ERP Printer");
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            return [];
        }

        var tasks = new List<LegacyTaskInfo>();
        foreach (dynamic task in folder.GetTasks(1))
        {
            DateTime lastRun = task.LastRunTime;
            tasks.Add(new LegacyTaskInfo(
                (string)task.Name,
                TaskStateName((int)task.State),
                (int)task.LastTaskResult,
                lastRun.Year > 1999 ? new DateTimeOffset(lastRun) : null));
        }
        return tasks;
    }

    private static string TaskStateName(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    private static List<LegacyTaskInfo> QuerySchtasks()
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH /V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                return [];
            }
            return ParseSchtasksCsv(output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses <c>schtasks /Query /FO CSV /NH /V</c> output: HostName, TaskName, Next Run Time, Status, Logon Mode, Last Run Time, Last Result, ...
    /// Only tasks in the <c>\ERP Printer\</c> folder are returned. The status text is localized; it is shown as is.
    /// </summary>
    internal static List<LegacyTaskInfo> ParseSchtasksCsv(string csv)
    {
        var tasks = new List<LegacyTaskInfo>();
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '"') continue;
            var fields = line[1..^1].Split("\",\"");
            if (fields.Length < 7) continue;
            const string folder = @"\ERP Printer\";
            if (!fields[1].StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            long? result = long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
            DateTimeOffset? lastRun = DateTime.TryParse(fields[5], CultureInfo.CurrentCulture, DateTimeStyles.None, out var run) && run.Year > 1999 ? new DateTimeOffset(run) : null;
            tasks.Add(new LegacyTaskInfo(fields[1][folder.Length..], fields[3], result, lastRun));
        }
        return tasks;
    }

    public IReadOnlyList<ErpProcessInfo>? GetErpProcesses()
    {
        try
        {
            var found = new List<ErpProcessInfo>();
            var powerShellHosts = 0;
            var readableCommandLines = 0;
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, Name, CommandLine, CreationDate FROM Win32_Process WHERE Name = '{ErpProcessInfo.ServiceExecutable}.exe' OR Name = 'powershell.exe' OR Name = 'pwsh.exe'");
            foreach (var process in searcher.Get().Cast<ManagementObject>())
            {
                using (process)
                {
                    var name = process["Name"]?.ToString() ?? "";
                    var commandLine = process["CommandLine"]?.ToString();
                    var started = process["CreationDate"] is string stamp ? new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(stamp)) : (DateTimeOffset?)null;
                    var info = new ErpProcessInfo(Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture), name, commandLine, started);
                    if (info.IsService)
                    {
                        found.Add(info);
                        continue;
                    }
                    powerShellHosts++;
                    if (!string.IsNullOrEmpty(commandLine)) readableCommandLines++;
                    if (commandLine?.Contains("ErpPrinter.ps1", StringComparison.OrdinalIgnoreCase) == true) found.Add(info);
                }
            }
            // PowerShell hosts exist but none shows a command line: this session may not read them (not elevated).
            return powerShellHosts > 0 && readableCommandLines == 0 ? null : found;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool? PipeExists(string pipeName)
    {
        try
        {
            var wanted = Naming.PortName(pipeName);
            return Directory.GetFiles(@"\\.\pipe\").Any(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? GetUserProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is int enabled && enabled == 1 && key.GetValue("ProxyServer") is string { Length: > 0 } server) return server;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No readable per-user proxy settings.
        }
        return null;
    }
}
