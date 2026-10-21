using System.Text.RegularExpressions;

namespace ErpPrinter.Diagnostics;

/// <summary>A Windows service as the diagnostics need it. <see cref="State"/> and <see cref="StartType"/> are the ServiceController names ("Running", "Stopped", "Automatic", "Manual", "Disabled").</summary>
public sealed record ServiceStatus(string Name, string State, string StartType, DateTimeOffset? LastStart = null)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => StartType.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Running" for a running service, otherwise state and start type: "Stopped, start type Manual".</summary>
    public string Describe() => IsRunning ? "Running" : $"{State}, start type {StartType}";
}

/// <summary>A leftover scheduled task of the PowerShell installation (<c>\ERP Printer\Listener</c> and friends).</summary>
public sealed record LegacyTaskInfo(string Name, string State, long? LastResult = null, DateTimeOffset? LastRun = null)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var run = LastRun is { } t && t.Year > 1999 ? t.ToString("yyyy-MM-dd HH:mm:ss") : "never";
        var result = LastResult is { } code ? LegacyTaskResult.Describe(code) : "unknown";
        return $"{Name}: state {State}; last run {run}; last result {result}";
    }
}

/// <summary>A process that may belong to ERP Printer: the service executable or a PowerShell host with a command line.</summary>
public sealed partial record ErpProcessInfo(int Id, string Name, string? CommandLine, DateTimeOffset? Started)
{
    public const string ServiceExecutable = "ErpPrinter.Service";

    [GeneratedRegex(@"ErpPrinter\.ps1.*-Command\s+(?<command>Listen|Upload)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyCommand();

    /// <summary>True for the C# service process (<c>ErpPrinter.Service.exe</c>).</summary>
    public bool IsService =>
        Name.Equals(ServiceExecutable, StringComparison.OrdinalIgnoreCase) || Name.Equals(ServiceExecutable + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Listen" or "Upload" when this is a PowerShell process of the old installation, otherwise null.</summary>
    public string? LegacyCommandName => CommandLine is { Length: > 0 } && LegacyCommand().Match(CommandLine) is { Success: true } m ? m.Groups["command"].Value : null;
}

/// <summary>The data folder write probe. <see cref="Error"/> is null when the folder is writable.</summary>
public sealed record FolderWriteProbe(bool Writable, string? Error = null);

/// <summary>Free space of the volume that holds a path.</summary>
public sealed record DiskSpace(string Volume, long FreeBytes);

/// <summary>
/// Operating-system facts the diagnostics need, so the decision logic stays portable and testable. Implementations must never connect
/// to a named pipe (that would consume the listener's only instance); <see cref="PipeExists"/> lists the pipe namespace instead.
/// </summary>
public interface ISystemInspector
{
    string ComputerName { get; }

    /// <summary>"DOMAIN\user" of this session.</summary>
    string CurrentUser { get; }

    /// <summary>"Windows 11 Pro, version 10.0.26200".</summary>
    string OperatingSystem { get; }

    bool IsElevated { get; }

    /// <summary>Tries to create and delete a file in <paramref name="folder"/>.</summary>
    Task<FolderWriteProbe> ProbeWritableAsync(string folder, CancellationToken cancellationToken = default);

    /// <summary>Free space where <paramref name="path"/> lives. May throw; the check then reports "Not checked".</summary>
    DiskSpace GetFreeSpace(string path);

    /// <summary>
    /// The Windows protected print mode policy (<c>EnableWindowsProtectedPrint</c> / <c>WindowsProtectedPrintMode</c> under the policy key,
    /// then the system key; the first value found wins), or null when neither is set. 1 means on.
    /// </summary>
    int? GetProtectedPrintMode();

    /// <summary>Null when no service of that name exists.</summary>
    ServiceStatus? GetService(string name);

    /// <summary>Scheduled tasks under <c>\ERP Printer\</c>, installed by the PowerShell version. Empty when there are none.</summary>
    IReadOnlyList<LegacyTaskInfo> GetLegacyTasks();

    /// <summary>
    /// Candidate ERP Printer processes (the service executable, and PowerShell hosts running <c>ErpPrinter.ps1</c>), or null when the process
    /// list cannot be read from this session (not elevated).
    /// </summary>
    IReadOnlyList<ErpProcessInfo>? GetErpProcesses();

    /// <summary>Whether a process hosts <c>\\.\pipe\&lt;pipeName&gt;</c>; null when the pipe list cannot be read.</summary>
    bool? PipeExists(string pipeName);

    /// <summary>The current user's enabled proxy server (<c>HKCU\...\Internet Settings</c>), or null.</summary>
    string? GetUserProxy();
}

/// <summary>Reads the first bytes of a job's spool shadow file. Port of Get-ErpSpoolFileFormat.</summary>
public interface ISpoolFileInspector
{
    /// <summary>The content type in the shadow file (<c>TYPE_XPS_OPEN</c>, <c>TYPE_EMF</c>, ...), or null when it cannot be read.</summary>
    string? GetFormat(int jobId);
}

public static class FolderAccess
{
    /// <summary>False only when the folder exists but this session may not list it (the data folder is limited to SYSTEM and Administrators).</summary>
    public static bool IsReadable(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
