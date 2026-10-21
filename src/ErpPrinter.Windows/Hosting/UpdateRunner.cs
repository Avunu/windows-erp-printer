using System.Diagnostics;
using ErpPrinter.Http;
using ErpPrinter.Logging;
using ErpPrinter.Settings;
using ErpPrinter.Updates;
using ErpPrinter.Windows.Setup;

namespace ErpPrinter.Windows.Hosting;

public sealed record UpdateResult(string CurrentVersion, string? AvailableVersion, bool UpdateAvailable, bool Installed, string Message);

/// <summary>Runs msiexec for an update. Passes LAUNCHCONFIG=0 so the configuration window does not open on a silent upgrade.</summary>
public sealed class MsiexecInstaller : IUpdateInstaller
{
    public async Task<int> InstallAsync(string packagePath, string logPath, CancellationToken cancellationToken)
    {
        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        using var process = Process.Start(new ProcessStartInfo(msiexec, $"/i \"{packagePath}\" /qn /norestart /l*v \"{logPath}\" LAUNCHCONFIG=0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start msiexec.");
        // The upgrade stops this service (the MSI owns that), which ends this process; msiexec's work continues in the
        // Windows Installer service, so the missing "installed" log line is the only casualty.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>Checks the update manifest and, when a newer version exists, downloads, verifies and installs it. Port of Invoke-ErpPrinterUpdate.</summary>
public sealed class UpdateRunner(ErpHttp http, IUpdateInstaller installer, IErpLog log, TimeProvider time)
{
    /// <param name="checkOnly">Report whether an update is available without installing it.</param>
    /// <param name="force">Install even if AutoUpdate is off or the version is not newer (reinstall or downgrade by manifest).</param>
    public async Task<UpdateResult> RunAsync(GlobalSettings settings, bool checkOnly, bool force, CancellationToken cancellationToken)
    {
        var current = BuildInfo.Version;
        UpdateResult Done(string message, string? available = null, bool availableFlag = false, bool installed = false) =>
            new(current, available, availableFlag, installed, message);

        if (!settings.AutoUpdate && !checkOnly && !force) return Done("Automatic updates are disabled.");
        if (settings.UpdateManifestUrl.Length == 0) return Done("No update manifest URL is configured.");

        var manifest = await ReadManifestAsync(settings, cancellationToken).ConfigureAwait(false);
        var newer = VersionComparer.Compare(current, manifest.Version) > 0;
        if (!newer && !force) return Done($"Up to date ({current}).", manifest.Version);
        if (checkOnly) return Done($"Version {manifest.Version} is available.", manifest.Version, newer);

        WindowsAdministrator.Assert();
        var folder = Path.Combine(settings.DataRoot, "updates");
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, UpdatePackage.FileNameFor(manifest.Version));
        var source = ManifestUrl.ResolvePackage(settings.UpdateManifestUrl, manifest.Url);

        if (!UpdatePackage.HasHash(package, manifest.Sha256))
        {
            log.Information($"Downloading update {manifest.Version} from {source}.");
            await DownloadAsync(settings, source, package, cancellationToken).ConfigureAwait(false);
        }
        UpdatePackage.VerifySha256(package, manifest.Sha256);
        CheckSignature(settings, package);

        var logPath = Path.Combine(folder, $"install-{Naming.ToSafeName(manifest.Version)}-{time.GetLocalNow():yyyyMMddHHmmss}.log");
        log.Information($"Installing update {current} -> {manifest.Version}. MSI log: {logPath}", 1010);
        var exit = await installer.InstallAsync(package, logPath, cancellationToken).ConfigureAwait(false);
        var message = UpdatePackage.InterpretMsiExitCode(exit, manifest.Version, logPath);
        log.Information(message, 1011);
        return Done(message, manifest.Version, newer, installed: true);
    }

    private async Task<UpdateManifest> ReadManifestAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.UpdateManifestUrl;
        if (!ManifestUrl.IsHttp(url)) return UpdateManifest.ReadFile(url);
        var response = await http.SendAsync(HttpMethod.Get, url, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
        ErpHttp.AssertSuccess(response, "Update manifest download");
        return UpdateManifest.Parse(response.Body, url);
    }

    private async Task DownloadAsync(GlobalSettings settings, string source, string target, CancellationToken cancellationToken)
    {
        if (ManifestUrl.IsHttp(source))
        {
            var response = await http.SendAsync(HttpMethod.Get, source, settings, outFile: target, cancellationToken: cancellationToken).ConfigureAwait(false);
            ErpHttp.AssertSuccess(response, "Update download");
        }
        else
        {
            File.Copy(ManifestUrl.ToLocalPath(source), target, overwrite: true);
        }
    }

    private static void CheckSignature(GlobalSettings settings, string package)
    {
        if (!settings.RequireSignedUpdates && settings.UpdateSignerThumbprint.Length == 0) return;
        var signature = AuthenticodeVerifier.Verify(package);
        if (!signature.Valid) throw new InvalidOperationException($"Update package signature is not valid: {signature.Status}");
        var wanted = settings.UpdateSignerThumbprint.Replace(" ", "").ToUpperInvariant();
        if (wanted.Length > 0 && !string.Equals(signature.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Update package is signed by {signature.Subject}, not the configured signer.");
    }
}
