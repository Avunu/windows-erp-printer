using System.Diagnostics;
using ErpPrinter.Logging;
using ErpPrinter.Printing;

namespace ErpPrinter.Windows.Printing;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs an external program and waits for it. A seam so the driver installer can be tested without DISM.</summary>
public interface IProcessRunner
{
    ProcessResult Run(string fileName, string arguments, TimeSpan timeout);
}

public sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, string arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        process.WaitForExit(); // flush the asynchronous output handlers
        lock (output) return new ProcessResult(process.ExitCode, output.ToString().Trim());
    }
}

/// <summary>
/// Makes sure the "Microsoft Print To PDF" driver exists (port of <c>Install-ErpPdfDriver</c>): first installs it from the
/// driver store, and when that fails enables the Print to PDF Windows optional feature through DISM.
/// </summary>
public sealed class PdfDriverInstaller
{
    public const string DriverName = "Microsoft Print To PDF";
    public const string FeatureName = "Printing-PrintToPDFServices-Features";

    private readonly ISpooler _spooler;
    private readonly IErpLog _log;
    private readonly IProcessRunner _runner;
    private readonly Func<string, int> _installFromStore;

    public PdfDriverInstaller(ISpooler spooler, IErpLog? log = null, IProcessRunner? runner = null, Func<string, int>? installFromStore = null)
    {
        _spooler = spooler;
        _log = log ?? NullLog.Instance;
        _runner = runner ?? new ProcessRunner();
        _installFromStore = installFromStore ?? (name => Winspool.InstallPrinterDriverFromPackage(null, null, name, null, 0));
    }

    public bool IsInstalled() => _spooler.GetDrivers().Contains(DriverName, StringComparer.OrdinalIgnoreCase);

    public void EnsureInstalled()
    {
        if (IsInstalled()) return;

        var hr = _installFromStore(DriverName);
        if (hr != 0 || !IsInstalled())
        {
            _log.Warning($"Installing the driver from the driver store failed (HRESULT 0x{hr:X8}); enabling the Print to PDF Windows feature.");
            var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
            var result = _runner.Run(dism, $"/Online /Enable-Feature /FeatureName:{FeatureName} /All /NoRestart", TimeSpan.FromMinutes(10));
            // 3010: success, restart required.
            if (result.ExitCode is not (0 or 3010))
                _log.Warning($"DISM exited with code {result.ExitCode}: {result.Output}");
        }

        if (!IsInstalled()) throw new SpoolerException($"The '{DriverName}' driver is not available on this machine.");
    }
}
