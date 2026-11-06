using SnowPakTool.Desktop;
using System.Drawing;
using System.Windows.Forms;
internal static class Program
{
 [STAThread] static void Main()
 {
  using var scope = new Panel();
  using var first = new Label { Text = "match first", BackColor = Color.White };
  using var second = new Label { Text = "match second", BackColor = Color.White };
  scope.Controls.Add(first); scope.Controls.Add(second);
  using var search = new CurrentTabSearch(() => scope);
  Check(search.Search("match") == 2, "match count");
  Check(first.BackColor == AppColors.Get("SearchActive") && second.BackColor == AppColors.Get("EngineMatch"), "initial active");
  search.MoveMatch(false);
  Check(second.BackColor == AppColors.Get("SearchActive") && first.BackColor == AppColors.Get("EngineMatch"), "next active");
  search.MoveMatch(true);
  Check(first.BackColor == AppColors.Get("SearchActive"), "previous active");
  search.MoveMatch(true);
  Check(second.BackColor == AppColors.Get("SearchActive"), "reverse wrap");
  search.Search("match"); Check(second.BackColor == AppColors.Get("SearchActive"), "refresh preserves active");
  search.Find("match"); Check(first.BackColor == AppColors.Get("SearchActive"), "find first");
  search.Clear(); Check(first.BackColor == Color.White && second.BackColor == Color.White, "restore colors");
  scope.Controls.Clear();
  using var grid = new DataGridView { AllowUserToAddRows = false, ColumnHeadersVisible = false };
  grid.Columns.Add("truck", "Truck"); grid.Rows.Add("match one"); grid.Rows.Add("match two"); scope.Controls.Add(grid);
  var cell = grid.Rows[0].Cells[0]; var original = cell.Style.SelectionBackColor;
  Check(search.Search("match") == 2, "grid matches");
  Check(cell.Style.BackColor == AppColors.Get("SearchActive") && cell.Style.SelectionBackColor == cell.Style.BackColor, "grid active selection");
  search.MoveMatch(false); Check(grid.Rows[1].Cells[0].Style.BackColor == AppColors.Get("SearchActive"), "grid navigation");
  search.Clear(); Check(cell.Style.BackColor.IsEmpty && cell.Style.SelectionBackColor == original, "grid restore");
  scope.Controls.Clear(); using var tile = new Tile { Text = "match tile" }; scope.Controls.Add(tile);
  search.Search("match"); Check(tile.SearchActive && tile.SearchMatch, "custom tile active");
  search.Clear(); Check(!tile.SearchActive && !tile.SearchMatch, "custom tile restore");
  Check(TruckClasses.ForTruck("ARAMATSU_FORESTER", "Offroad") == "Logging", "Forester class");
  Check(TruckClasses.ForTruck("kirovets_k700", "Heavy") == "Farming Tractor", "tractor preserved");
  Check(TruckClasses.ForTruck("other", "Heavy") == "Heavy", "normal class preserved");
  Console.WriteLine("PASS: active highlights, navigation, wrapping, refresh, grid selection, tile state, cleanup, and truck classes.");
 }
 static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
 sealed class Tile : Panel, ISearchHighlight
 {
  [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool SearchMatch { get; set; }
  [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool SearchActive { get; set; }
 }
}
namespace SnowPakTool.Desktop
{
 internal static class AppColors { public static Color Get(string key) => key == "SearchActive" ? Color.Orange : Color.LightYellow; }
}
