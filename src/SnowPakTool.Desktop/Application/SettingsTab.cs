namespace SnowPakTool.Desktop;

internal sealed class SettingsTab : UserControl
{
    private readonly FlowLayoutPanel _colors = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(12)
    };

    public SettingsTab()
    {
        Controls.Add(_colors);
        BuildColorRows();
        AppColors.Changed += ColorsChanged;
        Disposed += (_, _) => AppColors.Changed -= ColorsChanged;
    }

    private void ColorsChanged(object? sender, EventArgs e) => BuildColorRows();

    private void BuildColorRows()
    {
        _colors.SuspendLayout();
        foreach (Control control in _colors.Controls.Cast<Control>().ToArray()) control.Dispose();
        var generalRows = CreateColorPane("General colors");
        var tierRows = CreateColorPane("Tier colors");
        var classRows = CreateColorPane("Truck class colors");
        foreach (var entry in AppColors.GetEntries())
        {
            var row = new TableLayoutPanel
            {
                Width = Math.Max(650, _colors.ClientSize.Width - 40),
                Height = 42,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 3)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            var color = AppColors.Get(entry.Key);
            var code = new Label { Text = ToHex(color), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(3, 0, 4, 0) };
            var swatch = new Button { BackColor = color, FlatStyle = FlatStyle.Flat, Size = new Size(28, 28), Margin = new Padding(2), Anchor = AnchorStyles.Left, AccessibleName = $"Change {entry.Name} color" };
            swatch.FlatAppearance.BorderColor = SystemColors.ControlDark;
            swatch.Click += (_, _) =>
            {
                using var picker = new ColorDialog { Color = AppColors.Get(entry.Key), FullOpen = true, AnyColor = true, SolidColorOnly = true };
                if (picker.ShowDialog(FindForm()) == DialogResult.OK) AppColors.Set(entry.Key, picker.Color);
            };
            row.Controls.Add(swatch, 0, 0);
            var name = new Label { Text = entry.Name, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(3, 0, 0, 0) };
            row.Controls.Add(name, 1, 0);
            row.Controls.Add(new Label { Text = entry.Usage, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(3, 0, 0, 0) }, 2, 0);
            row.Controls.Add(code, 3, 0);
            (entry.Key.StartsWith("TruckClass", StringComparison.Ordinal) ? classRows :
                entry.Key.StartsWith("Tier", StringComparison.Ordinal) ? tierRows : generalRows).Controls.Add(row);
        }
        _colors.ResumeLayout();
    }

    private FlowLayoutPanel CreateColorPane(string title)
    {
        var pane = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8), Margin = new Padding(0, 0, 0, 12)
        };
        var rows = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(8, 24), Margin = Padding.Empty
        };
        pane.Controls.Add(rows);
        _colors.Controls.Add(pane);
        return rows;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
