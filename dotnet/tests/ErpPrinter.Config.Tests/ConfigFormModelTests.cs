using ErpPrinter.Config.Model;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Tests;

public class ConfigFormModelTests
{
    private readonly TestConfig _config = new();
    private readonly DesignTimeConfigActions _actions = new(delay: TimeSpan.Zero);

    private ConfigFormModel NewModel() => new(_config.Store);

    private static string[] Names(IEnumerable<SettingEditor> editors) => editors.Select(e => e.Name).ToArray();

    // --- Visibility per backend -------------------------------------------------------------

    [Fact]
    public void Shows_backend_specific_settings_only_for_the_selected_backend()
    {
        var model = NewModel();
        var profile = model.AddProfile("Test");

        var odoo = Names(profile.Editors());
        Assert.Contains("Database", odoo);
        Assert.Contains("OdooModel", odoo);
        Assert.DoesNotContain("Username", odoo);      // JSON-RPC only
        Assert.DoesNotContain("ApiSecret", odoo);     // ERPNext only
        Assert.DoesNotContain("WebhookFormat", odoo);

        profile.Set("Backend", "OdooJsonRpc");
        var rpc = Names(profile.Editors());
        Assert.Contains("Username", rpc);
        Assert.Contains("Database", rpc);

        profile.Set("Backend", "ERPNext");
        var erp = Names(profile.Editors());
        Assert.Contains("ApiSecret", erp);
        Assert.Contains("ERPNextFolder", erp);
        Assert.DoesNotContain("Database", erp);
        Assert.DoesNotContain("OdooModel", erp);

        profile.Set("Backend", "Webhook");
        var webhook = Names(profile.Editors());
        Assert.Contains("WebhookFormat", webhook);
        Assert.DoesNotContain("ERPNextFolder", webhook);
        Assert.DoesNotContain("OdooModel", webhook);
    }

    [Theory]
    [InlineData("OdooJson2")]
    [InlineData("OdooJsonRpc")]
    [InlineData("ERPNext")]
    [InlineData("Webhook")]
    public void Settings_without_a_backend_list_are_always_shown(string backend)
    {
        var profile = NewModel().AddProfile("Test");
        profile.Set("Backend", backend);
        var names = Names(profile.Editors());
        foreach (var common in new[] { "Enabled", "PrinterName", "PipeName", "FileNameTemplate", "Backend", "ServerUrl", "ApiKey" })
            Assert.Contains(common, names);
    }

    [Fact]
    public void Editors_follow_the_schema_order_and_keep_groups_together()
    {
        var profile = NewModel().AddProfile("Test");
        var groups = profile.Editors().Select(e => e.Group).ToList();
        // A group header is drawn whenever the group changes, so each group must appear as one block.
        Assert.Equal(groups.Distinct().Count(), groups.Zip(groups.Skip(1), (a, b) => a != b).Count(changed => changed) + 1);
        Assert.Equal(["Printer", "Connection", "Destination"], groups.Distinct());
    }

    [Fact]
    public void Global_editors_cover_the_whole_global_schema()
    {
        var model = NewModel();
        Assert.Equal(Schema.Global.Select(s => s.Name).Order(), Names(model.Global.Editors()).Order());
        // "Storage" is listed in two places in the schema but must be one block in the form.
        var groups = model.Global.Editors().Select(e => e.Group).ToList();
        Assert.Equal(groups.Distinct().Count(), groups.Zip(groups.Skip(1), (a, b) => a != b).Count(changed => changed) + 1);
        Assert.Equal(["Storage", "Delivery", "Updates"], groups.Distinct());
    }

    // --- Managed (policy) settings -----------------------------------------------------------

    [Fact]
    public void Managed_settings_are_read_only_and_ignore_edits()
    {
        _config.AddOdooProfile();
        _config.Registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Send to Odoo", "ServerUrl", "https://policy.example.com", RegistryValueType.String);
        _config.Registry.SetValue(ConfigStore.PolicyRoot, "PollIntervalSeconds", 77, RegistryValueType.DWord);
        var model = NewModel();

        var server = model.Find("Send to Odoo")!.Editors().Single(e => e.Name == "ServerUrl");
        Assert.True(server.ReadOnly);
        Assert.Equal("Server URL (policy)", server.Label);
        Assert.Equal("https://policy.example.com", server.Value);
        Assert.Contains("managed by Group Policy", server.ToolTip);
        server.Value = "https://attacker.example.com";
        Assert.Equal("https://policy.example.com", server.Value);

        var poll = model.Global.Editors().Single(e => e.Name == "PollIntervalSeconds");
        Assert.True(poll.ReadOnly);
        poll.Value = 5;
        Assert.Equal(77, poll.Value);
        Assert.False(model.Dirty);

        var notManaged = model.Global.Editors().Single(e => e.Name == "MaxAttempts");
        Assert.False(notManaged.ReadOnly);
        Assert.Equal("Max delivery attempts", notManaged.Label);
    }

    [Fact]
    public void Managed_settings_are_never_written_back()
    {
        _config.Registry.SetValue(ConfigStore.PolicyRoot, "PollIntervalSeconds", 77, RegistryValueType.DWord);
        var model = NewModel();
        model.Global.Set("PollIntervalSeconds", 5);
        model.Global.Set("MaxAttempts", 3);

        model.SaveAndApply(_actions);

        Assert.False(_config.GlobalValues.ContainsKey("PollIntervalSeconds"));
        Assert.Equal(3, _config.GlobalValues["MaxAttempts"]);
    }

    [Fact]
    public void Policy_defined_profiles_cannot_be_removed_or_renamed_and_are_labelled()
    {
        _config.Registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Head office", "ServerUrl", "https://hq.example.com", RegistryValueType.String);
        var model = NewModel();
        var profile = model.Find("Head office")!;

        Assert.True(profile.PolicyDefined);
        Assert.Equal("'Head office' is defined by Group Policy.", profile.HeaderText);
        Assert.Throws<InvalidOperationException>(() => model.RemoveProfile("Head office"));
        Assert.Throws<InvalidOperationException>(() => model.RenameProfile("Head office", "HQ"));
    }

    [Fact]
    public void Saving_a_policy_defined_profile_does_not_turn_it_into_a_local_one()
    {
        _config.Registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Head office", "ServerUrl", "https://hq.example.com", RegistryValueType.String);
        _config.Registry.SetValue($@"{ConfigStore.PolicyRoot}\Printers\Head office", "Backend", "Webhook", RegistryValueType.String);
        var model = NewModel();
        var profile = model.Find("Head office")!;
        profile.Set("FileNameTemplate", "{title}");   // equals the default: nothing to write
        profile.Set("Enabled", true);
        Assert.False(model.Dirty);

        Assert.True(model.SaveAndApply(_actions).Saved);

        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Head office"));
        Assert.True(model.Find("Head office")!.PolicyDefined);
    }

    // --- Secrets -----------------------------------------------------------------------------

    [Fact]
    public void Stored_secrets_are_never_echoed_into_the_editors()
    {
        _config.AddOdooProfile(apiKey: "super-secret-key");
        var model = NewModel();
        var profile = model.Find("Send to Odoo")!;

        var apiKey = profile.Editors().Single(e => e.Name == "ApiKey");
        Assert.Equal(SettingType.Secret, apiKey.Type);
        Assert.Equal("", apiKey.Value);
        Assert.Equal("Stored - leave empty to keep", apiKey.SecretCue);
        Assert.True(profile.SecretAvailable("ApiKey"));

        // Nothing the form can see contains the secret.
        Assert.DoesNotContain(profile.Editors(), e => Convert.ToString(e.Value) == "super-secret-key" || e.Label.Contains("super-secret"));
        Assert.DoesNotContain(profile.ToProfile().Values.Values, v => Convert.ToString(v) == "super-secret-key");
        Assert.DoesNotContain("ApiKey", profile.ToProfile().Values.Keys);
        Assert.Equal(BackendSecrets.Empty, profile.EnteredSecrets());
    }

    [Fact]
    public void A_missing_secret_says_not_set()
    {
        var profile = NewModel().AddProfile("Test");
        var apiKey = profile.Editors().Single(e => e.Name == "ApiKey");
        Assert.Equal("Not set", apiKey.SecretCue);
        Assert.False(profile.SecretAvailable("ApiKey"));
        Assert.Null(profile.Editors().First(e => e.Name == "ServerUrl").SecretCue);
    }

    [Fact]
    public void A_typed_secret_is_saved_and_an_empty_one_keeps_the_stored_value()
    {
        _config.AddOdooProfile(apiKey: "old-key");
        var model = NewModel();
        var profile = model.Find("Send to Odoo")!;

        profile.Set("ApiKey", "");                 // untouched box
        Assert.False(model.Dirty);
        model.SaveAndApply(_actions);
        Assert.Equal("old-key", _config.Store.Secrets.Get("Send to Odoo").ApiKey);

        model.Find("Send to Odoo")!.Set("ApiKey", "new-key");
        Assert.True(model.Dirty);
        Assert.Equal("new-key", model.Find("Send to Odoo")!.EnteredSecrets().ApiKey);
        model.SaveAndApply(_actions);

        Assert.Equal("new-key", _config.Store.Secrets.Get("Send to Odoo").ApiKey);
        Assert.Equal("", model.Find("Send to Odoo")!.Editors().Single(e => e.Name == "ApiKey").Value);   // after saving the box is empty again
    }

    [Fact]
    public void Secrets_are_not_trimmed_but_text_settings_are()
    {
        var profile = NewModel().AddProfile("Test");
        profile.Set("ApiKey", " key with spaces ");
        profile.Set("ServerUrl", "  https://example.com  ");
        Assert.Equal(" key with spaces ", profile.EnteredSecret("ApiKey"));
        Assert.Equal("https://example.com", profile.GetString("ServerUrl"));
    }

    // --- Dirty tracking and "write only what changed" -----------------------------------------

    [Fact]
    public void A_fresh_model_is_clean_and_reverting_an_edit_makes_it_clean_again()
    {
        _config.AddOdooProfile();
        var model = NewModel();
        Assert.False(model.Dirty);

        model.Global.Set("MaxAttempts", 5);
        Assert.True(model.Dirty);
        model.Global.Set("MaxAttempts", 0);
        Assert.False(model.Dirty);

        var profile = model.Find("Send to Odoo")!;
        profile.Set("FileNameTemplate", "{date}");
        Assert.True(profile.IsDirty);
        profile.Set("FileNameTemplate", "{title}");
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Saving_writes_only_changed_values()
    {
        _config.AddOdooProfile();
        var before = _config.ProfileValues("Send to Odoo").ToDictionary(p => p.Key, p => p.Value);
        var model = NewModel();
        model.Global.Set("PollIntervalSeconds", 30);
        model.Find("Send to Odoo")!.Set("FileNameTemplate", "{date}-{title}");

        var result = model.SaveAndApply(_actions);

        Assert.True(result.Saved);
        Assert.Equal(new Dictionary<string, object> { ["PollIntervalSeconds"] = 30 }, _config.GlobalValues.ToDictionary(p => p.Key, p => p.Value));
        var after = _config.ProfileValues("Send to Odoo");
        Assert.Equal("{date}-{title}", after["FileNameTemplate"]);
        Assert.Equal(before.Count + 1, after.Count);
        foreach (var (key, value) in before) Assert.Equal(value, after[key]);
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Saving_without_changes_writes_nothing()
    {
        _config.AddOdooProfile();
        var model = NewModel();
        model.SaveAndApply(_actions);
        Assert.Empty(_config.GlobalValues);
        Assert.Equal(["Backend", "ServerUrl"], _config.ProfileValues("Send to Odoo").Keys.Order().ToArray());
    }

    [Fact]
    public void Values_set_back_to_the_default_are_removed_from_the_registry()
    {
        _config.Store.SetSetting(null, "PollIntervalSeconds", 30);
        _config.AddOdooProfile(more: [("FileNameTemplate", "{date}")]);
        var model = NewModel();
        Assert.Equal(30, model.Global.Get("PollIntervalSeconds"));

        model.Global.Set("PollIntervalSeconds", 15);          // the default
        model.Find("Send to Odoo")!.Set("FileNameTemplate", "{title}");   // the default
        model.SaveAndApply(_actions);

        Assert.False(_config.GlobalValues.ContainsKey("PollIntervalSeconds"));
        Assert.False(_config.ProfileValues("Send to Odoo").ContainsKey("FileNameTemplate"));
    }

    [Fact]
    public void Settings_of_a_backend_that_is_not_selected_keep_their_values()
    {
        _config.AddOdooProfile(more: [("OdooModel", "documents.document")]);
        var model = NewModel();
        var profile = model.Find("Send to Odoo")!;
        profile.Set("Backend", "Webhook");
        Assert.DoesNotContain("OdooModel", Names(profile.Editors()));
        profile.Set("Backend", "OdooJson2");
        Assert.Equal("documents.document", profile.Editors().Single(e => e.Name == "OdooModel").Value);
    }

    [Fact]
    public void Int_and_choice_values_are_validated_by_the_schema()
    {
        var model = NewModel();
        Assert.Throws<SettingValueException>(() => model.Global.Set("PollIntervalSeconds", 1));
        Assert.Throws<SettingValueException>(() => model.Global.Set("LogLevel", "Chatty"));
        Assert.Throws<KeyNotFoundException>(() => model.Global.Set("NoSuchSetting", 1));
    }

    // --- Validation --------------------------------------------------------------------------

    [Fact]
    public void Invalid_profiles_are_reported_with_the_validator_messages()
    {
        var model = NewModel();
        var profile = model.AddProfile("Broken");   // no server URL, no API key

        var problems = model.Validate();

        var expected = ProfileValidator.Validate(profile.ToProfile(), _ => false).Select(p => $"Broken: {p}").ToList();
        Assert.Equal(expected, problems);
        Assert.Contains("Broken: Server URL is required.", problems);
        Assert.Contains("Broken: An API key is required.", problems);
    }

    [Fact]
    public void An_entered_secret_satisfies_the_validator_even_before_it_is_saved()
    {
        var model = NewModel();
        var profile = model.AddProfile("Fresh");
        profile.Set("ServerUrl", "https://example.com");
        Assert.Contains("Fresh: An API key is required.", model.Validate());
        profile.Set("ApiKey", "abc");
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Backend_requirements_are_validated_for_the_selected_backend()
    {
        var model = NewModel();
        var profile = model.AddProfile("Erp");
        profile.Set("Backend", "ERPNext");
        profile.Set("ServerUrl", "https://erp.example.com");
        profile.Set("ApiKey", "key");
        Assert.Equal(["Erp: ERPNext needs an API secret as well as an API key."], model.Validate());

        profile.Set("Backend", "OdooJsonRpc");
        Assert.Equal(["Erp: Odoo JSON-RPC needs the login of the API user."], model.Validate());

        profile.Set("Backend", "Webhook");
        Assert.Empty(model.Validate());   // a webhook needs no key
    }

    [Fact]
    public void Disabled_profiles_are_not_validated()
    {
        var model = NewModel();
        var profile = model.AddProfile("Off");
        Assert.NotEmpty(model.Validate());
        profile.Set("Enabled", false);
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Clashing_printer_names_are_reported_against_all_profiles()
    {
        _config.AddOdooProfile("Alpha", more: [("PrinterName", "Shared")]);
        _config.AddOdooProfile("Beta", more: [("PrinterName", "Shared")]);
        var problems = NewModel().Validate();
        Assert.Contains("Alpha: Printer name 'Shared' is also used by profile 'Beta'.", problems);
        Assert.Contains("Beta: Printer name 'Shared' is also used by profile 'Alpha'.", problems);
    }

    [Fact]
    public void Nothing_is_written_when_validation_fails()
    {
        var model = NewModel();
        model.AddProfile("Broken");
        model.Global.Set("MaxAttempts", 4);

        var result = model.SaveAndApply(_actions);

        Assert.False(result.Saved);
        Assert.NotEmpty(result.Problems);
        Assert.Empty(_config.GlobalValues);
        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Broken"));
        Assert.Empty(_actions.Calls);
        Assert.True(model.Dirty);
    }

    // --- Adding, removing and renaming profiles ----------------------------------------------

    [Fact]
    public void A_new_profile_is_unsaved_until_saved_and_only_non_defaults_are_written()
    {
        var model = NewModel();
        Assert.Equal("Send to ERP", model.SuggestName());
        var profile = model.AddProfile("  Invoices  ");

        Assert.Equal("Invoices", profile.Name);
        Assert.True(profile.IsNew);
        Assert.Equal("'Invoices' is new and not saved yet.", profile.HeaderText);
        Assert.Same(profile, model.Current);
        Assert.True(model.Dirty);
        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Invoices"));
        Assert.Equal("Invoices", profile.GetString("PrinterName"));
        Assert.Equal("ErpPrinter-Invoices", profile.GetString("PipeName"));

        profile.Set("ServerUrl", "https://example.com");
        profile.Set("ApiKey", "k");
        var result = model.SaveAndApply(_actions);

        Assert.True(result.Saved);
        Assert.Equal(["Backend", "ServerUrl"], _config.ProfileValues("Invoices").Keys.Order().ToArray());
        Assert.Equal("k", _config.Store.Secrets.Get("Invoices").ApiKey);
        Assert.False(model.Find("Invoices")!.IsNew);
        Assert.False(model.Dirty);
        Assert.Equal("Send to ERP 2", model.SuggestName());
    }

    [Theory]
    [InlineData("send to odoo")]
    [InlineData("Send to Odoo")]
    public void Duplicate_profile_names_are_rejected_ignoring_case(string name)
    {
        _config.AddOdooProfile();
        var model = NewModel();
        Assert.Equal($"A printer named '{name}' already exists.", model.CheckName(name));
        var ex = Assert.Throws<InvalidOperationException>(() => model.AddProfile(name));
        Assert.Equal($"A printer named '{name}' already exists.", ex.Message);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("back\\slash")]
    [InlineData("this name is way too long to be a printer name, it has more than 64 characters")]
    [InlineData("")]
    public void Invalid_profile_names_are_rejected_with_the_powershell_wording(string name)
    {
        var model = NewModel();
        Assert.Equal("Use up to 64 letters, digits, spaces and . ( ) & + - _", model.CheckName(name));
        Assert.Throws<InvalidOperationException>(() => model.AddProfile(name));
    }

    [Fact]
    public void Removing_an_existing_profile_deletes_its_key_and_secrets_on_save()
    {
        _config.AddOdooProfile("Keep");
        _config.AddOdooProfile("Drop");
        var model = NewModel();
        model.Select("Drop");

        model.RemoveProfile("Drop");

        Assert.Null(model.Current);
        Assert.Equal(["Keep"], model.Profiles.Select(p => p.Name));
        Assert.Equal(["Drop"], model.PendingRemovals);
        Assert.True(model.Dirty);
        Assert.True(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Drop"));   // not before saving

        model.SaveAndApply(_actions);

        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Drop"));
        Assert.False(_config.Store.Secrets.Has("Drop", "ApiKey"));
        Assert.True(_config.Store.Secrets.Has("Keep", "ApiKey"));
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Removing_a_profile_that_was_never_saved_leaves_nothing_to_save()
    {
        var model = NewModel();
        model.AddProfile("Scratch");
        model.RemoveProfile("Scratch");
        Assert.Empty(model.Profiles);
        Assert.Empty(model.PendingRemovals);
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Removing_and_adding_the_same_name_starts_from_a_clean_profile()
    {
        _config.AddOdooProfile("Again", more: [("FileNameTemplate", "{date}")]);
        var model = NewModel();
        model.RemoveProfile("Again");
        var fresh = model.AddProfile("Again");
        fresh.Set("ServerUrl", "https://new.example.com");
        fresh.Set("ApiKey", "new");

        model.SaveAndApply(_actions);

        var values = _config.ProfileValues("Again");
        Assert.False(values.ContainsKey("FileNameTemplate"));
        Assert.Equal("https://new.example.com", values["ServerUrl"]);
        Assert.Equal("new", _config.Store.Secrets.Get("Again").ApiKey);
    }

    [Fact]
    public void Renaming_an_existing_profile_moves_settings_and_secrets_on_save()
    {
        _config.AddOdooProfile("Old name", apiKey: "the-key", more: [("FileNameTemplate", "{date}"), ("PrinterName", "Custom printer")]);
        var model = NewModel();
        model.Select("Old name");

        var renamed = model.RenameProfile("Old name", "New name");

        Assert.Equal("New name", renamed.Name);
        Assert.True(renamed.IsNew);
        Assert.Same(renamed, model.Current);
        Assert.Equal(["New name"], model.Profiles.Select(p => p.Name));
        Assert.Equal(["Old name"], model.PendingRemovals);
        Assert.True(model.Dirty);
        Assert.Equal("", renamed.Editors().Single(e => e.Name == "ApiKey").Value);   // still not echoed
        Assert.Equal("Stored - leave empty to keep", renamed.Editors().Single(e => e.Name == "ApiKey").SecretCue);

        Assert.True(model.SaveAndApply(_actions).Saved);

        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Old name"));
        Assert.False(_config.Store.Secrets.Has("Old name", "ApiKey"));
        var values = _config.ProfileValues("New name");
        Assert.Equal("{date}", values["FileNameTemplate"]);
        Assert.Equal("Custom printer", values["PrinterName"]);          // a deliberate override survives
        Assert.Equal("https://example.odoo.com", values["ServerUrl"]);
        Assert.Equal("the-key", _config.Store.Secrets.Get("New name").ApiKey);
    }

    [Fact]
    public void Renaming_moves_names_derived_from_the_profile_name()
    {
        _config.AddOdooProfile("Old name");
        var model = NewModel();

        var renamed = model.RenameProfile("Old name", "New name");

        Assert.Equal("New name", renamed.GetString("PrinterName"));
        Assert.Equal("ErpPrinter-New_name", renamed.GetString("PipeName"));
        model.SaveAndApply(_actions);
        Assert.False(_config.ProfileValues("New name").ContainsKey("PrinterName"));   // still the default
    }

    [Fact]
    public void Renaming_an_unsaved_profile_just_renames_it()
    {
        var model = NewModel();
        var profile = model.AddProfile("Draft");
        profile.Set("ServerUrl", "https://example.com");
        profile.Set("ApiKey", "typed");

        var renamed = model.RenameProfile("Draft", "Final");

        Assert.Empty(model.PendingRemovals);
        Assert.Equal("https://example.com", renamed.GetString("ServerUrl"));
        model.SaveAndApply(_actions);
        Assert.Equal("typed", _config.Store.Secrets.Get("Final").ApiKey);
        Assert.False(_config.Registry.KeyExists($@"{ConfigStore.RegistryRoot}\Printers\Draft"));
    }

    [Fact]
    public void Renaming_to_an_existing_name_is_rejected()
    {
        _config.AddOdooProfile("One");
        _config.AddOdooProfile("Two");
        var model = NewModel();
        var ex = Assert.Throws<InvalidOperationException>(() => model.RenameProfile("One", "two"));
        Assert.Equal("A printer named 'two' already exists.", ex.Message);
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Renaming_keeps_the_position_in_the_list()
    {
        _config.AddOdooProfile("A");
        _config.AddOdooProfile("B");
        _config.AddOdooProfile("C");
        var model = NewModel();
        model.RenameProfile("B", "Z");
        Assert.Equal(["A", "Z", "C"], model.Profiles.Select(p => p.Name));
    }

    // --- Save and apply ----------------------------------------------------------------------

    [Fact]
    public void Saving_syncs_the_printers_and_restarts_the_service_in_that_order()
    {
        var model = NewModel();
        model.Global.Set("MaxAttempts", 2);
        var result = model.SaveAndApply(_actions);
        Assert.True(result.Saved);
        Assert.Null(result.Warning);
        Assert.Equal(["SyncPrinters", "RestartService"], _actions.Calls);
    }

    [Fact]
    public void A_printer_sync_failure_is_a_warning_and_the_settings_are_still_saved()
    {
        var model = NewModel();
        model.Global.Set("MaxAttempts", 2);
        _actions.SyncFailure = "The print spooler is not running.";

        var result = model.SaveAndApply(_actions);

        Assert.True(result.Saved);
        Assert.Equal("The print spooler is not running.", result.Warning);
        Assert.Equal(2, _config.GlobalValues["MaxAttempts"]);
        Assert.Contains("RestartService", _actions.Calls);
        Assert.False(model.Dirty);
    }

    [Fact]
    public void Reload_discards_edits_and_keeps_the_selection()
    {
        _config.AddOdooProfile("One");
        _config.AddOdooProfile("Two");
        var model = NewModel();
        model.Select("Two");
        model.Global.Set("MaxAttempts", 9);
        model.Find("Two")!.Set("ServerUrl", "https://other.example.com");
        model.RemoveProfile("One");

        model.Reload();

        Assert.False(model.Dirty);
        Assert.Equal(2, model.Profiles.Count);
        Assert.Equal("Two", model.Current?.Name);
        Assert.Equal(0, model.Global.Get("MaxAttempts"));
    }

    [Fact]
    public void The_profile_as_edited_is_what_connection_tests_receive()
    {
        _config.AddOdooProfile();
        var model = NewModel();
        var edit = model.Find("Send to Odoo")!;
        edit.Set("ServerUrl", "https://changed.example.com");
        edit.Set("ApiKey", "typed-key");

        var profile = edit.ToProfile();

        Assert.Equal("Send to Odoo", profile.Name);
        Assert.Equal("https://changed.example.com", profile.ServerUrl);
        Assert.Equal(new BackendSecrets("typed-key", ""), edit.EnteredSecrets());
    }

    [Fact]
    public void Tooltips_are_wrapped_help_text()
    {
        var model = NewModel();
        var proxy = model.Global.Editors().Single(e => e.Name == "ProxyUrl");
        Assert.Equal(Schema.Find(SettingScope.Global, "ProxyUrl").Help, proxy.ToolTip.Replace("\n", " "));

        var longHelp = model.AddProfile("T").Editors().Single(e => e.Name == "OdooModel").ToolTip;
        Assert.All(longHelp.Split('\n'), line => Assert.True(line.Length <= 80, line));
        Assert.Equal("", model.Global.Editors().Single(e => e.Name == "HttpTimeoutSeconds").ToolTip);
    }
}
