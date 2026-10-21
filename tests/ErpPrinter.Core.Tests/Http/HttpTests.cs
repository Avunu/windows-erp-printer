using System.Text;
using System.Text.Json.Nodes;
using ErpPrinter.Http;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

public class HttpAssertSuccessTests
{
    private static ErpException Failure(int code, string body = "", JsonNode? json = null) =>
        Assert.Throws<ErpException>(() => ErpHttp.AssertSuccess(new HttpResult(code, body, json), "Call"));

    [Theory]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(413, true)]
    [InlineData(417, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(408, false)]
    [InlineData(425, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public void Classifies_HTTP_status_as_permanent_or_transient(int code, bool permanent)
    {
        var ex = Failure(code);
        Assert.Equal(permanent, ex.Permanent);
        Assert.Equal(permanent, ErpException.IsPermanent(ex));
    }

    [Fact]
    public void Passes_2xx_responses()
    {
        ErpHttp.AssertSuccess(new HttpResult(201, "", null), "x");
        ErpHttp.AssertSuccess(new HttpResult(200, "", null), "x");
    }

    [Fact]
    public void Includes_the_server_error_message()
    {
        var json = JsonNode.Parse("""{"error": {"message": "Odoo Server Error", "data": {"message": "Invalid field folder_id"}}}""");
        var ex = Failure(422, "", json);
        Assert.Contains("Invalid field folder_id", ex.Message);
        Assert.StartsWith("Call failed with HTTP 422: ", ex.Message);
    }

    [Fact]
    public void Adds_a_hint_to_401_and_403()
    {
        Assert.EndsWith("(check the API key and the user's access rights)", Failure(401).Message);
        Assert.EndsWith("(check the API key and the user's access rights)", Failure(403).Message);
        Assert.DoesNotContain("API key", Failure(500).Message);
    }

    [Theory]
    [InlineData("""{"message": "top"}""", "top")]
    [InlineData("""{"error": {"data": {"message": "deep"}, "message": "mid"}}""", "deep")]
    [InlineData("""{"error": {"message": "mid"}}""", "mid")]
    [InlineData("""{"exception": "frappe.exceptions.ValidationError: Folder missing"}""", "frappe.exceptions.ValidationError: Folder missing")]
    [InlineData("""{"_server_messages": "[...]"}""", "[...]")]
    public void Reads_the_error_detail_from_the_usual_JSON_paths(string json, string expected) =>
        Assert.Equal(expected, ErpHttp.ErrorDetail(new HttpResult(400, json, JsonNode.Parse(json))));

    [Fact]
    public void Falls_back_to_the_tag_stripped_body_truncated_to_300_characters()
    {
        var html = "<html><body><h1>Bad   gateway</h1>\n<p>try later</p></body></html>";
        Assert.Equal("Bad gateway try later", ErpHttp.ErrorDetail(new HttpResult(502, html, null)));

        var detail = ErpHttp.ErrorDetail(new HttpResult(502, new string('a', 1000), null));
        Assert.Equal(303, detail.Length);
        Assert.EndsWith("...", detail);
    }

    [Fact]
    public void Ignores_non_string_JSON_values_and_limits_long_messages_to_500_characters()
    {
        var numeric = JsonNode.Parse("""{"message": 5}""");
        Assert.Equal("""{"message": 5}""", ErpHttp.ErrorDetail(new HttpResult(400, """{"message": 5}""", numeric)));
        var longJson = new JsonObject { ["message"] = new string('b', 900) };
        Assert.Equal(503, ErpHttp.ErrorDetail(new HttpResult(400, "", longJson)).Length);
    }
}

public class HttpHelperTests : IDisposable
{
    private readonly HttpTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Joins_URLs_regardless_of_slashes()
    {
        Assert.Equal("https://a.example/json/2/x", ErpHttp.JoinUrl("https://a.example/", "/json/2/x"));
        Assert.Equal("https://a.example/odoo/jsonrpc", ErpHttp.JoinUrl("https://a.example/odoo", "jsonrpc"));
    }

    [Fact]
    public void Splices_base64_file_content_into_JSON()
    {
        var pdf = _files.CreatePdf("splice.pdf");
        var body = new JsonObject
        {
            ["vals_list"] = new JsonArray(new JsonObject { ["name"] = "a\"b.pdf", ["datas"] = Base64JsonContent.Placeholder }),
        };
        using var content = new Base64JsonContent(body, pdf);
        var parsed = JsonNode.Parse(content.ToJsonText())!;
        var vals = parsed["vals_list"]![0]!;
        Assert.Equal("a\"b.pdf", (string?)vals["name"]);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(Convert.FromBase64String((string)vals["datas"]!)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(49151)]
    [InlineData(49152)]
    [InlineData(200_000)]
    public async Task Streams_base64_in_chunks_and_announces_the_exact_length(int extraBytes)
    {
        var pdf = _files.CreatePdf("big.pdf", extraBytes);
        var body = new JsonObject { ["contentBase64"] = Base64JsonContent.Placeholder, ["n"] = 1 };
        using var content = new Base64JsonContent(body, pdf);

        var bytes = await content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, content.Headers.ContentLength);

        var parsed = JsonNode.Parse(bytes)!;
        Assert.Equal(await File.ReadAllBytesAsync(pdf), Convert.FromBase64String((string)parsed["contentBase64"]!));
        Assert.Equal(1, (int)parsed["n"]!);
    }

    [Fact]
    public void Finds_the_permanent_flag_on_inner_exceptions()
    {
        var inner = new ErpException("inner", permanent: true);
        Assert.True(ErpException.IsPermanent(new Exception("outer", inner)));
        Assert.False(ErpException.IsPermanent(new Exception("plain")));
    }
}

public class HttpTransportTests : IClassFixture<HttpLoopbackServer>, IDisposable
{
    private readonly HttpLoopbackServer _server;
    private readonly HttpTestFiles _files = new();
    private readonly ErpHttp _http = new();

    public HttpTransportTests(HttpLoopbackServer server)
    {
        _server = server;
        _server.Reset();
    }

    public void Dispose()
    {
        _http.Dispose();
        _files.Dispose();
    }

    private static GlobalSettings Settings(int timeoutSeconds, string proxy = "")
    {
        var values = Schema.Global.ToDictionary(s => s.Name, s => s.DefaultFor(null));
        values["HttpTimeoutSeconds"] = timeoutSeconds;
        values["ProxyUrl"] = proxy;
        return new GlobalSettings(values);
    }

    [Fact]
    public async Task Sends_JSON_with_custom_headers_and_parses_the_reply()
    {
        var response = await _http.SendAsync(HttpMethod.Post, $"{_server.Url}/json/2/x/create",
            headers: new Dictionary<string, string> { ["Authorization"] = "bearer k", ["X-Odoo-Database"] = "db" },
            jsonBody: """{"a":1}""");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("remote-7", (string?)response.Json!["id"]);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("/json/2/x/create", request.Path);
        Assert.Equal("bearer k", request.Headers["Authorization"]);
        Assert.Equal("db", request.Headers["X-Odoo-Database"]);
        Assert.StartsWith("ErpPrinter/", request.Headers["User-Agent"]);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Equal("""{"a":1}""", request.Body);
    }

    [Fact]
    public async Task Returns_error_statuses_instead_of_throwing()
    {
        _server.Status = 503;
        _server.Body = "maintenance";

        var response = await _http.SendAsync(HttpMethod.Get, $"{_server.Url}/x");

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("maintenance", response.Body);
        Assert.Null(response.Json);
    }

    [Fact]
    public async Task Ignores_bodies_that_only_look_like_JSON()
    {
        _server.Body = "{not json";
        var response = await _http.SendAsync(HttpMethod.Get, $"{_server.Url}/x");
        Assert.Equal("{not json", response.Body);
        Assert.Null(response.Json);
    }

    [Fact]
    public async Task Streams_multipart_uploads()
    {
        var pdf = _files.CreatePdf("multi.pdf");
        using (var content = MultipartContents.Create(
                   [new("is_private", "1"), new("folder", "Home")], "file", pdf, "Invoice 7.pdf"))
        {
            await _http.SendAsync(HttpMethod.Post, $"{_server.Url}/api/method/upload_file", content: content);
        }

        var request = Assert.Single(_server.Requests);
        Assert.StartsWith("multipart/form-data; boundary=", request.ContentType);
        Assert.Matches("name=\"?is_private\"?", request.Body);
        Assert.Matches("filename=\"?Invoice 7.pdf\"?", request.Body);
        Assert.Contains("%PDF-1.4", request.Body);
    }

    [Fact]
    public async Task Sends_base64_JSON_payloads_with_a_content_length()
    {
        var pdf = _files.CreatePdf("payload.pdf", 100_000);
        var body = new JsonObject { ["datas"] = Base64JsonContent.Placeholder };
        using (var content = new Base64JsonContent(body, pdf))
            await _http.SendAsync(HttpMethod.Post, $"{_server.Url}/p", content: content);

        var request = Assert.Single(_server.Requests);
        Assert.False(request.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal(request.BodyBytes.Length.ToString(), request.Headers["Content-Length"]);
        var parsed = JsonNode.Parse(request.BodyBytes)!;
        Assert.Equal(await File.ReadAllBytesAsync(pdf), Convert.FromBase64String((string)parsed["datas"]!));
    }

    [Fact]
    public async Task Downloads_to_a_file()
    {
        _server.Body = "binary-ish";
        var output = Path.Combine(_files.Root, "download.bin");

        var response = await _http.SendAsync(HttpMethod.Get, $"{_server.Url}/pkg.msi", outFile: output);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("binary-ish", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Does_not_create_the_download_file_for_error_statuses()
    {
        _server.Status = 404;
        _server.Body = "gone";
        var output = Path.Combine(_files.Root, "missing.bin");

        var response = await _http.SendAsync(HttpMethod.Get, $"{_server.Url}/pkg.msi", outFile: output);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("gone", response.Body);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Reports_unreachable_hosts_as_transient_errors()
    {
        var port = HttpLoopbackServer.FreeTcpPort();

        var ex = await Assert.ThrowsAsync<ErpException>(() => _http.SendAsync(HttpMethod.Get, $"http://127.0.0.1:{port}/"));

        Assert.StartsWith("Could not reach", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Reports_timeouts_as_transient_errors()
    {
        using var slow = new SlowHandler();
        using var http = new ErpHttp(slow);

        var ex = await Assert.ThrowsAsync<ErpException>(() => http.SendAsync(HttpMethod.Post, "https://erp.example.com/x", Settings(1), jsonBody: "{}"));

        Assert.StartsWith("Timed out calling POST https://erp.example.com/x", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Lets_a_cancelled_token_through_as_cancellation()
    {
        using var slow = new SlowHandler();
        using var http = new ErpHttp(slow);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.SendAsync(HttpMethod.Get, "https://erp.example.com/x", cancellationToken: cts.Token));
    }

    [Fact]
    public void Caches_one_client_per_proxy_and_timeout()
    {
        var a = _http.GetClient(Settings(30));
        Assert.Same(a, _http.GetClient(Settings(30)));
        Assert.NotSame(a, _http.GetClient(Settings(31)));
        Assert.NotSame(a, _http.GetClient(Settings(30, "http://proxy.example:8080")));
        Assert.Equal(TimeSpan.FromSeconds(120), _http.GetClient(null).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), a.Timeout);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
