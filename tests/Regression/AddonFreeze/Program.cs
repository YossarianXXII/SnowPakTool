using System.Reflection;
using System.Diagnostics;
static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object F(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static IEnumerable<Control> All(Control c){yield return c;foreach(Control x in c.Controls)foreach(var y in All(x))yield return y;}
 [STAThread]static void Main(string[] args)
 {
  if(!args.Contains("--test"))
  {
   Application.EnableVisualStyles();Application.SetHighDpiMode(HighDpiMode.SystemAware);
   var trace=Path.Combine(AppContext.BaseDirectory,"native-scroll.log");File.WriteAllText(trace,"begin\n");
   using var window=(Form)Activator.CreateInstance(T("MainForm"))!;window.Text="Addon scrollbar diagnostic";
   ((TextBox)F(window,"_workspacePath")).Text=@"S:\snowrunner_Shenanigans\AI\workspace";
   var addon=F(F(window,"_truckEditTab"),"_addonsTab");var g=(DataGridView)F(addon,"_addons");
   var watchers=new List<NativeTrace>();
   void Attach(Control c,string label){if(c.IsHandleCreated)watchers.Add(new(c.Handle,trace,label));else c.HandleCreated+=(_,_)=>watchers.Add(new(c.Handle,trace,label));}
   Attach(g,"grid");foreach(Control c in g.Controls)Attach(c,c.GetType().Name);
   g.Scroll+=(_,e)=>File.AppendAllText(trace,$"{DateTime.Now:O} EVENT {e.ScrollOrientation} {e.Type} {e.NewValue}\n");
   g.CurrentCellChanged+=(_,_)=>File.AppendAllText(trace,"CELL\n");
   window.Shown+=(_,_)=>
   {
    var nav=All(window).OfType<TabControl>().Single(t=>t.Name=="MainNavigation");var e=nav.TabPages["Editors"]!.Controls.OfType<TabControl>().Single();
    e.SelectedTab=e.TabPages["Addons"];nav.SelectedTab=nav.TabPages["Editors"];
   };
   Application.Run(window);return;
  }
  var clock=Stopwatch.StartNew();void Log(string s)=>Console.WriteLine($"{clock.Elapsed.TotalSeconds:F2}s {s}");
  Application.SetHighDpiMode(HighDpiMode.SystemAware);
  using var form=(Form)Activator.CreateInstance(T("MainForm"))!;
  ((TextBox)F(form,"_workspacePath")).Text="";
  var host=form.Controls.OfType<Panel>().Single(p=>p.Controls.OfType<TabControl>().Any(t=>t.Name=="MainNavigation"));
  using var canvas=new Panel{Size=new Size(1100,850)};canvas.Controls.Add(host);canvas.CreateControl();host.CreateControl();
  var main=All(host).OfType<TabControl>().Single(t=>t.Name=="MainNavigation");
  var editors=main.TabPages["Editors"]!.Controls.OfType<TabControl>().Single();
  var addons=F(F(form,"_truckEditTab"),"_addonsTab");var grid=(DataGridView)F(addons,"_addons");
  grid.DataBindingComplete+=(_,_)=>Log("bound "+grid.Rows.Count+" rows");
  using var timer=new System.Windows.Forms.Timer{Interval=100};var ticks=0;timer.Tick+=(_,_)=>ticks++;timer.Start();
  Log("setting workspace");((TextBox)F(form,"_workspacePath")).Text=@"S:\snowrunner_Shenanigans\AI\workspace";
  Log("selecting Addons");editors.SelectedTab=editors.TabPages["Addons"];main.SelectedTab=main.TabPages["Editors"];
  Log("selected");
  var deadline=DateTime.UtcNow.AddSeconds(50);
  while(DateTime.UtcNow<deadline&&(grid.Rows.Count==0||(bool)F(addons,"_loading"))){Application.DoEvents();Thread.Sleep(5);}
  Log("completed: "+((Label)F(addons,"_status")).Text+"; ticks="+ticks);
  if(grid.Rows.Count<5000)throw new Exception("Catalog not loaded");
  var start=clock.Elapsed.TotalSeconds;Application.DoEvents();Log("post-load DoEvents "+(clock.Elapsed.TotalSeconds-start));
  grid.CurrentCellChanged+=(_,_)=>Log("current cell changed");grid.Scroll+=(_,e)=>Log("scroll "+e.ScrollOrientation+" "+e.NewValue);
  for(var c=1;c<grid.Columns.Count-1;c++)
  {
   Log("horizontal column "+c);grid.FirstDisplayedScrollingColumnIndex=c;Application.DoEvents();
   using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,canvas.Size));
   Log("painted "+c);
  }
  var bar=grid.Controls.OfType<HScrollBar>().Single();
  var scroll=typeof(ScrollBar).GetMethod("OnScroll",BindingFlags.NonPublic|BindingFlags.Instance)!;
  Log("native thumb events");
  for(var offset=0;offset<bar.Maximum-bar.LargeChange;offset+=20)
   scroll.Invoke(bar,new object[]{new ScrollEventArgs(ScrollEventType.ThumbTrack,offset,ScrollOrientation.HorizontalScroll)});
  Log("native thumb events finished");
  var names=Stopwatch.StartNew();var count=0;
  foreach(DataGridViewRow row in grid.Rows)
  {
   if(row.AccessibilityObject.Name!=$"Row {row.Index+1}")throw new Exception("Accessible row identity");
   var next=row.AccessibilityObject.Navigate(AccessibleNavigation.Next);
   if(row.Index+1<grid.Rows.Count && next?.Name!=$"Row {row.Index+2}")throw new Exception("Accessible sibling navigation");
   foreach(DataGridViewCell cell in row.Cells)
   {
    if(!cell.AccessibilityObject.Name!.Contains($"Row {row.Index+1}"))throw new Exception("Accessible cell identity");
    count++;
   }
  }
  Log($"PASS {count} cell and {grid.Rows.Count} row accessibility names in {names.Elapsed.TotalSeconds:F2}s");
  if(names.Elapsed>TimeSpan.FromSeconds(5))throw new Exception("Accessibility name enumeration too slow");
  var headers=T("TableHeaderBehavior").GetMethod("Attach")!.Invoke(null,new object?[]{grid,null,null,null})!;
  var filter=headers.GetType().GetMethod("AddFilter")!.Invoke(headers,new object?[]{"Type",null,Array.Empty<string>()})!;
  filter.GetType().GetMethod("SelectValues")!.Invoke(filter,new object[]{new[]{"Engine"}});Application.DoEvents();
  if(grid.Rows.Count==0||grid.Rows.Count>=6092||grid.Rows[0].AccessibilityObject.Name!="Row 1")throw new Exception("Filtered row numbering");
  headers.GetType().GetMethod("SetSort")!.Invoke(headers,new object[]{"ID",SortOrder.Descending});Application.DoEvents();
  if(!grid.Rows[0].Cells["ID"].AccessibilityObject.Name!.Contains("Sorted descending"))throw new Exception("Accessible sort state");
  if(!grid.Rows[0].Cells["ID"].AccessibilityObject.Value!.Equals(Convert.ToString(grid.Rows[0].Cells["ID"].Value)))throw new Exception("Accessible value lost");
  grid.Rows[0].Cells[0].AccessibilityObject.Select(AccessibleSelection.TakeSelection|AccessibleSelection.TakeFocus);
  if(grid.CurrentCell!=grid.Rows[0].Cells[0])throw new Exception("Accessible selection lost");
  Log("PASS filtered/sorted accessible row identity, values and sort state");
 }
 sealed class NativeTrace:NativeWindow
 {
  readonly string path,label;
  public NativeTrace(IntPtr handle,string p,string l){path=p;label=l;AssignHandle(handle);}
  protected override void WndProc(ref Message m)
  {
   var log=m.Msg is 0x114 or 0x2114 or 0x201 or 0x200 or 0x202 or 0xA1 or 0xA0 or 0xA2;
   if(log)File.AppendAllText(path,$"{DateTime.Now:O} {label} enter {m.Msg:X} {m.WParam}\n");
   base.WndProc(ref m);
   if(log)File.AppendAllText(path,$"{DateTime.Now:O} {label} exit {m.Msg:X}\n");
  }
 }
}
