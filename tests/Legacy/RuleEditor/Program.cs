using SnowPakTool.Desktop;
using System.Text.Json;
internal static class Program
{
 [STAThread] static void Main()
 {
  var path = Path.Combine(AppContext.BaseDirectory, "rules-test.json");
  if (File.Exists(path)) File.Delete(path);
  var store = new TruckRandomizationRuleStore(path);
  var state = store.Load(); Check(state.Obtainable.Count == 6 && state.Missions.Count == 5, "defaults");
  state.Obtainable[0] = "Edited rule\nwith multiple lines"; state.Obtainable.RemoveAt(1); state.Missions.Clear(); state.Missions.Add("Mission rule"); store.Save(state);
  var loaded = store.Load(); Check(loaded.Obtainable.Count == 5 && loaded.Obtainable[0] == state.Obtainable[0] && loaded.Missions.Single() == "Mission rule", "edit/remove/add roundtrip");
  loaded.Obtainable.Clear(); loaded.Missions.Clear(); store.Save(loaded); loaded = store.Load();
  Check(loaded.Obtainable.Count == 0 && loaded.Missions.Count == 0, "empty lists stay empty");
  var saved = File.ReadAllText(path); loaded.Missions.Add(" ");
  try { store.Save(loaded); throw new Exception("accepted blank rule"); } catch (JsonException) { }
  Check(File.ReadAllText(path) == saved, "failed validation preserves saved state");
  File.WriteAllText(path, "{\"Obtainable\":null,\"Missions\":[]}");
  try { store.Load(); throw new Exception("accepted null list"); } catch (JsonException) { }
  File.Delete(path);
  using(var panel=new TruckRandomizationTab(_=>Task.CompletedTask){Size=new System.Drawing.Size(1080,850)})
  {
   panel.CreateControl();panel.PerformLayout();
   var messages=(TextBox)typeof(TruckRandomizationTab).GetField("_messages",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
   messages.Text="Michigan: complete — 6 randomized, 2 kept, 0 unchanged.\r\nAlaska: complete — 3 randomized, 0 kept, 0 unchanged.";
   using var preview=new System.Drawing.Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(preview,new System.Drawing.Rectangle(System.Drawing.Point.Empty,panel.Size));preview.Save(Path.Combine(AppContext.BaseDirectory,"randomization-panel-preview.png"));
  }
  Console.WriteLine("PASS: defaults, add/edit/remove persistence, multiline text, empty lists, validation, and failed-save preservation.");
 }
 static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
}
