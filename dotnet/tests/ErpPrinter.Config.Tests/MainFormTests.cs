using ErpPrinter.Config.Model;
using ErpPrinter.Config.Ui;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Tests;

/// <summary>Drives the real form (on an STA thread, with the demo configuration and fake actions and dialogs).</summary>
public class MainFormTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(TimeSpan? delay = null)
        {
            Demo = Composition.CreateDemo(delay ?? TimeSpan.Zero);
            Actions = (DesignTimeConfigActions)Demo.Actions;
            Form = new MainForm(new ConfigFormModel(Demo.Store), Actions, Dialogs, Shell);
            Form.Show();
            Sta.Pump();
        }

        public ConfigComposition Demo { get; }
        public DesignTimeConfigActions Actions { get; }
        public FakeDialogs Dialogs { get; } = new();
        public FakeShell Shell { get; } = new();
        public MainForm Form { get; }

        public ListBox ProfileList => Form.Find<ListBox>(_ => true);

        public void SelectProfile(string name)
        {
            ProfileList.SelectedItem = name;
            Sta.Pump();
        }

        public void SelectTab(int index)
        {
            Form.Tabs.SelectedIndex = index;
            Sta.Pump();
        }

        public int StatusCalls => Actions.Calls.Count(c => c == "GetStatus");

        public void Dispose() => Form.Dispose();
    }

    // --- The window ---------------------------------------------------------------------------

    [Fact]
    public void Builds_the_three_tabs_and_lists_the_configured_printers() => Sta.Run(() =>
    {
        using var rig = new Rig();
        var form = rig.Form;

        Assert.Equal(["Printers", "Settings", "Status"], form.Tabs.TabPages.Cast<TabPage>().Select(t => t.Text));
        Assert.StartsWith("ERP Printer configuration (", form.Text);
        Assert.Equal("Segoe UI", form.Font.FontFamily.Name);
        Assert.Equal(9f, form.Font.SizeInPoints);
        Assert.NotNull(form.Icon);
        Assert.Equal(["Head office", "Send to Odoo", "Vendor bills", "Warehouse ERPNext", "Webhook archive"], rig.ProfileList.Items.Cast<string>());
        Assert.Equal("Head office", rig.ProfileList.SelectedItem);
        Assert.True(form.RefreshTimerEnabled);
        Assert.False(form.Model.Dirty);
    });

    [Fact]
    public void Generates_one_control_per_setting_by_type() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        var controls = rig.Form.ProfileTable.Controls;

        Assert.IsType<CheckBox>(controls["Enabled"]);
        Assert.IsType<TextBox>(controls["PrinterName"]);
        Assert.IsType<ComboBox>(controls["Backend"]);
        Assert.Equal(["OdooJson2", "OdooJsonRpc", "ERPNext", "Webhook"], ((ComboBox)controls["Backend"]).Items.Cast<string>());
        Assert.Equal(ComboBoxStyle.DropDownList, ((ComboBox)controls["Backend"]).DropDownStyle);
        Assert.Equal("OdooJson2", ((ComboBox)controls["Backend"]).SelectedItem);
        Assert.True(((CheckBox)controls["UserMapping"]).Checked);

        rig.SelectTab(1);
        var global = rig.Form.GlobalTable.Controls;
        var poll = Assert.IsType<NumericUpDown>(global["PollIntervalSeconds"]);
        Assert.Equal(2, poll.Minimum);
        Assert.Equal(3600, poll.Maximum);
        Assert.Equal(60, poll.Value);   // from policy
        Assert.IsType<ComboBox>(global["LogLevel"]);
        Assert.IsType<CheckBox>(global["AutoUpdate"]);
    });

    [Fact]
    public void Policy_managed_settings_are_read_only_in_the_form() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        var server = Assert.IsType<TextBox>(rig.Form.ProfileTable.Controls["ServerUrl"]);
        Assert.True(server.ReadOnly);
        Assert.True(((TextBox)rig.Form.ProfileTable.Controls["PrinterName"]).ReadOnly == false);

        var label = rig.Form.ProfileTable.Panel.Descendants().OfType<Label>().Single(l => l.Text == "Server URL (policy)");
        Assert.NotNull(label);

        rig.SelectTab(1);
        var poll = Assert.IsType<NumericUpDown>(rig.Form.GlobalTable.Controls["PollIntervalSeconds"]);
        Assert.False(poll.Enabled);
        Assert.True(rig.Form.GlobalTable.Controls["MaxAttempts"].Enabled);
    });

    [Fact]
    public void Secret_boxes_show_a_state_never_the_value() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        var apiKey = Assert.IsType<TextBox>(rig.Form.ProfileTable.Controls["ApiKey"]);
        Assert.True(apiKey.UseSystemPasswordChar);
        Assert.Equal("", apiKey.Text);
        Assert.Equal("Stored - leave empty to keep", apiKey.PlaceholderText);

        rig.SelectProfile("Webhook archive");   // no key stored for this one
        Assert.Equal("Not set", ((TextBox)rig.Form.ProfileTable.Controls["ApiKey"]).PlaceholderText);
    });

    [Fact]
    public void Changing_the_backend_regenerates_the_form_for_that_backend() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        var controls = () => rig.Form.ProfileTable.Controls;
        Assert.True(controls().ContainsKey("OdooModel"));
        Assert.False(controls().ContainsKey("ApiSecret"));

        ((ComboBox)controls()["Backend"]).SelectedItem = "ERPNext";
        Sta.Pump();

        Assert.True(controls().ContainsKey("ApiSecret"));
        Assert.True(controls().ContainsKey("ERPNextFolder"));
        Assert.False(controls().ContainsKey("OdooModel"));
        Assert.Equal("ERPNext", rig.Form.Model.Current!.Backend);
        Assert.True(rig.Form.Model.Dirty);
        Assert.Equal("ERPNext", ((ComboBox)controls()["Backend"]).SelectedItem);
    });

    [Fact]
    public void Edits_in_the_controls_reach_the_model_and_keep_between_profiles() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        ((TextBox)rig.Form.ProfileTable.Controls["FileNameTemplate"]).Text = "{date}-{title}";
        ((TextBox)rig.Form.ProfileTable.Controls["ApiKey"]).Text = "typed-secret";
        Assert.True(rig.Form.Model.Dirty);

        rig.SelectProfile("Vendor bills");
        rig.SelectProfile("Send to Odoo");

        Assert.Equal("{date}-{title}", ((TextBox)rig.Form.ProfileTable.Controls["FileNameTemplate"]).Text);
        Assert.Equal("typed-secret", rig.Form.Model.Current!.EnteredSecret("ApiKey"));
    });

    // --- Printers tab buttons -----------------------------------------------------------------

    [Fact]
    public void Add_rename_and_remove_printers() => Sta.Run(() =>
    {
        using var rig = new Rig();

        rig.Dialogs.Name = "Invoices";
        rig.Form.Button("Add...").PerformClick();
        Assert.Equal("Invoices", rig.ProfileList.SelectedItem);
        Assert.Contains("Invoices", rig.ProfileList.Items.Cast<string>());
        Assert.True(rig.Form.Model.Dirty);

        rig.Dialogs.Name = "Invoices 2";
        rig.Form.Button("Rename...").PerformClick();
        Assert.Equal("Invoices 2", rig.ProfileList.SelectedItem);
        Assert.DoesNotContain("Invoices", rig.ProfileList.Items.Cast<string>());

        rig.Dialogs.Answer = DialogResult.Yes;
        rig.Form.Button("Remove").PerformClick();
        Assert.DoesNotContain("Invoices 2", rig.ProfileList.Items.Cast<string>());
        Assert.False(rig.Form.Model.Dirty);
        Assert.Contains(rig.Dialogs.Messages, m => m.Caption == "Remove printer" && m.Text.StartsWith("Remove the printer 'Invoices 2'?"));
    });

    [Fact]
    public void Adding_with_a_bad_or_duplicate_name_shows_the_error() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.Dialogs.Name = "Send to Odoo";
        rig.Form.Button("Add...").PerformClick();
        rig.Dialogs.Name = "bad/name";
        rig.Form.Button("Add...").PerformClick();

        Assert.Equal(["A printer named 'Send to Odoo' already exists.", "Use up to 64 letters, digits, spaces and . ( ) & + - _"],
            rig.Dialogs.Messages.Where(m => m.Icon == MessageBoxIcon.Error).Select(m => m.Text));
        Assert.False(rig.Form.Model.Dirty);
        Assert.False(rig.Form.Busy);
    });

    [Fact]
    public void Policy_defined_printers_cannot_be_removed_or_renamed() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Head office");
        Assert.False(rig.Form.Button("Remove").Enabled);
        Assert.False(rig.Form.Button("Rename...").Enabled);
        rig.SelectProfile("Send to Odoo");
        Assert.True(rig.Form.Button("Remove").Enabled);
    });

    [Fact]
    public void Test_connection_reports_success_and_failure() => Sta.Run(async () =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        await rig.Form.TestConnectionAsync();
        var ok = rig.Dialogs.Messages.Last();
        Assert.Equal("Test connection - Send to Odoo", ok.Caption);
        Assert.Equal(MessageBoxIcon.Information, ok.Icon);
        Assert.Contains("https://example.odoo.com", ok.Text);

        rig.SelectProfile("Vendor bills");
        ((TextBox)rig.Form.ProfileTable.Controls["ServerUrl"]).Text = "https://fail.example.com";
        await rig.Form.TestConnectionAsync();
        Assert.Equal(MessageBoxIcon.Error, rig.Dialogs.Messages.Last().Icon);
        Assert.Equal(["TestConnection:Send to Odoo", "TestConnection:Vendor bills"], rig.Actions.Calls.Where(c => c.StartsWith("TestConnection", StringComparison.Ordinal)));
    });

    [Fact]
    public void Print_test_page_asks_to_save_first_when_there_are_unsaved_changes() => Sta.Run(async () =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        ((TextBox)rig.Form.ProfileTable.Controls["FileNameTemplate"]).Text = "{date}";

        rig.Dialogs.Answer = DialogResult.No;
        await rig.Form.PrintTestPageAsync();
        Assert.Equal("Save and apply your changes first?", rig.Dialogs.Messages.Last().Text);
        Assert.DoesNotContain(rig.Actions.Calls, c => c.StartsWith("TestPrint", StringComparison.Ordinal));
        Assert.True(rig.Form.Model.Dirty);

        rig.Dialogs.Answer = DialogResult.Yes;
        await rig.Form.PrintTestPageAsync();
        Assert.False(rig.Form.Model.Dirty);
        Assert.Equal(["SyncPrinters", "RestartService", "TestPrint:Send to Odoo"], rig.Actions.Calls.Where(c => c is "SyncPrinters" or "RestartService" || c.StartsWith("TestPrint", StringComparison.Ordinal)));
        Assert.Equal("Test page delivered.", rig.Form.StatusText);
        Assert.Equal("Print test page - Send to Odoo", rig.Dialogs.Reports.Single().Title);
        Assert.Contains("delivered", rig.Dialogs.Reports.Single().Text);
        Assert.Equal("{date}", rig.Demo.Store.GetProfile("Send to Odoo")!.FileNameTemplate);
    });

    [Fact]
    public void Print_test_page_without_changes_does_not_ask() => Sta.Run(async () =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        await rig.Form.PrintTestPageAsync();
        Assert.DoesNotContain(rig.Dialogs.Messages, m => m.Caption == "Print test page");
        Assert.Contains("TestPrint:Send to Odoo", rig.Actions.Calls);
    });

    [Fact]
    public void The_refresh_timer_does_nothing_while_a_long_operation_runs() => Sta.Run(() =>
    {
        using var rig = new Rig(delay: TimeSpan.FromMilliseconds(150));
        rig.SelectTab(2);
        var before = rig.StatusCalls;
        Assert.True(before >= 1);

        rig.Form.OnRefreshTick();
        Assert.Equal(before + 1, rig.StatusCalls);   // idle: refreshes

        rig.Form.Button("Diagnostics").PerformClick();
        Assert.True(rig.Form.Busy);
        var during = rig.StatusCalls;
        rig.Form.OnRefreshTick();
        Assert.Equal(during, rig.StatusCalls);

        Sta.WaitFor(() => !rig.Form.Busy);
        Assert.Equal("Diagnostics report saved to " + Path.Combine(rig.Actions.DataRoot, "diagnostics-demo.txt"), rig.Form.StatusText);
        Assert.Equal("ERP Printer diagnostics", rig.Dialogs.Reports.Single().Title);
        rig.Form.OnRefreshTick();
        Assert.True(rig.StatusCalls > during);
    });

    [Fact]
    public void The_refresh_timer_only_refreshes_the_status_tab() => Sta.Run(() =>
    {
        using var rig = new Rig();
        var before = rig.StatusCalls;
        rig.Form.OnRefreshTick();
        Assert.Equal(before, rig.StatusCalls);
    });

    // --- Saving and closing -------------------------------------------------------------------

    [Fact]
    public void Save_with_invalid_profiles_lists_what_to_fix_and_saves_nothing() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.Dialogs.Name = "Broken";
        rig.Form.Button("Add...").PerformClick();

        rig.Form.Button("Save && apply").PerformClick();

        var message = rig.Dialogs.Messages.Last();
        Assert.Equal("Cannot save", message.Caption);
        Assert.Equal(MessageBoxIcon.Warning, message.Icon);
        Assert.StartsWith("Please fix these first:", message.Text);
        Assert.Contains("Broken: Server URL is required.", message.Text);
        Assert.Contains("Broken: An API key is required.", message.Text);
        Assert.DoesNotContain(rig.Actions.Calls, c => c is "SyncPrinters" or "RestartService");
        Assert.Null(rig.Demo.Store.GetProfile("Broken"));
        Assert.True(rig.Form.Model.Dirty);
    });

    [Fact]
    public void Save_applies_and_reports_the_time() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(1);
        ((NumericUpDown)rig.Form.GlobalTable.Controls["MaxAttempts"]).Value = 7;

        rig.Form.Button("Save && apply").PerformClick();

        Assert.Equal(7, rig.Demo.Store.LoadGlobal().MaxAttempts);
        Assert.Equal(["SyncPrinters", "RestartService"], rig.Actions.Calls.Where(c => c is "SyncPrinters" or "RestartService"));
        Assert.StartsWith("Saved and applied at ", rig.Form.StatusText);
        Assert.False(rig.Form.Model.Dirty);
        Assert.Equal(7m, ((NumericUpDown)rig.Form.GlobalTable.Controls["MaxAttempts"]).Value);
    });

    [Fact]
    public void A_printer_sync_failure_is_shown_as_a_warning_after_saving() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.Actions.SyncFailure = "Spooler unavailable.";
        rig.SelectTab(1);
        ((NumericUpDown)rig.Form.GlobalTable.Controls["MaxAttempts"]).Value = 3;

        rig.Form.Button("Save && apply").PerformClick();

        var warning = rig.Dialogs.Messages.Last();
        Assert.Equal(MessageBoxIcon.Warning, warning.Icon);
        Assert.StartsWith("Settings were saved, but:", warning.Text);
        Assert.EndsWith("Spooler unavailable.", warning.Text);
        Assert.Equal(3, rig.Demo.Store.LoadGlobal().MaxAttempts);
    });

    [Fact]
    public void A_number_typed_but_not_yet_committed_counts_as_an_edit_when_closing() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(1);
        var number = (NumericUpDown)rig.Form.GlobalTable.Controls["MaxAttempts"];
        number.Text = "12";   // typed; NumericUpDown has not validated it yet
        rig.Dialogs.Answer = DialogResult.No;

        rig.Form.Close();

        Assert.Contains(rig.Dialogs.Messages, m => m.Text == "Save your changes before closing?");
    });

    [Fact]
    public void Closing_without_changes_does_not_ask() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.Form.Close();
        Assert.Empty(rig.Dialogs.Messages);
        Assert.True(rig.Form.IsDisposed);
    });

    [Fact]
    public void Closing_with_changes_offers_to_save() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        ((TextBox)rig.Form.ProfileTable.Controls["FileNameTemplate"]).Text = "{date}";

        rig.Dialogs.Answer = DialogResult.Cancel;
        rig.Form.Close();
        Assert.False(rig.Form.IsDisposed);
        Assert.Equal(("ERP Printer", "Save your changes before closing?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question), rig.Dialogs.Messages.Last());

        rig.Dialogs.Answer = DialogResult.Yes;
        rig.Form.Close();
        Assert.True(rig.Form.IsDisposed);
        Assert.Equal("{date}", rig.Demo.Store.GetProfile("Send to Odoo")!.FileNameTemplate);
    });

    [Fact]
    public void Closing_and_discarding_leaves_the_configuration_alone() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectProfile("Send to Odoo");
        ((TextBox)rig.Form.ProfileTable.Controls["FileNameTemplate"]).Text = "{date}";

        rig.Dialogs.Answer = DialogResult.No;
        rig.Form.Close();

        Assert.True(rig.Form.IsDisposed);
        Assert.Equal("{title}", rig.Demo.Store.GetProfile("Send to Odoo")!.FileNameTemplate);
    });

    [Fact]
    public void Closing_with_invalid_changes_stays_open_after_the_cannot_save_message() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.Dialogs.Name = "Broken";
        rig.Form.Button("Add...").PerformClick();
        rig.Dialogs.Answer = DialogResult.Yes;

        rig.Form.Close();

        Assert.False(rig.Form.IsDisposed);
        Assert.Equal("Cannot save", rig.Dialogs.Messages.Last().Caption);
    });

    // --- Status tab ---------------------------------------------------------------------------

    [Fact]
    public void The_status_tab_lists_profiles_with_problems_in_red() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(2);
        var items = rig.Form.StatusList.Items.Cast<ListViewItem>().ToList();

        Assert.Equal(8, rig.Form.StatusList.Columns.Count);
        Assert.Equal(["Head office", "Send to Odoo", "Vendor bills", "Warehouse ERPNext", "Webhook archive"], items.Select(i => i.Text));
        Assert.Equal(["OK", "OK", "Missing", "OK", "Disabled"], items.Select(i => i.SubItems[2].Text));
        Assert.Equal("HTTP 401: invalid API key", items[1].SubItems[7].Text);
        Assert.Equal([false, true, true, false, false], items.Select(i => i.ForeColor == Color.Firebrick));
    });

    [Fact]
    public void Status_buttons_call_the_actions() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(2);

        rig.Form.Button("Retry failed").PerformClick();
        Assert.Equal("1 document(s) moved back to the outbox.", rig.Form.StatusText);

        rig.Form.Button("Repair printers").PerformClick();
        rig.Form.Button("Event log").PerformClick();
        Assert.True(rig.Shell.EventLogOpened);

        rig.Form.Button("Send now").PerformClick();
        Sta.WaitFor(() => !rig.Form.Busy);
        rig.Form.Button("Restart service").PerformClick();
        Sta.WaitFor(() => !rig.Form.Busy, TimeSpan.FromSeconds(5));

        Assert.Equal(["RetryFailed", "SyncPrinters", "SendNow", "RestartService"],
            rig.Actions.Calls.Where(c => c is "RetryFailed" or "SyncPrinters" or "SendNow" or "RestartService"));
    });

    [Fact]
    public void Open_data_folder_opens_the_selected_outbox_else_the_data_folder_and_logs_fall_back() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(2);
        var odoo = rig.Actions.GetStatus().Profiles.Single(p => p.Profile == "Send to Odoo");
        var logs = rig.Actions.LogsPath;

        rig.Form.Button("Open data folder").PerformClick();
        Assert.Equal(rig.Actions.DataRoot, rig.Shell.Opened.Last());   // nothing selected

        rig.Form.StatusList.Items.Cast<ListViewItem>().Single(i => i.Text == "Send to Odoo").Selected = true;
        rig.Form.Button("Open data folder").PerformClick();
        Assert.Equal(rig.Actions.DataRoot, rig.Shell.Opened.Last());   // its outbox does not exist yet

        try
        {
            Directory.CreateDirectory(odoo.OutboxPath);
            Directory.CreateDirectory(logs);
            rig.Form.Button("Open data folder").PerformClick();
            Assert.Equal(odoo.OutboxPath, rig.Shell.Opened.Last());
            rig.Form.Button("Open logs").PerformClick();
            Assert.Equal(logs, rig.Shell.Opened.Last());
        }
        finally
        {
            try { Directory.Delete(rig.Actions.DataRoot, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void The_selected_status_row_survives_a_refresh() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(2);
        rig.Form.StatusList.Items.Cast<ListViewItem>().Single(i => i.Text == "Vendor bills").Selected = true;

        rig.Form.OnRefreshTick();

        Assert.Equal("Vendor bills", rig.Form.StatusList.SelectedItems.Cast<ListViewItem>().Single().Text);
    });

    [Fact]
    public void Check_for_updates_reports_the_result() => Sta.Run(() =>
    {
        using var rig = new Rig();
        rig.SelectTab(1);
        rig.Form.Button("Check for updates").PerformClick();
        Sta.WaitFor(() => !rig.Form.Busy);
        var message = rig.Dialogs.Messages.Last();
        Assert.Equal("Updates", message.Caption);
        Assert.Equal("You are running the latest version.", message.Text);
    });

    // --- Layout -------------------------------------------------------------------------------

    [Fact]
    public void Layout_has_no_clipped_or_overflowing_controls_and_screenshots_are_captured() => Sta.Run(() =>
    {
        using var rig = new Rig();
        var form = rig.Form;
        var shots = new List<string>();

        foreach (var (name, profile) in new[] { ("odoo-json2", "Send to Odoo"), ("odoo-jsonrpc", "Vendor bills"), ("erpnext", "Warehouse ERPNext"), ("webhook-policy", "Head office") })
        {
            rig.SelectTab(0);
            rig.SelectProfile(profile);
            AssertFits(form.ProfileTable, form);
            shots.Add(Sta.Capture(form, "printers-" + name));
        }

        rig.SelectTab(1);
        AssertFits(form.GlobalTable, form);
        shots.Add(Sta.Capture(form, "settings"));

        rig.SelectTab(2);
        shots.Add(Sta.Capture(form, "status"));
        Assert.All(form.Tabs.SelectedTab!.Descendants().OfType<Button>(), b => Assert.True(b.Right <= b.Parent!.ClientSize.Width, $"Button '{b.Text}' is clipped."));

        // The smallest the window may get.
        form.Size = form.MinimumSize;
        Sta.Pump();
        rig.SelectTab(0);
        rig.SelectProfile("Vendor bills");
        AssertFits(form.ProfileTable, form);
        shots.Add(Sta.Capture(form, "printers-minimum-size"));
        rig.SelectTab(2);
        shots.Add(Sta.Capture(form, "status-minimum-size"));

        using (var report = new ReportDialog("Print test page - Send to Odoo", "ERP Printer test print\r\nProfile : Send to Odoo\r\nResult  : delivered"))
        {
            report.Show();
            Sta.Pump();
            shots.Add(Sta.Capture(report, "report-dialog"));
        }
        using (var name = new NameDialog("Add printer", "Name of the new printer (shown to users in the print dialog):", "Send to ERP"))
        {
            name.Show();
            Sta.Pump();
            shots.Add(Sta.Capture(name, "name-dialog"));
        }

        Assert.All(shots, path => Assert.True(File.Exists(path)));
    });

    private static void AssertFits(SettingsTable table, Form _)
    {
        var panel = table.Panel;
        var parent = panel.Parent!;
        Assert.True(panel.Width <= parent.ClientSize.Width, $"The settings table is wider ({panel.Width}) than its panel ({parent.ClientSize.Width}).");
        foreach (var label in panel.Controls.OfType<Label>())
            Assert.True(label.Right <= panel.ClientSize.Width, $"Label '{label.Text}' sticks out of the table.");
        foreach (var control in table.Controls.Values)
            Assert.True(control.Right <= panel.ClientSize.Width && control.Width > 10, $"Editor of '{(control.Tag as SettingEditor)?.Name}' is clipped or collapsed.");
    }
}
