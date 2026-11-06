namespace SnowPakTool.Desktop;

/// <summary>
/// A bound catalog uses DataView filtering, so grid row indexes already are visible
/// indexes. Avoid WinForms' repeated visible-row searches when UI Automation asks
/// for names while scrolling a large catalog. Keep the standard accessibility
/// objects and their navigation, values, selection and table patterns.
/// </summary>
internal sealed class BoundCatalogGrid : DataGridView
{
    public BoundCatalogGrid() => RowTemplate = new CatalogRow();

    protected override void OnColumnAdded(DataGridViewColumnEventArgs e)
    {
        if (e.Column is DataGridViewTextBoxColumn) e.Column.CellTemplate = new CatalogCell();
        base.OnColumnAdded(e);
    }

    private static int RowStart => AppContext.TryGetSwitch("System.Windows.Forms.DataGridViewUIAStartRowCountAtZero", out var zero) && zero ? 0 : 1;

    private sealed class CatalogRow : DataGridViewRow
    {
        public CatalogRow() { }
        protected override AccessibleObject CreateAccessibilityInstance() => new CatalogRowAccessibility(this);

        private sealed class CatalogRowAccessibility(CatalogRow row) : DataGridViewRowAccessibleObject(row)
        {
            public override string Name => row.Index < 0 ? "" : $"Row {row.Index + RowStart}";

            public override AccessibleObject? Navigate(AccessibleNavigation direction)
            {
                if (row.Index < 0 || row.DataGridView is not { } grid) return null;
                return direction switch
                {
                    AccessibleNavigation.Next or AccessibleNavigation.Down => row.Index + 1 < grid.Rows.Count
                        ? grid.Rows[row.Index + 1].AccessibilityObject : null,
                    AccessibleNavigation.Previous or AccessibleNavigation.Up => row.Index > 0
                        ? grid.Rows[row.Index - 1].AccessibilityObject
                        : grid.ColumnHeadersVisible ? grid.AccessibilityObject.GetChild(0) : null,
                    _ => base.Navigate(direction)
                };
            }
        }
    }

    private sealed class CatalogCell : DataGridViewTextBoxCell
    {
        public CatalogCell() { }
        protected override AccessibleObject CreateAccessibilityInstance() => new CatalogCellAccessibility(this);

        private sealed class CatalogCellAccessibility(CatalogCell cell) : DataGridViewTextBoxCellAccessibleObject(cell)
        {
            public override string? Name
            {
                get
                {
                    if (cell.RowIndex < 0 || cell.OwningColumn is not { } column) return "";
                    var name = $"{column.HeaderText} Row {cell.RowIndex + RowStart}";
                    if (column.SortMode == DataGridViewColumnSortMode.NotSortable) return name;
                    return name + (column.HeaderCell.SortGlyphDirection switch
                    {
                        SortOrder.Ascending => ", Sorted ascending",
                        SortOrder.Descending => ", Sorted descending",
                        _ => ", Not sorted"
                    });
                }
            }
        }
    }
}
