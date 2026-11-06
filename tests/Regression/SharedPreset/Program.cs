using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Regression
{
 static Assembly A=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string type)=>A.GetType("SnowPakTool.Desktop."+type)!;
 static object? Call(string type,string method,params object?[] args)=>T(type).GetMethod(method,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,args);
 static void Check(bool test,string message){if(!test)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static void Reject(Action action,string message){try{action();}catch(TargetInvocationException e)when(e.InnerException is InvalidDataException or IOException or UnauthorizedAccessException){Check(true,message);return;}throw new Exception("Accepted "+message);}
 static void Put(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
 static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
 static void Copy(string from,string to){foreach(var f in Directory.GetFiles(from,"*",SearchOption.AllDirectories)){var target=Path.Combine(to,Path.GetRelativePath(from,f));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(f,target);}}
 [STAThread] static void Main()
 {
  ApplicationConfiguration.Initialize();
  var scratch=Path.Combine(AppContext.BaseDirectory,"fixtures-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
  string W(string name)=>Path.Combine(scratch,name);
  string Original(string ws,string path)=>Path.Combine(ws,"initial.pak","original_files",path);
  string Updated(string ws,string path)=>Path.Combine(ws,"initial.pak","updated",path);
  var sender=W("sender");var prefs=W("sender-prefs");
  var xml="<Truck><TruckData TruckType=\"SCOUT\" FuelCapacity=\"20\"/><GameData Price=\"100\" UnlockByRank=\"1\"/></Truck>";
  var relative="[media]/classes/trucks/test.xml";
  Put(Original(sender,relative),xml);Put(Original(sender,"[ps]/rank_settings.sso"),"rankExp = [0, 100, 200]");Put(Original(sender,"pak.load_list"),"test");
  var random=new byte[1024*1024];new Random(128).NextBytes(random);random[10]=0;
  Put(Original(sender,"[media]/unchanged.txt"),Convert.ToBase64String(random));
  Put(Updated(sender,relative),xml.Replace("20","30"));
  Put(Path.Combine(sender,"truck-properties/pending-changes.json"),"[{\"RelativeFile\":\""+relative+"\",\"TruckName\":\"Test\",\"Attribute\":\"FuelCapacity\",\"Before\":30,\"After\":40}]");
  Put(Path.Combine(sender,"progression-settings.json"),"{\"XpReductionSteps\":80}");
  Put(Path.Combine(prefs,"truck-tiers.json"),"[{\"Name\":\"S\",\"TruckCodes\":[\"test\"]}]");
  Put(Path.Combine(prefs,"region-progression.json"),"{\"Order\":[\"us_01\"],\"Difficulty\":1,\"IsFinalized\":true}");
  var scene=new byte[1024*1024];new Random(32).NextBytes(scene);BinaryPrimitives.WriteUInt32LittleEndian(scene,20190655);BinaryPrimitives.WriteInt32LittleEndian(scene.AsSpan(4),scene.Length);BinaryPrimitives.WriteInt32LittleEndian(scene.AsSpan(8),scene.Length);
  var baselineMap=Path.Combine(sender,"region-maps/backups/level_us_01_01/original.pak");Directory.CreateDirectory(Path.GetDirectoryName(baselineMap)!);
  using(var archive=ZipFile.Open(baselineMap,ZipArchiveMode.Create))
  {foreach(var name in new[]{"[scene]/data_for_ssl.bin","unrelated.txt"}){var e=archive.CreateEntry(name,CompressionLevel.NoCompression);e.LastWriteTime=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);using var stream=e.Open();stream.Write(name.StartsWith("[scene]")?scene:Encoding.UTF8.GetBytes("preserve me"));}}
  var mapOutput=Path.Combine(sender,"region-maps/updated/level_us_01_01.pak");Directory.CreateDirectory(Path.GetDirectoryName(mapOutput)!);
  var changed=(byte[])scene.Clone();Encoding.UTF8.GetBytes("new_truck").CopyTo(changed,256);Encoding.UTF8.GetBytes("other_edit").CopyTo(changed,900000);Call("MapArchiveWriter","Write",baselineMap,mapOutput,changed);
  var originalMapHash=Hash(baselineMap);var senderUpdatedHash=Hash(Updated(sender,relative));var stageBefore=File.ReadAllText(Path.Combine(sender,"truck-properties/pending-changes.json"));
  var preset=W("share.snowpreset");
  Call("SharedPreset","Export",sender,preset,(Action<string>)(temp=>Call("TruckPropertiesStore","Apply",temp)),prefs);
  Check(Hash(Updated(sender,relative))==senderUpdatedHash&&File.ReadAllText(Path.Combine(sender,"truck-properties/pending-changes.json"))==stageBefore,"export leaves sender applied files and staged intent unchanged");
  Check(new FileInfo(preset).Length<12000,"compact delta excludes two megabytes of unchanged game data ("+new FileInfo(preset).Length+" bytes)");
  var doc=Call("SharedPreset","Read",preset)!;var json=JsonSerializer.SerializeToNode(doc,doc.GetType())!;
  Check(!json.ToJsonString().Contains(sender.Replace("\\","\\\\")),"portable preset has no sender workspace paths");
  var receiver=W("different-person/other-workspace");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(receiver,"initial.pak/original_files"));
  var maps=W("installed-maps");Directory.CreateDirectory(maps);File.Copy(baselineMap,Path.Combine(maps,"level_us_01_01.pak"));
  var receiverPrefs=W("receiver-prefs");Put(Path.Combine(receiverPrefs,"truck-tiers.json"),"[]");
  Call("SharedPreset","Stage",receiver,preset,maps);
  Check(!Directory.Exists(Path.Combine(receiver,"initial.pak/updated"))&&File.ReadAllText(Path.Combine(receiverPrefs,"truck-tiers.json"))=="[]","load writes only staged preset; game outputs and planning untouched");
  Check(((IEnumerable)Call("SharedPreset","Pending",receiver)!).Cast<object>().Count()==3,"staged files and planning visible after rereading saved stage");
  Call("SharedPreset","Apply",receiver,maps,receiverPrefs);
  Check(File.ReadAllText(Updated(receiver,relative))==xml.Replace("20","40"),"apply combines sender applied edits and staged edits");
  Check(Hash(Path.Combine(receiver,"region-maps/updated/level_us_01_01.pak"))==Hash(mapOutput),"receiver map PAK is byte-identical to sender");
  Check(Hash(baselineMap)==originalMapHash&&Hash(Path.Combine(maps,"level_us_01_01.pak"))==originalMapHash,"sender and recipient input archives preserved");
  Check(File.ReadAllText(Path.Combine(receiverPrefs,"truck-tiers.json")).Contains("test")&&!File.Exists(Path.Combine(receiver,"shared-preset.staged")),"planning restored and staged snapshot cleared after Apply");
  var other=W("second-person");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(other,"initial.pak/original_files"));Call("SharedPreset","Stage",other,preset,maps);Call("SharedPreset","Apply",other,maps,W("other-prefs"));
  CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("tr-TR");var packed1=W("one.pak");Call("MainForm","PackWorkspace",receiver,packed1);
  File.SetLastWriteTime(Updated(other,relative),new DateTime(2001,2,3));CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("sk-SK");var packed2=W("two.pak");Call("MainForm","PackWorkspace",other,packed2);
  Check(Hash(packed1)==Hash(packed2),"packed initial PAK byte equality across paths, timestamps and cultures");
  var conflict=W("conflict");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(conflict,"initial.pak/original_files"));Put(Updated(conflict,relative),xml.Replace("20","99"));Reject(()=>Call("SharedPreset","Stage",conflict,preset,maps),"conflicting local edits rejected before staging");
  var stale=W("stale");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(stale,"initial.pak/original_files"));Put(Original(stale,relative),xml+" ");Reject(()=>Call("SharedPreset","Stage",stale,preset,maps),"different initial baseline rejected");
  var badMap=W("bad-map");Directory.CreateDirectory(badMap);File.Copy(mapOutput,Path.Combine(badMap,"level_us_01_01.pak"));var clean=W("clean");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(clean,"initial.pak/original_files"));Reject(()=>Call("SharedPreset","Stage",clean,preset,badMap),"different map archive rejected");
  Call("SharedPreset","Stage",clean,preset,maps);Put(Original(clean,relative),xml+" ");Reject(()=>Call("SharedPreset","Apply",clean,maps,W("clean-prefs")),"Apply revalidates changed originals and retains staged snapshot");Check((bool)Call("SharedPreset","HasPending",clean)!,"failed Apply preserves stage");
  void Malformed(string label,Action<JsonNode> modify){var copy=json.DeepClone();modify(copy);var file=W(label+".snowpreset");using(var output=File.Create(file)){output.Write("SNOWPRESET1\n"u8);using var zip=new BrotliStream(output,CompressionLevel.Optimal);JsonSerializer.Serialize(zip,copy);}Reject(()=>Call("SharedPreset","Stage",other,file,maps),label);}
  Malformed("path-traversal",n=>n["Files"]![0]!["Path"]="../outside.txt");
  Malformed("duplicate-target",n=>((JsonArray)n["Files"]!).Add(n["Files"]![0]!.DeepClone()));
  Malformed("corrupt-output",n=>n["Files"]![0]!["After"]=new string('A',64));
  Malformed("unsupported-version",n=>n["Version"]=9);
  Malformed("machine-path-plan",n=>n["Metadata"]!["region-maps/pending-replacements.json"]="{\"Pending\":[{}]}");
  Call("SharedPreset","Stage",other,preset,maps);Call("SharedPreset","Discard",other);Check(!(bool)Call("SharedPreset","HasPending",other)!,"discard removes staging without changing applied outputs");
  var renderTarget=W("preview-output");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(renderTarget,"initial.pak/original_files"));Copy(Path.Combine(sender,"initial.pak/updated"),Path.Combine(renderTarget,"initial.pak/updated"));
  void Set(object o,string field,object? value)=>o.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(o,value);
  object? Invoke(object o,string method,params object?[] values)=>o.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(o,values);
  using(var properties=(Control)Activator.CreateInstance(T("TruckPropertiesTab"),new object?[]{(Func<string>)(()=>sender),null})!)
  {
   var rows=Call("TruckPropertiesStore","LoadRows",sender)!;var row=((IList)rows)[0]!;
   ((Dictionary<string,decimal>)row.GetType().GetProperty("Draft")!.GetValue(row)!)["FuelCapacity"]=55;
   Set(properties,"_rows",rows);Set(properties,"_loadedWorkspace",sender);Invoke(properties,"ExportPreview",renderTarget);Call("TruckPropertiesStore","Apply",renderTarget);
   Check(File.ReadAllText(Updated(renderTarget,relative)).Contains("FuelCapacity=\"55\"")&&Hash(Updated(sender,relative))==senderUpdatedHash,"truck preview exports without staging or applying sender edits");
  }
  var xpStore=Activator.CreateInstance(T("RankXpStageStore"),new object?[]{W("xp-stage")})!;
  Invoke(xpStore,"Stage",sender,"rankExp = [0, 100, 200]",new[]{0,250,500});
  var owned=Activator.CreateInstance(T("OwnedRegionState"))!;
  using(var xp=(Control)Activator.CreateInstance(T("ExperienceEditorTab"),new object?[]{(Func<string>)(()=>sender),owned})!)
  {Set(xp,"_rankStageStore",xpStore);Invoke(xp,"ExportPreview",renderTarget);Check(File.ReadAllText(Updated(renderTarget,"[ps]/rank_settings.sso")).Contains("250")&&Invoke(xpStore,"Load",sender) is not null,"rank XP snapshot rendered without clearing sender stage");}
  var rollback=W("rollback");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(rollback,"initial.pak/original_files"));var rollbackPrefs=W("rollback-prefs");Put(Path.Combine(rollbackPrefs,"truck-tiers.json"),"[]");
  Call("SharedPreset","Stage",rollback,preset,maps);
  using(var locked=new FileStream(Path.Combine(rollbackPrefs,"truck-tiers.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
   Reject(()=>Call("SharedPreset","Apply",rollback,maps,rollbackPrefs),"locked settings cause Apply rollback");
  Check(!File.Exists(Updated(rollback,relative))&&!File.Exists(Path.Combine(rollback,"region-maps/updated/level_us_01_01.pak"))&&(bool)Call("SharedPreset","HasPending",rollback)!,"rollback restores earlier outputs and keeps staged snapshot");
  var cropped=json.DeepClone();cropped["Files"]!.AsArray().Single(n=>(string?)n!["Area"]=="map")!["Path"]="level_us_01_01_crop.pak";
  var croppedPath=W("cropped.snowpreset");using(var output=File.Create(croppedPath)){output.Write("SNOWPRESET1\n"u8);using var zip=new BrotliStream(output,CompressionLevel.Optimal);JsonSerializer.Serialize(zip,cropped);}
  File.Copy(baselineMap,Path.Combine(maps,"level_us_01_01_crop.pak"));var croppedReceiver=W("cropped-receiver");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(croppedReceiver,"initial.pak/original_files"));Call("SharedPreset","Stage",croppedReceiver,croppedPath,maps);Check((bool)Call("SharedPreset","HasPending",croppedReceiver)!,"cropped map names supported");
  Call("SharedPreset","Stage",other,preset,maps);
  var pendingMethod=T("SharedPreset").GetMethod("Pending",BindingFlags.Public|BindingFlags.Static)!;
  var pendingDelegate=Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(string),pendingMethod.ReturnType),pendingMethod);
  using(var panel=(Control)Activator.CreateInstance(T("StartChangesPanel"),new object[]{(Func<string>)(()=>other),new FlowLayoutPanel(),pendingDelegate,(Action)(()=>{}),(Func<Task>)(()=>Task.CompletedTask),(Func<Task>)(()=>Task.CompletedTask)})!)
  using(var form=new Form {Size=new Size(1120,920)})
  {
   panel.Dock=DockStyle.Fill;form.Controls.Add(panel);form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
   var refresh=(Task)Invoke(panel,"RefreshAsync")!;while(!refresh.IsCompleted){Application.DoEvents();Thread.Sleep(1);}refresh.GetAwaiter().GetResult();
   form.PerformLayout();using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"shared-preset-staged.png"));
   var button=(Button)panel.GetType().GetField("_applyButton",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(panel)!;
   Check(button.Enabled,"Start enables Apply for imported snapshot after refresh");
  }
  var draftSender=W("draft-sender");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(draftSender,"initial.pak/original_files"));
  Put(Original(draftSender,"[media]/classes/trucks/old_truck.xml"),"<Truck><TruckData TruckType=\"SCOUT\"/><GameData><UiDesc UiName=\"OLD\"/></GameData></Truck>");
  Put(Original(draftSender,"[media]/classes/trucks/new_truck.xml"),"<Truck><TruckData TruckType=\"SCOUT\"/><GameData><UiDesc UiName=\"NEW\"/></GameData></Truck>");
  byte[] DraftScene(){using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);writer.Write(20190654u);writer.Write(0);writer.Write(0);void Str(string v){writer.Write((ushort)(v.Length+1));writer.Write(Encoding.ASCII.GetBytes(v));writer.Write((byte)0);}Str("old_truck");Str("PICKUP");foreach(var f in new float[]{1,0,0,0,0,1,0,0,0,0,1,0,100,10,200,1})writer.Write(f);writer.Write(true);for(int i=0;i<6;i++)writer.Write(float.MaxValue);writer.Write(0u);for(int i=0;i<7;i++)Str("");writer.Write(new byte[26]);var end=(int)memory.Length;writer.Write("tail"u8);var data=memory.ToArray();BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4),end);BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8),data.Length);return data;}
  var draftMaps=W("draft-input-maps");Directory.CreateDirectory(draftMaps);var draftMap=Path.Combine(draftMaps,"level_us_01_01.pak");using(var zip=ZipFile.Open(draftMap,ZipArchiveMode.Create)){var e=zip.CreateEntry("[scene]/data_for_ssl.bin",CompressionLevel.NoCompression);using var stream=e.Open();stream.Write(DraftScene());}
  var draftHash=Hash(draftMap);var catalog=Call("RegionTruckCatalogService","Load",draftSender,draftMaps,new Dictionary<string,string>(),new Dictionary<string,string>(),null,false)!;
  var entry=((IEnumerable)catalog.GetType().GetProperty("Entries")!.GetValue(catalog)!).Cast<object>().Single();
  var targetTruck=Activator.CreateInstance(T("CustomTruck"),new object[]{"new_truck","New","Scout",""})!;
  Call("PendingTruckReplacements","Queue",draftSender,entry,targetTruck);
  var draftPreset=W("draft.snowpreset");Call("SharedPreset","Export",draftSender,draftPreset,(Action<string>)(_=>{}),prefs);
  Check(Hash(draftMap)==draftHash&&!Directory.Exists(Path.Combine(draftSender,"region-maps/updated"))&&((IEnumerable)Call("PendingTruckReplacements","LoadStaged",draftSender)!).Cast<object>().Count()==0,"export materializes map drafts without staging sender or changing external source map");
  var draftReceiver=W("draft-receiver");Copy(Path.Combine(draftSender,"initial.pak/original_files"),Path.Combine(draftReceiver,"initial.pak/original_files"));Call("SharedPreset","Stage",draftReceiver,draftPreset,draftMaps);Call("SharedPreset","Apply",draftReceiver,draftMaps,W("draft-prefs"));
  var draftOutput=Path.Combine(draftReceiver,"region-maps/updated/level_us_01_01.pak");var draftData=(byte[])Call("RegionTruckCatalogService","ReadMapData",draftOutput)!;
  Check(Encoding.ASCII.GetString(draftData).Contains("new_truck")&&!Encoding.ASCII.GetString(draftData).Contains("old_truck"),"recipient applies concrete map draft from portable snapshot");
  var replaceReceiver=W("replace-receiver");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(replaceReceiver,"initial.pak/original_files"));
  Put(Updated(replaceReceiver,relative),xml.Replace("20","99"));Put(Updated(replaceReceiver,"[media]/local-only.xml"),"local extra");
  var replacePrefs=W("replace-prefs");var pendingFiles=new[]{"truck-properties/pending-changes.json","frame-addon-fuel/pending-changes.json","winches/pending-changes.json","upgrade-randomization/staged.json","upgrade-randomization/draft.json","upgrade-randomization/addon-exploration.json"};
  foreach(var path in pendingFiles)Put(Path.Combine(replaceReceiver,path),"[]");
  var oldMap=Path.Combine(replaceReceiver,"region-maps/updated/level_us_01_02.pak");Directory.CreateDirectory(Path.GetDirectoryName(oldMap)!);File.Copy(mapOutput,oldMap);
  var replaceRank=Activator.CreateInstance(T("RankXpStageStore"),new object?[]{Path.Combine(replacePrefs,"staged-rank-xp")})!;Invoke(replaceRank,"Stage",replaceReceiver,"rankExp = [0, 100, 200]",new[]{0,333,666});
  Call("SharedPreset","StageReplacing",replaceReceiver,preset,maps);
  Check(File.ReadAllText(Updated(replaceReceiver,relative)).Contains("99")&&File.Exists(oldMap)&&pendingFiles.All(p=>File.Exists(Path.Combine(replaceReceiver,p))),"confirmed replacement only stages; old outputs and pending stores remain until Apply");
  Call("SharedPreset","Discard",replaceReceiver);Check(File.Exists(oldMap)&&Invoke(replaceRank,"Load",replaceReceiver) is not null,"discard replacement retains previous saved pending work and outputs");
  Call("SharedPreset","StageReplacing",replaceReceiver,preset,maps);Call("SharedPreset","Apply",replaceReceiver,maps,replacePrefs);
  Check(File.ReadAllText(Updated(replaceReceiver,relative))==xml.Replace("20","40")&&!File.Exists(Updated(replaceReceiver,"[media]/local-only.xml"))&&!File.Exists(oldMap),"Apply replaces conflicts and removes extra local initial/map outputs");
  Check(pendingFiles.All(p=>!File.Exists(Path.Combine(replaceReceiver,p)))&&Invoke(replaceRank,"Load",replaceReceiver) is null,"Apply clears superseded truck, fuel, winch, addon, upgrade and rank XP pending stores");
  Check(Directory.GetFiles(Path.Combine(replaceReceiver,"preset-history"),"*.before",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f)=="local extra"),"replaced local edits preserved in backup history");
  var replacementPacked=W("replacement.pak");Call("MainForm","PackWorkspace",replaceReceiver,replacementPacked);Check(Hash(replacementPacked)==Hash(packed1),"replacement still produces the exact shared initial PAK");
  Reject(()=>Call("SharedPreset","StageReplacing",stale,preset,maps),"replacement cannot bypass original initial baseline checks");
  var mismatchedMaps=W("replacement-bad-map");Copy(Path.Combine(sender,"initial.pak/original_files"),Path.Combine(mismatchedMaps,"initial.pak/original_files"));Reject(()=>Call("SharedPreset","StageReplacing",mismatchedMaps,preset,badMap),"replacement cannot bypass original map checks");
  Call("SharedPreset","StageReplacing",replaceReceiver,preset,maps);Call("SharedPreset","StageReplacing",replaceReceiver,preset,maps);
  Check(Directory.GetFiles(Path.Combine(replaceReceiver,"preset-history"),"shared-preset.staged.before",SearchOption.AllDirectories).Length==1,"replacing an earlier staged preset preserves a backup");
  var portableReplacement=W("replacement-export.snowpreset");Call("SharedPreset","Export",replaceReceiver,portableReplacement,(Action<string>)(_=>{}),replacePrefs);
  var portableDoc=Call("SharedPreset","Read",portableReplacement)!;Check(!(bool)portableDoc.GetType().GetProperty("ReplaceLocalChanges")!.GetValue(portableDoc)!,"local overwrite confirmation is not exported to other users");
  var reviewPrefs=W("review-prefs");Put(Path.Combine(reviewPrefs,"truck-tiers.json"),"[]");Put(Path.Combine(reviewPrefs,"owned-regions.json"),"{\"us_01\":false}");
  Put(Updated(sender,"[media]/local-extra.xml"),"<Extra/>");Put(Updated(sender,"pak.load_list"),"local-order");
  var changesType=typeof(List<>).MakeGenericType(T("WorkspaceChange"));
  var reviewChanges=JsonSerializer.Deserialize(JsonSerializer.Serialize(new[]{new{State="Preview",Target=Updated(sender,relative),Property="Test / FuelCapacity",Before="30",After="55"},new{State="Pending",Target=Updated(sender,relative),Property="Test / FuelCapacity",Before="30",After="40"}}),changesType)!;
  var reviewBefore=Hash(Updated(sender,relative));
  var reviewItems=Call("PresetOverwriteReview","Build",sender,doc,reviewChanges,reviewPrefs)!;var reviewRows=((IEnumerable)reviewItems).Cast<object>().ToArray();
  string Cell(object item,string property)=>(string)item.GetType().GetProperty(property)!.GetValue(item)!;
  Check(reviewRows.Any(r=>Cell(r,"State")=="Preview"&&Cell(r,"LocalValue").Contains("55"))&&reviewRows.Any(r=>Cell(r,"State")=="Staged / editor"&&Cell(r,"LocalValue").Contains("40")),"overwrite review lists unsaved preview and staged values separately");
  Check(reviewRows.Any(r=>Cell(r,"State")=="In files"&&Cell(r,"Change").Contains("FuelCapacity"))&&reviewRows.Any(r=>Cell(r,"Target").Contains("local-extra")&&Cell(r,"PresetResult").Contains("Remove")),"overwrite review lists applied edits and extra files to remove");
  Check(reviewRows.Any(r=>Cell(r,"Target")=="pak.load_list")&&reviewRows.Any(r=>Cell(r,"Area")=="Maps"),"overwrite review covers load lists and applied maps");
  Check(reviewRows.Any(r=>Cell(r,"Target")=="truck-tiers.json")&&reviewRows.Any(r=>Cell(r,"Target")=="owned-regions.json"),"overwrite review lists changed planning and ownership settings");
  Check(Hash(Updated(sender,relative))==reviewBefore&&!File.Exists(Path.Combine(sender,"shared-preset.staged")),"building replacement list neither stages nor changes local work");
  var upgradeDraft=new{Seed=4,CreatedAt=DateTimeOffset.UtcNow,InputStamp="test",Assignments=new[]{new{Slot=new{Source="[ps]/level.sso",Map="level_us_01_01",Region="us_01",Zone="upgrade-zone",RewardIndex=0,OriginalUpgradeId="old_upgrade"},UpgradeId="new_upgrade",Kind=0,Relation=0,Path=0,Fallback=false,Reused=false}},Report=(object?)null,ResetKeys=new[]{"reset-pickup"}};
  Put(Path.Combine(draftSender,"upgrade-randomization/draft.json"),JsonSerializer.Serialize(upgradeDraft));
  var emptyChanges=Activator.CreateInstance(changesType)!;var draftReview=((IEnumerable)Call("PresetOverwriteReview","Build",draftSender,Call("SharedPreset","Read",draftPreset)!,emptyChanges,reviewPrefs)!).Cast<object>().ToArray();
  Check(draftReview.Any(r=>Cell(r,"Area")=="Maps"&&Cell(r,"State")=="Draft"&&Cell(r,"LocalValue").Contains("New")),"overwrite review includes unstaged map replacements");
  Check(draftReview.Any(r=>Cell(r,"Area")=="Upgrades"&&Cell(r,"LocalValue").Contains("new_upgrade"))&&draftReview.Any(r=>Cell(r,"Change")=="Reset upgrade pickup"),"overwrite review includes upgrade drafts and resets");
  using(var overwriteDialog=(Form)Activator.CreateInstance(T("PresetOverwriteReviewDialog"),new object[]{"shared.snowpreset",reviewItems})!)
  {
   overwriteDialog.StartPosition=FormStartPosition.Manual;overwriteDialog.Location=new Point(-20000,-20000);overwriteDialog.Show();Application.DoEvents();
   Check(overwriteDialog.AcceptButton is Button{Text:"Cancel"}&&overwriteDialog.CancelButton is Button{Text:"Cancel"},"review defaults Enter and Escape to Cancel");
   using(var bitmap=new Bitmap(overwriteDialog.Width,overwriteDialog.Height)){overwriteDialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,overwriteDialog.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"preset-overwrite-review.png"));}
   ((Button)overwriteDialog.CancelButton!).PerformClick();Check(overwriteDialog.DialogResult==DialogResult.No&&Hash(Updated(sender,relative))==reviewBefore,"Cancel closes review without discarding or staging work");
  }
  using(var browseForm=new Form {Size=new Size(1120,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
  using(var navigation=new TabControl{Dock=DockStyle.Fill})
  {
   var planning=new TabPage("Planning");var randomizer=new TabPage("Randomizer");var editors=new TabPage("Editors");navigation.TabPages.AddRange(new[]{planning,randomizer,editors});browseForm.Controls.Add(navigation);
   var innerNavigation=new TabControl{Dock=DockStyle.Fill};var truckPage=new TabPage("Truck properties");var addonPage=new TabPage("Addons");innerNavigation.TabPages.AddRange(new[]{truckPage,addonPage});editors.Controls.Add(innerNavigation);
   var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};var mutate=new Button{Text="Stage changes",AutoSize=true};var reload=new Button{Text="Reload properties",AutoSize=true};var filter=new Button{Text="Columns ▾",AutoSize=true};var show=new CheckBox{Text="Show changed only",AutoSize=true};var ownership=new CheckBox{Text="Select all regions",AutoSize=true};var slider=new TrackBar();toolbar.Controls.AddRange(new Control[]{mutate,reload,filter,show,ownership,slider});truckPage.Controls.Add(toolbar);
   var browseGrid=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false};browseGrid.Columns.Add("Truck","Truck");browseGrid.Columns.Add("FuelCapacity","Fuel capacity");browseGrid.Columns.Add(new DataGridViewCheckBoxColumn{Name="SelectTruck",HeaderText="Select"});browseGrid.Rows.Add("Test truck",30,false);truckPage.Controls.Add(browseGrid);toolbar.BringToFront();
   var notes=new TextBox{Text="Planning notes",Multiline=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};planning.Controls.Add(notes);var tiles=new FlowLayoutPanel{AllowDrop=true,Dock=DockStyle.Bottom,Height=100,AutoScroll=true};planning.Controls.Add(tiles);
   using(var mode=(IDisposable)Activator.CreateInstance(T("PresetBrowseMode"),new object[]{new Control[]{planning,randomizer,editors}})!)
   {
    Invoke(mode,"SetReadOnly",true);browseForm.Show();Application.DoEvents();
    foreach(var page in new[]{planning,randomizer,editors}){navigation.SelectedTab=page;Application.DoEvents();Check(navigation.SelectedTab==page&&page.Enabled,"staged preset permits browsing "+page.Text);}
    innerNavigation.SelectedTab=addonPage;Check(innerNavigation.SelectedTab==addonPage,"staged preset permits sub-tab navigation");innerNavigation.SelectedTab=truckPage;
    Check(browseGrid.Enabled&&notes.Enabled&&tiles.Enabled&&notes.ReadOnly&&!tiles.AllowDrop,"browsing stays enabled while notes and drag-and-drop are read-only");
    Check(!mutate.Enabled&&!ownership.Enabled&&!slider.Enabled&&reload.Enabled&&filter.Enabled&&show.Enabled,"mutation controls disabled while reload, columns and view filters remain active");
    Check(browseGrid.Columns["FuelCapacity"].ReadOnly&&!browseGrid.Columns["SelectTruck"].ReadOnly,"property edits blocked while view-selection checkboxes remain usable");
    browseGrid.CurrentCell=browseGrid.Rows[0].Cells["FuelCapacity"];Check(!browseGrid.BeginEdit(false),"keyboard editing cannot start in read-only browsing");
    var mutations=0;mutate.Click+=(_,_)=>mutations++;mutate.PerformClick();Check(mutations==0,"disabled Stage button cannot mutate the staged setup");
    mutate.Enabled=true;Check(!mutate.Enabled,"asynchronous refresh cannot re-enable mutation buttons");
    browseGrid.Columns["FuelCapacity"].ReadOnly=false;Check(browseGrid.Columns["FuelCapacity"].ReadOnly,"table refresh cannot re-enable property cells");
    notes.ReadOnly=false;Check(notes.ReadOnly,"refresh cannot make planning text editable");
    browseGrid.AllowUserToAddRows=true;browseGrid.AllowUserToDeleteRows=true;Check(!browseGrid.AllowUserToAddRows&&!browseGrid.AllowUserToDeleteRows,"read-only browsing also blocks row insertion and deletion");
    var late=new Button{Text="Randomize upgrades"};randomizer.Controls.Add(late);var lateGrid=new DataGridView();randomizer.Controls.Add(lateGrid);lateGrid.Columns.Add("Price","Price");Check(!late.Enabled&&lateGrid.Columns["Price"].ReadOnly,"lazily created controls and columns inherit browsing restrictions");
    var descendant=new Panel();tiles.Controls.Add(descendant);Check((bool)Call("PresetBrowseMode","IsReadOnly",descendant)!,"raw-edit and drag guards recognize descendant controls");
    browseGrid.Sort(browseGrid.Columns["Truck"],System.ComponentModel.ListSortDirection.Ascending);browseGrid.CurrentCell=browseGrid.Rows[0].Cells["Truck"];Check(browseGrid.CurrentCell!=null,"read-only tables retain sorting and row selection");
    using(var bitmap=new Bitmap(browseForm.Width,browseForm.Height)){browseForm.DrawToBitmap(bitmap,new Rectangle(Point.Empty,browseForm.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"preset-read-only-browsing.png"));}
    Invoke(mode,"SetReadOnly",false);Check(mutate.Enabled&&ownership.Enabled&&slider.Enabled&&!notes.ReadOnly&&tiles.AllowDrop&&!browseGrid.Columns["FuelCapacity"].ReadOnly,"applying or discarding restores editing and drag-and-drop");
   }
  }
  var regionListType=typeof(List<>).MakeGenericType(T("CustomTruck"));var regions=(IList)Activator.CreateInstance(regionListType)!;regions.Add(Activator.CreateInstance(T("CustomTruck"),new object[]{"us_01","Michigan","Base game",""}));
  var regionResultType=typeof(IReadOnlyList<>).MakeGenericType(T("CustomTruck"));var regionDelegateType=typeof(Func<>).MakeGenericType(regionResultType);var regionDelegate=System.Linq.Expressions.Expression.Lambda(regionDelegateType,System.Linq.Expressions.Expression.Constant(regions,regionResultType)).Compile();
  var progressionFile=W("browse-progression.json");var progressionStore=Activator.CreateInstance(T("RegionProgressionStore"),new object[]{progressionFile})!;
  using(var progressionTab=(Control)Activator.CreateInstance(T("RegionProgressionTab"),new object?[]{regionDelegate,progressionStore,null})!)
  using(var progressionMode=(IDisposable)Activator.CreateInstance(T("PresetBrowseMode"),new object[]{new Control[]{progressionTab}})!)
  {Invoke(progressionMode,"SetReadOnly",true);Invoke(progressionTab,"RefreshRegions");Check(!File.Exists(progressionFile),"browsing region progression does not auto-save a new play order");var tiles=(Control)progressionTab.GetType().GetField("_tiles",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(progressionTab)!;Check(tiles.Controls.Count==1,"read-only progression still displays newly discovered regions");}
  using(var cellGrid=new DataGridView{AllowUserToAddRows=false})
  {
   var replacementCell=(DataGridViewCell)Activator.CreateInstance(T("TruckReplacementCell"))!;var opened=0;replacementCell.GetType().GetProperty("Open")!.SetValue(replacementCell,(Action<int>)(_=>opened++));cellGrid.Columns.Add(new DataGridViewColumn(replacementCell){Name="Truck"});cellGrid.Rows.Add("Test truck");
   using var cellMode=(IDisposable)Activator.CreateInstance(T("PresetBrowseMode"),new object[]{new Control[]{cellGrid}})!;Invoke(cellMode,"SetReadOnly",true);var key=new KeyEventArgs(Keys.F4);Invoke(cellGrid.Rows[0].Cells[0],"OnKeyDown",key,0);Check(opened==0&&key.SuppressKeyPress,"read-only map cells block replacement popup keyboard shortcuts");
  }
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"results.json"),JsonSerializer.Serialize(new{PresetBytes=new FileInfo(preset).Length,InitialSha256=Hash(packed1),MapSha256=Hash(mapOutput),Scratch=scratch},new JsonSerializerOptions{WriteIndented=true}));
 }
}

