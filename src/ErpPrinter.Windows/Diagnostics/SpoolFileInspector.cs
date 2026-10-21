using System.Text;
using System.Text.RegularExpressions;
using ErpPrinter.Diagnostics;

namespace ErpPrinter.Windows.Diagnostics;

/// <summary>
/// Reads the content type from a job's spool shadow file (<c>%SystemRoot%\System32\spool\PRINTERS\&lt;id:D5&gt;.SHD</c>), for example
/// <c>TYPE_XPS_OPEN</c> (a browser's own print preview) or <c>TYPE_EMF</c> (GDI). Needs administrator rights; any failure returns null.
/// </summary>
public sealed partial class SpoolFileInspector(string? spoolDirectory = null) : ISpoolFileInspector
{
    private const int HeaderBytes = 16384;

    [GeneratedRegex("TYPE_[A-Z0-9_]+")]
    private static partial Regex TypeName();

    private readonly string _directory = spoolDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "PRINTERS");

    public string? GetFormat(int jobId)
    {
        try
        {
            var path = Path.Combine(_directory, $"{jobId:D5}.SHD");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(stream.Length, HeaderBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ParseFormat(buffer, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The first <c>TYPE_*</c> name in the header, decoded as UTF-16, or null.</summary>
    internal static string? ParseFormat(byte[] header, int count)
    {
        var match = TypeName().Match(Encoding.Unicode.GetString(header, 0, count));
        return match.Success ? match.Value : null;
    }
}
