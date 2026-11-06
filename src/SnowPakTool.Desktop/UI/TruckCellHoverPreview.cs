namespace SnowPakTool.Desktop;

/// <summary>A non-activating comparison using the same truck tiles as the grouping and tier tabs.</summary>
internal sealed class TruckCellHoverPreview : Form
{
    public TruckCellHoverPreview(RegionTruckEntry entry)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(386, 290);
        BackColor = SystemColors.Control;
        AccessibleName = "Original to current truck comparison";
        var current = new CustomTruck(entry.TruckCode, entry.Truck, entry.Class);
        var original = new CustomTruck(entry.BaselineTruckCode, entry.BaselineTruck,
            string.IsNullOrEmpty(entry.OriginalClass) ? entry.FileEntry?.Class ?? entry.Class : entry.OriginalClass);
        AddTile("Original", original, 8);
        var arrowFont = new Font("Segoe MDL2 Assets", 20f);
        Disposed += (_, _) => arrowFont.Dispose();
        Controls.Add(new Label
        {
            Text = "\uE72A", Font = arrowFont, TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(174, 26), Size = new Size(38, 210),
            AccessibleName = "Replaced by", TabStop = false
        });
        AddTile(entry.FileEntry is null ? "Current" : "Current (preview)", current, 212);
        Controls.Add(new Label
        {
            Text = $"Original ID: {original.Code}{Environment.NewLine}Current ID: {current.Code}",
            Location = new Point(8, 250), Size = new Size(370, 34), AutoEllipsis = true
        });
    }

    private void AddTile(string title, CustomTruck truck, int left)
    {
        var group = new GroupBox { Text = title, Location = new Point(left, 6), Size = new Size(166, 238), TabStop = false };
        var tile = CustomGroupWipTab.CreateTile(truck);
        tile.Location = new Point(8, 20);
        tile.AccessibleName = title + ": " + truck.Name;
        group.Controls.Add(tile);
        Controls.Add(group);
    }

    protected override bool ShowWithoutActivation => true;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowFrame, ButtonBorderStyle.Solid);
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084) { message.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref message);
    }

    public void PlaceNear(Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X + 18;
        var y = cursor.Y + 20;
        if (x + Width > area.Right) x = cursor.X - Width - 18;
        if (y + Height > area.Bottom) y = cursor.Y - Height - 20;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
}
