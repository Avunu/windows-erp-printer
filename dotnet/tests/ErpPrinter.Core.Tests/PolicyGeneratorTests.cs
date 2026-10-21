using System.Text;
using System.Xml;
using ErpPrinter.Policy;

namespace ErpPrinter.Core.Tests;

/// <summary>The C# generators must reproduce the committed PowerShell-generated files exactly, so the port cannot drift from the schema docs.</summary>
public class PolicyGeneratorTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not find " + Path.Combine(parts) + " above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Admx_matches_the_committed_template_byte_for_byte()
    {
        var committed = File.ReadAllBytes(RepoFile("policies", "ErpPrinter.admx"));
        var generated = new UTF8Encoding(false).GetBytes(PolicyTemplateGenerator.Generate().Admx);
        Assert.Equal(Encoding.UTF8.GetString(committed), Encoding.UTF8.GetString(generated));
    }

    [Fact]
    public void Adml_matches_the_committed_template_byte_for_byte()
    {
        var committed = File.ReadAllBytes(RepoFile("policies", "en-US", "ErpPrinter.adml"));
        var generated = new UTF8Encoding(false).GetBytes(PolicyTemplateGenerator.Generate().Adml);
        Assert.Equal(Encoding.UTF8.GetString(committed), Encoding.UTF8.GetString(generated));
    }

    [Fact]
    public void Settings_reference_matches_the_committed_document()
    {
        var committed = File.ReadAllText(RepoFile("docs", "settings.md")).Replace("\r\n", "\n");
        Assert.Equal(committed, SettingsReferenceGenerator.Generate());
    }

    [Fact]
    public void Templates_are_well_formed_xml()
    {
        var templates = PolicyTemplateGenerator.Generate();
        new XmlDocument().LoadXml(templates.Admx);
        new XmlDocument().LoadXml(templates.Adml);
    }

    [Fact]
    public void Writes_both_files_with_crlf_and_no_bom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erp-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            PolicyTemplateGenerator.WriteTo(dir);
            var admx = File.ReadAllBytes(Path.Combine(dir, "ErpPrinter.admx"));
            Assert.NotEqual(0xEF, admx[0]);
            Assert.Contains("\r\n", Encoding.UTF8.GetString(admx));
            Assert.DoesNotContain("\n", Encoding.UTF8.GetString(admx).Replace("\r\n", ""));
            Assert.True(File.Exists(Path.Combine(dir, "en-US", "ErpPrinter.adml")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
