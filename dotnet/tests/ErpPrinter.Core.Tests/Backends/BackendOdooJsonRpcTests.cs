using System.Text.Json.Nodes;

namespace ErpPrinter.Core.Tests;

public class BackendOdooJsonRpcTests : BackendTestBase
{
    private static List<string?> Strings(JsonNode? array) => array!.AsArray().Select(n => (string?)n).ToList();

    [Fact]
    public async Task Authenticates_then_creates_through_execute_kw()
    {
        Reply(200, """{"jsonrpc": "2.0", "id": 1, "result": 6}""");
        Reply(200, """{"jsonrpc": "2.0", "id": 2, "result": 77}""");
        var profile = NewProfile("OdooJsonRpc", new()
        {
            ["Database"] = "prod", ["Username"] = "api@corp.example", ["OdooModel"] = "documents.document", ["OdooDefaultValues"] = """{"folder_id": 4}""",
        });

        Assert.Equal("77", await SendAsync(profile));

        Assert.Equal("https://erp.example.com/jsonrpc", Calls[0].Uri);
        var auth = Json(Calls[0]);
        Assert.Equal("2.0", (string?)auth["jsonrpc"]);
        Assert.Equal("call", (string?)auth["method"]);
        Assert.Equal("common", (string?)auth["params"]!["service"]);
        Assert.Equal("authenticate", (string?)auth["params"]!["method"]);
        var authArgs = auth["params"]!["args"]!.AsArray();
        Assert.Equal(["prod", "api@corp.example", "key"], authArgs.Take(3).Select(n => (string?)n));

        var create = Json(Calls[1])["params"]!;
        Assert.Equal("object", (string?)create["service"]);
        Assert.Equal("execute_kw", (string?)create["method"]);
        var args = create["args"]!.AsArray();
        Assert.Equal(6, (int)args[1]!);
        Assert.Equal("documents.document", (string?)args[3]);
        Assert.Equal("create", (string?)args[4]);
        Assert.Equal("Invoice 7.pdf", (string?)args[5]![0]!["name"]);
        Assert.Equal(4, (int)args[5]![0]!["folder_id"]!);
        Assert.StartsWith("%PDF", Decoded(args[5]![0]!["datas"]));
    }

    [Fact]
    public async Task Reuses_the_session_for_the_next_document()
    {
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": 1}""");
        Reply(200, """{"result": 2}""");
        var profile = NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" });

        await SendAsync(profile);
        await SendAsync(profile);

        Assert.Equal(3, Calls.Count);
        Assert.Equal("execute_kw", (string?)Json(Calls[2])["params"]!["method"]);
    }

    [Fact]
    public async Task Authenticates_again_after_a_failed_call()
    {
        Reply(200, """{"result": 6}""");
        Reply(200, """{"error": {"message": "Odoo Server Error", "data": {"name": "psycopg2.OperationalError", "message": "db down"}}}""");
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": 3}""");
        var profile = NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" });

        await Assert.ThrowsAsync<ErpException>(() => SendAsync(profile));
        Assert.Equal("3", await SendAsync(profile));

        Assert.Equal("authenticate", (string?)Json(Calls[2])["params"]!["method"]);
    }

    [Fact]
    public async Task Creates_a_record_in_any_model_with_its_own_field_names()
    {
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": 91}""");
        var profile = NewProfile("OdooJsonRpc", new()
        {
            ["Database"] = "prod", ["Username"] = "api", ["OdooModel"] = "edi.exchange.record", ["OdooFileField"] = "exchange_file", ["OdooNameField"] = "exchange_filename",
            ["OdooDefaultValues"] = """{"backend_id": 3, "type_id": 8, "edi_exchange_state": "input_received"}""",
        });

        Assert.Equal("91", await SendAsync(profile));

        var create = Json(Calls[1])["params"]!["args"]!.AsArray();
        Assert.Equal(["edi.exchange.record", "create"], create.Skip(3).Take(2).Select(n => (string?)n));
        var vals = create[5]![0]!;
        Assert.Equal(8, (int)vals["type_id"]!);
        Assert.Equal("input_received", (string?)vals["edi_exchange_state"]);
        Assert.Equal("Invoice 7.pdf", (string?)vals["exchange_filename"]);
        Assert.StartsWith("%PDF", Decoded(vals["exchange_file"]));
    }

    [Fact]
    public async Task Maps_the_Windows_user_through_execute_kw()
    {
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": [{"id": 12}]}""");
        Reply(200, """{"result": 31}""");
        var profile = NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api", ["UserMapping"] = true, ["UserEmailDomain"] = "corp.example" });

        Assert.Equal("31", await SendAsync(profile));

        var search = Json(Calls[1])["params"]!["args"]!.AsArray();
        Assert.Equal("res.users", (string?)search[3]);
        Assert.Equal("search_read", (string?)search[4]);
        Assert.Equal("|", (string?)search[5]![0]![0]);
        Assert.Contains("jdoe@corp.example", Strings(search[5]![0]![1]![2]));
        Assert.Equal(["id"], Strings(search[6]!["fields"]));
        Assert.Equal(1, (int)search[6]!["limit"]!);
        Assert.Equal(12, (int)Json(Calls[2])["params"]!["args"]![5]![0]!["owner_id"]!);
    }

    [Fact]
    public async Task Checks_the_fields_through_fields_get_in_the_connection_test()
    {
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": 4}""");
        Reply(200, """{"result": {"exchange_file": {"type": "binary"}, "exchange_filename": {"type": "char"}, "backend_id": {"type": "many2one"}}}""");
        var profile = NewProfile("OdooJsonRpc", new()
        {
            ["Database"] = "prod", ["Username"] = "api", ["OdooModel"] = "edi.exchange.record", ["OdooFileField"] = "exchange_file", ["OdooNameField"] = "exchange_filename",
            ["OdooDefaultValues"] = """{"backend_id": 3}""",
        });

        var message = await TestAsync(profile);

        Assert.Equal("Connected to Odoo database 'prod' as uid 6. The API user can see 4 edi.exchange.record records. Fields found: exchange_file, exchange_filename, backend_id.", message);
        var count = Json(Calls[1])["params"]!["args"]!.AsArray();
        Assert.Equal("search_count", (string?)count[4]);
        Assert.Empty(count[5]![0]!.AsArray());
        var fieldsGet = Json(Calls[2])["params"]!["args"]!.AsArray();
        Assert.Equal(["edi.exchange.record", "fields_get"], fieldsGet.Skip(3).Take(2).Select(n => (string?)n));
        Assert.Equal(["exchange_file", "exchange_filename", "backend_id"], Strings(fieldsGet[5]![0]));
        Assert.Equal(["type"], Strings(fieldsGet[6]!["attributes"]));
    }

    [Fact]
    public async Task Discovers_the_database_when_the_server_has_exactly_one()
    {
        Reply(200, """{"result": ["only"]}""");
        Reply(200, """{"result": 6}""");
        Reply(200, """{"result": 3}""");
        Reply(200, """{"result": {"datas": {"type": "binary"}, "name": {"type": "char"}}}""");

        var message = await TestAsync(NewProfile("OdooJsonRpc", new() { ["Username"] = "api" }));

        Assert.Contains("database 'only' as uid 6", message);
        Assert.Equal("db", (string?)Json(Calls[0])["params"]!["service"]);
        Assert.Equal("list", (string?)Json(Calls[0])["params"]!["method"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Asks_for_the_database_setting_when_the_server_hosts_none_or_several(int count)
    {
        var names = string.Join(", ", Enumerable.Range(1, count).Select(i => $"\"db{i}\""));
        Reply(200, $$"""{"result": [{{names}}]}""");

        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Username"] = "api" })));

        Assert.Equal($"Set the Database setting; the server lists {count} databases.", ex.Message);
    }

    [Theory]
    [InlineData("odoo.exceptions.ValidationError", true)]
    [InlineData("builtins.ValueError", true)]
    [InlineData("odoo.exceptions.AccessError", false)]
    [InlineData("psycopg2.OperationalError", false)]
    public async Task Treats_Odoo_error_names_as_permanent_or_transient(string name, bool permanent)
    {
        var error = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = 200, ["message"] = "Odoo Server Error", ["data"] = new JsonObject { ["name"] = name, ["message"] = "nope" } },
        };
        Reply(200, """{"result": 6}""");
        Reply(200, error.ToJsonString());

        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" })));

        Assert.Contains($"nope ({name})", ex.Message);
        Assert.Equal(permanent, ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Falls_back_to_the_top_level_error_message()
    {
        Reply(200, """{"error": {"code": 100, "message": "Session expired"}}""");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" })));
        Assert.Equal("Odoo common.authenticate failed: Session expired", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Reports_a_rejected_login()
    {
        Reply(200, """{"result": false}""");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" })));
        Assert.Contains("rejected login 'api'", ex.Message);
    }

    [Fact]
    public async Task Reports_non_JSON_answers()
    {
        Reply(200, "<html>proxy login</html>");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" })));
        Assert.Equal("Odoo returned a non-JSON response to common.authenticate.", ex.Message);
    }

    [Fact]
    public async Task Classifies_HTTP_errors()
    {
        Reply(503, "maintenance");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJsonRpc", new() { ["Database"] = "db", ["Username"] = "api" })));
        Assert.Equal("Odoo common.authenticate failed with HTTP 503: maintenance", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }
}
