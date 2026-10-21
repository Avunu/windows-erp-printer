namespace ErpPrinter.Core.Tests;

/// <summary>A scratch folder per test class instance, deleted when the test is done.</summary>
public sealed class HttpTestFiles : IDisposable
{
    public HttpTestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "ErpPrinterTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes a small PDF-looking file (optionally padded) and returns its path.</summary>
    public string CreatePdf(string name = "doc.pdf", int extraBytes = 0)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, "%PDF-1.4\n% test document\n" + new string('x', extraBytes) + "\n%%EOF\n");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
    }
}
