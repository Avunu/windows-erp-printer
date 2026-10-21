using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ErpPrinter.Settings;

namespace ErpPrinter.Http;

/// <summary>
/// One HttpClient-based transport for every backend (and the updater). It honours an explicit proxy (the SYSTEM
/// account has no per-user proxy settings), streams file uploads instead of buffering them, and returns the status
/// code instead of throwing so errors can be classified. Port of Http.ps1.
/// </summary>
public sealed partial class ErpHttp : IDisposable
{
    private const int DefaultTimeoutSeconds = 120;

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();
    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">
    /// Test seam: when given, every client uses this handler (which stays owned by the caller) instead of a real
    /// socket handler. The user agent, timeout and error classification still apply.
    /// </param>
    public ErpHttp(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>
    /// The client for a proxy and timeout combination (cached: one client per <c>proxy|timeout</c> key).
    /// </summary>
    public HttpClient GetClient(GlobalSettings? global = null)
    {
        var proxy = global?.ProxyUrl ?? "";
        var timeout = global is { HttpTimeoutSeconds: > 0 } ? global.HttpTimeoutSeconds : DefaultTimeoutSeconds;
        return _clients.GetOrAdd($"{proxy}|{timeout}", _ => CreateClient(proxy, timeout));
    }

    private HttpClient CreateClient(string proxy, int timeoutSeconds)
    {
        HttpClient client;
        if (_handler is not null)
        {
            client = new HttpClient(_handler, disposeHandler: false);
        }
        else
        {
            var socketsHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            if (proxy.Length > 0)
            {
                socketsHandler.Proxy = new WebProxy(proxy, BypassOnLocal: true) { UseDefaultCredentials = true };
                socketsHandler.UseProxy = true;
            }
            client = new HttpClient(socketsHandler, disposeHandler: true);
        }
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"ErpPrinter/{BuildInfo.Version}");
        return client;
    }

    /// <summary>
    /// Sends an HTTP request. Network failures and timeouts are thrown as transient <see cref="ErpException"/>s;
    /// HTTP error statuses are returned (call <see cref="AssertSuccess"/> to classify them).
    /// </summary>
    /// <param name="jsonBody">A JSON string body (sent as application/json, UTF-8). Ignored when <paramref name="content"/> is given.</param>
    /// <param name="content">A prepared body such as multipart or <see cref="Base64JsonContent"/>. Disposed with the request.</param>
    /// <param name="outFile">Streams a successful response to this file instead of reading it; the result then has an empty body.</param>
    public async Task<HttpResult> SendAsync(
        HttpMethod method,
        string uri,
        GlobalSettings? global = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? jsonBody = null,
        HttpContent? content = null,
        string? outFile = null,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(global);
        using var request = new HttpRequestMessage(method, uri);
        if (headers is not null)
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

        if (content is not null) request.Content = content;
        else if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var completion = outFile is not null ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        try
        {
            using var response = await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (outFile is not null && response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(outFile);
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                return new HttpResult(status, "", null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            var trimmed = body.TrimStart();
            var json = body.Length > 0 && (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('{') || trimmed.StartsWith('['))
                ? ErpJson.TryParse(body)
                : null;
            return new HttpResult(status, body, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new ErpException($"Timed out calling {method} {uri}.", inner: ex);
        }
        catch (Exception ex) when (ex is not ErpException)
        {
            var inner = ex;
            while (inner.InnerException is not null && inner is not TaskCanceledException) inner = inner.InnerException;
            if (inner is TaskCanceledException) throw new ErpException($"Timed out calling {method} {uri}.", inner: ex);
            throw new ErpException($"Could not reach {uri}: {inner.Message}", inner: ex);
        }
    }

    /// <summary>
    /// Throws for non-2xx. 408, 425, 429, 5xx, 401 and 403 are transient (the server or credentials may be fixed
    /// later); other 4xx mean the request itself is wrong and are permanent.
    /// </summary>
    public static void AssertSuccess(HttpResult response, string context)
    {
        var code = response.StatusCode;
        if (code is >= 200 and < 300) return;

        var detail = ErrorDetail(response);
        var message = $"{context} failed with HTTP {code}{(detail.Length > 0 ? ": " + detail : "")}";
        if (code is 401 or 403)
            throw new ErpException($"{message} (check the API key and the user's access rights)");
        var permanent = code is >= 400 and < 500 && code is not (408 or 425 or 429);
        throw new ErpException(message, permanent);
    }

    private static readonly string[][] ErrorPaths =
    [
        ["message"], ["error", "data", "message"], ["error", "message"], ["exception"], ["_server_messages"],
    ];

    /// <summary>
    /// The server's own explanation of an error: a string at one of the usual JSON paths (message,
    /// error.data.message, error.message, exception, _server_messages), else the tag-stripped body, truncated.
    /// </summary>
    public static string ErrorDetail(HttpResult response)
    {
        if (response.Json is not null)
        {
            foreach (var path in ErrorPaths)
            {
                var value = response.Json;
                foreach (var part in path) value = ErpJson.Get(value, part);
                if (value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
                    return Limit(text, 500);
            }
        }
        if (response.Body.Length > 0)
            return Limit(Whitespace().Replace(Tags().Replace(response.Body, " "), " ").Trim(), 300);
        return "";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>Joins a base URL and a path with exactly one slash.</summary>
    public static string JoinUrl(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }
}
