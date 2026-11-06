using System.Reflection;
using System.Collections;
internal static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object Field(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(o,args);
 static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var d in All(child))yield return d;}
 static void Check(bool b,string n){if(!b)throw new Exception(n);}
 [STAThread]static void Main()
 {
  Application.SetHighDpiMode(HighDpiMode.SystemAware);
  using var form=(Form)Activator.CreateInstance(T("MainForm"))!;
  ((TextBox)Field(form,"_workspacePath")).Text="";
  var host=form.Controls.OfType<Panel>().Single(p=>p.Controls.OfType<TabControl>().Any(t=>t.Name=="MainNavigation"));
  using var canvas=new Panel{Size=new Size(1100,820)};canvas.Controls.Add(host);canvas.CreateControl();host.CreateControl();
  var main=All(host).OfType<TabControl>().Single(t=>t.Name=="MainNavigation");
  Console.WriteLine("Actual tabs: "+string.Join(" | ",main.TabPages.Cast<TabPage>().Select(p=>p.Text)));
  Check(main.TabPages.Cast<TabPage>().Select(p=>p.Text).SequenceEqual(new[]{"Start","Planning","Randomizer","Editors"}),"four main tabs in agreed order");
  var expected=new Dictionary<string,string[]>{{"Planning",new[]{"Owned content","Truck tiers","Region difficulty","Truck groups"}},{"Randomizer",new[]{"Region progression","Truck randomization","Map trucks"}},{"Editors",new[]{"Truck properties","Engines","Winches","Addons","Experience / Ranks","Cargo"}}};
  foreach(var (name,titles) in expected)
  {
   var page=main.TabPages[name]!;var child=page.Controls.OfType<TabControl>().Single();
   Check(child.TabPages.Cast<TabPage>().Select(p=>p.Text).SequenceEqual(titles),name+" child pages");
   main.SelectedTab=page;child.SelectedIndex=child.TabCount-1;main.SelectedTab=main.TabPages["Start"];main.SelectedTab=page;
   Check(child.SelectedIndex==child.TabCount-1,"sub-tab retained: "+name);
  }
  foreach(var t in All(host).OfType<TabControl>())
  {
   var depth=0;for(var p=t.Parent;p is not null;p=p.Parent)if(p is TabControl)depth++;
   Check(depth<=1,"at most two tab rows");Check(t.DrawMode==TabDrawMode.OwnerDrawFixed,"active tab style on both levels");
  }
  var controller=Field(form,"_truckEditTab");
  Check(((TabControl)Field(controller,"_subTabs")).TabCount==0,"editor pages detached from third level");
  var editors=main.TabPages["Editors"]!.Controls.OfType<TabControl>().Single();
  Check(editors.TabPages["Engines"]!.Controls.Contains((Control)Field(controller,"_engineEditTab")),"original engine editor retained directly");
  var gear=All(host).OfType<Button>().Single(b=>b.Name=="SettingsButton");
  Check(gear.AccessibleName=="Settings"&&gear.Right<=host.Width,"settings gear visible at right");
  using(var settings=(Form)Call(form,"CreateSettingsWindow")!)
  {
   Check(!All(settings).OfType<TabControl>().Any(),"settings has no tab nesting");
   Check(All(settings).Any(c=>c.GetType().Name=="SettingsTab"),"settings window contains color controls");
  }
  using(var settings=(Form)Call(form,"CreateSettingsWindow")!)Check(!settings.IsDisposed,"settings can be opened again after closing");
  // Use actual moved pages for search; inactive editor pages must remain outside the search scope.
  var addons=editors.TabPages["Addons"]!;addons.Controls.Add(new Label{Text="active_editor_marker",Dock=DockStyle.Top});
  editors.TabPages["Engines"]!.Controls.Add(new Label{Text="hidden_engine_marker",Dock=DockStyle.Top});
  main.SelectedTab=main.TabPages["Editors"];editors.SelectedTab=addons;canvas.PerformLayout();Application.DoEvents();
  using(var search=(IDisposable)Activator.CreateInstance(T("CurrentTabSearch"),new object[]{new Func<Control?>(()=>main.SelectedTab)})!)
  {
   Check((int)Call(search,"Search","active_editor_marker")!>0,"search finds active flattened editor");
   Check((int)Call(search,"Search","hidden_engine_marker")==0,"search excludes inactive flattened editor");
  }
  foreach(var marker in addons.Controls.OfType<Label>().Where(c=>c.Text=="active_editor_marker").ToArray())marker.Dispose();
  // Check the actual active/inactive tab rendering at both sizes.
  foreach(var width in new[]{1100,760})
  {
   canvas.Width=width;canvas.PerformLayout();host.PerformLayout();Application.DoEvents();
   Check(main.GetTabRect(main.TabCount-1).Right<gear.Left,"main labels do not overlap gear at "+width);
   Check(editors.GetTabRect(editors.TabCount-1).Right<editors.Width,"editor labels fit one row at "+width);
   using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,canvas.Size));
   Check(gear.Visible&&host.Controls.GetChildIndex(gear)==0,"settings gear is visible and in front of native tabs");
   // WinForms DrawToBitmap renders overlapping sibling HWNDs in reverse z-order; compose the topmost gear last.
   using(var icon=new Bitmap(gear.Width,gear.Height)){gear.DrawToBitmap(icon,new Rectangle(Point.Empty,gear.Size));using var g=Graphics.FromImage(bitmap);g.DrawImageUnscaled(icon,gear.Location);}
   bitmap.Save(Path.Combine(AppContext.BaseDirectory,"navigation-"+width+".png"));
   foreach(var tabs in new[]{main,editors})
   {
    using var strip=new Bitmap(tabs.Width,50);using var g=Graphics.FromImage(strip);
    var index=tabs.SelectedIndex;var bounds=tabs.GetTabRect(index);
    var args=new DrawItemEventArgs(g,tabs.Font,bounds,index,DrawItemState.Selected);
    typeof(TabControl).GetMethod("OnDrawItem",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(tabs,new object[]{args});
    Check(strip.GetPixel(bounds.Left+5,bounds.Top+5).ToArgb()==SystemColors.Highlight.ToArgb(),"selected tab has solid system highlight");
   }
  }
  var emptyWorkspace=Path.Combine(AppContext.BaseDirectory,"empty-workspace");Directory.CreateDirectory(emptyWorkspace);
  ((TextBox)Field(form,"_workspacePath")).Text=emptyWorkspace;
  Call(controller,"EnsureEditorLoaded",editors.TabPages["Engines"]);
  Check((string)Field(Field(controller,"_engineEditTab"),"_loadedWorkspace")==emptyWorkspace,"direct Engines page loads original engine controller");
  Call(controller,"EnsureEditorLoaded",editors.TabPages["Winches"]);
  Check((string)Field(controller,"_loadedWorkspace")==emptyWorkspace,"direct Winches page loads original winch controller");
  Console.WriteLine("PASS navigation: four main groups, correct flattened pages, two-level maximum, remembered group selection, Settings lifecycle, active-page search, strong selection rendering and header fit at 760/1100px.");
 }
}
