using System.Text.RegularExpressions;
using ErpPrinter.Outbox;

namespace ErpPrinter.Templating;

/// <summary>Builds the name a document is sent under. Port of Get-ErpDocumentFileName.</summary>
public static partial class DocumentFileName
{
    private const int MaxLength = 150;

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"\.pdf$", RegexOptions.IgnoreCase)]
    private static partial Regex PdfExtension();

    /// <summary>A safe "&lt;name&gt;.pdf" from the profile's FileNameTemplate (at most 150 characters before the extension).</summary>
    public static string Build(string template, OutboxMeta meta, TimeProvider? time = null)
    {
        var name = Template.Expand(template, TemplateTokens.Create(meta, time));
        name = Unsafe().Replace(name, "_").Trim().TrimEnd('.');
        name = PdfExtension().Replace(name, "");
        if (name.Length > MaxLength) name = name[..MaxLength].Trim();
        if (name.Length == 0) name = "Untitled";
        return name + ".pdf";
    }
}
