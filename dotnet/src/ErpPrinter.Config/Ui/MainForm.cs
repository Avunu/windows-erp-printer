using ErpPrinter.Config.Model;

namespace ErpPrinter.Config.Ui;

/// <summary>
/// The configuration window: Printers, Settings and Status tabs. Port of ErpPrinterConfig.ps1. The controls are generated from the
/// settings schema by <see cref="SettingsTable"/>; every rule lives in <see cref="ConfigFormModel"/> and everything that touches the
/// machine goes through <see cref="IConfigActions"/>.
/// </summary>
internal sealed class MainForm : Form
{
    private const string AppTitle = "ERP Printer";

    private readonly ConfigFormModel _model;
    private readonly IConfigActions _actions;
    private readonly IDialogs _dialogs;
    private readonly IShell _shell;
    private readonly TimeProvider _time;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _printersTab = new("Printers");
    private readonly TabPage _settingsTab = new("Settings");
    private readonly TabPage _statusTab = new("Status");

    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private readonly ListBox _profileList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Panel _profileScroll = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Label _profileHeader = new() { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(10, 6, 0, 0) };
    private readonly FlowLayoutPanel _profileButtons = new() { Dock = DockStyle.Bottom, AutoSize = true };
    private readonly Panel _globalScroll = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Label _serviceLabel = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
    private readonly ListView _statusList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, ShowItemToolTips = true };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };

    private readonly Button _addButton;
    private readonly Button _renameButton;
    private readonly Button _removeButton;
    private readonly Button _saveButton;
    private readonly Button _closeButton;

    private readonly SettingsTable _profileTable;
    private readonly SettingsTable _globalTable;

    private readonly Font _font;
    private bool _rendering;
    private bool _scaled;

    public MainForm(ConfigFormModel model, IConfigActions actions, IDialogs? dialogs = null, IShell? shell = null, TimeProvider? time = null)
    {
        _model = model;
        _actions = actions;
        _dialogs = dialogs ?? new WinFormsDialogs();
        _shell = shell ?? new WindowsShell();
        _time = time ?? TimeProvider.System;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = _font = new Font("Segoe UI", 9F);
        Text =$"{AppTitle} configuration ({BuildInfo.Version})";
        Size = new Size(940, 700);
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Load();

        _profileTable = new SettingsTable(_toolTip, Scale);
        _globalTable = new SettingsTable(_toolTip, Scale);

        _addButton = MakeButton("Add...", AddProfile);
        _renameButton = MakeButton("Rename...", RenameProfile);
        _removeButton = MakeButton("Remove", RemoveProfile);
        _saveButton = MakeButton("Save && apply", () => SaveAll());
        // Not through Safely(): closing while "busy" would cancel itself.
        _closeButton = MakeButton("Close");
        _closeButton.Click += (_, _) => Close();

        BuildPrintersTab();
        BuildSettingsTab();
        BuildStatusTab();
        _tabs.TabPages.AddRange([_printersTab, _settingsTab, _statusTab]);
        Controls.Add(_tabs);
        Controls.Add(BuildBottomBar());

        _profileTable.ValueEdited += OnProfileValueEdited;
        _profileList.SelectedIndexChanged += (_, _) =>
        {
            if (_rendering) return;
            Safely(() =>
            {
                _model.Select(_profileList.SelectedItem as string);
                ShowProfile();
            });
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _statusTab) Safely(UpdateStatus);
        };
        _statusList.SizeChanged += (_, _) => FitStatusColumns();
        _refreshTimer.Tick += (_, _) => OnRefreshTick();

        RefreshProfileList(null);
        ShowGlobalSettings();
        if (_model.Profiles.Count == 0) SetStatus("No printers yet. Click \"Add...\" to create one.");
    }

    /// <summary>True while a long operation (test print, diagnostics, a connection test...) runs; the status refresh timer then does nothing.</summary>
    public bool Busy { get; private set; }

    internal ConfigFormModel Model => _model;
    internal TabControl Tabs => _tabs;
    internal TabPage StatusTab => _statusTab;
    internal SettingsTable ProfileTable => _profileTable;
    internal SettingsTable GlobalTable => _globalTable;
    internal ListView StatusList => _statusList;
    internal string StatusText => _statusLabel.Text;
    internal bool RefreshTimerEnabled => _refreshTimer.Enabled;

    private int Scale(int logical) => _scaled ? LogicalToDeviceUnits(logical) : logical;

    #region Layout

    private void BuildPrintersTab()
    {
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        listButtons.Controls.AddRange([_addButton, _renameButton, _removeButton]);
        _split.Panel1.Controls.Add(_profileList);
        _split.Panel1.Controls.Add(listButtons);

        _profileScroll.Controls.Add(_profileTable.Panel);
        _profileButtons.Controls.Add(MakeButton("Test connection", TestConnectionAsync));
        _profileButtons.Controls.Add(MakeButton("Print test page", PrintTestPageAsync));
        _split.Panel2.Controls.Add(_profileScroll);
        _split.Panel2.Controls.Add(_profileHeader);
        _split.Panel2.Controls.Add(_profileButtons);
        _printersTab.Controls.Add(_split);
    }

    private void BuildSettingsTab()
    {
        _globalScroll.Controls.Add(_globalTable.Panel);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.Add(MakeButton("Check for updates", CheckForUpdatesAsync));
        _settingsTab.Controls.Add(_globalScroll);
        _settingsTab.Controls.Add(buttons);
    }

    private void BuildStatusTab()
    {
        foreach (var (header, width) in StatusFormatter.Columns) _statusList.Columns.Add(header, width);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange(
        [
            MakeButton("Refresh", UpdateStatus),
            MakeButton("Send now", SendNowAsync),
            MakeButton("Retry failed", RetryFailed),
            MakeButton("Restart service", RestartServiceAsync),
            MakeButton("Repair printers", RepairPrinters),
            MakeButton("Open data folder", OpenDataFolder),
            MakeButton("Diagnostics", ShowDiagnosticsAsync),
            MakeButton("Open logs", OpenLogs),
            MakeButton("Event log", _shell.OpenEventLog),
        ]);
        _statusTab.Controls.Add(_statusList);
        _statusTab.Controls.Add(_serviceLabel);
        _statusTab.Controls.Add(buttons);
    }

    private TableLayoutPanel BuildBottomBar()
    {
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_statusLabel, 0, 0);
        bottom.Controls.Add(_saveButton, 1, 0);
        bottom.Controls.Add(_closeButton, 2, 0);
        return bottom;
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2) };

    private Button MakeButton(string text, Action action)
    {
        var button = MakeButton(text);
        button.Click += (_, _) => Safely(action);
        return button;
    }

    private Button MakeButton(string text, Func<Task> action)
    {
        var button = MakeButton(text);
        button.Click += async (_, _) => await SafelyAsync(action);
        return button;
    }

    #endregion

    #region Lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _refreshTimer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Scaling has happened by now: sizes of controls generated from here on must be scaled by hand, and the splitter can be set.
        _scaled = true;
        _split.SplitterDistance = Scale(240);
        FitStatusColumns();
        // Controls generated before scaling have logical sizes and label widths measured at the wrong scale: generate them again.
        ShowProfile();
        ShowGlobalSettings();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
        if (Busy)
        {
            SetStatus("Please wait for the running operation to finish.");
            e.Cancel = true;
            return;
        }
        CommitPendingEdits();
        if (!_model.Dirty) return;

        var answer = _dialogs.Message(this, "Save your changes before closing?", AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (answer == DialogResult.Yes)
        {
            var saved = false;
            Safely(() => saved = SaveAll());
            if (!saved) e.Cancel = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _profileTable.Dispose();
            _globalTable.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) _font.Dispose();
    }

    #endregion

    #region Running actions

    /// <summary>Runs <paramref name="action"/> with a wait cursor; an exception becomes an error box with its message, like the PowerShell GUI.</summary>
    private void Safely(Action action)
    {
        if (Busy) return;
        Busy = true;
        UseWaitCursor = true;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            UseWaitCursor = false;
            Busy = false;
        }
    }

    private async Task SafelyAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        UseWaitCursor = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            UseWaitCursor = false;
            Busy = false;
        }
    }

    private void ShowError(Exception ex) => _dialogs.Message(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
        _toolTip.SetToolTip(_statusLabel, text);
    }

    /// <summary>Numeric boxes only commit what was typed when they lose focus; read them so that closing right after typing keeps the edit.</summary>
    private void CommitPendingEdits()
    {
        _profileTable.CommitPending();
        _globalTable.CommitPending();
    }

    #endregion

    #region Printers tab

    private void RefreshProfileList(string? select)
    {
        _rendering = true;
        try
        {
            _profileList.Items.Clear();
            foreach (var profile in _model.Profiles) _profileList.Items.Add(profile.Name);
            var index = select is null ? -1 : _profileList.Items.IndexOf(select);
            if (index < 0 && _profileList.Items.Count > 0) index = 0;
            _profileList.SelectedIndex = index;
        }
        finally
        {
            _rendering = false;
        }
        _model.Select(_profileList.SelectedItem as string);
        ShowProfile();
    }

    private void ShowProfile()
    {
        var profile = _model.Current;
        var scroll = new Point(-_profileScroll.AutoScrollPosition.X, -_profileScroll.AutoScrollPosition.Y);
        _profileHeader.Text = profile?.HeaderText ?? "";
        _profileHeader.Visible = _profileHeader.Text.Length > 0;
        _profileTable.Render(profile?.Editors() ?? [], profile?.AllLabels());
        _profileButtons.Enabled = profile is not null;
        _removeButton.Enabled = profile is { PolicyDefined: false };
        _renameButton.Enabled = profile is { PolicyDefined: false };
        if (profile is not null && scroll != Point.Empty) _profileScroll.AutoScrollPosition = scroll;
    }

    private void OnProfileValueEdited(object? sender, SettingEditor editor)
    {
        if (editor.Name != "Backend") return;
        // The settings shown depend on the backend: regenerate the form (after the combo box finished handling its event).
        void Rebuild()
        {
            ShowProfile();
            if (_profileTable.Controls.TryGetValue("Backend", out var combo)) combo.Focus();
        }
        if (IsHandleCreated) BeginInvoke(Rebuild);
        else Rebuild();
    }

    private void AddProfile()
    {
        var name = _dialogs.PromptName(this, "Add printer", "Name of the new printer (shown to users in the print dialog):", _model.SuggestName());
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = _model.AddProfile(name);
        RefreshProfileList(profile.Name);
    }

    private void RenameProfile()
    {
        if (_model.Current is not { } current) return;
        var name = _dialogs.PromptName(this, "Rename printer",
            "New name of the printer. Documents still waiting in the old outbox stay on disk.", current.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == current.Name) return;
        var renamed = _model.RenameProfile(current.Name, name);
        RefreshProfileList(renamed.Name);
    }

    private void RemoveProfile()
    {
        if (_model.Current is not { } current) return;
        var answer = _dialogs.Message(this,
            $"Remove the printer '{current.Name}'? Its stored API key is deleted as well. Documents still in its outbox stay on disk.",
            "Remove printer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        _model.RemoveProfile(current.Name);
        RefreshProfileList(null);
    }

    internal async Task TestConnectionAsync()
    {
        if (_model.Current is not { } current) return;
        CommitPendingEdits();
        var result = await _actions.TestConnectionAsync(current.ToProfile(), current.EnteredSecrets());
        _dialogs.Message(this, result.Message, $"Test connection - {current.Name}", MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    internal async Task PrintTestPageAsync()
    {
        if (_model.Current is not { } current) return;
        var name = current.Name;
        CommitPendingEdits();
        if (_model.Dirty)
        {
            var answer = _dialogs.Message(this, "Save and apply your changes first?", "Print test page", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            if (!SaveAll()) return;
        }

        SetStatus("Printing a test page and following it to the ERP...");
        IProgress<string> progress = new Progress<string>(SetStatus);
        var result = await _actions.TestPrintAsync(name, progress.Report);
        UpdateStatus();
        SetStatus(result.Success ? "Test page delivered." : "Test page did not arrive; see the report.");
        _dialogs.ShowReport(this, $"Print test page - {name}", result.Report);
    }

    #endregion

    #region Settings tab

    private void ShowGlobalSettings() => _globalTable.Render(_model.Global.Editors(), _model.Global.AllLabels());

    internal async Task CheckForUpdatesAsync()
    {
        var check = await _actions.CheckForUpdateAsync();
        if (!check.UpdateAvailable)
        {
            _dialogs.Message(this, check.Message, "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = _dialogs.Message(this,
            $"Version {check.AvailableVersion} is available (installed: {check.CurrentVersion}). Install it now?",
            "Updates", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        var message = await _actions.InstallUpdateAsync();
        _dialogs.Message(this, $"{message}{Environment.NewLine}{Environment.NewLine}Reopen this window to use the new version.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    #endregion

    #region Save

    /// <summary>Validates, saves and applies all changes. Returns false (after telling the user why) if nothing was saved.</summary>
    internal bool SaveAll()
    {
        CommitPendingEdits();
        var result = _model.SaveAndApply(_actions);
        if (!result.Saved)
        {
            _dialogs.Message(this, "Please fix these first:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Problems),
                "Cannot save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        RefreshProfileList(_model.Current?.Name);
        ShowGlobalSettings();
        UpdateStatus();
        if (result.Warning is not null)
            _dialogs.Message(this, $"Settings were saved, but:{Environment.NewLine}{Environment.NewLine}{result.Warning}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            SetStatus($"Saved and applied at {_time.GetLocalNow():T}.");
        return true;
    }

    #endregion

    #region Status tab

    internal void UpdateStatus()
    {
        var status = _actions.GetStatus();
        _serviceLabel.Text = StatusFormatter.ServiceLine(status);
        var selected = _statusList.SelectedItems.Count > 0 ? (_statusList.SelectedItems[0].Tag as ProfileStatus)?.Profile : null;
        _statusList.BeginUpdate();
        try
        {
            _statusList.Items.Clear();
            foreach (var profile in status.Profiles)
            {
                var item = new ListViewItem(StatusFormatter.Row(profile)) { Tag = profile };
                if (StatusFormatter.IsProblem(profile)) item.ForeColor = Color.Firebrick;
                if (profile.Profile == selected) item.Selected = true;
                _statusList.Items.Add(item);
            }
        }
        finally
        {
            _statusList.EndUpdate();
        }
    }

    /// <summary>The 5 second refresh: does nothing while a long operation runs or when another tab is showing.</summary>
    internal void OnRefreshTick()
    {
        if (Busy || _tabs.SelectedTab != _statusTab) return;
        try
        {
            UpdateStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    /// <summary>Gives the last column (the error text) the width the others leave over.</summary>
    private void FitStatusColumns()
    {
        if (_statusList.Columns.Count == 0) return;
        var last = _statusList.Columns.Count - 1;
        var fixedWidth = 0;
        for (var i = 0; i < last; i++) fixedWidth += _statusList.Columns[i].Width;
        var available = _statusList.ClientSize.Width - fixedWidth;
        if (available > 0) _statusList.Columns[last].Width = Math.Max(available, Scale(120));
    }

    private async Task SendNowAsync()
    {
        await _actions.SendNowAsync();
        UpdateStatus();
    }

    private void RetryFailed()
    {
        var count = _actions.RetryFailed();
        UpdateStatus();
        SetStatus($"{count} document(s) moved back to the outbox.");
    }

    private async Task RestartServiceAsync()
    {
        _actions.RestartService();
        await Task.Delay(1000);
        UpdateStatus();
    }

    private void RepairPrinters()
    {
        _actions.SyncPrinters();
        UpdateStatus();
    }

    private void OpenDataFolder()
    {
        var selected = _statusList.SelectedItems.Count > 0 ? (_statusList.SelectedItems[0].Tag as ProfileStatus)?.OutboxPath : null;
        var path = selected is not null && Directory.Exists(selected) ? selected : _actions.DataRoot;
        _shell.OpenFolder(path);
    }

    private void OpenLogs()
    {
        var path = Directory.Exists(_actions.LogsPath) ? _actions.LogsPath : _actions.DataRoot;
        _shell.OpenFolder(path);
    }

    internal async Task ShowDiagnosticsAsync()
    {
        SetStatus("Running diagnostics...");
        var result = await _actions.RunDiagnosticsAsync();
        SetStatus(result.ReportPath is not null ? $"Diagnostics report saved to {result.ReportPath}" : "Diagnostics finished.");
        _dialogs.ShowReport(this, "ERP Printer diagnostics", result.Report);
    }

    #endregion
}
