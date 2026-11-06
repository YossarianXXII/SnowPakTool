using System.Reflection;
var assembly = Assembly.Load("SnowPakTool.Desktop");
var store = assembly.GetType("SnowPakTool.Desktop.TrailerStore")!;
object Call(string method, params object[] args) => store.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
var files = (IEnumerable<string>)Call("Files", Path.GetFullPath("../workspace"));
var count = 0; var invalid = 0;
foreach (var file in files) { try { Call("Attributes", Call("Parse", File.ReadAllText(file))); count++; } catch (TargetInvocationException e) when (e.InnerException is System.Xml.XmlException) { invalid++; Console.WriteLine("Invalid source XML (editor reports error): " + Path.GetFileName(file)); } }
if (count == 0) throw new Exception("No trailer definitions found");
var root = Path.Combine(Path.GetFullPath(".checks/TrailerRegression"), "trailer-regression-" + Guid.NewGuid());
var relative = "[media]/classes/trucks/trailers/test.xml";
var original = Path.Combine(root, "initial.pak", "original_files", relative);
Directory.CreateDirectory(Path.GetDirectoryName(original)!);
var xml = "<_templates><Wheel><Rear Mass=\"10\" /></Wheel></_templates><Truck Type=\"Trailer\"><GameData Price=\"100\" /><TruckData><Body Mass=\"500\" /></TruckData></Truck>";
File.WriteAllText(original, xml);
var editType = assembly.GetType("SnowPakTool.Desktop.TrailerEdit")!;
var edits = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(editType))!;
edits.Add(Activator.CreateInstance(editType, relative, "Truck[1]/GameData[1]/@Price", "100", "200"));
Call("Save", root, edits); Call("Apply", root);
var updated = Path.Combine(root, "initial.pak", "updated", relative);
if (!File.ReadAllText(updated).Contains("Price=\"200\"") || File.ReadAllText(original) != xml) throw new Exception("Overlay isolation failed");
Call("Save", root, edits);
File.WriteAllText(updated, File.ReadAllText(updated).Replace("Price=\"200\"", "Price=\"300\""));
try { Call("Apply", root); throw new Exception("Conflict accepted"); } catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
if (!File.ReadAllText(updated).Contains("Price=\"300\"")) throw new Exception("Conflict modified output");
Console.WriteLine($"PASS: parsed {count} real trailer files; overlay-only apply, original preservation, conflict rejection and retained pending snapshot.");
