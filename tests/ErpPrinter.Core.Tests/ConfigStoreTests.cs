using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

/// <summary>Reversible stand-in for DPAPI.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(string plainText) => System.Text.Encoding.UTF8.GetBytes("enc:" + plainText);
    public string Unprotect(byte[] cipher) => System.Text.Encoding.UTF8.GetString(cipher)["enc:".Length..];
}

public class ConfigStoreTests
{
    private const string Root = ConfigStore.RegistryRoot;
    private const string Policy = ConfigStore.PolicyRoot;

    private readonly MemoryRegistryStore _registry = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests()
    {
        _store = new ConfigStore(_registry, new SecretStore(_registry, new FakeProtector()));
    }

    // --- Loading ---------------------------------------------------------------------------

    [Fact]
    public void Returns_schema_defaults_when_nothing_is_configured()
    {
        var config = _store.Load();
        Assert.Equal(15, config.Global.PollIntervalSeconds);
        Assert.Equal("Information", config.Global.LogLevel);
        Assert.True(config.Global.AutoUpdate);
        Assert.EndsWith("ErpPrinter", config.Global.DataRoot);
        Assert.Empty(config.Profiles);
    }

    [Fact]
    public void Machine_values_override_defaults()
    {
        _registry.SetValue(Root, "PollIntervalSeconds", 30, RegistryValueType.DWord);
        _registry.SetValue(Root, "AutoUpdate", 0, RegistryValueType.DWord);
        var global = _store.LoadGlobal();
        Assert.Equal(30, global.PollIntervalSeconds);
        Assert.False(global.AutoUpdate);
    }

    [Fact]
    public void Policy_values_override_machine_values_and_are_reported_as_managed()
    {
        _registry.SetValue(Root, "PollIntervalSeconds", 30, RegistryValueType.DWord);
        _registry.SetValue(Policy, "PollIntervalSeconds", 60, RegistryValueType.DWord);
        var global = _store.LoadGlobal();
        Assert.Equal(60, global.PollIntervalSeconds);
        Assert.Contains("PollIntervalSeconds", global.Managed);
        Assert.DoesNotContain("MaxAttempts", global.Managed);
    }

    [Fact]
    public void Invalid_registry_values_are_ignored_and_logged()
    {
        var log = new Logging.MemoryLog();
        var store = new ConfigStore(_registry, new SecretStore(_registry, new FakeProtector()), log);
        _registry.SetValue(Root, "PollIntervalSeconds", 1, RegistryValueType.DWord);
        _registry.SetValue(Root, "LogLevel", "Chatty", RegistryValueType.String);
        var global = store.LoadGlobal();
        Assert.Equal(15, global.PollIntervalSeconds);
        Assert.Equal("Information", global.LogLevel);
        Assert.Equal(2, log.Entries.Count(e => e.Level == Logging.ErpLogLevel.Warning));
    }

    [Fact]
    public void Lists_machine_and_policy_only_profiles_with_derived_defaults()
    {
        _registry.EnsureKey($@"{Root}\Printers\Send to Odoo");
        _registry.EnsureKey($@"{Policy}\Printers\From policy");
        var profiles = _store.LoadProfiles();
        Assert.Equal(["From policy", "Send to Odoo"], profiles.Select(p => p.Name));
        var local = profiles.Single(p => p.Name == "Send to Odoo");
        Assert.Equal("Send to Odoo", local.PrinterName);
        Assert.Equal("ErpPrinter-Send_to_Odoo", local.PipeName);
        Assert.Equal(@"\\.\pipe\ErpPrinter-Send_to_Odoo", local.PortName);
        Assert.False(local.PolicyDefined);
        Assert.True(profiles.Single(p => p.Name == "From policy").PolicyDefined);
    }

    // --- Writing ---------------------------------------------------------------------------

    [Fact]
    public void Stores_booleans_and_integers_as_DWORD_and_strings_as_String()
    {
        _store.SetSetting(null, "PollIntervalSeconds", 30);
        _store.SetSetting(null, "AutoUpdate", false);
        _store.SetSetting(null, "ProxyUrl", "http://proxy:8080");
        Assert.Equal(RegistryValueType.DWord, _registry.Kinds[$@"{Root}|PollIntervalSeconds"]);
        Assert.Equal(RegistryValueType.DWord, _registry.Kinds[$@"{Root}|AutoUpdate"]);
        Assert.Equal(RegistryValueType.String, _registry.Kinds[$@"{Root}|ProxyUrl"]);
        Assert.Equal(0, _registry.GetValues(Root)["AutoUpdate"]);
    }

    [Fact]
    public void Removes_values_that_equal_the_default()
    {
        _store.SetSetting(null, "PollIntervalSeconds", 30);
        _store.SetSetting(null, "PollIntervalSeconds", 15);
        Assert.False(_registry.GetValues(Root).ContainsKey("PollIntervalSeconds"));
    }

    [Fact]
    public void Stores_an_empty_file_name_field_explicitly_so_it_overrides_the_default()
    {
        _store.CreateProfile("P", new Dictionary<string, object?> { ["OdooNameField"] = "" });
        Assert.Equal("", _store.GetProfile("P")!.OdooNameField);
    }

    [Fact]
    public void Always_stores_the_backend_of_a_profile_explicitly()
    {
        _store.CreateProfile("P");
        Assert.Equal("OdooJson2", _registry.GetValues($@"{Root}\Printers\P")["Backend"]);
    }

    [Fact]
    public void Keeps_an_all_defaults_profile_key()
    {
        _store.SetSetting("P", "Enabled", true);
        Assert.True(_registry.KeyExists($@"{Root}\Printers\P"));
    }

    [Fact]
    public void Rejects_unknown_settings_secrets_and_out_of_range_values()
    {
        Assert.Throws<ArgumentException>(() => _store.SetSetting(null, "Nope", 1));
        Assert.Throws<InvalidOperationException>(() => _store.SetSetting("P", "ApiKey", "x"));
        Assert.Throws<SettingValueException>(() => _store.SetSetting(null, "PollIntervalSeconds", 1));
        Assert.Throws<SettingValueException>(() => _store.SetSetting(null, "LogLevel", "Chatty"));
    }

    [Fact]
    public void Clears_a_value()
    {
        _store.SetSetting(null, "PollIntervalSeconds", 30);
        _store.ClearSetting(null, "PollIntervalSeconds");
        Assert.Equal(15, _store.LoadGlobal().PollIntervalSeconds);
    }

    // --- Profiles and secrets --------------------------------------------------------------

    [Fact]
    public void Creates_a_profile_with_settings_and_encrypted_secrets()
    {
        _store.CreateProfile("Send to Odoo", new Dictionary<string, object?>
        {
            ["Backend"] = "OdooJson2", ["ServerUrl"] = "https://acme.odoo.com", ["OdooModel"] = "documents.document", ["ApiKey"] = "s3cret",
        });
        var profile = _store.GetProfile("Send to Odoo")!;
        Assert.Equal("https://acme.odoo.com", profile.ServerUrl);
        Assert.Equal("documents.document", profile.OdooModel);
        Assert.Equal("s3cret", _store.Secrets.Get("Send to Odoo").ApiKey);
        var stored = (byte[])_registry.GetValues(SecretStore.PathFor("Send to Odoo"))["ApiKey"];
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes("s3cret"), stored); // never stored as plain text
        Assert.Equal(RegistryValueType.Binary, _registry.Kinds[$@"{SecretStore.PathFor("Send to Odoo")}|ApiKey"]);
    }

    [Fact]
    public void Refuses_to_overwrite_a_profile_unless_forced()
    {
        _store.CreateProfile("P");
        Assert.Throws<InvalidOperationException>(() => _store.CreateProfile("P"));
        _store.CreateProfile("P", force: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("bad\\name")]
    [InlineData("bad/name")]
    public void Rejects_invalid_profile_names(string name)
    {
        Assert.Throws<ArgumentException>(() => _store.CreateProfile(name));
    }

    [Fact]
    public void Removes_a_profile_together_with_its_secrets()
    {
        _store.CreateProfile("P", new Dictionary<string, object?> { ["ApiKey"] = "k" });
        _store.RemoveProfile("P");
        Assert.Null(_store.GetProfile("P"));
        Assert.False(_store.Secrets.Has("P", SecretNames.ApiKey));
    }

    [Fact]
    public void An_empty_value_clears_a_secret()
    {
        _store.Secrets.Set("P", SecretNames.ApiKey, "k");
        Assert.True(_store.Secrets.Has("P", SecretNames.ApiKey));
        _store.Secrets.Set("P", SecretNames.ApiKey, "");
        Assert.False(_store.Secrets.Has("P", SecretNames.ApiKey));
    }

    [Fact]
    public void Unsaved_secrets_take_precedence_over_stored_ones_only_when_given()
    {
        _store.Secrets.Set("P", SecretNames.ApiKey, "stored");
        Assert.Equal("typed", _store.Secrets.Effective("P", new BackendSecrets("typed")).ApiKey);
        Assert.Equal("stored", _store.Secrets.Effective("P", new BackendSecrets("")).ApiKey);
    }

    [Fact]
    public void An_undecryptable_secret_reads_as_empty_and_is_logged()
    {
        var log = new Logging.MemoryLog();
        var secrets = new SecretStore(_registry, new FakeProtector(), log);
        _registry.SetValue(SecretStore.PathFor("P"), "ApiKey", new byte[] { 1 }, RegistryValueType.Binary); // shorter than the fake's prefix
        Assert.Equal("", secrets.Get("P").ApiKey);
        Assert.Contains(log.Entries, e => e.Level == Logging.ErpLogLevel.Warning && e.Message.Contains("Could not decrypt ApiKey"));
    }

    // --- Validation ------------------------------------------------------------------------

    private static PrinterProfile Odoo(Dictionary<string, object>? overrides = null)
    {
        var values = new Dictionary<string, object> { ["ServerUrl"] = "https://acme.odoo.com" };
        if (overrides is not null) foreach (var (k, v) in overrides) values[k] = v;
        return PrinterProfile.Create("Send to Odoo", values);
    }

    private static IReadOnlyList<string> Check(PrinterProfile p, bool key = true, bool secret = false, IEnumerable<PrinterProfile>? others = null) =>
        ProfileValidator.Validate(p, name => name == "ApiKey" ? key : secret, others);

    [Fact]
    public void Accepts_a_complete_Odoo_profile() => Assert.Empty(Check(Odoo()));

    [Fact]
    public void Reports_a_missing_key_and_url()
    {
        Assert.Contains("An API key is required.", Check(Odoo(), key: false));
        Assert.Contains("Server URL is required.", Check(PrinterProfile.Create("P")));
        Assert.Contains(Check(Odoo(new() { ["ServerUrl"] = "ftp://x" })), p => p.Contains("not an http(s) URL"));
    }

    [Fact]
    public void Reports_bad_names_and_fields()
    {
        Assert.Contains(Check(Odoo(new() { ["PipeName"] = "a b" })), p => p.StartsWith("Pipe name"));
        Assert.Contains(Check(Odoo(new() { ["OdooModel"] = "bad model" })), p => p.StartsWith("Odoo model"));
        Assert.Contains(Check(Odoo(new() { ["OdooFileField"] = "1x" })), p => p.StartsWith("File field"));
        Assert.Contains(Check(Odoo(new() { ["OdooNameField"] = "bad field" })), p => p.StartsWith("File name field"));
    }

    [Fact]
    public void Checks_default_values_json()
    {
        Assert.Empty(Check(Odoo(new() { ["OdooDefaultValues"] = "{\"folder_id\": 7, \"description\": \"Printed by {user}\"}" })));
        Assert.Contains(Check(Odoo(new() { ["OdooDefaultValues"] = "[1]" })), p => p.StartsWith("Default values are not a valid JSON object"));
        Assert.Contains(Check(Odoo(new() { ["OdooDefaultValues"] = "{oops" })), p => p.StartsWith("Default values are not a valid JSON object"));
    }

    [Fact]
    public void Does_not_check_the_user_field_while_users_are_not_mapped()
    {
        Assert.Empty(Check(Odoo(new() { ["OdooUserField"] = "not valid" })));
        Assert.Contains(Check(Odoo(new() { ["OdooUserField"] = "not valid", ["UserMapping"] = true })), p => p.StartsWith("User field"));
    }

    [Fact]
    public void JsonRpc_needs_a_login_and_ERPNext_needs_a_secret_and_a_pair_of_attachment_fields()
    {
        Assert.Contains("Odoo JSON-RPC needs the login of the API user.", Check(Odoo(new() { ["Backend"] = "OdooJsonRpc" })));
        var next = PrinterProfile.Create("N", new Dictionary<string, object> { ["Backend"] = "ERPNext", ["ServerUrl"] = "https://erp.example.com" });
        Assert.Contains("ERPNext needs an API secret as well as an API key.", Check(next));
        Assert.Empty(Check(next, secret: true));
        var half = PrinterProfile.Create("N", new Dictionary<string, object> { ["Backend"] = "ERPNext", ["ServerUrl"] = "https://x.example", ["ERPNextDoctype"] = "Sales Invoice" });
        Assert.Contains(Check(half, secret: true), p => p.Contains("both"));
    }

    [Fact]
    public void Allows_a_webhook_without_a_key()
    {
        var hook = PrinterProfile.Create("W", new Dictionary<string, object> { ["Backend"] = "Webhook", ["ServerUrl"] = "https://hooks.example.com/x" });
        Assert.Empty(Check(hook, key: false));
    }

    [Fact]
    public void Detects_printer_and_pipe_name_clashes()
    {
        var other = PrinterProfile.Create("Other", new Dictionary<string, object> { ["PrinterName"] = "Send to Odoo", ["PipeName"] = "ErpPrinter-Send_to_Odoo" });
        var problems = Check(Odoo(), others: [other]);
        Assert.Contains(problems, p => p.StartsWith("Printer name 'Send to Odoo' is also used"));
        Assert.Contains(problems, p => p.StartsWith("Pipe name 'ErpPrinter-Send_to_Odoo' is also used"));
    }

    // --- Naming ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Send to Odoo", "Send_to_Odoo")]
    [InlineData("  ..weird//name  ", "weird_name")]
    [InlineData("", "default")]
    [InlineData("***", "default")]
    public void Makes_names_safe_for_files_and_pipes(string input, string expected) => Assert.Equal(expected, Naming.ToSafeName(input));
}
