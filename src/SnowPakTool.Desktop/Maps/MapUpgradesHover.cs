using System.Data;

namespace SnowPakTool.Desktop;

internal sealed partial class MapUpgradesTab
{
    private readonly System.Windows.Forms.Timer _compatibilityHoverTimer = new() { Interval = 350 };
    private UpgradeDistributionSlot? _hoverSlot;
    private UpgradeCompatibilityPreview? _compatibilityPreview;

    private void InitializeCompatibilityHover()
    {
        _upgrades.CellMouseEnter += (_, e) => ScheduleCompatibilityHover(e.RowIndex, e.ColumnIndex);
        _upgrades.CellMouseMove += (_, e) => ScheduleCompatibilityHover(e.RowIndex, e.ColumnIndex);
        _upgrades.CellMouseLeave += (_, _) => HideCompatibilityHover();
        _upgrades.MouseLeave += (_, _) => HideCompatibilityHover();
        _upgrades.CellMouseDown += (_, _) => HideCompatibilityHover();
        _upgrades.Scroll += (_, _) => HideCompatibilityHover();
        _upgrades.Sorted += (_, _) => HideCompatibilityHover();
        _upgrades.DataSourceChanged += (_, _) => HideCompatibilityHover();
        _upgrades.ColumnWidthChanged += (_, _) => HideCompatibilityHover();
        _upgrades.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && _upgrades.Columns[e.ColumnIndex].Name == "Compatible trucks") e.ToolTipText = "";
        };
        VisibleChanged += (_, _) => { if (!Visible) HideCompatibilityHover(); };
        EnabledChanged += (_, _) => { if (!Enabled) HideCompatibilityHover(); };
        _compatibilityHoverTimer.Tick += (_, _) => ShowCompatibilityHover();
        Disposed += (_, _) => { HideCompatibilityHover(); _compatibilityHoverTimer.Dispose(); };
    }

    private UpgradeDistributionSlot? CompatibilitySlotAt(int row, int column) => row >= 0 && row < _upgrades.Rows.Count
        && column >= 0 && column < _upgrades.Columns.Count && _upgrades.Columns[column].Name == "Compatible trucks"
        && _upgrades.Rows[row].DataBoundItem is DataRowView view ? view["Slot"] as UpgradeDistributionSlot : null;

    private void ScheduleCompatibilityHover(int row, int column)
    {
        var slot = CompatibilitySlotAt(row, column);
        if (ReferenceEquals(slot, _hoverSlot)) return;
        HideCompatibilityHover();
        if (slot is null || _loading) return;
        _hoverSlot = slot; _compatibilityHoverTimer.Interval = 350; _compatibilityHoverTimer.Start();
    }

    private void ShowCompatibilityHover()
    {
        if (_hoverSlot is null || _catalog is null || !Visible || !Enabled || _loading
            || FindForm() is not { ContainsFocus: true } owner)
        { HideCompatibilityHover(); return; }
        var point = _upgrades.PointToClient(Cursor.Position);
        var hit = _upgrades.HitTest(point.X, point.Y);
        if (!ReferenceEquals(CompatibilitySlotAt(hit.RowIndex, hit.ColumnIndex), _hoverSlot))
        { HideCompatibilityHover(); return; }
        if (_compatibilityPreview is not null) return;
        var assignment = _draft?.Assignments.FirstOrDefault(a => UpgradeTrialService.Key(a.Slot) == UpgradeTrialService.Key(_hoverSlot));
        var currentId = DesiredId(_hoverSlot, assignment);
        _catalog.Parts.TryGetValue(currentId, out var current);
        _catalog.Parts.TryGetValue(_hoverSlot.OriginalUpgradeId, out var original);
        _compatibilityPreview = new UpgradeCompatibilityPreview(current?.Name ?? currentId,
            TruckNames(current), original?.Name ?? _hoverSlot.OriginalUpgradeId, TruckNames(original));
        _compatibilityPreview.PlaceNear(Cursor.Position);
        _compatibilityPreview.Show(owner);
        _compatibilityPreview.PlaceNear(Cursor.Position);
        _compatibilityHoverTimer.Interval = 100;
    }

    private void HideCompatibilityHover()
    {
        _compatibilityHoverTimer.Stop(); _hoverSlot = null;
        var preview = _compatibilityPreview; _compatibilityPreview = null; preview?.Dispose();
    }
}

internal sealed class UpgradeCompatibilityPreview : Form
{
    internal UpgradeCompatibilityPreview(string current, string[] currentTrucks, string original, string[] originalTrucks)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Info; ForeColor = SystemColors.InfoText;
        AccessibleName = "New and original upgrade compatibility";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var width = Math.Min(720, Screen.FromPoint(Cursor.Position).WorkingArea.Width - 40);
        var columnWidth = width / 2 - 24;
        var newText = currentTrucks.Length == 0 ? "No compatible trucks" : string.Join(Environment.NewLine, currentTrucks);
        var oldText = originalTrucks.Length == 0 ? "No compatible trucks" : string.Join(Environment.NewLine, originalTrucks);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        var height = Math.Max(TextRenderer.MeasureText(newText, Font, new Size(columnWidth, int.MaxValue), flags).Height,
            TextRenderer.MeasureText(oldText, Font, new Size(columnWidth, int.MaxValue), flags).Height);
        ClientSize = new Size(width, Math.Min(height + 86, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 50));
        AddColumn(0, "New upgrade: " + current, newText);
        AddColumn(1, "Original upgrade: " + original, oldText);
        Controls.Add(layout);
        void AddColumn(int column, string title, string trucks)
        {
            layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Padding = new Padding(3), UseMnemonic = false }, column, 0);
            layout.Controls.Add(new Label { Text = trucks, Dock = DockStyle.Fill, Padding = new Padding(3), UseMnemonic = false }, column, 1);
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) { message.Result = (IntPtr)(-1); return; }
        base.WndProc(ref message);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowFrame, ButtonBorderStyle.Solid);
    }
    internal void PlaceNear(Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X + 18; var y = cursor.Y + 20;
        if (x + Width > area.Right) x = cursor.X - Width - 18;
        if (y + Height > area.Bottom) y = cursor.Y - Height - 20;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
}
