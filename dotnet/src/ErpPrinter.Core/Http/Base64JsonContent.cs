using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ErpPrinter.Http;

/// <summary>
/// A JSON request body in which every occurrence of the string value <see cref="Placeholder"/> is replaced by the
/// base64 text of a file. The file is encoded in chunks straight into the request stream, so a multi-megabyte
/// document is never held as one giant string (the PowerShell product spliced it into a string instead).
/// </summary>
public sealed class Base64JsonContent : HttpContent
{
    /// <summary>The string value a document payload stands for while the request is being built.</summary>
    public const string Placeholder = "@@ERP_PAYLOAD@@";

    private const int ChunkBytes = 3 * 16 * 1024; // a multiple of 3, so chunks encode without padding

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] Quote = "\""u8.ToArray();

    private readonly byte[][] _segments;
    private readonly string _filePath;

    public Base64JsonContent(JsonNode body, string filePath)
    {
        _filePath = filePath;
        var json = ErpJson.Serialize(body);
        _segments = json.Split('"' + Placeholder + '"').Select(s => Utf8.GetBytes(s)).ToArray();
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    /// <summary>The JSON text with the base64 content spliced in; for tests and small files only.</summary>
    public string ToJsonText()
    {
        using var buffer = new MemoryStream();
        Write(buffer);
        return Utf8.GetString(buffer.ToArray());
    }

    protected override bool TryComputeLength(out long length)
    {
        var base64 = (new FileInfo(_filePath).Length + 2) / 3 * 4;
        length = _segments.Sum(s => (long)s.Length) + (base64 + 2) * (_segments.Length - 1); // +2: the quotes around the base64 text
        return true;
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        Write(stream, cancellationToken);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            await stream.WriteAsync(_segments[i], cancellationToken).ConfigureAwait(false);
            if (i == _segments.Length - 1) break;

            await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var chunk = new byte[ChunkBytes];
            var text = new byte[ChunkBytes / 3 * 4];
            await stream.WriteAsync(Quote, cancellationToken).ConfigureAwait(false);
            int read;
            while ((read = await file.ReadAtLeastAsync(chunk, chunk.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
            {
                Base64.EncodeToUtf8(chunk.AsSpan(0, read), text, out _, out var written);
                await stream.WriteAsync(text.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
            await stream.WriteAsync(Quote, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Write(Stream stream, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            stream.Write(_segments[i]);
            if (i == _segments.Length - 1) break;

            using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var chunk = new byte[ChunkBytes];
            var text = new byte[ChunkBytes / 3 * 4];
            stream.Write(Quote);
            int read;
            while ((read = file.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Base64.EncodeToUtf8(chunk.AsSpan(0, read), text, out _, out var written);
                stream.Write(text, 0, written);
            }
            stream.Write(Quote);
        }
    }
}
