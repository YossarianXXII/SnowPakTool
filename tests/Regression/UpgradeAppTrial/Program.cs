using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static object? Prepared;
    static Type T(string n) => App.GetType("SnowPakTool.Desktop." + n)!;
    static object P(object o,string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
    static object F(object o,string n) => o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    static object? Call(object o,string n,params object?[] a) => o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(o,a);
    static object? Static(string t,string n,params object?[] a) => T(t).GetMethod(n,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,a);
    static object[] Items(object o) => ((IEnumerable)o).Cast<object>().ToArray();
    static void Check(bool b,string n) { if(!b)throw new Exception(n); }
    static void Pump(Task task) { var until=DateTime.UtcNow.AddMinutes(3);while(!task.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(task.IsCompleted,"async work completed");task.GetAwaiter().GetResult(); }
    public static Task<TCatalog> Load<TCatalog>(bool force) => Task.FromResult((TCatalog)Prepared!);
    public static void Log<TEntry>(TEntry entry) => Console.WriteLine(P(entry!,"Message"));
    static IEnumerable<Control> All(Control c) {yield return c;foreach(Control child in c.Controls)foreach(var x in All(child))yield return x;}

    [STAThread] static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../.."));
        var workspace=@"S:\snowrunner_Shenanigans\AI\workspace";
        var original=Path.Combine(workspace,"initial.pak","original_files","[ps]");
        var before=Directory.GetFiles(original,"level_*.sso").ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        using var main=(Form)Activator.CreateInstance(T("MainForm"))!;
        ((TextBox)F(main,"_workspacePath")).Text=workspace;
        var task=(Task)Call(main,"GetUpgradeTrialCatalogAsync",false)!;
        Pump(task);var catalog=P(task,"Result");
        var allSlots=Items(P(catalog,"AllSlots"));var input=P(catalog,"Input");
        var parts=(IDictionary)P(catalog,"Parts");
        Console.WriteLine($"Catalog: {allSlots.Length} original slots, {Items(P(input,"Slots")).Length} owned-region slots, {parts.Count} resolved definitions.");
        Check(allSlots.Length==166,"all original slots loaded");
        Check(parts.Contains("wheels_extend_kirovets_k7m_tire_1"),"composite tire resolved");
        var tire=parts["wheels_extend_kirovets_k7m_tire_1"]!;
        Check((string)P(tire,"Name")=="MSH II","tire localized display name");
        var definitions=Items(P(input,"Definitions"));
        var extras=definitions.Where(d=>(bool)P(d,"Eligible")&&!allSlots.Any(s=>(string)P(s,"OriginalUpgradeId")== (string)P(d,"Id"))).ToArray();
        Check(extras.Length>10,"full replacement catalog extends beyond old map rewards");
        var trucks=(Task)Call(F(main,"_regionsTrucksTab"),"GetRandomizationCatalogAsync")!;Pump(trucks);
        var enabled=Items(Call(F(main,"_ownedContentTab"),"GetAvailableCustomGroupingTrucks")!).Select(t=>(string)P(t,"Code")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(enabled.Contains("tayga_6455b")) Check(Items(P(input,"TruckHomes")).Any(h=>(string)P(h,"Truck")=="tayga_6455b"&&(string)P(h,"Region")=="us_06"),"owned direct Maine garage reward included");
        var names=(IReadOnlyDictionary<string,string>)Static("WorkspaceDataCache","GetLocalizedNames",workspace)!;
        var allTrucks=(Dictionary<string,(string,string)>)Static("RegionTruckCatalogService","LoadTrucks",workspace,names,new List<string>())!;
        var allEnabled=allTrucks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allRegions=allSlots.Select(s=>(string)P(s,"Region")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expanded=Static("UpgradeTrialService","Prepare",workspace,P(trucks,"Result"),allEnabled,allRegions,new HashSet<string>(),P(input,"RegionOrder"))!;
        Check(Items(P(P(expanded,"Input"),"TruckHomes")).Any(h=>(string)P(h,"Truck")=="tayga_6455b"&&(string)P(h,"Region")=="us_06"),"direct garage reward resolves independently of the user's current ownership choices");
        var entries=Items(P(P(trucks,"Result"),"Entries"));
        var changed=entries.Where(e=>(bool)P(e,"TruckChanged")&&enabled.Contains((string)P(e,"TruckCode"))&&((string)P(e,"Availability")).StartsWith("Obtainable")).ToArray();
        Console.WriteLine($"Current obtainable placements differing from baseline and enabled: {changed.Length}.");
        foreach(var entry in changed)
            Check(Items(P(input,"TruckHomes")).Any(h=>(string)P(h,"Truck")== (string)P(entry,"TruckCode")&&(string)P(h,"Region")== (string)P(entry,"RegionCode")),"adapter uses effective randomized model instead of original model");
        var obtainable=entries.First(e=>((string)P(e,"Availability")).StartsWith("Obtainable"));
        var newModel=allEnabled.First(code=>code!=(string)P(obtainable,"TruckCode"));
        var payload=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(obtainable))!;
        payload["TruckCode"]=newModel;
        var edited=JsonSerializer.Deserialize(payload.ToJsonString(),T("RegionTruckEntry"))!;
        var syntheticEntries=Array.CreateInstance(T("RegionTruckEntry"),1);syntheticEntries.SetValue(edited,0);
        var syntheticTrucks=Activator.CreateInstance(T("RegionTruckCatalog"),new object[]{syntheticEntries,P(P(trucks,"Result"),"Warnings"),P(P(trucks,"Result"),"MapsRead"),P(P(trucks,"Result"),"EquipmentCatalog")})!;
        var simulated=Static("UpgradeTrialService","Prepare",workspace,syntheticTrucks,allEnabled,allRegions,new HashSet<string>(),P(input,"RegionOrder"))!;
        Check(Items(P(P(simulated,"Input"),"TruckHomes")).Any(h=>(string)P(h,"Truck")==newModel&&(string)P(h,"Region")== (string)P(obtainable,"RegionCode")),"a changed obtainable model controls the new home snapshot");
        var fixture=Path.Combine(project,".checks","UpgradeRegionAnalysis","source-workspace");
        var isolated=Activator.CreateInstance(T("UpgradeTrialCatalog"),new object[]{fixture,input,P(catalog,"AllSlots"),P(catalog,"Parts"),P(catalog,"Stamp"),P(catalog,"Warnings")})!;
        var entryType=T("RandomizationLogEntry");
        var logger=typeof(Program).GetMethod(nameof(Log))!.MakeGenericMethod(entryType).CreateDelegate(typeof(Action<>).MakeGenericType(entryType));
        var progress=Activator.CreateInstance(typeof(Progress<>).MakeGenericType(entryType),logger)!;
        var draft=Static("UpgradeTrialService","Generate",isolated,progress,20261105)!;
        Application.DoEvents();
        var assignments=Items(P(draft,"Assignments"));
        Check(assignments.Length==Items(P(input,"Slots")).Length,"owned pickup count preserved");
        Check(assignments.All(a=>P(a,"Kind").Equals(P(P(parts[(string)P(P(a,"Slot"),"OriginalUpgradeId")]!,"Definition"),"Kind"))),"each original pickup type preserved");
        Check(assignments.Where(a=>P(a,"Kind").ToString()=="MetalDetector").All(a=>(string)P(a,"UpgradeId")== (string)P(P(a,"Slot"),"OriginalUpgradeId")),"detector pinned");
        var newRewards=assignments.Count(a=>!allSlots.Any(s=>(string)P(s,"OriginalUpgradeId")== (string)P(a,"UpgradeId")));
        Check(newRewards>0,"new catalog rewards actually selected");
        Check(Items(P(Static("UpgradeTrialStore","Load",fixture)!,"Assignments")).Length==assignments.Length,"draft survives reload");

        Prepared=isolated;
        var loader=typeof(Program).GetMethod(nameof(Load))!.MakeGenericMethod(T("UpgradeTrialCatalog"))
            .CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool),typeof(Task<>).MakeGenericType(T("UpgradeTrialCatalog"))));
        using var canvas=new Panel{Size=new Size(1100,850)};
        using var tab=(Control)Activator.CreateInstance(T("MapUpgradesTab"),new object[]{new Func<string>(()=>fixture),new Func<string>(()=>""),loader})!;
        tab.Dock=DockStyle.Fill;canvas.Controls.Add(tab);canvas.CreateControl();tab.CreateControl();
        Pump((Task)Call(tab,"EnsureLoadedAsync")!);Application.DoEvents();
        var grid=(DataGridView)F(tab,"_upgrades");
        Check(grid.Rows.Count==166,"review table contains original/current rows");
        Check(grid.Rows.Cast<DataGridViewRow>().Any(r=>Convert.ToString(r.Cells["Change state"].Value)=="Draft"),"review table shows changed draft rows");
        Check(!All(tab).OfType<Button>().Single(b=>b.Text=="Stage changes").Enabled,"Stage remains unavailable for draft-only trial");
        using(var bitmap=new Bitmap(canvas.Width,canvas.Height)){canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,canvas.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"map-upgrades.png"));}
        All(tab).OfType<Button>().Single(b=>b.Text=="Reset all to original").PerformClick();
        Check(Items(P(Static("UpgradeTrialStore","Load",fixture)!,"Assignments")).Length==0,"reset clears isolated draft");
        Check(before.All(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))==p.Value),"all original level scripts unchanged");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"summary.json"),JsonSerializer.Serialize(new {OriginalSlots=allSlots.Length,OwnedSlots=assignments.Length,Definitions=parts.Count,ExtraEligible=extras.Length,NewRewardChoices=newRewards,RandomizedTruckPlacements=changed.Length},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS trial adapter, actual randomized homes, {extras.Length} additional eligible IDs, {newRewards} new reward choices, composite tire, saved/reset draft, review UI and unchanged original game settings.");
    }
}
