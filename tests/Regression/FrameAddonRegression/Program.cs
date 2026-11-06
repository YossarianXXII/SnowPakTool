using System.Collections;
using System.Reflection;
using System.Data;
static class Program
{
 static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n) => App.GetType("SnowPakTool.Desktop." + n)!;
 static object P(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
 static object F(object o, string n) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
 static void Check(bool b, string n) { if (!b) throw new Exception(n); Console.WriteLine("PASS " + n); }
 [STAThread] static void Main()
 {
  var workspace = Path.GetFullPath("../workspace");
  var catalog = T("AddonCatalogService").GetMethod("Load")!.Invoke(null, [workspace, new Dictionary<string,string>()])!;
  var entries = ((IEnumerable)P(catalog,"Entries")).Cast<object>().ToArray();
  var roof = entries.Single(e => (string)P(e,"Id") == "don_71_roofrack_2");
  Check((decimal)P(P(roof,"Frame"),"Fuel") == 40 && (decimal)P(P(roof,"Frame"),"Repairs") == 150 && (decimal)P(P(roof,"Frame"),"SpareWheels") == 2, "roof rack supply capacities");
  var bed = entries.Single(e => (string)P(e,"Id") == "frame_addon_flatbed_2_zikz_612h_mastodont");
  Check((decimal)P(P(bed,"Frame"),"CargoSlots") == 2, "cargo slots read from AddonSlots");
  using var tab = (Control)Activator.CreateInstance(T("AddonsTab"), new object[] { (Func<string>)(() => workspace), true })!;
  tab.GetType().GetField("_catalog", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tab,catalog);
  using var form = new Form { Size = new Size(1450,900) };
  form.Controls.Add(tab); tab.Dock = DockStyle.Fill; form.CreateControl(); tab.CreateControl();
  tab.GetType().GetMethod("ApplyFilter",BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tab,null);
  var grid = (DataGridView)F(tab,"_addons");
  var expected = entries.Count(e => (string)P(e,"Type") == "Addon" && string.Equals((string)P(e,"Category"),"frame_addons",StringComparison.OrdinalIgnoreCase));
  Check(grid.Rows.Count == expected && expected > 100 && grid.ReadOnly, "frame-only read-only catalog: " + expected + " rows");
  var row = grid.Rows.Cast<DataGridViewRow>().Single(r => (string)r.Cells["ID"].Value == "don_71_roofrack_2");
  grid.CurrentCell = row.Cells["Frame addon"];
  tab.GetType().GetMethod("ShowProperties",BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tab,null);
  Check((decimal)row.Cells["Fuel (L)"].Value == 40 && row.Cells["Cargo slots"].Value == DBNull.Value, "numeric capacities and unresolved blank slots");
  Check(((DataGridView)F(tab,"_properties")).Rows.Count > 20, "selection shows full XML properties");
  var table = (DataTable)grid.DataSource!;
  var headers = T("TableHeaderBehavior").GetMethod("Attach")!.Invoke(null, new object?[] { grid, null, null, null })!;
  headers.GetType().GetMethod("SetSort")!.Invoke(headers, new object[] { "Fuel (L)", SortOrder.Descending });
  Check(table.DefaultView.Sort.Contains("DESC"), "numeric header sorting");
  var filter = headers.GetType().GetMethod("AddFilter")!.Invoke(headers, new object?[] { "ID", null, Array.Empty<string>() })!;
  filter.GetType().GetMethod("SelectValues")!.Invoke(filter, new object[] { new[] { "don_71_roofrack_2" } });
  Check(grid.Rows.Count == 1, "table supports filtering and numeric sorting");
  filter.GetType().GetMethod("Clear")!.Invoke(filter,null);
  using var main = (Form)Activator.CreateInstance(T("MainForm"))!;
  var editors = (TabControl)main.Controls.Find("EditorsNavigation", true).Single();
  Check(editors.TabPages.Cast<TabPage>().Any(p => p.Name == "Frame addons" && p.Controls.Count == 1), "main window exposes Frame addons under Editors");
  Console.WriteLine("Frame addon regression checks passed.");
 }
}
