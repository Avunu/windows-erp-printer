using ErpPrinter.Logging;

namespace ErpPrinter.Core.Tests;

public class BackendOdooJson2Tests : BackendTestBase
{
    [Fact]
    public async Task Creates_an_ir_attachment_with_the_file_name_and_base64_content_by_default()
    {
        Reply(200, "[42]");

        var id = await SendAsync(NewProfile("OdooJson2", new() { ["Database"] = "prod" }));

        Assert.Equal("42", id);
        var call = Assert.Single(Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://erp.example.com/json/2/ir.attachment/create", call.Uri);
        Assert.Equal("bearer key", call.Headers["Authorization"]);
        Assert.Equal("prod", call.Headers["X-Odoo-Database"]);
        Assert.StartsWith("ErpPrinter/", call.Headers["User-Agent"]);
        var vals = Json(call)["vals_list"]![0]!;
        Assert.Equal(["name", "datas"], Names(vals));
        Assert.Equal("Invoice 7.pdf", (string?)vals["name"]);
        Assert.StartsWith("%PDF", Decoded(vals["datas"]));
    }

    [Fact]
    public async Task Sends_no_database_header_when_none_is_set()
    {
        Reply(200, "[1]");
        await SendAsync(NewProfile("OdooJson2"));
        Assert.False(Calls[0].Headers.ContainsKey("X-Odoo-Database"));
    }

    [Fact]
    public async Task Creates_a_record_in_any_model_with_its_own_field_names_and_default_values()
    {
        Reply(200, "[55]");
        var profile = NewProfile("OdooJson2", new()
        {
            ["OdooModel"] = "edi.exchange.record", ["OdooFileField"] = "exchange_file", ["OdooNameField"] = "exchange_filename",
            ["OdooDefaultValues"] = """{"backend_id": 3, "type_id": 8, "edi_exchange_state": "input_received"}""",
        });

        Assert.Equal("55", await SendAsync(profile));

        Assert.Single(Calls);
        Assert.EndsWith("/json/2/edi.exchange.record/create", Calls[0].Uri);
        var vals = Json(Calls[0])["vals_list"]![0]!;
        Assert.Equal(3, (int)vals["backend_id"]!);
        Assert.Equal(8, (int)vals["type_id"]!);
        Assert.Equal("input_received", (string?)vals["edi_exchange_state"]);
        Assert.Equal("Invoice 7.pdf", (string?)vals["exchange_filename"]);
        Assert.StartsWith("%PDF", Decoded(vals["exchange_file"]));
        Assert.Null(vals["name"]);
        Assert.Null(vals["datas"]);
        Assert.Equal("exchange_file", Names(vals).Last());
    }

    [Fact]
    public async Task Expands_tokens_in_default_values_and_passes_nested_JSON_through()
    {
        Reply(200, "[5]");
        var profile = NewProfile("OdooJson2", new()
        {
            ["OdooDefaultValues"] = """{"description": "From {user} on {computer}", "public": false, "res_id": 3, "tag_ids": [[6, 0, [3]]]}""",
        });

        Assert.Equal("5", await SendAsync(profile));

        var body = Calls[0].Body;
        var vals = Json(Calls[0])["vals_list"]![0]!;
        Assert.Equal(@"From CORP\jdoe on PC-01", (string?)vals["description"]);
        Assert.False((bool)vals["public"]!);
        Assert.Equal(3, (int)vals["res_id"]!);
        Assert.Contains("\"tag_ids\":[[6,0,[3]]]", body);
    }

    [Fact]
    public async Task Lets_default_values_override_the_file_name_but_never_the_document()
    {
        Reply(200, "[6]");

        Assert.Equal("6", await SendAsync(NewProfile("OdooJson2", new() { ["OdooDefaultValues"] = """{"name": "Fixed.pdf", "datas": "not a pdf"}""" })));

        var vals = Json(Calls[0])["vals_list"]![0]!;
        Assert.Equal("Fixed.pdf", (string?)vals["name"]);
        Assert.StartsWith("%PDF", Decoded(vals["datas"]));
    }

    [Fact]
    public async Task Sends_no_name_when_the_name_field_is_empty()
    {
        Reply(200, "[7]");

        Assert.Equal("7", await SendAsync(NewProfile("OdooJson2", new() { ["OdooNameField"] = "" })));

        Assert.Equal(["datas"], Names(Json(Calls[0])["vals_list"]![0]));
    }

    [Fact]
    public async Task Maps_the_Windows_user_to_the_user_field_and_caches_the_lookup()
    {
        Reply(200, """[{"id": 9}]""");
        Reply(200, "[1]");
        Reply(200, "[2]");
        var profile = NewProfile("OdooJson2", new() { ["OdooModel"] = "documents.document", ["UserMapping"] = true, ["UserEmailDomain"] = "corp.example" });

        Assert.Equal("1", await SendAsync(profile));
        Assert.Equal("2", await SendAsync(profile));

        Assert.Equal(3, Calls.Count);
        var search = Json(Calls[0]);
        Assert.EndsWith("/json/2/res.users/search_read", Calls[0].Uri);
        Assert.Equal("|", (string?)search["domain"]![0]);
        var candidates = search["domain"]![1]![2]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("jdoe@corp.example", candidates);
        Assert.Contains("john.doe@corp.example", candidates);
        Assert.Equal(["login", "in"], search["domain"]![1]!.AsArray().Take(2).Select(n => (string?)n));
        Assert.Equal(["email", "in"], search["domain"]![2]!.AsArray().Take(2).Select(n => (string?)n));
        Assert.Equal(["id"], search["fields"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(1, (int)search["limit"]!);
        Assert.EndsWith("/json/2/documents.document/create", Calls[1].Uri);
        Assert.Equal(9, (int)Json(Calls[1])["vals_list"]![0]!["owner_id"]!);
        Assert.Equal(9, (int)Json(Calls[2])["vals_list"]![0]!["owner_id"]!);
    }

    [Fact]
    public async Task Writes_the_mapped_user_to_a_custom_user_field()
    {
        Reply(200, """[{"id": 9}]""");
        Reply(200, "[1]");

        Assert.Equal("1", await SendAsync(NewProfile("OdooJson2", new() { ["UserMapping"] = true, ["OdooUserField"] = "user_id" })));

        var vals = Json(Calls[1])["vals_list"]![0]!;
        Assert.Equal(9, (int)vals["user_id"]!);
        Assert.Null(vals["owner_id"]);
    }

    [Fact]
    public async Task Does_not_look_users_up_unless_mapping_is_on()
    {
        Reply(200, "[1]");
        Assert.Equal("1", await SendAsync(NewProfile("OdooJson2")));
        Assert.Single(Calls);
    }

    [Fact]
    public async Task Sends_a_single_user_candidate_as_a_list()
    {
        DirectoryLookup = new BackendFakeDirectory();
        Meta.User = "jdoe";
        Reply(200, "[]");
        Reply(200, "[1]");

        await SendAsync(NewProfile("OdooJson2", new() { ["UserMapping"] = true }));

        Assert.Contains("[\"login\",\"in\",[\"jdoe\"]]", Calls[0].Body);
    }

    [Fact]
    public async Task Creates_without_an_owner_and_remembers_the_miss_when_no_user_matches()
    {
        DirectoryLookup = new BackendFakeDirectory();
        Reply(200, "[]");
        Reply(200, "[1]");
        Reply(200, "[2]");
        var profile = NewProfile("OdooJson2", new() { ["UserMapping"] = true });

        await SendAsync(profile);
        await SendAsync(profile);

        Assert.Equal(3, Calls.Count);
        Assert.Null(Json(Calls[1])["vals_list"]![0]!["owner_id"]);
        Assert.Contains(Log.Entries, e => e.Level == ErpLogLevel.Information && e.Message.Contains("No Odoo user matches 'CORP\\jdoe'"));
    }

    [Fact]
    public async Task Still_creates_the_record_when_the_user_lookup_fails()
    {
        Reply(500, "boom");
        Reply(200, "[1]");

        Assert.Equal("1", await SendAsync(NewProfile("OdooJson2", new() { ["UserMapping"] = true })));

        Assert.Null(Json(Calls[1])["vals_list"]![0]!["owner_id"]);
        Assert.Contains(Log.Entries, e => e.Level == ErpLogLevel.Warning && e.Message.StartsWith("User lookup for 'CORP\\jdoe' failed"));
    }

    [Fact]
    public async Task Fails_permanently_without_calling_Odoo_when_the_default_values_are_not_a_JSON_object()
    {
        var ex = await Assert.ThrowsAsync<ErpException>(() =>
            SendAsync(NewProfile("OdooJson2", new() { ["OdooDefaultValues"] = """{"partner_id": {id}}""" })));

        Assert.Contains("Default values are not a valid JSON object", ex.Message);
        Assert.True(ErpException.IsPermanent(ex));
        Assert.Empty(Calls);
    }

    [Fact]
    public async Task Fails_permanently_when_the_profile_has_no_file_field()
    {
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJson2", new() { ["OdooFileField"] = "" })));

        Assert.Contains("no File field", ex.Message);
        Assert.True(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Explains_a_missing_JSON2_endpoint()
    {
        Reply(404, "");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJson2")));
        Assert.Contains("OdooJsonRpc backend", ex.Message);
        Assert.False(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Classifies_an_error_status_from_Odoo()
    {
        Reply(422, """{"error": {"message": "Odoo Server Error", "data": {"message": "Invalid field folder_id"}}}""");
        var ex = await Assert.ThrowsAsync<ErpException>(() => SendAsync(NewProfile("OdooJson2")));
        Assert.Contains("Odoo ir.attachment.create failed with HTTP 422: Invalid field folder_id", ex.Message);
        Assert.True(ErpException.IsPermanent(ex));
    }

    [Fact]
    public async Task Tests_the_connection_with_search_count_then_checks_the_fields_with_fields_get()
    {
        Reply(200, "12");
        Reply(200, """{"datas": {"type": "binary"}, "name": {"type": "char"}, "folder_id": {"type": "many2one"}}""");
        var profile = NewProfile("OdooJson2", new() { ["OdooModel"] = "documents.document", ["OdooDefaultValues"] = """{"folder_id": 7}""" });

        var message = await TestAsync(profile);

        Assert.Equal("Connected to Odoo (JSON-2). The API user can see 12 documents.document records. Fields found: datas, name, folder_id.", message);
        Assert.EndsWith("/documents.document/search_count", Calls[0].Uri);
        Assert.EndsWith("/documents.document/fields_get", Calls[1].Uri);
        var fieldsGet = Json(Calls[1]);
        Assert.Equal(["datas", "name", "folder_id"], fieldsGet["allfields"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["type"], fieldsGet["attributes"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public async Task Includes_the_user_field_in_the_field_check_only_when_users_are_mapped()
    {
        Reply(200, "1");
        Reply(200, """{"datas": {"type": "binary"}, "name": {"type": "char"}, "owner_id": {"type": "many2one"}}""");

        Assert.EndsWith("Fields found: datas, name, owner_id.", await TestAsync(NewProfile("OdooJson2", new() { ["UserMapping"] = true })));
    }

    [Fact]
    public async Task Reports_fields_the_model_does_not_have()
    {
        Reply(200, "1");
        Reply(200, """{"datas": {"type": "binary"}}""");

        var ex = await Assert.ThrowsAsync<ErpException>(() =>
            TestAsync(NewProfile("OdooJson2", new() { ["OdooDefaultValues"] = """{"folder_id": 7, "nope": 1}""" })));

        Assert.EndsWith("Model ir.attachment has no field named name, folder_id, nope.", ex.Message);
    }

    [Fact]
    public async Task Requires_the_file_field_to_be_binary()
    {
        Reply(200, "1");
        Reply(200, """{"datas": {"type": "char"}, "name": {"type": "char"}}""");

        var ex = await Assert.ThrowsAsync<ErpException>(() => TestAsync(NewProfile("OdooJson2")));

        Assert.Contains("datas is of type char; it must be a binary field", ex.Message);
    }
}
