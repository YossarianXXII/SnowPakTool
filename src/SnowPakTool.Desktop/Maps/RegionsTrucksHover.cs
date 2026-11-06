using System.Data;

namespace SnowPakTool.Desktop;

internal sealed partial class RegionsTrucksTab
{
    private readonly System.Windows.Forms.Timer _truckHoverTimer = new() { Interval = 350 };
    private RegionTruckEntry? _hoverEntry;
    private TruckCellHoverPreview? _truckHoverPreview;

    private void InitializeTruckHover()
    {
        _trucks.CellMouseEnter += (_, e) => ScheduleTruckHover(e.RowIndex, e.ColumnIndex);
        _trucks.CellMouseMove += (_, e) => ScheduleTruckHover(e.RowIndex, e.ColumnIndex);
        _trucks.CellMouseLeave += (_, _) => HideTruckHover();
        _trucks.MouseLeave += (_, _) => HideTruckHover();
        _trucks.CellMouseDown += (_, _) => HideTruckHover();
        _trucks.Scroll += (_, _) => HideTruckHover();
        _trucks.Sorted += (_, _) => HideTruckHover();
        _trucks.DataSourceChanged += (_, _) => HideTruckHover();
        _trucks.ColumnWidthChanged += (_, _) => HideTruckHover();
        _trucks.CellToolTipTextNeeded += (_, e) =>
        {
            // The rich truck preview replaces only this column's native tooltip.
            if (e.ColumnIndex >= 0 && e.ColumnIndex < _trucks.Columns.Count && _trucks.Columns[e.ColumnIndex].Name == "Truck") e.ToolTipText = "";
        };
        VisibleChanged += (_, _) => { if (!Visible) HideTruckHover(); };
        EnabledChanged += (_, _) => { if (!Enabled) HideTruckHover(); };
        _truckHoverTimer.Tick += (_, _) => ShowTruckHover();
        Disposed += (_, _) => { HideTruckHover(); _truckHoverTimer.Dispose(); };
    }

    private RegionTruckEntry? TruckEntryAt(int row, int column) => row >= 0 && row < _trucks.Rows.Count
        && column >= 0 && column < _trucks.Columns.Count && _trucks.Columns[column].Name == "Truck"
        && _trucks.Rows[row].DataBoundItem is DataRowView view && view["Entry"] is RegionTruckEntry entry ? entry : null;

    private void ScheduleTruckHover(int row, int column)
    {
        var entry = TruckEntryAt(row, column);
        if (ReferenceEquals(entry, _hoverEntry)) return;
        HideTruckHover();
        if (entry is null || _loading) return;
        _hoverEntry = entry;
        _truckHoverTimer.Interval = 350;
        _truckHoverTimer.Start();
    }

    private void ShowTruckHover()
    {
        if (_hoverEntry is null || !Visible || !Enabled || _loading || !_trucks.IsHandleCreated
            || FindForm() is not { ContainsFocus: true } owner)
        { HideTruckHover(); return; }
        var point = _trucks.PointToClient(Cursor.Position);
        var hit = _trucks.HitTest(point.X, point.Y);
        if (!ReferenceEquals(TruckEntryAt(hit.RowIndex, hit.ColumnIndex), _hoverEntry)) { HideTruckHover(); return; }
        if (_truckHoverPreview is not null) return;
        _truckHoverPreview = new TruckCellHoverPreview(_hoverEntry);
        _truckHoverPreview.PlaceNear(Cursor.Position);
        _truckHoverPreview.Show(owner);
        _truckHoverPreview.PlaceNear(Cursor.Position);
        _truckHoverTimer.Interval = 100; // Also dismiss when the app loses focus, without stealing focus ourselves.
    }

    private void HideTruckHover()
    {
        _truckHoverTimer.Stop();
        _hoverEntry = null;
        var preview = _truckHoverPreview;
        _truckHoverPreview = null;
        preview?.Dispose();
    }
}
