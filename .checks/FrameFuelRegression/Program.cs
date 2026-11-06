using System.Reflection;
using System.Collections;
using System.Data;
static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object? Call(string type,string method,params object?[] args)=>T(type).GetMethod(method,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,args);
 static object F(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static void Set(object o,string n,object value)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(o,value);
 static object? Invoke(object o,string n)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(o,null);
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static object Edit(string path,decimal before,decimal after)=>Activator.CreateInstance(T("FrameAddonFuelEdit"),[path,"Test",before,after])!;
 static object Edits(params object[] edits){var array=Array.CreateInstance(T("FrameAddonFuelEdit"),edits.Length);for(int i=0;i<edits.Length;i++)array.SetValue(edits[i],i);var list=Activator.CreateInstance(typeof(List<>).MakeGenericType(T("FrameAddonFuelEdit")))!;foreach(var e in edits)((IList)list).Add(e);return list;}
 [STAThread]static void Main()
 {
  Check((decimal)Call("TruckPropertiesStore","RoundFuel",12.5m)! ==15m && (decimal)Call("TruckPropertiesStore","RoundFuel",1m)! ==5m && (decimal)Call("TruckPropertiesStore","RoundFuel",300000m)! ==100000m,"shared rounding, ties up and bounds");
  Check((decimal)Call("FrameAddonFuelStore","RoundFuel",0m)! ==0m && (decimal)Call("FrameAddonFuelStore","RoundFuel",30000m)! ==10000m,"addon-specific zero and 10,000 litre bounds leave truck rules unchanged");
  var workspace=Path.Combine(AppContext.BaseDirectory,"fixture-"+Guid.NewGuid().ToString("N"));
  var relative="[media]/classes/trucks/addons/tank.xml";var original=Path.Combine(workspace,"original_files",relative);Directory.CreateDirectory(Path.GetDirectoryName(original)!);Directory.CreateDirectory(Path.Combine(workspace,"updated"));
  var text="<_templates><TruckData><Example FuelCapacity=\"999\" /></TruckData></_templates>\n<TruckAddon><TruckData FuelCapacity=\"25\" RepairsCapacity=\"150\"/><GameData Category=\"frame_addons\" Price=\"100\" /></TruckAddon>";
  File.WriteAllText(original,text);
  var folder=Path.GetDirectoryName(original)!;
  File.WriteAllText(Path.Combine(folder,"water.xml"),"<TruckAddon><TruckData WaterCapacity=\"1800\"/><GameData Category=\"frame_addons\"/></TruckAddon>");
  File.WriteAllText(Path.Combine(folder,"child.xml"),"<_parent File=\"tank\"/><TruckAddon><GameData Category=\"frame_addons\"/></TruckAddon>");
  File.WriteAllText(Path.Combine(folder,"zero.xml"),"<TruckAddon><TruckData FuelCapacity=\"0\"/><GameData Category=\"frame_addons\"/></TruckAddon>");
  var catalog=Call("AddonCatalogService","Load",workspace,new Dictionary<string,string>())!;
  var rows=Call("AddonsTab","LoadFuelRows",workspace,catalog)!;
  Check(((IDictionary)rows).Count==2,"skip inherited and undefined fuel capacities while retaining explicit zero");
  using var tab=(Control)Activator.CreateInstance(T("AddonsTab"),new object[]{(Func<string>)(()=>workspace),true})!;
  using var form=new Form { Size=new Size(1120,920) };tab.Dock=DockStyle.Fill;form.Controls.Add(tab);form.CreateControl();tab.CreateControl();
  Set(tab,"_catalog",catalog);Set(tab,"_loadedWorkspace",workspace);Set(tab,"_fuelRows",rows);Invoke(tab,"ApplyFilter");
  var grid=(DataGridView)F(tab,"_addons");var multiplier=(TrackBar)F(tab,"_fuelMultiplier");
  decimal Fuel()=> (decimal)((DataTable)grid.DataSource!).Rows.Cast<DataRow>().Single(r=>(string)r["ID"]=="tank")["Fuel (L)"];
  var changedOnly=(CheckBox)F(tab,"_changedOnly");_=grid.Handle;changedOnly.Checked=true;Application.DoEvents();
  Check(grid.Rows.Count==0,"frame changed-only hides unchanged capacities");
  multiplier.Value=1;Check(Fuel()==25m,"moving slider leaves fuel unchanged");Invoke(tab,"MultiplyFuel");Check(Fuel()==15m,"half multiplier rounds ties upward");
  multiplier.Value=4;Invoke(tab,"MultiplyFuel");Invoke(tab,"MultiplyFuel");Check(Fuel()==60m,"repeated multiplications use current preview");
  Check((decimal)((DataTable)grid.DataSource!).Rows.Cast<DataRow>().Single(r=>(string)r["ID"]=="zero")["Fuel (L)"]==0m,"multiplication leaves zero addon fuel capacities unchanged");
  Application.DoEvents();Check(grid.Rows.Count==1&&grid.Rows[0].Cells["ID"].Value?.ToString()=="tank","frame changed-only updates after fuel previews change");
  changedOnly.Checked=false;Application.DoEvents();Check(grid.Rows.Count==4,"frame changed-only can restore all rows");
  Check(!((IEnumerable)Call("FrameAddonFuelStore","Pending",workspace)!).Cast<object>().Any() && File.ReadAllText(original)==text,"preview neither stages nor writes source");
  void Layout(Control c){_=c.Handle;c.PerformLayout();foreach(Control child in c.Controls)Layout(child);}
  Layout(form);
  using(var bitmap=new Bitmap(tab.Width,tab.Height)){tab.DrawToBitmap(bitmap,new Rectangle(Point.Empty,tab.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"frame-fuel.png"));}
  Invoke(tab,"StageFuel");Check(((IEnumerable)Call("FrameAddonFuelStore","Pending",workspace)!).Cast<object>().Count()==1,"explicit staging persists pending edit");
  Invoke(tab,"MultiplyFuel");Call("FrameAddonFuelStore","Apply",workspace);
  var updated=Path.Combine(workspace,"updated",relative);var applied=File.ReadAllText(updated);
  Check((decimal)Call("FrameAddonFuelStore","Read",applied)! ==60m && applied==text.Replace("FuelCapacity=\"25\"","FuelCapacity=\"60\""),"apply uses staged snapshot and preserves all other text/templates");
  Check(File.ReadAllText(original)==text && !((IEnumerable)Call("FrameAddonFuelStore","Pending",workspace)!).Cast<object>().Any(),"original preserved and staged snapshot cleared");
  var other="[media]/classes/trucks/addons/other.xml";var otherOriginal=Path.Combine(workspace,"original_files",other);File.WriteAllText(otherOriginal,text);
  Call("FrameAddonFuelStore","Save",workspace,Edits(Edit(relative,60,100),Edit(other,99,100)));
  bool rejected=false;try{Call("FrameAddonFuelStore","Apply",workspace);}catch(TargetInvocationException e)when(e.InnerException is InvalidDataException){rejected=true;}
  Check(rejected && File.ReadAllText(updated)==applied && !File.Exists(Path.Combine(workspace,"updated",other)),"conflict preflight prevents partial writes");
  Check(((IEnumerable)Call("FrameAddonFuelStore","Pending",workspace)!).Cast<object>().Count()==2,"failed apply retains pending edits");
  rejected=false;try{Call("FrameAddonFuelStore","Patch",text,Edit("../outside.xml",25,50));}catch(TargetInvocationException e)when(e.InnerException is InvalidDataException){rejected=true;}
  Check(rejected,"unsafe staged path rejected");
  var exact=(string)Call("FrameAddonFuelStore","Patch",text,Edit(relative,25,12))!;
  Check((decimal)Call("FrameAddonFuelStore","Read",exact)! ==12m,"restoration accepts an exact original capacity that is not a multiple of five");
  Check((decimal)Call("FrameAddonFuelStore","Read",(string)Call("FrameAddonFuelStore","Patch",text,Edit(relative,25,0))!)! ==0m,"zero is an allowed staged addon fuel capacity");
  rejected=false;try{Call("FrameAddonFuelStore","Patch",text,Edit(relative,25,10001));}catch(TargetInvocationException e)when(e.InnerException is InvalidDataException){rejected=true;}
  Check(rejected,"addon fuel above 10,000 litres is rejected");
 }
}

