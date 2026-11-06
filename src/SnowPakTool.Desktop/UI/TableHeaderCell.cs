namespace SnowPakTool.Desktop;

internal sealed class TableHeaderCell : DataGridViewColumnHeaderCell
{
    private Func<bool> _isFiltered;
    public bool HasFilter { get; set; } = true;
    public TableHeaderCell() : this(() => false) { }
    public TableHeaderCell(Func<bool> isFiltered) => _isFiltered = isFiltered;
    public TableHeaderCell(bool sortable, bool hasFilter, Func<bool> isFiltered) : this(isFiltered) => HasFilter = hasFilter;
    public override object Clone()
    {
        var clone = (TableHeaderCell)base.Clone();
        clone._isFiltered = _isFiltered;
        clone.HasFilter = HasFilter;
        return clone;
    }
    internal static Rectangle FilterBounds(Rectangle bounds, float scale) => new(bounds.Right - (int)(20 * scale), bounds.Top, (int)(20 * scale), bounds.Height);
    internal static Rectangle TextBounds(Rectangle bounds, float scale, bool hasFilter, SortOrder sort)
    {
        var left = (int)(4 * scale);
        var icons = (hasFilter ? 20 : 0) + (sort != SortOrder.None ? 10 : 0) + 2;
        return new(bounds.Left + left, bounds.Top, Math.Max(0, bounds.Width - left - (int)(icons * scale)), bounds.Height);
    }

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates dataGridViewElementState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        // The themed base header paints its native sort arrow even without ContentForeground,
        // overlapping our funnel. Paint only header chrome here, then our text and icons below.
        if ((paintParts & (DataGridViewPaintParts.Background | DataGridViewPaintParts.ContentBackground)) != 0)
        {
            var element = System.Windows.Forms.VisualStyles.VisualStyleElement.Header.Item.Normal;
            if (Application.RenderWithVisualStyles && System.Windows.Forms.VisualStyles.VisualStyleRenderer.IsElementDefined(element))
                new System.Windows.Forms.VisualStyles.VisualStyleRenderer(element).DrawBackground(graphics, cellBounds);
            else
            {
                using var background = new SolidBrush(cellStyle.BackColor);
                graphics.FillRectangle(background, cellBounds);
            }
        }
        if (paintParts.HasFlag(DataGridViewPaintParts.Border))
        {
            using var border = new Pen(SystemColors.ControlDark);
            graphics.DrawLine(border, cellBounds.Right - 1, cellBounds.Top, cellBounds.Right - 1, cellBounds.Bottom - 1);
            graphics.DrawLine(border, cellBounds.Left, cellBounds.Bottom - 1, cellBounds.Right - 1, cellBounds.Bottom - 1);
        }
        if (!paintParts.HasFlag(DataGridViewPaintParts.ContentForeground)) return;
        var scale = graphics.DpiX / 96f;
        var filter = FilterBounds(cellBounds, scale);
        var text = TextBounds(cellBounds, scale, HasFilter, SortGlyphDirection);
        TextRenderer.DrawText(graphics, Convert.ToString(formattedValue), cellStyle.Font, text, cellStyle.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var y = cellBounds.Top + cellBounds.Height / 2f;
        var x = HasFilter ? filter.Left - 5 * scale : cellBounds.Right - 7 * scale;
        if (SortGlyphDirection != SortOrder.None)
        {
            var sign = SortGlyphDirection == SortOrder.Ascending ? 1 : -1;
            using var brush = new SolidBrush(cellStyle.ForeColor);
            PointF[] arrow = [new(x - 4 * scale, y + sign * 3 * scale), new(x + 4 * scale, y + sign * 3 * scale), new(x, y - sign * 3 * scale)];
            graphics.FillPolygon(brush, arrow);
        }
        if (!HasFilter) return;
        x = filter.Left + filter.Width / 2f;
        PointF[] funnel = [new(x - 5 * scale, y - 5 * scale), new(x + 5 * scale, y - 5 * scale), new(x + 1.5f * scale, y),
            new(x + 1.5f * scale, y + 4 * scale), new(x - 1.5f * scale, y + 6 * scale), new(x - 1.5f * scale, y)];
        using var pen = new Pen(_isFiltered() ? SystemColors.Highlight : cellStyle.ForeColor, scale);
        if (_isFiltered()) { using var brush = new SolidBrush(SystemColors.Highlight); graphics.FillPolygon(brush, funnel); }
        else graphics.DrawPolygon(pen, funnel);
    }
}

