using System.Text;
using ErpPrinter.Windows.Diagnostics;

namespace ErpPrinter.Windows.Tests.Diagnostics;

public class SpoolFileInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erp-spool-" + Guid.NewGuid().ToString("N"));

    public SpoolFileInspectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>A shadow file is mostly UTF-16 strings (printer, document, user, datatype...) around binary fields.</summary>
    private static byte[] ShadowFile(string datatype)
    {
        using var stream = new MemoryStream();
        stream.Write([0x68, 0x00, 0x00, 0x00, 0x01, 0xFF, 0x00, 0x7F]);
        stream.Write(Encoding.Unicode.GetBytes("Send to Odoo\0Wikipedia, the free encyclopedia\0jdoe\0" + datatype + "\0winprint\0"));
        stream.Write(new byte[64]);
        return stream.ToArray();
    }

    [Fact]
    public void Reads_the_content_type_from_a_shadow_file()
    {
        File.WriteAllBytes(Path.Combine(_dir, "00004.SHD"), ShadowFile("TYPE_XPS_OPEN"));

        Assert.Equal("TYPE_XPS_OPEN", new SpoolFileInspector(_dir).GetFormat(4));
    }

    [Fact]
    public void Pads_the_job_id_to_five_digits()
    {
        File.WriteAllBytes(Path.Combine(_dir, "12345.SHD"), ShadowFile("TYPE_EMF"));

        Assert.Equal("TYPE_EMF", new SpoolFileInspector(_dir).GetFormat(12345));
    }

    [Fact]
    public void Reads_a_file_that_is_still_open_for_writing()
    {
        using var held = new FileStream(Path.Combine(_dir, "00007.SHD"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        held.Write(ShadowFile("TYPE_RAW"));
        held.Flush();

        Assert.Equal("TYPE_RAW", new SpoolFileInspector(_dir).GetFormat(7));
    }

    [Fact]
    public void Returns_null_for_a_missing_file()
    {
        Assert.Null(new SpoolFileInspector(_dir).GetFormat(99));
    }

    [Fact]
    public void Returns_null_when_there_is_no_type_name()
    {
        File.WriteAllBytes(Path.Combine(_dir, "00008.SHD"), Encoding.Unicode.GetBytes("nothing to see here"));

        Assert.Null(new SpoolFileInspector(_dir).GetFormat(8));
    }

    [Fact]
    public void Only_looks_at_the_first_16_KiB()
    {
        var bytes = new byte[16384 + 64];
        Encoding.Unicode.GetBytes("TYPE_LATE").CopyTo(bytes, 16384);
        File.WriteAllBytes(Path.Combine(_dir, "00009.SHD"), bytes);

        Assert.Null(new SpoolFileInspector(_dir).GetFormat(9));
    }

    [Fact]
    public void Returns_null_for_an_empty_file()
    {
        File.WriteAllBytes(Path.Combine(_dir, "00010.SHD"), []);

        Assert.Null(new SpoolFileInspector(_dir).GetFormat(10));
    }

    [Fact]
    public void Defaults_to_the_system_spool_folder_without_throwing_when_the_file_is_missing()
    {
        Assert.Null(new SpoolFileInspector().GetFormat(int.MaxValue));
    }
}
