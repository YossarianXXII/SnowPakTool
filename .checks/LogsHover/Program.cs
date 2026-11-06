using System.Collections;
using System.Data;
using System.Reflection;
using System.Text.Json;
internal static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string name)=>App.GetType("SnowPakTool.Desktop."+name)!;
 static object Field(object obj,string name)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
 static void Set(object obj,string name,object? value)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(obj,value);
 static object? Call(object obj,string name,params object?[] args)=>obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(obj,args);
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
 static IEnumerable<Control> All(Control root){foreach(Control c in root.Controls){yield return c;foreach(var d in All(c))yield return d;}}
 static object Entry(string code,string name,string original,string originalName)=>JsonSerializer.Deserialize(JsonSerializer.Serialize(new
 {
  RegionCode="us_01",Region="Michigan",MapCode="level_us_01_01",Map="Black River",TruckCode=code,Truck=name,Class="Offroad",Availability="Obtainable — discovery/start",Task="",PlacementId="A",X=0,Y=0,Z=0,Evidence="",Source="",OriginalTruckCode=original,OriginalTruck=originalName,OriginalClass="Heavy-duty",TireDetails="Native tire tooltip"
 }),T("RegionTruckEntry"))!;
 [STAThread]static void Main()
 {
  Application.SetHighDpiMode(HighDpiMode.SystemAware);
  using(var panel=(Control)Activator.CreateInstance(T("TruckRandomizationTab"))!)
  {
   var toggle=(CheckBox)Field(panel,"_showDetails");var text=(TextBox)Field(panel,"_messages");Check(!toggle.Checked,"details hidden by default");
   void Add(string message,bool detail=false)=>Call(panel,"AddMessage",Activator.CreateInstance(T("RandomizationLogEntry"),new object[]{message,detail}));
   Add("Region one started");Add("Mudtires → Offroad: unsupported category",true);Add("Region one complete");
   Check(text.Lines.Where(l=>l.Length>0).SequenceEqual(new[]{"Region one started","Region one complete"}),"summary hides detail");
   toggle.Checked=true;Check(text.Lines.Where(l=>l.Length>0).SequenceEqual(new[]{"Region one started","Mudtires → Offroad: unsupported category","Region one complete"}),"hidden detail restored in chronological position");
   Add("Region two tire detail",true);Add("Region two complete");var full=text.Text;
   toggle.Checked=false;Check(!text.Text.Contains("Offroad")&&!text.Text.Contains("tire detail")&&text.Text.Contains("Region two complete"),"hide detail again");
   toggle.Checked=true;Check(text.Text==full,"toggle does not reorder or duplicate entries");
   panel.Size=new Size(1080,850);panel.CreateControl();panel.PerformLayout();using var bitmap=new Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"detailed-log-preview.png"));
  }
  var entry=Entry("voron_grad","Voron Grad","international_fleetstar_f2070a","Fleetstar F2070A");
  using(var preview=(Form)Activator.CreateInstance(T("TruckCellHoverPreview"),new[]{entry})!)
  {
   preview.CreateControl();preview.PerformLayout();
   var groups=preview.Controls.OfType<GroupBox>().OrderBy(g=>g.Left).ToArray();Check(groups.Length==2&&groups[0].Text=="Original"&&groups[1].Text=="Current","side by side current/original headings");
   Check(All(groups[0]).OfType<Label>().Any(l=>l.Text=="Fleetstar F2070A")&&All(groups[1]).OfType<Label>().Any(l=>l.Text=="Voron Grad"),"correct truck names on each tile");
   Check(All(preview).OfType<Label>().Any(l=>l.Text.Contains("voron_grad")&&l.Text.Contains("international_fleetstar_f2070a")),"truck IDs retained in preview");
   var noActivate=(bool)preview.GetType().GetProperty("ShowWithoutActivation",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(preview)!;Check(noActivate&&!preview.ShowInTaskbar,"preview cannot steal activation");
   var area=Screen.PrimaryScreen!.WorkingArea;Call(preview,"PlaceNear",new Point(area.Right-1,area.Bottom-1));Check(area.Contains(preview.Bounds),"preview stays inside screen bounds");
   var placeholder=(Image)T("TruckThumbnailProvider").GetProperty("PlaceholderImage")!.GetValue(null)!;
   var pictures=All(preview).OfType<PictureBox>().ToArray();var limit=DateTime.UtcNow.AddSeconds(5);
   while(pictures.Any(p=>ReferenceEquals(p.Image,placeholder))&&DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(10);}
   Check(pictures.Length==2&&pictures.All(p=>p.Image is not null&&!ReferenceEquals(p.Image,placeholder)),"actual truck thumbnails loaded");
   using var canvas=new Panel{Size=preview.ClientSize,BackColor=preview.BackColor};
   foreach(Control child in preview.Controls.Cast<Control>().ToArray())canvas.Controls.Add(child);canvas.CreateControl();canvas.PerformLayout();
   using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,canvas.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"truck-hover-preview.png"));
  }
  var root=Path.Combine(AppContext.BaseDirectory,"fixture");Directory.CreateDirectory(root);
  var availability=Activator.CreateInstance(T("RegionTruckAvailabilityStore"),new object[]{Path.Combine(root,"availability.json")})!;
  using(var tab=(Control)Activator.CreateInstance(T("RegionsTrucksTab"),new object?[]{new Func<string>(()=>root),new Func<string>(()=>""),availability,null})!)
  {
   var other=Entry("gmc_9500","GMC MH9500","gmc_9500","GMC MH9500");
   var entries=Array.CreateInstance(T("RegionTruckEntry"),2);entries.SetValue(entry,0);entries.SetValue(other,1);
   var catalog=Activator.CreateInstance(T("RegionTruckCatalog"),new object?[]{entries,Array.Empty<string>(),1,null})!;
   Set(tab,"_catalog",catalog);((HashSet<string>)Field(tab,"_selectedRegions")).Add("us_01");tab.BindingContext=new BindingContext();tab.Size=new Size(1100,850);tab.CreateControl();Call(tab,"ApplyFilter");
   var grid=(DataGridView)Field(tab,"_trucks");grid.Sort(grid.Columns["Truck"]!,System.ComponentModel.ListSortDirection.Descending);
   foreach(DataGridViewRow row in grid.Rows)
   {
    var bound=((DataRowView)row.DataBoundItem!)["Entry"];Call(tab,"ScheduleTruckHover",row.Index,grid.Columns["Truck"]!.Index);Check(ReferenceEquals(Field(tab,"_hoverEntry"),bound),"hover uses sorted bound row");
    var popup=(Form)Activator.CreateInstance(T("TruckCellHoverPreview"),new[]{bound})!;Set(tab,"_truckHoverPreview",popup);
    typeof(DataGridView).GetMethod("OnScroll",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(ScrollEventArgs)},null)!.Invoke(grid,new object[]{new ScrollEventArgs(ScrollEventType.SmallIncrement,0,1,ScrollOrientation.VerticalScroll)});
    Check(popup.IsDisposed&&Field(tab,"_truckHoverPreview") is null,"scroll dismisses and disposes preview");
   }
   object Tip(string column)
   {
    var args=Activator.CreateInstance(typeof(DataGridViewCellToolTipTextNeededEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,new object[]{grid.Columns[column]!.Index,0,""},null)!;
    typeof(DataGridView).GetMethod("OnCellToolTipTextNeeded",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(DataGridViewCellToolTipTextNeededEventArgs)},null)!.Invoke(grid,new[]{args});return args;
   }
   Check(((DataGridViewCellToolTipTextNeededEventArgs)Tip("Truck")).ToolTipText=="","native truck tooltip suppressed to avoid overlapping cards");
   Check(((DataGridViewCellToolTipTextNeededEventArgs)Tip("Tires")).ToolTipText=="Native tire tooltip","equipment tooltips preserved");
   Call(tab,"ScheduleTruckHover",0,grid.Columns["Tires"]!.Index);Check(Field(tab,"_hoverEntry") is null,"no truck preview for non-truck column");
  }
  Console.WriteLine("PASS: default-hidden details, chronological backfill/toggle without duplicates; real truck tiles and IDs, screen clamping/no activation, sorted-row hover identity, scroll cleanup and equipment tooltip preservation.");
 }
}
