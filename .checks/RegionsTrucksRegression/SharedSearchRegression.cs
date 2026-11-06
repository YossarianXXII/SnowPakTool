using System.Data;
using System.Reflection;
using SnowPakTool.Desktop;

internal static class SharedSearchRegression
{
    internal static void Run()
    {
        using var root = new Panel { Size = new Size(900, 700) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var current = new TabPage("Active page");
        var other = new TabPage("Other page");
        tabs.TabPages.AddRange([current, other]);
        root.Controls.Add(tabs);
        var label = new Label { Text = "visible label marker", BackColor = Color.Pink, ForeColor = Color.Blue, AutoSize = true };
        current.Controls.Add(label);
        other.Controls.Add(new Label { Text = "inactive_outer_marker" });
        var nested = new TabControl { Location = new Point(0, 40), Size = new Size(400, 150) };
        var inner = new TabPage("Active subpage");
        var innerOther = new TabPage("Other subpage");
        inner.Controls.Add(new Label { Text = "active_inner_marker" });
        innerOther.Controls.Add(new Label { Text = "inactive_inner_marker" });
        nested.TabPages.AddRange([inner, innerOther]);
        current.Controls.Add(nested);
        var choice = new ComboBox { Location = new Point(420, 40), DropDownStyle = ComboBoxStyle.DropDownList };
        choice.Items.AddRange(["First", "Choice needle"]); choice.SelectedIndex = 0; current.Controls.Add(choice);
        var check = new CheckBox { Text = "checkbox needle", Location = new Point(420, 80), Checked = false };
        current.Controls.Add(check);
        var grid = new DataGridView { Location = new Point(0, 220), Size = new Size(800, 200), AllowUserToAddRows = false, BindingContext = new BindingContext() };
        var table = new DataTable();
        table.Columns.Add("Secret Header"); table.Columns.Add("Entry", typeof(SearchFixtureRecord));
        table.Rows.Add("alpha cell marker", new SearchFixtureRecord("detail_source_marker", [new("deep_nested_marker")]));
        grid.DataSource = table;
        current.Controls.Add(grid);
        root.CreateControl(); tabs.CreateControl(); nested.CreateControl(); grid.CreateControl();
        grid.Columns["Entry"]!.Visible = false;
        grid.Rows[0].Cells[0].Style.BackColor = Color.Red;
        grid.Rows[0].Cells[0].ToolTipText = "tooltip_value_marker";
        using var search = new CurrentTabSearch(() => tabs.SelectedTab);
        search.Attach(root);
        Check(search.Search("vi") == 0 && label.BackColor == Color.Pink, "Three-character minimum applies to all targets");
        Check(search.Search("visible label") == 1 && label.BackColor == AppColors.Get("EngineMatch"), "Control text is searched");
        search.Clear();
        Check(label.BackColor == Color.Pink && label.ForeColor == Color.Blue, "Original control colors restored");
        var firstMatch = new TextBox { Text = "navigation target first", Location = new Point(420, 115) };
        var secondMatch = new TextBox { Text = "navigation target second", Location = new Point(550, 115) };
        current.Controls.Add(firstMatch); current.Controls.Add(secondMatch);
        Check(search.Search("navigation target") == 2 && search.MoveMatch(false), "Tab navigation starts at the first live result");
        int ActiveHit() => (int)typeof(CurrentTabSearch).GetField("_activeHit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!;
        Check(ActiveHit() == 0 && search.MoveMatch(false) && ActiveHit() == 1 && search.MoveMatch(false) && ActiveHit() == 0,
            "Forward navigation advances and wraps");
        Check(search.MoveMatch(true) && ActiveHit() == 1, "Backward navigation wraps to the previous match");
        search.Search("navigation target"); Check(ActiveHit() == 1, "Refreshing a live query preserves the active result");
        search.Clear(); Check(!search.MoveMatch(false), "Normal Tab behavior resumes without search results");
        current.Controls.Remove(firstMatch); current.Controls.Remove(secondMatch); firstMatch.Dispose(); secondMatch.Dispose();
        Check(search.Search("inactive_outer_marker") == 0 && search.Search("inactive_inner_marker") == 0, "Inactive tabs excluded at both levels");
        Check(search.Search("active_inner_marker") == 1, "Active nested tab included");
        Check(search.Search("alpha cell") == 1 && grid.Rows[0].Cells[0].Style.BackColor == AppColors.Get("EngineMatch"), "Table cells searched and highlighted");
        search.Clear(); Check(grid.Rows[0].Cells[0].Style.BackColor == Color.Red, "Table styles restored");
        grid.Height = 10;
        Check(search.Search("alpha cell") == 1, "Search also works while a table has no room to display rows");
        grid.Height = 200;
        foreach (var query in new[] { "Secret Header", "tooltip_value_marker", "detail_source_marker", "deep_nested_marker", "Choice needle", "checkbox needle" })
            Check(search.Search(query) > 0, "Shared search text source: " + query);
        Check(choice.SelectedIndex == 0 && !check.Checked && grid.Rows.Count == 1, "Search does not change filters, choices or rows");
        search.Clear(); Check(grid.EnableHeadersVisualStyles, "Header theme restored");
        search.Search("visible label"); nested.SelectedTab = innerOther;
        Check(label.BackColor == Color.Pink, "Sub-tab change clears results");
        tabs.SelectedTab = other;
        Check(search.Search("inactive_outer_marker") == 1 && search.Search("visible label") == 0, "Changing tabs changes search scope");
        var popup = (QuickSearchOverlay)typeof(CurrentTabSearch).GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(search)!;
        typeof(QuickSearchOverlay).GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(popup, null);
        tabs.SelectedTab = current;
        Check(!(bool)typeof(QuickSearchOverlay).GetField("_isOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!, "Tab changes close the shared popup");
        Console.WriteLine("PASS: one search scopes active tabs/sub-tabs, covers controls/headers/cells/details, preserves styles/data and enforces 3 characters.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

namespace SnowPakTool.Desktop
{
    internal sealed record SearchFixtureRecord(string Details, IReadOnlyList<SearchFixtureChild> Children);
    internal sealed record SearchFixtureChild(string Text);
}
