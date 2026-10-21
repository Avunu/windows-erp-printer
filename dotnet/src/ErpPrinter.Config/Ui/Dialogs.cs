using System.Diagnostics;
using System.Text;

namespace ErpPrinter.Config.Ui;

/// <summary>Every modal window the form opens, so tests can answer them instead of blocking.</summary>
internal interface IDialogs
{
    DialogResult Message(IWin32Window owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None);

    /// <summary>Asks for a name; null when cancelled.</summary>
    string? PromptName(IWin32Window owner, string title, string prompt, string suggestion);

    /// <summary>A modal window with selectable report text that can be copied or saved, to send to support.</summary>
    void ShowReport(IWin32Window owner, string title, string text);
}

/// <summary>Opens Explorer and the event viewer.</summary>
internal interface IShell
{
    void OpenFolder(string path);
    void OpenEventLog();
}

internal sealed class WinFormsDialogs : IDialogs
{
    public DialogResult Message(IWin32Window owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None) =>
        MessageBox.Show(owner, text, caption, buttons, icon);

    public string? PromptName(IWin32Window owner, string title, string prompt, string suggestion)
    {
        using var dialog = new NameDialog(title, prompt, suggestion);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Value : null;
    }

    public void ShowReport(IWin32Window owner, string title, string text)
    {
        using var dialog = new ReportDialog(title, text);
        dialog.ShowDialog(owner);
    }
}

internal sealed class WindowsShell : IShell
{
    public void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public void OpenEventLog() => Process.Start(new ProcessStartInfo("eventvwr.msc", "/c:Application") { UseShellExecute = true });
}

/// <summary>The "Add printer" / "Rename printer" box (replaces the VisualBasic InputBox of the PowerShell GUI).</summary>
internal sealed class NameDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Top };

    public NameDialog(string title, string prompt, string suggestion)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 130);
        Icon = AppIcon.Load();

        _box.Text = suggestion;
        _box.Dock = DockStyle.Fill;
        var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(416, 0), Margin = new Padding(0, 0, 0, 8) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_box, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => _box.SelectAll();
    }

    public string Value => _box.Text.Trim();
}

internal sealed class ReportDialog : Form
{
    public ReportDialog(string title, string text)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = title;
        Size = new Size(860, 600);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Icon = AppIcon.Load();

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.White,
            Text = text,
        };

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "Copy to clipboard", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(box.Text);
                copy.Text = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                MessageBox.Show(this, "Could not copy to the clipboard: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var save = new Button { Text = "Save as...", AutoSize = true };
        save.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt",
                FileName = new string(title.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-').ToArray()) + ".txt",
            };
            if (picker.ShowDialog(this) == DialogResult.OK) File.WriteAllText(picker.FileName, box.Text, new UTF8Encoding(false));
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange([close, copy, save]);
        Controls.Add(box);
        Controls.Add(buttons);
        AcceptButton = close;
        CancelButton = close;
        Shown += (_, _) => box.Select(0, 0);
    }
}

/// <summary>The application icon, embedded as a resource.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Load()
    {
        if (_icon is not null) return _icon;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("erp-printer.ico");
        if (stream is null) return null;
        return _icon = new Icon(stream);
    }
}
