using System.Text;
using ErpPrinter.Updates;

namespace ErpPrinter.Core.Tests;

/// <summary>Port of the pure parts of tests/Unit/Update.Tests.ps1: version comparison, package URL resolution, manifest parsing, package hash.</summary>
public sealed class UpdateLogicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erp-update-tests-" + Guid.NewGuid().ToString("N"));

    public UpdateLogicTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private string WriteManifest(string version = "1.2.3", string? hash = null)
    {
        var msi = Path.Combine(_dir, $"ErpPrinter-{version}-x64.msi");
        File.WriteAllText(msi, "not really an msi");
        hash ??= UpdatePackage.ComputeSha256(msi).ToUpperInvariant();
        var manifest = Path.Combine(_dir, "latest.json");
        File.WriteAllText(manifest, $$"""{ "version": "{{version}}", "url": "ErpPrinter-{{version}}-x64.msi", "sha256": "{{hash}}" }""");
        return manifest;
    }

    // --- Compare-ErpVersion ---------------------------------------------------------------------

    [Theory]
    [InlineData("1.2.3", "1.2.4", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.10.0", "1.9.9", -1)]
    [InlineData("0.0.0", "v2.0.0", 1)]
    [InlineData("1.0.0", "1.0.1-beta.1", 1)]
    [InlineData("v1.4.0+abc123", "1.4.0", 0)]
    [InlineData(" 1.0.0 ", "1.0.0.1", 1)]
    public void Compares_versions(string current, string available, int expected) =>
        Assert.Equal(expected, VersionComparer.Compare(current, available));

    [Theory]
    [InlineData("banana", "1.0.0")]
    [InlineData("1.0.0", "")]
    [InlineData("1", "1.0.0")]
    public void Rejects_versions_it_cannot_parse(string current, string available) =>
        Assert.Throws<FormatException>(() => VersionComparer.Compare(current, available));

    // --- Resolve-ErpUpdatePackageUrl -----------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/o/r/releases/latest/download/latest.json", "ErpPrinter-1.0.0-x64.msi", "https://github.com/o/r/releases/latest/download/ErpPrinter-1.0.0-x64.msi")]
    [InlineData("https://updates.example/erp/latest.json", "https://cdn.example/p.msi", "https://cdn.example/p.msi")]
    [InlineData(@"\\fs01\deploy\erp\latest.json", @"\\fs02\p.msi", @"\\fs02\p.msi")]
    [InlineData("https://updates.example/erp/latest.json", @"C:\pkgs\p.msi", @"C:\pkgs\p.msi")]
    [InlineData("https://updates.example/erp/latest.json", "file:///srv/p.msi", "file:///srv/p.msi")]
    [InlineData("https://updates.example/erp/latest.json", "../p.msi", "https://updates.example/p.msi")]
    public void Resolves_the_package_url(string manifest, string package, string expected) =>
        Assert.Equal(expected, ManifestUrl.ResolvePackage(manifest, package));

    [Fact]
    public void Resolves_a_relative_package_next_to_a_manifest_file()
    {
        var manifest = Path.Combine(_dir, "latest.json");
        Assert.Equal(Path.Combine(_dir, "p.msi"), ManifestUrl.ResolvePackage(manifest, "p.msi"));
        Assert.Equal(Path.Combine(_dir, "p.msi"), ManifestUrl.ResolvePackage(new Uri(manifest).AbsoluteUri, "p.msi"));
    }

    // --- Get-ErpUpdateManifest (parse + validate) ----------------------------------------------

    [Fact]
    public void Reads_a_manifest_from_a_file_path()
    {
        var manifest = UpdateManifest.ReadFile(WriteManifest());
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("ErpPrinter-1.2.3-x64.msi", manifest.Url);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.Null(manifest.Notes);
    }

    [Fact]
    public void Reads_a_manifest_from_a_file_url()
    {
        Assert.Equal("1.2.3", UpdateManifest.ReadFile(new Uri(WriteManifest()).AbsoluteUri).Version);
    }

    [Fact]
    public void Rejects_manifests_with_missing_fields()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, """{"version": "1.0.0"}""");
        var ex = Assert.Throws<InvalidDataException>(() => UpdateManifest.ReadFile(path));
        Assert.Contains("missing 'url'", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Theory]
    [InlineData("""{"url": "a.msi", "sha256": "ab"}""", "version")]
    [InlineData("""{"version": "1.0.0", "url": "a.msi"}""", "sha256")]
    [InlineData("""{"version": "1.0.0", "url": "", "sha256": "ab"}""", "url")]
    [InlineData("""{"version": null, "url": "a.msi", "sha256": "ab"}""", "version")]
    [InlineData("[]", "version")]
    [InlineData("null", "version")]
    public void Requires_version_url_and_sha256(string json, string missing)
    {
        var ex = Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(json, "https://x/latest.json"));
        Assert.Equal($"Update manifest at https://x/latest.json is missing '{missing}'.", ex.Message);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        var ex = Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse("{ nope", "src"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Parses_notes_and_is_case_insensitive_about_field_names()
    {
        var manifest = UpdateManifest.Parse("""{"Version": "1.4.0", "URL": "a.msi", "SHA256": "ab", "notes": "https://notes"}""", "src");
        Assert.Equal(new UpdateManifest("1.4.0", "a.msi", "ab", "https://notes"), manifest);
    }

    [Fact]
    public void Accepts_a_numeric_version()
    {
        Assert.Equal("1.5", UpdateManifest.Parse("""{"version": 1.5, "url": "a", "sha256": "b"}""", "src").Version);
    }

    // --- Assert-ErpUpdatePackage (hash part) -----------------------------------------------------

    [Fact]
    public void Deletes_a_package_whose_hash_does_not_match()
    {
        var file = Path.Combine(_dir, "pkg.msi");
        File.WriteAllText(file, "tampered");
        var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.VerifySha256(file, new string('0', 64)));
        Assert.Contains("hash mismatch", ex.Message);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Accepts_a_matching_hash_regardless_of_case()
    {
        var file = Path.Combine(_dir, "ok.msi");
        File.WriteAllText(file, "fine");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("fine")));
        UpdatePackage.VerifySha256(file, hash.ToUpperInvariant());
        UpdatePackage.VerifySha256(file, " " + hash.ToLowerInvariant() + " ");
        Assert.True(File.Exists(file));
        Assert.True(UpdatePackage.HasHash(file, hash));
        Assert.False(UpdatePackage.HasHash(file, new string('a', 64)));
        Assert.False(UpdatePackage.HasHash(Path.Combine(_dir, "missing.msi"), hash));
    }

    // --- msiexec exit codes -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "Installed version 9.9.9.")]
    [InlineData(3010, "Installed version 9.9.9 (a restart is required to finish).")]
    [InlineData(1641, "Installed version 9.9.9 (a restart is required to finish).")]
    public void Interprets_successful_msi_exit_codes(int exitCode, string message) =>
        Assert.Equal(message, UpdatePackage.InterpretMsiExitCode(exitCode, "9.9.9", "install.log"));

    [Fact]
    public void Throws_for_msi_failures()
    {
        Assert.Contains("Another installation is in progress", Assert.Throws<InvalidOperationException>(() => UpdatePackage.InterpretMsiExitCode(1618, "9.9.9", "l")).Message);
        Assert.Equal("msiexec failed with exit code 1603. See install.log.", Assert.Throws<InvalidOperationException>(() => UpdatePackage.InterpretMsiExitCode(1603, "9.9.9", "install.log")).Message);
    }

    [Fact]
    public void Names_the_package_file_after_the_version()
    {
        Assert.Equal("ErpPrinter-9.9.9.msi", UpdatePackage.FileNameFor("9.9.9"));
        Assert.Equal("ErpPrinter-1.0.0_x.msi", UpdatePackage.FileNameFor("1.0.0/x"));
    }
}
