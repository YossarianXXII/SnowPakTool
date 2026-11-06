using SnowPakTool.Desktop;
internal static class RealRandomizerEquipmentChecks
{
 public static void Run(string workspace)
 {
  var warnings=new List<string>();var names=LocalizedStrings.Load(WorkspaceOriginals.GetCurrentPath(workspace,Path.Combine("[strings]","strings_english.str")));
  var equipment=new TruckEquipmentCatalog(workspace,names,warnings);
  var source=new RegionTruckEntry("us_01","Region","level_us_01_01","Map","international_fleetstar_f2070a","Original","Heavy-duty","Obtainable — discovery/start","","",0,0,0,"","",
   OriginalEquipment:new(true,["frame_addon_sideboard_2"],"","","",WheelScale:0));
  var count=0;
  foreach(var target in new[]{"international_paystar_5070","ws_4964_white","voron_ae4380","voron_grad"})
   for(var category=0;category<4;category++)
   {
    if(!equipment.TryRandomizationConfiguration(source,target,category,out var config,out _))
    {
     object? Call(string method,params object[] values)=>equipment.GetType().GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(equipment,values);
     Console.WriteLine("sockets="+((Array)Call("RandomizationSockets",target)!).Length+" tires="+((Array)Call("RandomizationTires",target)!).Length+" bed="+Call("CanInstallAddons",target,new[]{"frame_addon_sideboard_2"}));
     foreach(var addon in equipment.Resolve(source.TruckCode,source.OriginalEquipment!).InstalledAddons!)Console.WriteLine(addon.Id+" => "+Call("RequiredAddonKind",addon.Id));
     foreach(var warning in warnings.Take(4))Console.WriteLine(warning);
     throw new Exception("Real definition did not produce a valid sideboard/tire configuration: "+target);
    }
    if(!equipment.IsConfigurationSupported(target,config))throw new Exception("Real configuration failed validation");
    if(!equipment.ResolveConfigured(target,config).InstalledAddons!.Any(a=>a.Name.Contains("Sideboard",StringComparison.OrdinalIgnoreCase)))throw new Exception("Real sideboard missing");
    count++;
   }
  Console.WriteLine($"PASS read-only real definitions: {count} sideboard/tire combinations across four vehicles; configurations validated without queuing or writing maps.");
 }
}
