using ErpPrinter.Config.Model;
using ErpPrinter.Settings;

namespace ErpPrinter.Config.Ui;

/// <summary>
/// Generates one label and one editor per setting (grouped, with tooltips) from <see cref="SettingEditor"/>s and writes edits
/// back to them. Contains no rules: which settings appear, which are read-only and what a secret shows is decided by the model.
/// </summary>
internal sealed class SettingsTable : IDisposable
{
    private readonly ToolTip _toolTip;
    private readonly Func<int, int> _scale;
    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private Font? _headerFont;
    private bool _rendering;

    /// <param name="scale">Converts a 96-DPI pixel size to device pixels (identity until the form has been scaled).</param>
    public SettingsTable(ToolTip toolTip, Func<int, int> scale)
    {
        _toolTip = toolTip;
        _scale = scale;
        Panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(8),
        };
        Panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public TableLayoutPanel Panel { get; }

    /// <summary>The generated editor of each rendered setting, by setting name.</summary>
    public IReadOnlyDictionary<string, Control> Controls => _controls;

    /// <summary>Raised after an edit was written to its <see cref="SettingEditor"/>.</summary>
    public event EventHandler<SettingEditor>? ValueEdited;

    /// <param name="editors">What to show.</param>
    /// <param name="allLabels">
    /// Labels of every setting that can be shown in this table. The label column is sized to the widest of them, so the editors
    /// do not jump sideways when the selected backend or printer changes. Null sizes the column to what is shown.
    /// </param>
    public void Render(IReadOnlyList<SettingEditor> editors, IReadOnlyList<string>? allLabels = null)
    {
        _rendering = true;
        Panel.SuspendLayout();
        try
        {
            Clear();
            _headerFont = new Font(Panel.Font, FontStyle.Bold);
            Panel.ColumnStyles[0] = allLabels is { Count: > 0 }
                ? new ColumnStyle(SizeType.Absolute, allLabels.Max(l => TextRenderer.MeasureText(l, Panel.Font).Width) + _scale(3 + 12) + 10)
                : new ColumnStyle(SizeType.AutoSize);
            string? group = null;
            foreach (var editor in editors)
            {
                if (editor.Group != group)
                {
                    group = editor.Group;
                    AddRow(new Label { Text = group, AutoSize = true, Font = _headerFont, Margin = new Padding(0, _scale(12), 0, _scale(4)) }, null);
                }
                var label = new Label
                {
                    Text = editor.Label,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(3, _scale(7), _scale(12), 3),
                };
                var control = CreateEditor(editor);
                control.Tag = editor;
                control.AccessibleName = editor.Label;
                if (editor.ToolTip.Length > 0)
                {
                    _toolTip.SetToolTip(label, editor.ToolTip);
                    _toolTip.SetToolTip(control, editor.ToolTip);
                }
                AddRow(label, control);
                _controls[editor.Name] = control;
            }
        }
        finally
        {
            Panel.ResumeLayout(true);
            _rendering = false;
        }
    }

    private void Clear()
    {
        _controls.Clear();
        var old = Panel.Controls.Cast<Control>().ToList();
        Panel.Controls.Clear();
        Panel.RowStyles.Clear();
        Panel.RowCount = 0;
        foreach (var control in old) control.Dispose();
        _headerFont?.Dispose();
        _headerFont = null;
    }

    private void AddRow(Control left, Control? right)
    {
        var row = Panel.RowCount;
        Panel.RowCount = row + 1;
        Panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Panel.Controls.Add(left, 0, row);
        if (right is null) Panel.SetColumnSpan(left, 2);
        else Panel.Controls.Add(right, 1, row);
    }

    private Control CreateEditor(SettingEditor editor)
    {
        var definition = editor.Definition;
        switch (definition.Type)
        {
            case SettingType.Bool:
            {
                var box = new CheckBox { AutoSize = true, Checked = (bool)editor.Value, Enabled = !editor.ReadOnly };
                box.CheckedChanged += (_, _) => Edited(editor, box.Checked);
                return box;
            }
            case SettingType.Int:
            {
                var min = definition.Min;
                var max = definition.Max;
                var number = new NumericUpDown { Minimum = min, Maximum = max, Width = _scale(120), Enabled = !editor.ReadOnly };
                number.Value = Math.Clamp(Convert.ToDecimal(editor.Value), number.Minimum, number.Maximum);
                number.ValueChanged += (_, _) => Edited(editor, (int)number.Value);
                return number;
            }
            case SettingType.Choice:
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = _scale(240), Enabled = !editor.ReadOnly };
                combo.Items.AddRange([.. definition.Choices]);
                combo.SelectedItem = (string)editor.Value;
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string choice) Edited(editor, choice);
                };
                return combo;
            }
            case SettingType.Secret:
            {
                var text = new TextBox
                {
                    UseSystemPasswordChar = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    Text = (string)editor.Value,
                    PlaceholderText = editor.SecretCue ?? "",
                    ReadOnly = editor.ReadOnly,
                };
                text.TextChanged += (_, _) => Edited(editor, text.Text);
                return text;
            }
            default:
            {
                var text = new TextBox
                {
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    Text = (string)editor.Value,
                    ReadOnly = editor.ReadOnly,
                };
                text.TextChanged += (_, _) => Edited(editor, text.Text);
                return text;
            }
        }
    }

    /// <summary>Numeric boxes only commit typed text when they lose focus; reading <c>Value</c> commits it.</summary>
    public void CommitPending()
    {
        foreach (var number in _controls.Values.OfType<NumericUpDown>())
            if (number.Tag is SettingEditor editor && !editor.ReadOnly) Edited(editor, (int)number.Value);
    }

    private void Edited(SettingEditor editor, object value)
    {
        if (_rendering) return;
        editor.Value = value;
        ValueEdited?.Invoke(this, editor);
    }

    public void Dispose()
    {
        Clear();
        Panel.Dispose();
    }
}
