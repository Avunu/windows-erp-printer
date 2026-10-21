using ErpPrinter.Backends;
using ErpPrinter.Http;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

public class BackendERPNextTests : BackendTestBase
{
    [Fact]
    public async Task Uploads_with_token_auth_and_attachment_fields()
    {
        Reply(200, """{"message": {"name": "f00d", "file_url": "/private/files/Invoice 7.pdf"}}""");
        var profile = NewProfile("ERPNext", new() { ["ERPNextDoctype"] = "Purchase Invoice", ["ERPNextDocname"] = "PINV-{jobid}", ["ERPNextFolder"] = "Home/Scans" });

        Assert.Equal("f00d", await SendAsync(profile));

        var call = Assert.Single(Calls);
        Assert.Equal("https://erp.example.com/api/method/upload_file", call.Uri);
        Assert.Equal("token key:secret", call.Headers["Authorization"]);
        Assert.Equal("application/json", call.Headers["Accept"]);
        Assert.Matches("name=\"?doctype\"?[\\s\\S]*Purchase Invoice", call.Body);
        Assert.Matches("name=\"?docname\"?[\\s\\S]*PINV-3", call.Body);
        Assert.Matches("name=\"?folder\"?[\\s\\S]*Home/Scans", call.Body);
        Assert.Matches("name=\"?is_private\"?[\\s\\S]*1", call.Body);
        Assert.Matches("name=\"?file_name\"?[\\s\\S]*Invoice 7.pdf", call.Body);
        Assert.Matches("name=\"?file\"?; filename=\"?Invoice 7.pdf\"?", call.Body);
        Assert.Contains("%PDF-1.4", call.Body);
    }

    [Fact]
    public async Task Uses_the_Home_folder_and_skips_the_attachment_without_a_doctype()
    {
        Reply(200, """{"message": {"name": "f00d"}}""");

        await SendAsync(NewProfile("ERPNext", new() { ["ERPNextFolder"] = "", ["ERPNextDocname"] = "X", ["ERPNextIsPrivate"] = false }));

        var body = Calls[0].Body;
        Assert.Matches("name=\"?folder\"?[\\s\\S]*Home", body);
        Assert.Matches("name=\"?is_private\"?[\\s\\S]*0", body);
        Assert.DoesNotContain("docname", body);
        Assert.DoesNotContain("doctype", body);
    }

    [Fact]
    public async Task Returns_an_empty_id_when_the_reply_has_no_message()
    {
        Reply(200, "{}");
        Assert.Equal("", await SendAsync(NewProfile("ERPNext")));
    }

    [Fact]
    public async Task Reads_the_logged_in_user_for_the_connection_test()
    {
        Reply(200, """{"message": "api@example.com"}""");

        Assert.Equal("Connected to ERPNext as api@example.com.", await TestAsync(NewProfile("ERPNext")));

        Assert.Equal(HttpMethod.Get, Calls[0].Method);
        Assert.Equal("https://erp.example.com/api/method/frappe.auth.get_logged_user", Calls[0].Uri);
    }

    [Fact]
    public async Task Prefers_the_given_secrets_and_reports_rejected_credentials_as_transient()
    {
        Reply(401, "");

        var ex = await Assert.ThrowsAsync<ErpException>(() => TestAsync(NewProfile("ERPNext"), new BackendSecrets("k", "s")));

        Assert.Equal("token k:s", Calls[0].Headers["Authorization"]);
        Assert.Contains("HTTP 401", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Treats_a_Frappe_validation_error_HTTP_417_as_permanent()
    {
        Reply(417, """{"exception": "frappe.exceptions.ValidationError: Folder missing"}""");

        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("ERPNext")));

        Assert.Contains("Folder missing", ex.Message);
        Assert.True(ErpException.IsPermanent(ex));
    }
}

public class BackendWebhookTests : BackendTestBase
{
    [Fact]
    public async Task Posts_multipart_with_metadata_and_an_idempotency_key()
    {
        Reply(201, "");
        var profile = NewProfile("Webhook", new() { ["ServerUrl"] = "https://hooks.example.com/print", ["WebhookAuthHeader"] = "X-Api-Key", ["WebhookFileField"] = "document" });

        Assert.Equal("HTTP 201", await SendAsync(profile));

        var call = Assert.Single(Calls);
        Assert.Equal("https://hooks.example.com/print", call.Uri);
        Assert.Equal("key", call.Headers["X-Api-Key"]);
        Assert.Equal("doc1", call.Headers["Idempotency-Key"]);
        Assert.Equal("document", call.Headers["X-ErpPrinter-Event"]);
        Assert.Matches("name=\"?document\"?", call.Body);
        Assert.Contains(@"CORP\jdoe", call.Body);
        Assert.Matches("name=\"?pages\"?[\\s\\S]*\r\n2\r\n", call.Body);
        Assert.Matches("name=\"?metadata\"?", call.Body);
        Assert.Contains("\"fileName\":\"Invoice 7.pdf\"", call.Body);
        Assert.Contains("%PDF-1.4", call.Body);
    }

    [Fact]
    public async Task Names_the_file_field_file_when_none_is_set()
    {
        Reply(200, "");
        await SendAsync(NewProfile("Webhook", new() { ["WebhookFileField"] = "" }));
        Assert.Matches("name=\"?file\"?; filename=", Calls[0].Body);
    }

    [Fact]
    public async Task Posts_JSON_with_base64_content()
    {
        Reply(200, """{"id": "w-1"}""");

        Assert.Equal("w-1", await SendAsync(NewProfile("Webhook", new() { ["WebhookFormat"] = "Json" }), new BackendSecrets("")));

        var body = Json(Calls[0]);
        Assert.Equal("application/pdf", (string?)body["contentType"]);
        Assert.Equal("Invoice 7", (string?)body["metadata"]!["title"]);
        Assert.Equal("Invoice 7.pdf", (string?)body["metadata"]!["fileName"]);
        Assert.Equal(2, (int)body["metadata"]!["pages"]!);
        Assert.Equal("3", (string?)body["metadata"]!["jobId"]);
        Assert.StartsWith("2026-01-02T03:04:05", (string?)body["metadata"]!["printed"]);
        Assert.StartsWith("%PDF", Decoded(body["contentBase64"]));
        Assert.False(Calls[0].Headers.ContainsKey("Authorization"));
        Assert.Equal("doc1", Calls[0].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task Sends_the_API_key_in_the_configured_header_only_when_both_are_set()
    {
        Reply(200, "");
        Reply(200, "");

        await SendAsync(NewProfile("Webhook"));
        await SendAsync(NewProfile("Webhook", new() { ["WebhookAuthHeader"] = "" }));

        Assert.Equal("key", Calls[0].Headers["Authorization"]);
        Assert.False(Calls[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Tests_the_connection_with_a_test_event()
    {
        Reply(204, "");

        Assert.Equal("Webhook answered HTTP 204.", await TestAsync(NewProfile("Webhook")));

        var call = Assert.Single(Calls);
        Assert.Equal("test", call.Headers["X-ErpPrinter-Event"]);
        Assert.False(call.Headers.ContainsKey("Idempotency-Key"));
        var body = Json(call);
        Assert.Equal("test", (string?)body["event"]);
        Assert.Equal("P", (string?)body["profile"]);
        Assert.Equal(Environment.MachineName, (string?)body["computer"]);
    }

    [Fact]
    public async Task Classifies_webhook_errors()
    {
        Reply(400, "<p>bad  request</p>");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("Webhook")));
        Assert.Equal("Webhook failed with HTTP 400: bad request", ex.Message);
        Assert.True(ErpException.IsPermanent(ex));
    }
}

public class BackendRegistryTests
{
    private readonly ErpHttp _http = new(new BackendFakeHandler());

    [Theory]
    [InlineData("OdooJson2")]
    [InlineData("OdooJsonRpc")]
    [InlineData("ERPNext")]
    [InlineData("Webhook")]
    [InlineData("webhook")]
    public void Finds_every_backend_in_the_schema(string name) =>
        Assert.Equal(name, new BackendRegistry(_http).Get(name).Name, ignoreCase: true);

    [Fact]
    public void Provides_a_backend_for_each_name_in_the_schema() =>
        Assert.All(Schema.BackendNames, name => Assert.Equal(name, new BackendRegistry(_http).Get(name).Name));

    [Fact]
    public void Throws_a_permanent_error_for_unknown_backends()
    {
        var ex = Assert.Throws<ErpException>(() => new BackendRegistry(_http).Get("Nope"));
        Assert.Equal("Backend 'Nope' is not available in this version.", ex.Message);
        Assert.True(ex.Permanent);
    }
}
