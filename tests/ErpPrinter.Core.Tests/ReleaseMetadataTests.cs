using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErpPrinter.Core.Tests;

/// <summary>
/// release-please bumps the version by editing marked lines. If the marker disappears (or the files drift apart), the release
/// pull request still opens but ships a build whose version does not match its tag, which breaks the updater's comparison.
/// </summary>
public class ReleaseMetadataTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not find {name} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void The_version_line_carries_the_release_please_marker_and_matches_the_manifest()
    {
        var props = File.ReadAllText(RepoFile("Directory.Build.props"));
        var match = Regex.Match(props, @"<Version>(?<v>\d+\.\d+\.\d+)</Version>\s*<!--\s*x-release-please-version\s*-->");
        Assert.True(match.Success, "Directory.Build.props needs a <Version>x.y.z</Version> line followed by <!-- x-release-please-version -->.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(RepoFile(".release-please-manifest.json")));
        Assert.Equal(manifest.RootElement.GetProperty(".").GetString(), match.Groups["v"].Value);
    }

    [Fact]
    public void Release_please_is_configured_to_bump_that_file()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(RepoFile("release-please-config.json")));
        var extra = config.RootElement.GetProperty("packages").GetProperty(".").GetProperty("extra-files").EnumerateArray().Select(e => e.GetString());
        Assert.Contains("Directory.Build.props", extra);
    }

    [Fact]
    public void The_built_assembly_reports_the_version_of_the_props_file()
    {
        var props = File.ReadAllText(RepoFile("Directory.Build.props"));
        var expected = Regex.Match(props, @"<Version>(?<v>\d+\.\d+\.\d+)</Version>").Groups["v"].Value;
        Assert.Equal(expected, BuildInfo.Version);
    }
}
