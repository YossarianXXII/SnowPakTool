using System.Text.Json;
using SnowPakTool.Desktop;

static class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Signature(UpgradeDistributionPlan p) => string.Join("|", p.Assignments.Select(a => $"{a.Slot.Zone}:{a.UpgradeId}:{a.Path}:{a.Fallback}:{a.Reused}"));
    static UpgradeDistributionSlot Slot(int i, string region, string id) => new($"[ps]/level_{region}_01.sso", $"level_{region}_01", region, "ZONE_"+i, 0, id);
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid input accepted"); }
    static void CheckTypes(UpgradeDistributionInput input, UpgradeDistributionPlan plan)
    {
        var definitions=input.Definitions.ToDictionary(d=>d.Id);
        Check(plan.Assignments.Count==input.Slots.Count,"Every slot filled");
        foreach(var a in plan.Assignments)
        {
            Check(a.Kind==definitions[a.Slot.OriginalUpgradeId].Kind,"Every individual slot retains its original type");
            if(definitions[a.Slot.OriginalUpgradeId].KeepInPlace || a.Kind==UpgradeKind.MetalDetector)
                Check(a.UpgradeId==a.Slot.OriginalUpgradeId && a.Path==UpgradeSelectionPath.Fixed,"Fixed pickup stays at exact identity");
            else if(a.Path!=UpgradeSelectionPath.KeptOriginal)
                Check(definitions[a.UpgradeId].Eligible && !definitions[a.UpgradeId].KeepInPlace,"Only eligible replacement candidates");
        }
        Check(plan.Report.OriginalTypes.All(p=>plan.Report.PlannedTypes[p.Key]==p.Value),"Global type totals preserved");
        foreach(var region in input.Slots.Select(s=>s.Region).Distinct())
        foreach(var kind in Enum.GetValues<UpgradeKind>())
            Check(input.Slots.Count(s=>s.Region==region && definitions[s.OriginalUpgradeId].Kind==kind)==plan.Assignments.Count(a=>a.Slot.Region==region && a.Kind==kind),"Regional type totals preserved");
    }
    static UpgradeDistributionInput RealFixture(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, ".checks/UpgradeRegionAnalysis/results/original-trucks-upgrades.json")));
        var root = document.RootElement;
        var models = root.GetProperty("Models").EnumerateArray().ToArray();
        var references = root.GetProperty("References").EnumerateArray().ToArray();
        var info = root.GetProperty("RewardInfo");
        var definitions = new List<UpgradeDistributionDefinition>();
        foreach (var reference in references.DistinctBy(r => r.GetProperty("UpgradeId").GetString()))
        {
            var id = reference.GetProperty("UpgradeId").GetString()!;
            var compatible = models.Where(m => m.GetProperty("CompatibleRewardIds").EnumerateArray().Any(v => v.GetString() == id))
                .Select(m => m.GetProperty("Truck").GetString()!).ToArray();
            UpgradeKind kind;
            if (id == "wheels_extend_kirovets_k7m_tire_1") { kind = UpgradeKind.Tire; compatible = ["kirovets_k7m"]; }
            else kind = info.GetProperty(id).GetProperty("Type").GetString() switch
            {
                "Engine" => UpgradeKind.Engine, "Suspension" => UpgradeKind.Suspension,
                "Gearbox" => UpgradeKind.Gearbox, "awd" => UpgradeKind.Awd,
                "diff_lock" => UpgradeKind.DifferentialLock,
                "frame_addons" when id == "frame_addon_metal_detector" => UpgradeKind.MetalDetector,
                _ => throw new Exception("Unresolved fixture item " + id)
            };
            definitions.Add(new(id, kind, compatible));
        }
        var homes = models.SelectMany(m => m.GetProperty("Regions").EnumerateArray()
            .Select(region => new UpgradeTruckHome(m.GetProperty("Truck").GetString()!, region.GetString()!))).ToList();
        // Verified direct garage reward: Starting The Plank Production, Maine.
        homes.Add(new("tayga_6455b", "us_06"));
        var slots = references.Select(r => new UpgradeDistributionSlot(r.GetProperty("Source").GetString()!, r.GetProperty("Map").GetString()!,
            r.GetProperty("Region").GetString()!, r.GetProperty("Zone").GetString()!, r.GetProperty("Index").GetInt32(), r.GetProperty("UpgradeId").GetString()!)).ToArray();
        var regions = slots.Select(s => s.Region).Distinct().OrderBy(code => code == "us_01" ? 0 : 1).ThenBy(code => code, StringComparer.Ordinal).ToArray();
        return new(slots, definitions, homes, regions);
    }

    static void Main()
    {
        var noBias=new UpgradeDistributionOptions(0);
        var replacement=new UpgradeDistributionInput(
            [Slot(0,"aa","old")],
            [new("old",UpgradeKind.Engine,["old_truck"],Eligible:false),new("new",UpgradeKind.Engine,["new_truck"]),new("stock",UpgradeKind.Engine,["new_truck"],Eligible:false)],
            [new("new_truck","aa")],["aa"]);
        var changed=UpgradeDistributionPlanner.Generate(replacement,1);
        CheckTypes(replacement,changed);
        Check(changed.Assignments.Single().UpgradeId=="new" && changed.Assignments.Single().Path==UpgradeSelectionPath.LocalTruck,"Selects a new reward outside the original map pool for a new local truck");

        var generic=new UpgradeDistributionInput([Slot(0,"aa","old_shared")],
            [new("old_shared",UpgradeKind.Gearbox,["old_a","old_b"],Eligible:false),new("shared_local",UpgradeKind.Gearbox,["local_a","local_b"]),new("shared_foreign",UpgradeKind.Gearbox,["foreign_a","foreign_b"])],
            [new("local_a","aa"),new("local_b","aa"),new("foreign_a","bb"),new("foreign_b","bb")],["aa","bb"]);
        var foundLocal=false;
        for(var seed=0;seed<40;seed++)
        {
            var normal=UpgradeDistributionPlanner.Generate(generic,seed,noBias);
            var foreign=UpgradeDistributionPlanner.Generate(generic,seed,new(1));
            CheckTypes(generic,normal);CheckTypes(generic,foreign);
            Check(foreign.Assignments.Single().UpgradeId=="shared_foreign","First-region shared-only foreign preference");
            if(normal.Assignments.Single().UpgradeId=="shared_local")foundLocal=true;
        }
        Check(foundLocal,"Generic slots include upgrades for regionally obtainable trucks, not just purchase-only trucks");
        Check(Signature(UpgradeDistributionPlanner.Generate(replacement,33,new(0)))==Signature(UpgradeDistributionPlanner.Generate(replacement,33,new(1))),"No foreign bias or locality roll for truck-specific slots");

        var definitions=new List<UpgradeDistributionDefinition>();
        var slots=new List<UpgradeDistributionSlot>();
        for(var i=0;i<4;i++){definitions.Add(new("old_"+i,UpgradeKind.Engine,["old_truck"],Eligible:false));slots.Add(Slot(i,"aa","old_"+i));}
        definitions.AddRange([new("local",UpgradeKind.Engine,["new_truck"]),new("shared",UpgradeKind.Engine,["other_a","other_b"]),new("remote_specific",UpgradeKind.Engine,["other_c"]),new("detector",UpgradeKind.MetalDetector,[])]);
        slots.Add(Slot(4,"aa","detector"));
        var scarcity=new UpgradeDistributionInput(slots,definitions,[new("new_truck","aa")],["aa"]);
        var fallback=UpgradeDistributionPlanner.Generate(scarcity,4);
        CheckTypes(scarcity,fallback);
        var paths=fallback.Assignments.Where(a=>a.Path!=UpgradeSelectionPath.Fixed).ToArray();
        Check(paths.Count(a=>a.Path==UpgradeSelectionPath.LocalTruck)==2 && paths.Count(a=>a.Path==UpgradeSelectionPath.Shared)==1 && paths.Count(a=>a.Path==UpgradeSelectionPath.OtherSameType)==1,"Local, shared and broader pools precede compatible reuse");
        Check(paths.Count(a=>a.Reused)==1 && fallback.Report.Fallbacks==3,"Reuse only after unused same-type candidates are exhausted");
        var moved=UpgradeDistributionPlanner.Generate(scarcity with {TruckHomes=[new("other_c","aa")]},4);
        Check(moved.Assignments.Count(a=>a.Path==UpgradeSelectionPath.LocalTruck)==2 && moved.Assignments.Any(a=>a.Path==UpgradeSelectionPath.LocalTruck && a.UpgradeId=="remote_specific"),"Local choices follow the supplied randomized snapshot");
        Check(Signature(fallback)==Signature(UpgradeDistributionPlanner.Generate(scarcity,4)),"Repeatable seed");
        var reversed=scarcity with {Slots=scarcity.Slots.Reverse().ToArray(),Definitions=scarcity.Definitions.Reverse().ToArray(),TruckHomes=scarcity.TruckHomes.Reverse().ToArray()};
        Check(Signature(fallback)==Signature(UpgradeDistributionPlanner.Generate(reversed,4)),"Caller collection order does not affect seed");
        Check(Signature(fallback)!=Signature(UpgradeDistributionPlanner.Generate(scarcity,5)),"Different seeds vary assignments");

        var noCandidates=replacement with {Definitions=[new("old",UpgradeKind.Engine,["old_truck"],Eligible:false)]};
        var kept=UpgradeDistributionPlanner.Generate(noCandidates,1);
        Check(kept.Assignments.Single().Path==UpgradeSelectionPath.KeptOriginal && kept.Report.KeptOriginal==1,"Empty same-type candidate pool retains original with note");
        var sharedUnavailable=generic with {Definitions=[generic.Definitions[0],new("unique",UpgradeKind.Gearbox,["only_one"])]};
        var broadened=UpgradeDistributionPlanner.Generate(sharedUnavailable,1);
        Check(broadened.Assignments.Single().UpgradeId=="unique" && broadened.Assignments.Single().Fallback,"Generic slot can fall back to a specific part of the same type");
        var extraPin=scarcity with {Definitions=scarcity.Definitions.Select(d=>d.Id=="old_0"?d with {KeepInPlace=true}:d).ToArray()};
        CheckTypes(extraPin,UpgradeDistributionPlanner.Generate(extraPin,5));
        Reject(()=>UpgradeDistributionPlanner.Generate(replacement with {Definitions=[]},1));
        Reject(()=>UpgradeDistributionPlanner.Generate(replacement with {Definitions=[new("old",UpgradeKind.Engine,[])]},1));
        Reject(()=>UpgradeDistributionPlanner.Generate(replacement with {Slots=[replacement.Slots[0],replacement.Slots[0]]},1));
        Reject(()=>UpgradeDistributionPlanner.Generate(replacement with {RegionOrder=[]},1));
        Reject(()=>UpgradeDistributionPlanner.Generate(replacement,1,new(double.NaN)));
        Check(UpgradeDistributionPlanner.Generate(new([],[],[],[]),1).Assignments.Count==0,"Empty scope");

        var project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
        var realInput=RealFixture(project);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        var real=UpgradeDistributionPlanner.Generate(realInput,20261105);
        CheckTypes(realInput,real);
        Check(real.Assignments.Count==166 && real.Assignments.Count(a=>a.Path==UpgradeSelectionPath.Fixed)==1,"166-slot baseline with fixed detector");
        var models=realInput.Definitions.SelectMany(d=>d.CompatibleTrucks).Distinct().Order(StringComparer.Ordinal).ToArray();
        var random=new Random(4567);
        var randomInput=realInput with {TruckHomes=realInput.TruckHomes.Select(h=>h.Truck=="tayga_6455b"?h:h with {Truck=models[random.Next(models.Length)]}).ToArray()};
        var randomized=UpgradeDistributionPlanner.Generate(randomInput,20261105);
        CheckTypes(randomInput,randomized);
        Check(Signature(real)!=Signature(randomized),"Real-sized assignments respond to randomized trucks");
        var output=Path.Combine(project,".checks/UpgradePlannerRegression/results");Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"slot-example-plan.json"),JsonSerializer.Serialize(real,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"slot-randomized-homes-plan.json"),JsonSerializer.Serialize(randomized,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS new-reward selection, per-slot/regional/global type preservation, pins, shared eligibility, first-region bias isolation, fallback/reuse order, seeded snapshots and validation.");
        Console.WriteLine($"166-slot baseline: local choices for specific slots {real.Report.LocalChoicesForSpecificSlots}/{real.Report.OriginalSpecificSlots}; fallbacks {real.Report.Fallbacks}; reused {real.Report.Reused}; retained originals {real.Report.KeptOriginal}; first-region foreign shared {real.Report.FirstRegionForeignSharedChoices}/{real.Report.FirstRegionSharedSlots}; two real-sized plans in {timer.Elapsed.TotalMilliseconds:F0}ms.");
    }
}
