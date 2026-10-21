using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;
using ErpPrinter.Windows.Storage;
using Microsoft.Win32;

namespace ErpPrinter.Windows.Tests;

public sealed class RegistryStoreTests : IDisposable
{
    private readonly WinTempRegistry _temp = new();
    private readonly RegistryStore _store;

    public RegistryStoreTests() => _store = new RegistryStore(_temp.Root);

    public void Dispose() => _temp.Dispose();

    private const string Key = @"SOFTWARE\ErpPrinter\Printers\Send to Odoo";

    [Fact]
    public void Values_round_trip_with_their_types()
    {
        _store.SetValue(Key, "PrinterName", "Send to Odoo", RegistryValueType.String);
        _store.SetValue(Key, "Enabled", 1, RegistryValueType.DWord);
        _store.SetValue(Key, "Blob", new byte[] { 1, 2, 3, 255 }, RegistryValueType.Binary);
        _store.SetValue(Key, "Lines", new[] { "a|b", "c|d" }, RegistryValueType.MultiString);

        var values = _store.GetValues(Key);

        Assert.Equal("Send to Odoo", values["PrinterName"]);
        Assert.Equal(1, values["Enabled"]);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, (byte[])values["Blob"]);
        Assert.Equal(new[] { "a|b", "c|d" }, (string[])values["Lines"]);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public void Names_are_case_insensitive()
    {
        _store.SetValue(Key, "PipeName", "x", RegistryValueType.String);

        Assert.Equal("x", _store.GetValues(Key.ToUpperInvariant())["PIPENAME"]);
        Assert.True(_store.KeyExists(Key.ToLowerInvariant()));
    }

    [Fact]
    public void A_bool_is_stored_as_a_dword()
    {
        _store.SetValue(Key, "Enabled", true, RegistryValueType.DWord);

        using var key = _temp.Root.OpenSubKey(Key)!;
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("Enabled"));
        Assert.Equal(1, key.GetValue("Enabled"));
    }

    [Fact]
    public void Missing_keys_read_as_empty()
    {
        Assert.False(_store.KeyExists(@"SOFTWARE\Nope"));
        Assert.Empty(_store.GetValues(@"SOFTWARE\Nope"));
        Assert.Empty(_store.GetSubKeyNames(@"SOFTWARE\Nope"));
    }

    [Fact]
    public void Sub_keys_are_listed_and_keys_can_be_created_and_removed_recursively()
    {
        _store.EnsureKey(@"SOFTWARE\ErpPrinter\Printers\B");
        _store.SetValue(@"SOFTWARE\ErpPrinter\Printers\A", "Enabled", 0, RegistryValueType.DWord);
        _store.SetValue(@"SOFTWARE\ErpPrinter\Printers\A\Child", "x", "y", RegistryValueType.String);

        Assert.Equal(["A", "B"], _store.GetSubKeyNames(@"SOFTWARE\ErpPrinter\Printers").Order(StringComparer.OrdinalIgnoreCase));

        _store.RemoveKey(@"SOFTWARE\ErpPrinter\Printers\A");

        Assert.False(_store.KeyExists(@"SOFTWARE\ErpPrinter\Printers\A"));
        Assert.False(_store.KeyExists(@"SOFTWARE\ErpPrinter\Printers\A\Child"));
        Assert.True(_store.KeyExists(@"SOFTWARE\ErpPrinter\Printers\B"));
        _store.RemoveKey(@"SOFTWARE\ErpPrinter\Printers\A"); // removing a missing key is not an error
    }

    [Fact]
    public void Values_can_be_removed_and_removing_a_missing_one_is_fine()
    {
        _store.SetValue(Key, "A", "1", RegistryValueType.String);
        _store.SetValue(Key, "B", "2", RegistryValueType.String);

        _store.RemoveValue(Key, "A");
        _store.RemoveValue(Key, "Missing");
        _store.RemoveValue(@"SOFTWARE\Nope", "A");

        Assert.Equal(["B"], _store.GetValues(Key).Keys);
    }

    [Fact]
    public void A_base_path_scopes_the_store()
    {
        var scoped = new RegistryStore(_temp.Root, "Scoped");
        scoped.SetValue(@"SOFTWARE\X", "v", "1", RegistryValueType.String);

        Assert.True(_store.KeyExists(@"Scoped\SOFTWARE\X"));
        Assert.Equal("1", scoped.GetValues(@"SOFTWARE\X")["v"]);
    }

    [Fact]
    public void ConfigStore_works_on_the_real_registry()
    {
        // No secrets here: the secrets key locks itself to SYSTEM and Administrators, which an unelevated test could not use afterwards.
        var config = new ConfigStore(_store, new SecretStore(_store, new DpapiSecretProtector()));

        config.CreateProfile("Send to Odoo", new Dictionary<string, object?> { ["ServerUrl"] = "https://x.example", ["Enabled"] = false });
        config.SetSetting(null, "PollIntervalSeconds", 30);

        var loaded = config.Load();

        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal("Send to Odoo", profile.Name);
        Assert.Equal("https://x.example", profile.ServerUrl);
        Assert.False(profile.Enabled);
        Assert.Equal(30, loaded.Global.PollIntervalSeconds);
    }

    [Fact]
    public void The_secrets_security_is_protected_and_limited_to_system_and_administrators()
    {
        var security = RegistryStore.CreateSecretsSecurity();

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().ToList();
        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], rules.Select(r => r.IdentityReference.Value).Order());
        Assert.All(rules, r =>
        {
            Assert.Equal(RegistryRights.FullControl, r.RegistryRights);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
        });
    }

    [Fact]
    public void EnsureSecretsKey_creates_a_locked_down_key()
    {
        _store.EnsureSecretsKey();

        using var key = _temp.Root.OpenSubKey(@"SOFTWARE\ErpPrinter\Secrets", RegistryKeyPermissionCheck.Default, RegistryRights.ReadPermissions);
        Assert.NotNull(key);
        var security = key.GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().ToList();
        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], rules.Select(r => r.IdentityReference.Value).Order());
        Assert.All(rules, r => Assert.Equal(RegistryRights.FullControl, r.RegistryRights & RegistryRights.FullControl));

        _store.EnsureSecretsKey(); // idempotent, also on a key that is already locked
    }
}

public sealed class DpapiSecretProtectorTests
{
    [Fact]
    public void Round_trips_text_including_non_ascii()
    {
        var protector = new DpapiSecretProtector();

        var cipher = protector.Protect("pässwörd-€-🔑");

        Assert.DoesNotContain("pässwörd", Encoding.UTF8.GetString(cipher));
        Assert.Equal("pässwörd-€-🔑", protector.Unprotect(cipher));
    }

    [Fact]
    public void Uses_the_entropy_of_the_PowerShell_module()
    {
        // What Protect-ErpSecret does: UTF-8 bytes, entropy "ErpPrinter/v1", machine scope.
        var entropy = Encoding.UTF8.GetBytes("ErpPrinter/v1");
        var fromPowerShell = System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes("secret"), entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);

        Assert.Equal("secret", new DpapiSecretProtector().Unprotect(fromPowerShell));

        var ours = new DpapiSecretProtector().Protect("secret");
        var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(ours, entropy, System.Security.Cryptography.DataProtectionScope.LocalMachine);
        Assert.Equal("secret", Encoding.UTF8.GetString(decrypted));
    }

    [Fact]
    public void Garbage_does_not_decrypt()
    {
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new DpapiSecretProtector().Unprotect([1, 2, 3, 4]));
    }
}
