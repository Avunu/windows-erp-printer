using System.Security.Cryptography;

namespace ErpPrinter.Updates;

/// <summary>
/// The seam to the part of the updater that is platform specific and lives in <c>ErpPrinter.Windows</c>: running
/// <c>msiexec /i &lt;package&gt; /qn /norestart /l*v &lt;log&gt; LAUNCHCONFIG=0</c> (after the Authenticode check when
/// <c>RequireSignedUpdates</c> or <c>UpdateSignerThumbprint</c> is set). Downloading is the updater's job too.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>Installs the package and returns msiexec's exit code (see <see cref="UpdatePackage.InterpretMsiExitCode"/>).</summary>
    Task<int> InstallAsync(string packagePath, string logPath, CancellationToken cancellationToken);
}

public static class UpdatePackage
{
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>True when the file exists and its SHA-256 equals <paramref name="expectedSha256"/> (case-insensitive).</summary>
    public static bool HasHash(string path, string expectedSha256) =>
        File.Exists(path) && string.Equals(ComputeSha256(path), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws and deletes the file when its SHA-256 differs from <paramref name="expectedSha256"/>.</summary>
    /// <exception cref="InvalidDataException">Hash mismatch; the downloaded file is discarded.</exception>
    public static void VerifySha256(string path, string expectedSha256)
    {
        var actual = ComputeSha256(path);
        if (string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        throw new InvalidDataException($"Update package hash mismatch (expected {expectedSha256}, got {actual}). The download was discarded.");
    }

    /// <summary>The package file name used in the updates folder: <c>ErpPrinter-&lt;safe version&gt;.msi</c>.</summary>
    public static string FileNameFor(string version) => $"ErpPrinter-{Naming.ToSafeName(version)}.msi";

    /// <summary>Maps msiexec's exit code to the result message (0 installed, 3010/1641 installed but a restart is pending) or throws.</summary>
    /// <exception cref="InvalidOperationException">1618 (another installation is running) or any other failure.</exception>
    public static string InterpretMsiExitCode(int exitCode, string version, string logPath) => exitCode switch
    {
        0 => $"Installed version {version}.",
        3010 or 1641 => $"Installed version {version} (a restart is required to finish).",
        1618 => throw new InvalidOperationException("Another installation is in progress; the update will be retried next time."),
        _ => throw new InvalidOperationException($"msiexec failed with exit code {exitCode}. See {logPath}."),
    };
}
