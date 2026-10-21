using System.Text.RegularExpressions;

namespace ErpPrinter.Updates;

/// <summary>Resolution of the package location named in an update manifest. The manifest can live on GitHub releases, any HTTPS server, or a UNC share.</summary>
public static partial class ManifestUrl
{
    [GeneratedRegex(@"^(https?|file)://", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrl();

    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrl();

    [GeneratedRegex(@"^file://", RegexOptions.IgnoreCase)]
    private static partial Regex FileUrl();

    [GeneratedRegex(@"^(\\\\|[A-Za-z]:\\)")]
    private static partial Regex UncOrDrivePath();

    public static bool IsHttp(string url) => HttpUrl().IsMatch(url);

    /// <summary>A <c>file://</c> URL becomes its local path; anything else is returned unchanged.</summary>
    public static string ToLocalPath(string urlOrPath) => FileUrl().IsMatch(urlOrPath) ? new Uri(urlOrPath).LocalPath : urlOrPath;

    /// <summary>
    /// Absolute package URLs and UNC or drive paths are returned unchanged; a relative <paramref name="packageUrl"/> is resolved
    /// against the folder (or URL directory) of <paramref name="manifestUrl"/>.
    /// </summary>
    public static string ResolvePackage(string manifestUrl, string packageUrl)
    {
        if (AbsoluteUrl().IsMatch(packageUrl) || UncOrDrivePath().IsMatch(packageUrl)) return packageUrl;
        if (HttpUrl().IsMatch(manifestUrl)) return new Uri(new Uri(manifestUrl), packageUrl).AbsoluteUri;
        var manifestPath = ToLocalPath(manifestUrl);
        return Path.Combine(Path.GetDirectoryName(manifestPath) ?? "", packageUrl);
    }
}
