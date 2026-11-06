using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

// Copy instructions refer only to the recipient's verified baseline. Literal bytes
// contain the edits, not complete game archives. The whole document is Brotli compressed.
internal sealed record PresetBlock(int Offset, int Length, byte[]? Data = null);
internal sealed record PresetPatch(string Area, string Path, string Before, string After, int Length, List<PresetBlock> Blocks,
    string ArchiveHash = "");
internal sealed record SharedPresetDocument(int Version, string Baseline, List<PresetPatch> Files,
    Dictionary<string, string?> Planning, Dictionary<string, string?> Metadata, List<WorkspaceChange>? Review = null,
    bool ReplaceLocalChanges = false);

internal static class SharedPreset
{
    private const int MaxDocument = 128 * 1024 * 1024;
    private static readonly byte[] Magic = "SNOWPRESET1\n"u8.ToArray();
    internal static readonly string[] PlanningFiles = ["truck-tiers.json", "region-tiers.json", "custom-truck-groups.json",
        "owned-dlcs.json", "owned-regions.json", "region-progression.json", "region-truck-availability.json",
        "truck-randomization-rules.json", "upgrade-randomization-rules.json"];
    private static readonly string[] MetadataFiles = ["progression-settings.json", "truck-properties/unlock-rank-settings.json",
        "winches/custom-presets.json", "region-maps/pending-replacements.json"];
    private static string StagePath(string workspace) => Path.Combine(WorkspaceOriginals.GetWorkspaceRoot(workspace), "shared-preset.staged");
    public static bool HasPending(string workspace) => !string.IsNullOrWhiteSpace(workspace) && File.Exists(StagePath(workspace));
    internal static string PreferencesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPakTool.Desktop");

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid preset path: " + relative);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Preset path escapes its workspace.");
        for (var p = full; p is not null; p = Path.GetDirectoryName(p))
        {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Preset paths cannot follow filesystem links.");
            if (p.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static IEnumerable<string> Files(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];

    internal static string Fingerprint(string workspace)
    {
        var root = WorkspaceOriginals.GetOriginalRoot(workspace);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var file in Files(root))
        {
            var path = Relative(root, file); SafePath(root, path);
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "\0"));
            hash.AppendData(Convert.FromHexString(FileHash(file))); count++;
        }
        if (count == 0) throw new InvalidDataException("Unpack the initial PAK before sharing presets.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static PresetPatch Diff(string area, string path, byte[] before, byte[] after, string archiveHash = "")
    {
        // Small fixed anchors resynchronize after insertions, including map model names.
        const int anchor = 32;
        var index = new Dictionary<ulong, int>();
        static ulong Key(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
        for (var i = 0; i <= before.Length - anchor; i += 16) index.TryAdd(Key(before, i), i);
        var blocks = new List<PresetBlock>(); var literal = new MemoryStream();
        void Flush() { if (literal.Length > 0) { blocks.Add(new(0, (int)literal.Length, literal.ToArray())); literal.SetLength(0); } }
        for (var i = 0; i < after.Length;)
        {
            if (i <= after.Length - anchor && index.TryGetValue(Key(after, i), out var at)
                && before.AsSpan(at, anchor).SequenceEqual(after.AsSpan(i, anchor)))
            {
                Flush(); var length = anchor;
                while (at + length < before.Length && i + length < after.Length && before[at + length] == after[i + length]) length++;
                blocks.Add(new(at, length)); i += length;
            }
            else literal.WriteByte(after[i++]);
        }
        Flush(); literal.Dispose();
        return new(area, path, Hash(before), Hash(after), after.Length, blocks, archiveHash);
    }

    internal static byte[] Patch(PresetPatch patch, byte[] before)
    {
        if (Hash(before) != patch.Before) throw new InvalidDataException("Preset baseline differs: " + patch.Path);
        if (patch.Length < 0 || patch.Length > MaxDocument || patch.Blocks is null) throw new InvalidDataException("Invalid preset output size.");
        var result = new byte[patch.Length]; var cursor = 0;
        foreach (var block in patch.Blocks)
        {
            if (block is null || block.Offset < 0 || block.Length <= 0 || block.Length > result.Length - cursor
                || (block.Data is null ? block.Offset > before.Length - block.Length : block.Data.Length != block.Length))
                throw new InvalidDataException("Invalid preset copy instruction.");
            (block.Data is null ? before.AsSpan(block.Offset, block.Length) : block.Data.AsSpan()).CopyTo(result.AsSpan(cursor)); cursor += block.Length;
        }
        if (cursor != result.Length || Hash(result) != patch.After) throw new InvalidDataException("Preset output failed verification: " + patch.Path);
        return result;
    }

    internal static SharedPresetDocument Read(string path)
    {
        using var input = File.OpenRead(path); var magic = new byte[Magic.Length]; input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a SnowPakTool shared preset.");
        using var compressed = new BrotliStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = compressed.Read(buffer)) > 0)
        { if (decoded.Length + read > MaxDocument) throw new InvalidDataException("Preset exceeds the supported size."); decoded.Write(buffer, 0, read); }
        var document = JsonSerializer.Deserialize<SharedPresetDocument>(decoded.ToArray()) ?? throw new InvalidDataException("Empty preset.");
        if (document.Version != 1 || document.Files is null || document.Planning is null || document.Metadata is null
            || document.Planning.Count != PlanningFiles.Length || PlanningFiles.Any(p => !document.Planning.ContainsKey(p))
            || document.Metadata.Count != MetadataFiles.Length || MetadataFiles.Any(p => !document.Metadata.ContainsKey(p)))
            throw new InvalidDataException("Unsupported or incomplete shared preset.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in document.Files)
        {
            if (patch is null || patch.Area is not ("initial" or "map") || !keys.Add(patch.Area + "/" + patch.Path)) throw new InvalidDataException("Invalid or duplicate preset target.");
            SafePath(Path.GetTempPath(), patch.Path);
            if (patch.Area == "initial" && !(patch.Path.StartsWith('[') || patch.Path == "pak.load_list" || patch.Path == "sound.sound_list"))
                throw new InvalidDataException("Unsupported initial PAK target.");
            if (patch.Area == "map" && !Regex.IsMatch(patch.Path, @"^level_(us|ru)_\d{2}_\d{2}(?:_[a-z0-9]+)*\.(pak|bin)$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid map target.");
            if (!Regex.IsMatch(patch.Before ?? "", "^[A-F0-9]{64}$") || !Regex.IsMatch(patch.After ?? "", "^[A-F0-9]{64}$")
                || patch.Area == "map" && !Regex.IsMatch(patch.ArchiveHash ?? "", "^[A-F0-9]{64}$"))
                throw new InvalidDataException("Invalid preset checksum.");
        }
        foreach (var json in document.Planning.Values.Concat(document.Metadata.Values).Where(v => v is not null))
            using (JsonDocument.Parse(json!)) { }
        foreach (var change in document.Review ?? [])
            if (change is null || !document.Files.Any(p => p.Area == "initial" && p.Path.Equals(change.Target, StringComparison.OrdinalIgnoreCase))
                || change.Property is null || change.Before is null || change.After is null)
                throw new InvalidDataException("Invalid preset review details.");
        var mapState = document.Metadata["region-maps/pending-replacements.json"];
        if (mapState is not null)
        {
            using var state = JsonDocument.Parse(mapState);
            if (!state.RootElement.TryGetProperty("Pending", out var pending) || pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() != 0)
                throw new InvalidDataException("Shared map results must be materialized, with no machine-specific pending paths.");
        }
        return document;
    }

    internal static void Write(string path, SharedPresetDocument document)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            { output.Write(Magic); using var zip = new BrotliStream(output, CompressionLevel.SmallestSize); JsonSerializer.Serialize(zip, document); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Files(source))
        { var relative = Relative(source, file); SafePath(source, relative); var output = SafePath(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output); }
    }

    private static string InputStamp(string workspace, string preferences)
    {
        var files = Files(WorkspaceOriginals.GetUpdatedRoot(workspace))
            .Concat(new[] { "truck-properties", "frame-addon-fuel", "cargo-mass", "trailers", "various", "winches", "upgrade-randomization", "region-maps" }
                .SelectMany(p => Files(WorkspaceOriginals.GetMetadataPath(workspace, p))))
            .Concat(PlanningFiles.Select(p => Path.Combine(preferences, p)))
            .Append(WorkspaceOriginals.GetMetadataPath(workspace, "progression-settings.json"))
            .Concat(PendingTruckReplacements.Load(workspace).Select(p => p.Original.Source));
        return string.Join('\n', files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(p =>
        { var info = new FileInfo(p); return p + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing"); }));
    }

    // Rendering runs against an isolated copy, including all drafts. It never applies
    // the sender's staged edits or changes the sender's original/updated files.
    public static void Export(string workspace, string path, Action<string> render, string? preferences = null)
    {
        if (!path.EndsWith(".snowpreset", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Use the .snowpreset extension for shared presets.");
        var output = Path.GetFullPath(path);
        foreach (var protectedRoot in new[] { WorkspaceOriginals.GetOriginalRoot(workspace), WorkspaceOriginals.GetUpdatedRoot(workspace), Path.Combine(WorkspaceOriginals.GetWorkspaceRoot(workspace), "region-maps") })
            if (output.StartsWith(Path.TrimEndingDirectorySeparator(protectedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Save the shared preset outside the game data folders.");
        if (HasPending(workspace)) { var staged = Read(StagePath(workspace)); Write(path, staged with { ReplaceLocalChanges = false }); return; }
        var baseline = Fingerprint(workspace);
        var stamp = InputStamp(workspace, preferences ?? PreferencesRoot);
        var temporary = Path.Combine(Path.GetTempPath(), "snowpreset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            CopyTree(WorkspaceOriginals.GetOriginalRoot(workspace), Path.Combine(temporary, "initial.pak", "original_files"));
            CopyTree(WorkspaceOriginals.GetUpdatedRoot(workspace), Path.Combine(temporary, "initial.pak", "updated"));
            foreach (var relative in new[] { "truck-properties", "frame-addon-fuel", "cargo-mass", "trailers", "various", "winches", "upgrade-randomization" })
                CopyTree(WorkspaceOriginals.GetMetadataPath(workspace, relative), Path.Combine(temporary, relative));
            var pending = PendingTruckReplacements.Load(workspace);
            var savedMaps = TruckReplacementStore.SavedMaps(workspace).ToArray();
            var sources = pending.Select(p => (Map: p.Original.MapCode, p.Original.Source)).GroupBy(x => x.Map, StringComparer.OrdinalIgnoreCase);
            var mapBaselines = savedMaps.ToDictionary(x => x.Map,
                x => RegionTruckCatalogService.OriginalMapPath(workspace, x.Map)
                    ?? throw new InvalidDataException("Missing preserved original map: " + x.Map), StringComparer.OrdinalIgnoreCase);
            foreach (var group in sources)
            {
                var source = group.First().Source;
                var original = RegionTruckCatalogService.OriginalMapPath(workspace, group.Key) ?? source;
                mapBaselines[group.Key] = original;
                var extension = original.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ? ".pak" : ".bin";
                var dest = SafePath(temporary, "region-maps/backups/" + group.Key + "/original" + extension);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(original, dest);
            }
            var statePath = WorkspaceOriginals.GetMetadataPath(temporary, "region-maps", "pending-replacements.json");
            // Map writers read Source and write only to the temporary workspace.
            var plans = pending.Select(p => p with { Original = p.Original with { FileEntry = null }, Staged = true }).ToArray();
            TruckPropertiesStore.WriteAtomic(statePath, JsonSerializer.Serialize(new { Pending = plans, Assignments = PendingTruckReplacements.LoadAssignments(workspace) }));
            render(temporary);
            PendingTruckReplacements.Stage(temporary); PendingTruckReplacements.ApplyAll(temporary);
            var patches = new List<PresetPatch>();
            var originalRoot = WorkspaceOriginals.GetOriginalRoot(temporary); var updatedRoot = WorkspaceOriginals.GetUpdatedRoot(temporary);
            foreach (var file in Files(updatedRoot))
            {
                var relative = Relative(updatedRoot, file); var original = SafePath(originalRoot, relative);
                var before = File.Exists(original) ? File.ReadAllBytes(original) : [];
                var after = File.ReadAllBytes(file);
                if (!File.Exists(original) || !before.AsSpan().SequenceEqual(after)) patches.Add(Diff("initial", relative, before, after));
            }
            foreach (var (map, source) in savedMaps.Concat(TruckReplacementStore.SavedMaps(temporary))
                         .GroupBy(x => x.Map, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                var original = mapBaselines[map];
                var before = RegionTruckCatalogService.ReadMapData(original)!; var after = RegionTruckCatalogService.ReadMapData(source)!;
                TruckReplacementStore.ValidateScene(before); TruckReplacementStore.ValidateScene(after);
                var extension = original.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ? ".pak" : ".bin";
                // Normalize through the preserved archive layout for reproducible maps.
                patches.Add(Diff("map", map + extension, before, after, FileHash(original)));
            }
            var planning = PlanningFiles.ToDictionary(p => p, p => ReadJson(Path.Combine(preferences ?? PreferencesRoot, p)));
            var metadata = MetadataFiles.ToDictionary(p => p, p => ReadJson(WorkspaceOriginals.GetMetadataPath(p == "region-maps/pending-replacements.json" ? temporary : workspace, p.Replace('/', Path.DirectorySeparatorChar))));
            // All map drafts were rendered; assignments have no machine-specific paths.
            var review = WorkspaceChangeAudit.PakFiles(temporary).Select(c => c with { State = WorkspaceChange.Pending, Target = c.Target.Replace('\\', '/') }).ToList();
            var document = new SharedPresetDocument(1, baseline, patches, planning, metadata, review);
            if (Fingerprint(workspace) != baseline) throw new IOException("Workspace originals changed during export. Retry sharing the preset.");
            if (InputStamp(workspace, preferences ?? PreferencesRoot) != stamp) throw new IOException("Edits or planning changed during export. Wait for active randomizers to finish and save the preset again.");
            var export = Path.Combine(temporary, "result.snowpreset");
            Write(export, document); Read(export); // Validate before replacing an existing preset.
            Write(path, document);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static string? ReadJson(string path)
    { if (!File.Exists(path)) return null; using var json = JsonDocument.Parse(File.ReadAllText(path)); return JsonSerializer.Serialize(json.RootElement); }

    private static string MapBaseline(string workspace, PresetPatch patch, string mapFolder)
    {
        var map = Path.GetFileNameWithoutExtension(patch.Path);
        var original = RegionTruckCatalogService.OriginalMapPath(workspace, map);
        if (original is null)
        {
            original = patch.Path.EndsWith(".pak", StringComparison.Ordinal) ? Path.Combine(mapFolder, patch.Path)
                : Path.Combine(mapFolder, map, "[scene]", "data_for_ssl.bin");
        }
        if (!File.Exists(original) || FileHash(original) != patch.ArchiveHash)
            throw new InvalidDataException("Map baseline differs or is missing: " + map + ". Select the matching game map folder.");
        return original;
    }

    private static void ValidateWorkspace(string workspace, SharedPresetDocument document, string mapFolder, bool replaceLocalChanges = false)
    {
        if (Fingerprint(workspace) != document.Baseline) throw new InvalidDataException("This preset requires the same initial PAK originals/game version as its sender.");
        var initial = document.Files.Where(p => p.Area == "initial").ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(WorkspaceOriginals.GetUpdatedRoot(workspace)))
        {
            if (replaceLocalChanges) break;
            var relative = Relative(WorkspaceOriginals.GetUpdatedRoot(workspace), file);
            var original = WorkspaceOriginals.GetOriginalReadPath(workspace, relative); var hash = FileHash(file);
            if ((File.Exists(original) && hash == FileHash(original)) || initial.TryGetValue(relative, out var patch) && hash == patch.After) continue;
            throw new InvalidDataException("Local edits conflict with this preset: " + relative + ". Use a separate clean workspace.");
        }
        foreach (var patch in document.Files)
        {
            var before = patch.Area == "initial" ? WorkspaceOriginals.GetOriginalReadPath(workspace, patch.Path) : MapBaseline(workspace, patch, mapFolder);
            Patch(patch, patch.Area == "initial" ? File.Exists(before) ? File.ReadAllBytes(before) : [] : RegionTruckCatalogService.ReadMapData(before)!);
        }
        var maps = document.Files.Where(p => p.Area == "map").ToDictionary(p => Path.GetFileNameWithoutExtension(p.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (map, file) in TruckReplacementStore.SavedMaps(workspace))
        {
            if (replaceLocalChanges) break;
            var bytes = RegionTruckCatalogService.ReadMapData(file)!; var original = RegionTruckCatalogService.OriginalMapPath(workspace, map);
            if (original is not null && bytes.AsSpan().SequenceEqual(RegionTruckCatalogService.ReadMapData(original))) continue;
            if (maps.TryGetValue(map, out var patch) && Hash(bytes) == patch.After) continue;
            throw new InvalidDataException("Local map edits conflict with this preset: " + map + ". Use a separate clean workspace.");
        }
    }

    public static void Stage(string workspace, string path, string mapFolder)
    {
        var document = Read(path) with { ReplaceLocalChanges = false }; ValidateWorkspace(workspace, document, mapFolder);
        // Only the portable snapshot is staged. No planning or game output is written.
        Write(StagePath(workspace), document);
    }

    // Called only after the user confirms replacing local work. Keep that permission
    // in the local stage, never trust or export it as part of a portable preset.
    public static void StageReplacing(string workspace, string path, string mapFolder)
    {
        var document = Read(path) with { ReplaceLocalChanges = true };
        ValidateWorkspace(workspace, document, mapFolder, replaceLocalChanges: true);
        if (HasPending(workspace))
        {
            var history = Path.Combine(WorkspaceOriginals.GetWorkspaceRoot(workspace), "preset-history", "replaced-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(history); File.Copy(StagePath(workspace), Path.Combine(history, "shared-preset.staged.before"));
        }
        Write(StagePath(workspace), document);
    }

    public static IReadOnlyList<WorkspaceChange> Pending(string workspace)
    {
        if (!HasPending(workspace)) return [];
        var document = Read(StagePath(workspace));
        var review = (document.Review ?? []).Select(c => c with { State = WorkspaceChange.Pending, Target = WorkspaceOriginals.GetUpdatedPath(workspace, c.Target) });
        return review.Concat(document.Files.Where(p => p.Area != "initial" || !(document.Review ?? []).Any(c => c.Target.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new WorkspaceChange(WorkspaceChange.Pending,
            p.Area == "initial" ? WorkspaceOriginals.GetUpdatedPath(workspace, p.Path) : Path.Combine(WorkspaceOriginals.GetUpdatedRoot(workspace), "Shared maps", p.Path),
            "Shared preset / " + (p.Area == "initial" ? "File changes" : "Map changes"), p.Before[..12], p.After[..12],
            SummaryGroup: p.Area == "map" ? "shared:map:" + p.Path : "", SummaryName: p.Area == "map" ? p.Path : "")))
            .Append(new(WorkspaceChange.Pending, Path.Combine(WorkspaceOriginals.GetUpdatedRoot(workspace), "Shared planning"), "Shared preset / Planning and settings", "Local settings", "Shared settings (staged)",
                SummaryGroup: "shared:planning", SummaryName: "Gameplay planning and settings")).ToArray();
    }

    public static void Discard(string workspace) => File.Delete(StagePath(workspace));

    public static void Apply(string workspace, string mapFolder, string? preferences = null)
    {
        var document = Read(StagePath(workspace)); ValidateWorkspace(workspace, document, mapFolder, document.ReplaceLocalChanges);
        var root = WorkspaceOriginals.GetWorkspaceRoot(workspace);
        var transaction = Path.Combine(root, "preset-history", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var outputs = new List<(string Target, string? Temporary, string? Backup)>();
        void Prepare(string target, byte[]? data)
        {
            if (Directory.Exists(target)) throw new IOException("A preset output conflicts with a folder: " + target);
            var number = outputs.Count.ToString(); var temp = data is null ? null : Path.Combine(transaction, number + ".new");
            if (temp is not null) File.WriteAllBytes(temp, data!);
            var backup = File.Exists(target) ? Path.Combine(transaction, number + ".before") : null;
            if (backup is not null) File.Copy(target, backup);
            outputs.Add((target, temp, backup));
        }
        void PrepareFile(string target, string source)
        {
            if (Directory.Exists(target)) throw new IOException("A preset output conflicts with a folder: " + target);
            var number = outputs.Count.ToString(); var temp = Path.Combine(transaction, number + ".new");
            File.Copy(source, temp);
            var backup = File.Exists(target) ? Path.Combine(transaction, number + ".before") : null;
            if (backup is not null) File.Copy(target, backup);
            outputs.Add((target, temp, backup));
        }
        foreach (var patch in document.Files)
        {
            if (patch.Area == "initial")
            {
                var original = WorkspaceOriginals.GetOriginalReadPath(workspace, patch.Path);
                Prepare(SafePath(WorkspaceOriginals.GetUpdatedRoot(workspace), patch.Path), Patch(patch, File.Exists(original) ? File.ReadAllBytes(original) : []));
            }
            else
            {
                var original = MapBaseline(workspace, patch, mapFolder); var map = Path.GetFileNameWithoutExtension(patch.Path);
                var data = Patch(patch, RegionTruckCatalogService.ReadMapData(original)!);
                var extension = Path.GetExtension(patch.Path);
                var backup = SafePath(root, "region-maps/backups/" + map + "/original" + extension);
                if (!File.Exists(backup)) PrepareFile(backup, original);
                if (extension == ".pak")
                {
                    var packed = Path.Combine(transaction, map + ".pak"); MapArchiveWriter.Write(original, packed, data);
                    PrepareFile(SafePath(root, "region-maps/updated/" + patch.Path), packed);
                }
                else Prepare(SafePath(root, "region-maps/updated/" + map + "/[scene]/data_for_ssl.bin"), data);
            }
        }
        foreach (var (relative, json) in document.Planning) Prepare(SafePath(preferences ?? PreferencesRoot, relative), json is null ? null : Encoding.UTF8.GetBytes(json));
        foreach (var (relative, json) in document.Metadata) Prepare(SafePath(root, relative), json is null ? null : Encoding.UTF8.GetBytes(json));
        if (document.ReplaceLocalChanges)
        {
            // This is a complete setup replacement: retaining extra local overlays
            // would otherwise produce different packed files from the same preset.
            var updated = WorkspaceOriginals.GetUpdatedRoot(workspace);
            if (Path.GetFullPath(updated).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Migrate to a separate updated-files folder before replacing a preset.");
            var targets = outputs.Select(o => o.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files(updated).Concat(TruckReplacementStore.SavedMaps(workspace).Select(m => m.Source)))
            {
                var safe = SafePath(root, Relative(root, file));
                if (targets.Add(safe)) Prepare(safe, null);
            }
            foreach (var relative in new[] { "truck-properties/pending-changes.json", "frame-addon-fuel/pending-changes.json", "cargo-mass/pending-changes.json", "trailers/pending-changes.json", "various/pending-changes.json",
                "winches/pending-changes.json", "upgrade-randomization/staged.json", "upgrade-randomization/draft.json",
                "upgrade-randomization/addon-exploration.json" })
            {
                var path = WorkspaceOriginals.GetMetadataPath(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) Prepare(SafePath(root, Relative(root, path)), null);
            }
            var rankStage = new RankXpStageStore(Path.Combine(preferences ?? PreferencesRoot, "staged-rank-xp")).StatePath(workspace);
            if (File.Exists(rankStage)) Prepare(rankStage, null);
        }
        File.WriteAllText(Path.Combine(transaction, "targets.json"), JsonSerializer.Serialize(outputs.Select(o => new { o.Target, o.Backup })));
        var committed = new List<(string Target, string? Temporary, string? Backup)>();
        try
        {
            foreach (var item in outputs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                if (item.Temporary is null) File.Delete(item.Target);
                else { var temp = item.Target + ".preset-tmp"; File.Copy(item.Temporary, temp, true); File.Move(temp, item.Target, true); }
                committed.Add(item);
                WorkspaceDataCache.InvalidateFile(item.Target);
            }
            File.Delete(StagePath(workspace));
        }
        catch
        {
            foreach (var item in committed.AsEnumerable().Reverse())
                if (item.Backup is null) File.Delete(item.Target); else File.Copy(item.Backup, item.Target, true);
            throw;
        }
    }
}
