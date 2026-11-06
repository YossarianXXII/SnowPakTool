using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type T(string n) => App.GetType("SnowPakTool.Desktop." + n)!;
    static object P(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
    static object? S(string t, string n, params object?[] args)
    {
        try { return T(t).GetMethod(n, BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args); }
        catch(TargetInvocationException e) { throw e.InnerException!; }
    }
    static object Obj(string t, object data) => JsonSerializer.Deserialize(JsonSerializer.Serialize(data), T(t))!;
    static object[] Items(object o) => ((IEnumerable)o).Cast<object>().ToArray();
    static void Check(bool value, string name) { if(!value)throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Fails(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true,name);return; } throw new Exception(name); }
    static void Write(string root, string path, string text) { var p=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text); }
    static object? Prepared;
    public static Task<TCatalog> ReturnCatalog<TCatalog>(bool force) => Task.FromResult((TCatalog)Prepared!);
    static void Pump(Task task) { var until=DateTime.UtcNow.AddMinutes(2);while(!task.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}task.GetAwaiter().GetResult(); }
    static object Field(object o,string name) => o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
    static object? Call(object o,string name,params object?[] args) => o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(o,args);
    static string Slot(string zone,int index=0) => JsonSerializer.Serialize(new{Source="[ps]/level_us_01_01.sso",Map="level_us_01_01",Region="us_01",Zone=zone,RewardIndex=index,OriginalUpgradeId="old"});
    static object Catalog(string workspace) => Obj("UpgradeTrialCatalog",new {
        Workspace=workspace, Input=new{Slots=new[]{JsonSerializer.Deserialize<object>(Slot("zone"))},Definitions=Array.Empty<object>(),TruckHomes=Array.Empty<object>(),RegionOrder=new[]{"us_01"}},
        AllSlots=new object[]{JsonSerializer.Deserialize<object>(Slot("zone"))!,new{Source="[ps]/level_us_02_01.sso",Map="level_us_02_01",Region="us_02",Zone="unowned",RewardIndex=0,OriginalUpgradeId="old"}},
        Parts=new Dictionary<string,object>{
            ["old"]=new{Definition=new{Id="old",Kind=0,CompatibleTrucks=new[]{"a"}},Name="Old",Source="[media]/classes/engines/test.xml"},
            ["new"]=new{Definition=new{Id="new",Kind=0,CompatibleTrucks=new[]{"a"}},Name="New",Source="[media]/classes/engines/test.xml"},
            ["absent"]=new{Definition=new{Id="absent",Kind=0,CompatibleTrucks=new[]{"a"}},Name="Absent",Source="[media]/classes/engines/test.xml"},
            ["wheel_tire_1"]=new{Definition=new{Id="wheel_tire_1",Kind=5,CompatibleTrucks=new[]{"a"}},Name="Tire1",Source="[media]/classes/wheels/wheel.xml"},
            ["wheel_tire_2"]=new{Definition=new{Id="wheel_tire_2",Kind=5,CompatibleTrucks=new[]{"b"}},Name="Tire2",Source="[media]/classes/wheels/wheel.xml"}},
        Stamp="test",Warnings=Array.Empty<string>()});
    static object Draft(string id) => Obj("UpgradeTrialDraft",new{Seed=1,CreatedAt=DateTimeOffset.UtcNow,InputStamp="test",Assignments=new[]{new{
        Slot=JsonSerializer.Deserialize<object>(Slot("zone")),UpgradeId=id,Kind=0,Relation=0,Path=1,Fallback=false,Reused=false}},Report=(object?)null});
    [STAThread] static void Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        if (args.Contains("--real-readonly"))
        {
            using var form=(Form)Activator.CreateInstance(T("MainForm"))!;
            var field=T("MainForm").GetField("_workspacePath",BindingFlags.Instance|BindingFlags.NonPublic)!;
            ((TextBox)field.GetValue(form)!).Text=@"S:\snowrunner_Shenanigans\AI\workspace";
            var task=(Task)T("MainForm").GetMethod("GetUpgradeTrialCatalogAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,new object[]{true})!;
            while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(5);}task.GetAwaiter().GetResult();
            var real=P(task,"Result");
            var isolated=Path.Combine(AppContext.BaseDirectory,"real-copy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(isolated,"updated"));
            var sources=Items(P(real,"AllSlots")).Select(s=>(string)P(s,"Source")).Concat(((IDictionary)P(real,"Parts")).Values.Cast<object>().Select(p=>(string)P(p,"Source"))).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach(var source in sources){var src=(string)S("WorkspaceOriginals","GetCurrentPath",P(real,"Workspace"),source)!;var dst=Path.Combine(isolated,"original_files",source);Directory.CreateDirectory(Path.GetDirectoryName(dst)!);File.Copy(src,dst);}
            var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(real))!;json["Workspace"]=isolated;
            var copy=JsonSerializer.Deserialize(json.ToJsonString(),T("UpgradeTrialCatalog"))!;
            var entryType=T("RandomizationLogEntry");
            var progress=Activator.CreateInstance(typeof(Progress<>).MakeGenericType(entryType))!;
            S("UpgradeTrialService","Generate",copy,progress,20261105);
            var copiedOriginal=Path.Combine(isolated,"original_files");
            var copyHashes=Directory.GetFiles(copiedOriginal,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            var snapshot=S("UpgradeChanges","Build",copy)!;
            var outputs=(IDictionary)S("UpgradeChanges","Validate",isolated,snapshot)!;
            S("UpgradeChanges","Stage",copy); S("UpgradeChanges","Apply",isolated);
            Check(Items(P(S("UpgradeChanges","Build",copy)!,"Edits")).Length==0,"real-data randomization applies cleanly and is idempotent in isolated copy");
            Check(copyHashes.All(pair=>pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key)))),"real-data copied originals preserved byte for byte");
            Console.WriteLine($"PASS real-data isolated Stage/Apply: {Items(P(real,"AllSlots")).Length} pickups, {((IDictionary)P(real,"Parts")).Count} resolved IDs, {Items(P(snapshot,"Edits")).Length} edits across {outputs.Count} files; real workspace read only.");
            return;
        }
        var workspace=Path.Combine(AppContext.BaseDirectory,"fixture-"+Guid.NewGuid().ToString("N"));
        var original=Path.Combine(workspace,"original_files");var updated=Path.Combine(workspace,"updated");Directory.CreateDirectory(updated);
        const string sso="// old stays here\r\nzoneDesc = { zone = { props = { Other = { id = \"old\" }, ZonePropertyUpgradesGiver = { upgrades = [ \"old\" ] } } }, other = { props = { ZonePropertyUpgradesGiver = { upgrades = [ \"wheel_tire_2\" ] } } } }\r\n";
        Write(original,"[ps]/level_us_01_01.sso",sso);
        Write(original,"[ps]/level_us_02_01.sso","zoneDesc = { unowned = { props = { ZonePropertyUpgradesGiver = { upgrades = [ \"old\" ] } } } }");
        // Unowned current override must survive; it also makes one tire scale discoverable.
        Write(updated,"[ps]/level_us_02_01.sso","zoneDesc = { unowned = { props = { ZonePropertyUpgradesGiver = { upgrades = [ \"wheel_tire_2\" ] } } } }");
        const string xml="<?xml version=\"1.0\"?>\r\n<_templates><Engine><Template Name=\"old\" /></Engine></_templates>\r\n<EngineVariants>\r\n<Engine Name=\"old\"><GameData Price=\"500\" UnlockByExploration=\"true\" /></Engine>\r\n<Engine Name=\"new\"><GameData Price=\"700\" UnlockByExploration=\"false\" /></Engine>\r\n<Engine Name=\"absent\"><GameData UnlockByExploration=\"true\" /></Engine>\r\n</EngineVariants>";
        Write(original,"[media]/classes/engines/test.xml",xml);
        Write(original,"[media]/classes/wheels/wheel.xml","<TruckWheels><TruckTires><TruckTire Name=\"tire\"><GameData UnlockByExploration=\"false\" /></TruckTire></TruckTires></TruckWheels>");
        Write(original,"[strings]/strings_english.str","");
        var hashes=Directory.GetFiles(original,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
        var catalog=Catalog(workspace); S("UpgradeTrialStore","Save",workspace,Draft("new"));
        Check(Items(S("UpgradeChanges","Pending",workspace)!).Length==0,"randomization alone does not stage or write");
        S("UpgradeChanges","Stage",catalog);
        var pending=Items(S("UpgradeChanges","Pending",workspace)!);
        Check(pending.Length==3,"stages pickup and engine/tire exploration flags together");
        Check(S("UpgradeChanges","State",workspace,JsonSerializer.Deserialize(Slot("zone"),T("UpgradeDistributionSlot"))!,"new")!.ToString()=="Staged","staged state visible");
        Check(!File.Exists(Path.Combine(updated,"[ps]/level_us_01_01.sso")),"staging never writes game settings");
        Prepared=catalog;
        var loader=typeof(Program).GetMethod(nameof(ReturnCatalog))!.MakeGenericMethod(T("UpgradeTrialCatalog")).CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(bool),typeof(Task<>).MakeGenericType(T("UpgradeTrialCatalog"))));
        using(var host=new Panel{Size=new Size(1100,850)})
        using(var tab=(Control)Activator.CreateInstance(T("MapUpgradesTab"),new object[]{new Func<string>(()=>workspace),new Func<string>(()=>""),loader})!)
        {
            tab.Dock=DockStyle.Fill;host.Controls.Add(tab);host.CreateControl();tab.CreateControl();
            _ = host.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Call(tab,"ConfigureStaging",new Func<Task>(()=>{S("UpgradeChanges","Stage",catalog);return Task.CompletedTask;}));
            Pump((Task)Call(tab,"EnsureLoadedAsync")!);
            var grid=(DataGridView)Field(tab,"_upgrades");
            Check(grid.Rows.Count==2,"map-upgrade review loads staged rows");
            Check(grid.Rows.Cast<DataGridViewRow>().Any(r=>Convert.ToString(r.Cells["Change state"].Value)=="Staged"),"map-upgrade review labels staged pickups");
            Check(((Button)Field(tab,"_stage")).Enabled,"map-upgrade staging button enabled");
            Call(tab,"ConfigureStaging",new Func<Task>(()=>throw new InvalidOperationException("test conflict")));
            ((Button)Field(tab,"_stage")).PerformClick();
            var until=DateTime.UtcNow.AddSeconds(15);while(!((Label)Field(tab,"_status")).Text.Contains("test conflict")&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}
            Check(((Label)Field(tab,"_status")).Text.Contains("test conflict"),"staging errors remain visible after reloading rows");
            Call(tab,"ConfigureApplying",new Func<Task>(()=>{S("UpgradeChanges","Apply",workspace);return Task.CompletedTask;}));
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Pump((Task)Call(tab,"EnsureLoadedAsync")!);
            Check(((Button)Field(tab,"_apply")).Enabled,"direct Apply enabled for persisted staged upgrade snapshot: " + ((Label)Field(tab,"_status")).Text);
            // Applying must use the saved snapshot even if the live draft subsequently changes.
            S("UpgradeTrialStore","Save",workspace,Draft("old"));
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ((Button)Field(tab,"_apply")).PerformClick();
            var appliedUntil=DateTime.UtcNow.AddSeconds(30);
            while(!((Label)Field(tab,"_status")).Text.Contains("Pack to a new PAK")&&DateTime.UtcNow<appliedUntil){Application.DoEvents();Thread.Sleep(5);}
            Check(((Label)Field(tab,"_status")).Text.Contains("Pack to a new PAK"),"direct Apply explains remaining packing/install step");
            Check(S("UpgradeChanges","CurrentId",workspace,JsonSerializer.Deserialize(Slot("zone"),T("UpgradeDistributionSlot"))!)!.ToString()=="new","direct Apply writes staged pickup and ignores later draft");
            Check(!((Button)Field(tab,"_apply")).Enabled,"direct Apply disabled after staged snapshot is consumed");
            S("UpgradeTrialStore","Save",workspace,Draft("new"));
            Write(updated,"[ps]/level_us_01_01.sso",sso);
            Write(updated,"[media]/classes/engines/test.xml",xml);
            Write(updated,"[media]/classes/wheels/wheel.xml",File.ReadAllText(Path.Combine(original,"[media]/classes/wheels/wheel.xml")));
            S("UpgradeChanges","Stage",catalog);
            using var bitmap=new Bitmap(host.Width,host.Height);host.DrawToBitmap(bitmap,new Rectangle(Point.Empty,host.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"upgrade-staging.png"));
        }
        using(var main=(Form)Activator.CreateInstance(T("MainForm"))!)
        {
            ((TextBox)Field(main,"_workspacePath")).Text=workspace;
            Check(Items(Call(main,"PendingPakChanges",workspace)!).Length==3,"Start aggregates persisted upgrade edits without opening upgrade tabs");
        }
        // Frozen snapshot: changing the draft after staging must not change what Apply writes.
        S("UpgradeTrialStore","Save",workspace,Draft("old"));
        Write(updated,"[media]/classes/engines/test.xml",xml.Replace("Price=\"500\"","Price=\"600\""));
        S("UpgradeChanges","Apply",workspace);
        var saved=File.ReadAllText(Path.Combine(updated,"[ps]/level_us_01_01.sso"));
        Check(saved==sso.Replace("upgrades = [ \"old\" ]", "upgrades = [ \"new\" ]"),"only exact source/zone/index changes; comments and other zone stay intact");
        var engines=File.ReadAllText(Path.Combine(updated,"[media]/classes/engines/test.xml"));
        Check(engines.Contains("Price=\"600\" UnlockByExploration=\"false\"")&&engines.Contains("Price=\"700\" UnlockByExploration=\"true\""),"resulting pickups lock new upgrade, unlock removed one, preserve unrelated price");
        Check(engines.Contains("Name=\"absent\"><GameData UnlockByExploration=\"false\""),"absent upgrade is unlocked");
        Check(File.ReadAllText(Path.Combine(updated,"[media]/classes/wheels/wheel.xml")).Contains("UnlockByExploration=\"true\""),"any tire scale pickup locks the common variant");
        Check(S("UpgradeChanges","CurrentId",workspace,JsonSerializer.Deserialize("{\"Source\":\"[ps]/level_us_02_01.sso\",\"Zone\":\"unowned\",\"RewardIndex\":0}",T("UpgradeDistributionSlot"))!)!.ToString()=="wheel_tire_2","unowned-region current pickup preserved");
        Check(Items(S("UpgradeChanges","Pending",workspace)!).Length==0,"successful Apply clears staged snapshot");
        // Reset must restore applied pickup even after removing the assignment.
        var slots=Array.CreateInstance(T("UpgradeDistributionSlot"),1);slots.SetValue(JsonSerializer.Deserialize(Slot("zone"),T("UpgradeDistributionSlot")),0);
        S("UpgradeTrialStore","Reset",workspace,slots);S("UpgradeChanges","Stage",catalog);S("UpgradeChanges","Apply",workspace);
        Check(File.ReadAllText(Path.Combine(updated,"[ps]/level_us_01_01.sso"))==sso,"reset after Apply restores exact original pickup");
        S("UpgradeTrialStore","Save",workspace,Draft("new")); S("UpgradeChanges","Stage",catalog);
        Write(updated,"[ps]/level_us_01_01.sso",sso.Replace("[ \"old\" ]","[ \"external\" ]"));
        var before=File.ReadAllText(Path.Combine(updated,"[media]/classes/engines/test.xml"));
        Fails(()=>S("UpgradeChanges","Apply",workspace),"external pickup conflict rejects Apply");
        Check(before==File.ReadAllText(Path.Combine(updated,"[media]/classes/engines/test.xml")),"conflict validates all files before any write");
        Check(Items(S("UpgradeChanges","Pending",workspace)!).Length>0,"failed Apply retains staged snapshot");
        Write(updated,"[ps]/level_us_01_01.sso",sso.Replace("other =", "extra = { props = { ZonePropertyUpgradesGiver = { upgrades = [ \"new\" ] } } }, other ="));
        Fails(()=>S("UpgradeChanges","Apply",workspace),"new pickup after staging rejects inconsistent exploration flags");
        Write(updated,"[ps]/level_us_01_01.sso",sso);
        Write(updated,"[media]/classes/engines/test.xml",before.Replace("Price=\"700\" UnlockByExploration=\"false\"", "Price=\"700\""));
        Fails(()=>S("UpgradeChanges","Apply",workspace),"exploration flag conflict rejects Apply");
        Check(hashes.All(pair=>pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key)))),"all original files remain byte identical");
        // XMLs with inherited or self-closing variants can get explicit flags without serializing siblings.
        var edit=Obj("UpgradeFileEdit",new{Source="x",Zone="",RewardIndex=-1,Element="Engine",Variant="inherit",Before=(string?)null,After="true"});
        var patched=(string)S("UpgradeChanges","Patch","<EngineVariants><Engine Name=\"inherit\" _template=\"base\" /></EngineVariants>",edit)!;
        Check(patched.Contains("<GameData UnlockByExploration=\"true\" />"),"self-closing inherited variants supported");
        Console.WriteLine("Fixture: "+workspace);
    }
}
