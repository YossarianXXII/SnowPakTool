using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed class CargoMassDialog : Form
{
    private readonly DataGridView _bodies = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnEnter,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) },
        RowTemplate = { Height = 26 }
    };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    internal Dictionary<string, string?> Values { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    internal CargoMassDialog(string cargo, string role, IReadOnlyList<CargoMassRow> rows)
    {
        Text = $"{cargo} — {role.ToLowerInvariant()} body masses";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 580); MinimumSize = new Size(800, 360);
        MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8),
            Text = "Edit each body's raw Mass (kg). Values show new (original). Blank restores inheritance; entering a value creates a local override.\nOK updates the cargo preview. Stage changes and Start → Apply pending changes are still required."
        }, 0, 0);
        layout.Controls.Add(_bodies, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, CausesValidation = false };
        var ok = new Button { Text = "OK", AutoSize = true };
        var reset = new Button { Text = "Reset to original values", AutoSize = true, CausesValidation = false };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); buttons.Controls.Add(reset);
        layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        AcceptButton = ok; CancelButton = cancel;
        _bodies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Body", HeaderText = "Body / Mass attribute", Width = 360, ReadOnly = true });
        _bodies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Frame", HeaderText = "Model frame", Width = 170, ReadOnly = true });
        _bodies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mass", HeaderText = "Raw mass (kg)", Width = 180, ValueType = typeof(string) });
        _bodies.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source file", Width = 390, ReadOnly = true });
        foreach (var row in rows)
        {
            var index = _bodies.Rows.Add(row.Target.BodyPath + "/@Mass", row.Target.ModelFrame, Input(row.Draft), row.Target.RelativeFile);
            var gridRow = _bodies.Rows[index]; gridRow.Tag = row;
            gridRow.Cells["Body"].ToolTipText = row.Target.BodyPath + "/@Mass";
            gridRow.Cells["Source"].ToolTipText = row.Target.RelativeFile;
        }
        _bodies.BackgroundColor = _bodies.DefaultCellStyle.BackColor = AppColors.Get("Surface");
        _bodies.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
        _bodies.DefaultCellStyle.SelectionBackColor = AppColors.Get("SelectionBlue");
        _bodies.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        _bodies.CellFormatting += FormatMass;
        _bodies.CellValidating += (_, e) =>
        {
            if (e.RowIndex < 0 || _bodies.Columns[e.ColumnIndex].Name != "Mass") return;
            if (!TryMass(e.FormattedValue, out var value))
            { e.Cancel = true; _status.Text = "Enter a mass from 0 to 1,000,000 kg, or leave blank for inheritance."; }
            else _status.Text = "";
        };
        _bodies.EditingControlShowing += (_, e) =>
        {
            if (_bodies.CurrentCell is not { OwningColumn.Name: "Mass" } cell || e.Control is not TextBox text) return;
            text.Text = Convert.ToString(cell.Value, CultureInfo.CurrentCulture) ?? ""; text.SelectAll();
        };
        _bodies.DataError += (_, e) => { e.ThrowException = false; _status.Text = "Enter a valid raw mass in kg."; };
        reset.Click += (_, _) =>
        {
            _bodies.CancelEdit();
            foreach (DataGridViewRow gridRow in _bodies.Rows)
                if (gridRow.Tag is CargoMassRow { HasOriginal: true } row) gridRow.Cells["Mass"].Value = Input(row.Original);
            _bodies.Invalidate(); _status.Text = "Original raw masses restored in this popup. Choose OK to update the preview.";
        };
        ok.Click += (_, _) =>
        {
            if (!_bodies.EndEdit()) return;
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow gridRow in _bodies.Rows)
            {
                if (gridRow.Tag is not CargoMassRow row || !TryMass(gridRow.Cells["Mass"].Value, out var value))
                { _status.Text = "Enter a mass from 0 to 1,000,000 kg, or leave blank for inheritance."; return; }
                values[CargoMassStore.Key(row.Target)] = CargoMassStore.Equal(value, row.Draft) ? row.Draft : value;
            }
            Values = values; DialogResult = DialogResult.OK;
        };
        Shown += (_, _) => { if (_bodies.Rows.Count > 0) _bodies.CurrentCell = _bodies.Rows[0].Cells["Mass"]; };
    }

    private static string Input(string? value) => value is null ? "" : decimal.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture);
    private static string Display(string? value) => value is null ? "Inherited" : Input(value);
    private static bool TryMass(object? input, out string? value)
    {
        var text = Convert.ToString(input, CultureInfo.CurrentCulture)?.Trim() ?? "";
        value = null;
        if (text.Length == 0) return true;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number) || number < 0 || number > 1000000) return false;
        value = number.ToString(CultureInfo.InvariantCulture); return true;
    }
    private void FormatMass(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _bodies.Columns[e.ColumnIndex].Name != "Mass" || _bodies.Rows[e.RowIndex].Tag is not CargoMassRow row
            || !TryMass(_bodies[e.ColumnIndex, e.RowIndex].Value, out var value)) return;
        e.CellStyle!.BackColor = !CargoMassStore.Equal(value, row.Current) ? Color.LightGoldenrodYellow
            : row.HasOriginal && !CargoMassStore.Equal(row.Current, row.Original) ? Color.Honeydew : AppColors.Get("Surface");
        if (!_bodies[e.ColumnIndex, e.RowIndex].IsInEditMode)
        {
            e.Value = value is null ? "" : Display(value);
            if (row.HasOriginal && !CargoMassStore.Equal(value, row.Original)) e.Value = $"{Display(value)} ({Display(row.Original)})";
            e.FormattingApplied = true;
        }
        _bodies[e.ColumnIndex, e.RowIndex].ToolTipText = $"Original raw: {(row.HasOriginal ? Display(row.Original) : "Unavailable")}\nIn files: {Display(row.Current)}\nInherited mass: {row.Target.InheritedMass?.ToString(CultureInfo.CurrentCulture) ?? "Unresolved"}\nBlank removes this local Mass attribute. Cargo variants sharing this source body change together.";
    }
}
