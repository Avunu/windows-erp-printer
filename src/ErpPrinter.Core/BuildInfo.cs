using System.Reflection;

namespace ErpPrinter;

/// <summary>Version and build stamps, set at build time (see ErpPrinter.Core.csproj).</summary>
public static class BuildInfo
{
    private static string? Metadata(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>"major.minor.patch", without any "+commit" suffix.</summary>
    public static string Version { get; } =
        (typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+', '-')[0];

    public static string Commit { get; } = Metadata("Commit") is { Length: > 0 } c ? c : "dev";

    public static string UpdateManifestUrl { get; } = Metadata("UpdateManifestUrl") ?? "";
}
