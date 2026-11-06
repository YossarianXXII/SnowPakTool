using System.Windows.Forms.VisualStyles;

namespace SnowPakTool.Desktop;

/// <summary>A truck name with a separate replacement action at the right edge.</summary>
internal sealed class TruckReplacementCell : DataGridViewTextBoxCell
{
    public Action<int>? Open { get; set; }
    public override object Clone()
    {
        var clone = (TruckReplacementCell)base.Clone();
        clone.Open = Open;
        return clone;
    }
    private static Rectangle ButtonBounds(Rectangle cell) => new(cell.Right - 29, cell.Top + 2, 26, Math.Max(18, cell.Height - 4));

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates cellState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        var style = cellStyle.Clone();
        style.Padding = new Padding(style.Padding.Left, style.Padding.Top, style.Padding.Right + 30, style.Padding.Bottom);
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText, style, advancedBorderStyle, paintParts);
        if (rowIndex < 0 || !paintParts.HasFlag(DataGridViewPaintParts.ContentForeground)) return;
        var button = ButtonBounds(cellBounds);
        var state = DataGridView is { } grid && (!grid.Enabled || PresetBrowseMode.IsReadOnly(grid)) ? PushButtonState.Disabled : PushButtonState.Normal;
        ButtonRenderer.DrawButton(graphics, button, state);
        TextRenderer.DrawText(graphics, "⇄", cellStyle.Font, button, SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (DataGridView is { } grid && PresetBrowseMode.IsReadOnly(grid)) return;
        if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && ButtonBounds(new Rectangle(Point.Empty, Size)).Contains(e.Location)) Open?.Invoke(e.RowIndex);
    }

    protected override void OnKeyDown(KeyEventArgs e, int rowIndex)
    {
        if (DataGridView is { } grid && PresetBrowseMode.IsReadOnly(grid) && (e.KeyCode == Keys.F4 || e.Alt && e.KeyCode == Keys.Down))
        { e.Handled = e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
        { e.Handled = e.SuppressKeyPress = true; Open?.Invoke(rowIndex); }
        else base.OnKeyDown(e, rowIndex);
    }
}
