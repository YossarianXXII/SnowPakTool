using System.Collections;
using System.Reflection;
internal static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type Type(string n) => App.GetType("SnowPakTool.Desktop." + n)!;
    static object? Call(string type, string name, params object[] args) => Type(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null,args);
    static List<object> Rows(object value) => ((IEnumerable)value).Cast<object>().ToList();
    static string Value(object row, string n) => (string)row.GetType().GetProperty(n)!.GetValue(row)!;
    static void Assert(bool yes, string message) { if (!yes) throw new Exception(message); }
    [STAThread]
    static void Main()
    {
        var workspace = Path.Combine(AppContext.BaseDirectory,"fixture");
        var original = Path.Combine(workspace,"initial.pak","original_files","[ps]","rank_settings.sso");
        var updated = Path.Combine(workspace,"initial.pak","updated","[ps]","rank_settings.sso");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!); Directory.CreateDirectory(Path.GetDirectoryName(updated)!);
        File.WriteAllText(original,"rankExp = [700, 1000,]\nother = 1");
        File.WriteAllText(updated,"rankExp = [1000, 2000,]\nother = 1");
        var rows = Rows(Call("WorkspaceChangeAudit","PakFiles",workspace)!);
        Assert(rows.Count==1 && Value(rows[0],"Property")=="Rank progression (rankExp)","Saved progression was not one row");
        Assert(Value(rows[0],"Before")=="2 steps; 1,700 XP total" && Value(rows[0],"After")=="2 steps; 3,000 XP total","Incorrect saved totals");
        File.WriteAllText(updated,"rankExp = [1000, 2000,]\nother = 2");
        Assert(Rows(Call("WorkspaceChangeAudit","PakFiles",workspace)!).Count==2,"Other setting changes hidden");
        File.WriteAllText(updated,"rankExp = [ 700, 1000, ]\nother = 1");
        Assert(Rows(Call("WorkspaceChangeAudit","PakFiles",workspace)!).Count==0,"Whitespace-only progression falsely changed");
        var owned = Activator.CreateInstance(Type("OwnedRegionState"))!;
        using var editor = (Control)Activator.CreateInstance(Type("ExperienceEditorTab"), new object[]{new Func<string>(()=>""),owned})!;
        void Set(string n,object v) => editor.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(editor,v);
        var grid = (DataGridView)editor.GetType().GetField("_grid",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
        Set("_isLoading",true); grid.Rows.Clear(); grid.Rows.Add(2,700,1000);grid.Rows.Add(3,1000,2000);
        Set("_loadedWorkspace",workspace);Set("_rankSettingsPath",updated);Set("_loadedValues",new List<int>{700,1000});Set("_isLoading",false);
        List<object> Pending() => Rows(editor.GetType().GetMethod("PendingChanges")!.Invoke(editor,[workspace])!);
        rows=Pending(); Assert(rows.Count==1 && Value(rows[0],"After")=="2 steps; 3,000 XP total","Pending ranks not consolidated accurately");
        grid.Rows[1].Cells["UpdatedXP"].Value="invalid";
        Assert(Pending().Count==1 && Value(Pending()[0],"After").StartsWith("Invalid XP"),"Invalid pending edits disappeared");
        grid.Rows[0].Cells["UpdatedXP"].Value=700;grid.Rows[1].Cells["UpdatedXP"].Value=1000;
        Assert(Pending().Count==0,"Unchanged grid falsely pending");
        Console.WriteLine("PASS: actual pending editor and saved-file audit use one rank progression row, correct totals, unchanged values, invalid pending edits, and other-file settings remain visible.");
    }
}
