using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ErpPrinter.Core.Tests;

/// <summary>One request the loopback server received.</summary>
public sealed record HttpLoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] BodyBytes)
{
    public string Body => Encoding.UTF8.GetString(BodyBytes);
}

/// <summary>
/// Minimal loopback HTTP server on <see cref="HttpListener"/>. Records every request in <see cref="Requests"/> and
/// answers with <see cref="Status"/>, <see cref="Body"/> and <see cref="ContentType"/> (changeable per test).
/// </summary>
public sealed class HttpLoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<HttpLoopbackRequest> _requests = new();
    private readonly Task _loop;

    public HttpLoopbackServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreeTcpPort();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                Url = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // the port was taken between probing and starting; try another one
            }
        }
        Reset();
        _loop = Task.Run(Loop);
    }

    public string Url { get; }
    public int Status { get; set; }
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "";

    public IReadOnlyList<HttpLoopbackRequest> Requests => _requests.ToArray();

    /// <summary>Forgets recorded requests and restores the default reply (200, <c>{"id": "remote-7"}</c>).</summary>
    public void Reset()
    {
        _requests.Clear();
        Status = 200;
        Body = "{\"id\": \"remote-7\"}";
        ContentType = "application/json";
    }

    public static int FreeTcpPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try { return ((IPEndPoint)tcp.LocalEndpoint).Port; }
        finally { tcp.Stop(); }
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }

            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in context.Request.Headers.AllKeys)
                if (key is not null) headers[key] = context.Request.Headers[key] ?? "";
            _requests.Enqueue(new HttpLoopbackRequest(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, headers, context.Request.ContentType, buffer.ToArray()));

            var bytes = Encoding.UTF8.GetBytes(Body);
            context.Response.StatusCode = Status;
            context.Response.ContentType = ContentType;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* the loop ends with the listener */ }
    }
}
