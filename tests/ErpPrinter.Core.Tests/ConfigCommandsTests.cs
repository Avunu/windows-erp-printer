using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Core.Tests;

public class ConfigCommandsTests
{
    private readonly MemoryRegistryStore _registry = new();
    private readonly ConfigStore _store;
    private readonly ConfigCommands _commands;

    public ConfigCommandsTests()
    {
        _store = new ConfigStore(_registry, new SecretStore(_registry, new FakeProtector()));
        _commands = new ConfigCommands(_store);
    }

    private static Dictionary<string, string> Pairs(params string[] items) =>
        items.Select(i => i.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void Lists_no_profiles_when_there_are_none() => Assert.Equal("No printer profiles.", _commands.ListProfiles());

    [Fact]
    public void Adds_a_profile_with_settings_and_an_encrypted_secret()
    {
        _commands.AddProfile("Send to Odoo", Pairs("backend=OdooJson2", "ServerUrl=https://acme.odoo.com", "OdooModel=documents.document", "ApiKey=s3cret"), force: false);
        var profile = _store.GetProfile("Send to Odoo")!;
        Assert.Equal("documents.document", profile.OdooModel);
        Assert.Equal("s3cret", _store.Secrets.Get("Send to Odoo").ApiKey);
        Assert.Contains("Send to Odoo  [enabled, OdooJson2, printer 'Send to Odoo']", _commands.ListProfiles());
    }

    [Fact]
    public void Setting_names_are_matched_without_regard_to_case()
    {
        _commands.AddProfile("P", Pairs("serverurl=https://x.example"), force: false);
        Assert.Equal("https://x.example", _store.GetProfile("P")!.ServerUrl);
    }

    [Fact]
    public void Unknown_setting_names_are_rejected_with_the_valid_ones_listed()
    {
        var ex = Assert.Throws<ArgumentException>(() => _commands.AddProfile("P", Pairs("Nope=1"), force: false));
        Assert.Contains("Unknown profile setting 'Nope'", ex.Message);
        Assert.Contains("ServerUrl", ex.Message);
    }

    [Fact]
    public void Show_never_reveals_secrets_and_hides_other_backends_settings()
    {
        _commands.AddProfile("P", Pairs("Backend=Webhook", "ServerUrl=https://hooks.example/x", "ApiKey=topsecret"), force: false);
        var text = _commands.ShowProfile("P");
        Assert.DoesNotContain("topsecret", text);
        Assert.Contains("ApiKey = (stored)", text);
        Assert.Contains("WebhookFormat = Multipart", text);
        Assert.DoesNotContain("OdooModel", text);
    }

    [Fact]
    public void Sets_global_and_profile_settings_and_clears_them()
    {
        _commands.AddProfile("P", Pairs("ServerUrl=https://x.example"), force: false);
        _commands.Set(null, Pairs("PollIntervalSeconds=30"));
        _commands.Set("P", Pairs("FileNameTemplate={date} {title}"));
        Assert.Equal(30, _store.LoadGlobal().PollIntervalSeconds);
        Assert.Equal("{date} {title}", _store.GetProfile("P")!.FileNameTemplate);
        _commands.Clear(null, ["pollintervalseconds"]);
        Assert.Equal(15, _store.LoadGlobal().PollIntervalSeconds);
    }

    [Fact]
    public void Rejects_invalid_values_and_missing_profiles()
    {
        Assert.Throws<SettingValueException>(() => _commands.Set(null, Pairs("PollIntervalSeconds=1")));
        Assert.Throws<ArgumentException>(() => _commands.Set("Missing", Pairs("Enabled=false")));
        Assert.Throws<ArgumentException>(() => _commands.RemoveProfile("Missing"));
        Assert.Throws<ArgumentException>(() => _commands.SetSecret("Missing", "ApiKey", "x"));
    }

    [Fact]
    public void Stores_and_reports_secrets()
    {
        _commands.AddProfile("P", Pairs("ServerUrl=https://x.example"), force: false);
        Assert.Contains("ApiKey: not set", _commands.SecretStatus("P"));
        _commands.SetSecret("P", "apikey", "k");
        Assert.Contains("ApiKey: stored", _commands.SecretStatus("P"));
        Assert.Throws<ArgumentException>(() => _commands.SetSecret("P", "ServerUrl", "x"));
    }

    [Fact]
    public void Removes_a_profile_and_its_secrets()
    {
        _commands.AddProfile("P", Pairs("ServerUrl=https://x.example", "ApiKey=k"), force: false);
        _commands.RemoveProfile("P");
        Assert.Null(_store.GetProfile("P"));
        Assert.False(_store.Secrets.Has("P", "ApiKey"));
    }

    [Fact]
    public void Refuses_to_overwrite_a_profile_unless_forced()
    {
        _commands.AddProfile("P", Pairs("ServerUrl=https://a.example"), force: false);
        Assert.Throws<InvalidOperationException>(() => _commands.AddProfile("P", Pairs("ServerUrl=https://b.example"), force: false));
        _commands.AddProfile("P", Pairs("ServerUrl=https://b.example"), force: true);
        Assert.Equal("https://b.example", _store.GetProfile("P")!.ServerUrl);
    }
}
