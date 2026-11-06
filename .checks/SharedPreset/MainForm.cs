using System.Diagnostics;
using System.IO.Compression;
using SnowPakTool;

namespace SnowPakTool.Desktop;

internal sealed class MainForm : Form
{
    private readonly TextBox _pakPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workspacePath = new() { Dock = DockStyle.Fill };
    private readonly ExperienceEditorTab _experienceTab;
    private readonly OwnedContentTab _ownedContentTab;
    private readonly CustomGroupWipTab _customGroupWipTab;
    private readonly CustomGroupWipTab _tierListTab;
    private readonly CustomGroupWipTab _regionTierListTab;
    private readonly RegionProgressionTab _regionProgressionTab;
    private IReadOnlyList<CustomTruck> _tierRegions = Array.Empty<CustomTruck>();
    private readonly TruckEditTab _truckEditTab;
    private readonly TrucksCargoTab _trucksCargoTab;
    private readonly RegionsTrucksTab _regionsTrucksTab;
    private readonly TruckPropertiesTab _truckPropertiesTab;
    private readonly TruckRandomizationTab _truckRandomizationTab;
    private readonly UpgradeRandomizationTab _upgradeRandomizationTab;
    private readonly MapUpgradesTab _mapUpgradesTab;
    private UpgradeTrialCatalog? _upgradeTrialCatalog;
    private string? _upgradeTrialStamp;
    private StartChangesPanel? _startChanges;
    private readonly OwnedRegionState _ownedRegionState = new();
    private readonly Button _unpackButton = new() { Text = "Unpack / merge PAK + cache block", AutoSize = true };
    private readonly Button _openButton = new() { Text = "Open workspace", AutoSize = true };
    private readonly Button _packButton = new() { Text = "Pack to new PAK…", AutoSize = true };
    private readonly ProgressBar _loadingBar = new() { Dock = DockStyle.Fill, Height = 18, Style = ProgressBarStyle.Blocks, MarqueeAnimationSpeed = 0 };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        WordWrap = false
    };

    public MainForm()
    {
        Text = "SnowPakTool — Workspace Editor";
        MinimumSize = new Size(760, 440);
        Size = UserPreferences.LoadWindowSize(new Size(1120, 920), MinimumSize);
        StartPosition = FormStartPosition.CenterScreen;
        _pakPath.Text = UserPreferences.LoadLastPakPath();
        _workspacePath.Text = UserPreferences.LoadLastWorkspacePath();
        if (!string.IsNullOrWhiteSpace(_workspacePath.Text))
        {
            try { _workspacePath.Text = WorkspaceLayout.Migrate(_workspacePath.Text).Root; }
            catch (Exception ex) { AppendLog("Workspace layout could not be upgraded: " + ex.Message); }
        }
        _experienceTab = new ExperienceEditorTab(() => _workspacePath.Text.Trim(), _ownedRegionState);
        _ownedContentTab = new OwnedContentTab(() => _workspacePath.Text.Trim(), _ownedRegionState);
        _customGroupWipTab = new CustomGroupWipTab(_ownedContentTab.GetAvailableCustomGroupingTrucks);
        _ownedContentTab.OwnedTrucksChanged += _customGroupWipTab.RefreshOwnedTrucks;
        _tierListTab = new CustomGroupWipTab(_ownedContentTab.GetAvailableCustomGroupingTrucks, tierList: true);
        _ownedContentTab.OwnedTrucksChanged += _tierListTab.RefreshOwnedTrucks;
        _regionTierListTab = new CustomGroupWipTab(() => _tierRegions, regionList: true);
        _regionProgressionTab = new RegionProgressionTab(() => _tierRegions);
        _truckEditTab = new TruckEditTab(() => _workspacePath.Text.Trim(), _ownedContentTab);
        _trucksCargoTab = new TrucksCargoTab(() => _workspacePath.Text.Trim());
        _regionsTrucksTab = new RegionsTrucksTab(() => _workspacePath.Text.Trim(), () => _pakPath.Text.Trim(), getEnabledTrucks: () =>
        {
            _ownedContentTab.EnsureLoaded();
            return _ownedContentTab.GetAvailableCustomGroupingTrucks();
        });
        _truckRandomizationTab = new TruckRandomizationTab(RandomizeObtainableAsync, RandomizeMissionsAsync);
        _upgradeRandomizationTab = new UpgradeRandomizationTab(RandomizeUpgradesAsync);
        _mapUpgradesTab = new MapUpgradesTab(() => _workspacePath.Text.Trim(), () => _pakPath.Text.Trim(), GetUpgradeTrialCatalogAsync);
        _upgradeRandomizationTab.ConfigureMapStaging(StageUpgradeChangesAsync, () => Directory.Exists(_workspacePath.Text.Trim())
            && _upgradeTrialCatalog is not null && _upgradeTrialCatalog.Workspace.Equals(_workspacePath.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            && UpgradeChanges.HasDraft(_upgradeTrialCatalog));
        _mapUpgradesTab.ConfigureStaging(StageUpgradeChangesAsync);
        _mapUpgradesTab.ConfigureApplying(ApplyUpgradeChangesAsync);
        _truckRandomizationTab.ConfigureMapStaging(_regionsTrucksTab.StageChangesAsync, () =>
            Directory.Exists(_workspacePath.Text.Trim()) && PendingTruckReplacements.HasUnstagedChanges(_workspacePath.Text.Trim()));
        _regionsTrucksTab.ChangesStaged += async () =>
        {
            _truckRandomizationTab.RefreshStagingState();
            if (_startChanges is not null) await _startChanges.RefreshAsync();
        };
        _truckPropertiesTab = new TruckPropertiesTab(() => _workspacePath.Text.Trim(), _regionsTrucksTab.GetRandomizationCatalogAsync, () =>
        {
            _ownedContentTab.EnsureLoaded();
            return _ownedContentTab.GetAvailableCustomGroupingTrucks();
        });
        BuildLayout();
        _workspacePath.TextChanged += (_, _) => _openButton.Enabled = _packButton.Enabled = Directory.Exists(_workspacePath.Text.Trim());
        _unpackButton.Click += async (_, _) => await UnpackAsync();
        _openButton.Click += (_, _) => OpenWorkspace();
        _packButton.Click += async (_, _) => await PackAsync();
        FormClosing += (_, _) =>
        {
            RememberPaths();
            UserPreferences.SaveWindowSize(WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size);
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        var title = new Label
        {
            Text = "Find your game installation folder. The app will then pick up the initial.pak and map files from there and make a backup. " +
			"Then hit Unpack button which will extract the files inside the archive. If there are some changes(like after game update" +
			" it will ask you to review. You can do it one by one, or just apply everything from new initial.pak. If you do it manually and one by one, " +
			"you can probably keep your own changes.",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title, 0, 0);

        var paths = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 2, AutoSize = true };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.Controls.Add(new Label { Text = "Input PAK", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 8, 0) }, 0, 0);
        paths.Controls.Add(_pakPath, 1, 0);
        var browsePak = new Button { Text = "Browse…", AutoSize = true };
        browsePak.Click += (_, _) => BrowsePak();
        paths.Controls.Add(browsePak, 2, 0);
        paths.Controls.Add(new Label { Text = "Workspace", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 8, 0) }, 0, 1);
        paths.Controls.Add(_workspacePath, 1, 1);
        var browseWorkspace = new Button { Text = "Browse…", AutoSize = true };
        browseWorkspace.Click += (_, _) => BrowseWorkspace();
        paths.Controls.Add(browseWorkspace, 2, 1);
        root.Controls.Add(paths, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        actions.Controls.Add(_unpackButton);
        actions.Controls.Add(_openButton);
        actions.Controls.Add(_packButton);
        _startChanges = new StartChangesPanel(() => _workspacePath.Text.Trim(), actions, PendingPakChanges, SavePendingPakChanges,
            ApplyMapChangesAsync, () => _regionsTrucksTab.RefreshChangesAsync());
        var startSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1000, 750),
            SplitterDistance = 640, Panel1MinSize = 180, Panel2MinSize = 40, SplitterWidth = 6 };
        startSplit.Panel1.Controls.Add(_startChanges); startSplit.Panel2.Controls.Add(_log);
        root.Controls.Add(startSplit, 0, 3);
        root.Controls.Add(_loadingBar, 0, 4);

        var tabs = new NavigationTabControl { Name = "MainNavigation" };
        TabPage Page(string text, string name, Control content)
        {
            var page = new TabPage(text) { Name = name };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }
        var unpackPage = Page("Start", "Start", root);
        var planningTabs = new NavigationTabControl { Name = "PlanningNavigation" };
        var ownedContentPage = Page("Owned content", "OwnedContent", _ownedContentTab);
        var tierListPage = Page("Truck tiers", "TruckTiers", _tierListTab);
        var regionTierListPage = Page("Region difficulty", "RegionDifficulty", _regionTierListTab);
        var customGroupingPage = Page("Truck groups", "TruckGroups", _customGroupWipTab);
        planningTabs.TabPages.AddRange([ownedContentPage, tierListPage, regionTierListPage, customGroupingPage]);
        var planningPage = Page("Planning", "Planning", planningTabs);
        var randomizerTabs = new NavigationTabControl { Name = "RandomizerNavigation" };
        var regionProgressionPage = Page("Region progression", "RegionProgression", _regionProgressionTab);
        var truckRandomizationPage = Page("Truck randomization", "TruckRandomization", _truckRandomizationTab);
        randomizerTabs.TabPages.AddRange([regionProgressionPage, truckRandomizationPage]);
        var randomizerPage = Page("Randomizer", "Randomizer", randomizerTabs);
        var editorTabs = new NavigationTabControl { Name = "EditorsNavigation" };
        var truckPropertiesPage = Page("Truck properties", "TruckProperties", _truckPropertiesTab);
        var experiencePage = Page("Experience / Ranks", "ExperienceRanks", _experienceTab);
        editorTabs.TabPages.Add(truckPropertiesPage);
        var truckEditorPages = _truckEditTab.TakeEditorPages();
        foreach (var name in new[] { "Engines", "Winches", "Addons", "Frame addons" })
            editorTabs.TabPages.Add(truckEditorPages.Single(page => page.Name == name));
        editorTabs.TabPages.Add(experiencePage);
        var editorsPage = Page("Editors", "Editors", editorTabs);
        var regionsTrucksPage = Page("Map trucks", "MapTrucks", _regionsTrucksTab);
        var trucksCargoPage = Page("Cargo", "Cargo", _trucksCargoTab);
        randomizerTabs.TabPages.Add(regionsTrucksPage);
        var upgradesPage = Page("Upgrades", "Upgrades", _upgradeRandomizationTab);
        var mapUpgradesPage = Page("Map upgrades", "MapUpgrades", _mapUpgradesTab);
        randomizerTabs.TabPages.AddRange([upgradesPage, mapUpgradesPage]);
        editorTabs.TabPages.Add(trucksCargoPage);
        tabs.TabPages.AddRange([unpackPage, planningPage, randomizerPage, editorsPage]);

        void ShowPendingCount(int count) => unpackPage.Text = count > 0 ? $"Start ({count})" : "Start";
        void RefreshPendingCount()
        {
            var workspace = _workspacePath.Text.Trim();
            try { ShowPendingCount(Directory.Exists(workspace) ? PendingPakChanges(workspace).Count + PendingTruckReplacements.LoadStaged(workspace).Count : 0); }
            catch { /* Preserve the last known count; Start reports detailed read errors. */ }
        }
        _startChanges.PendingCountChanged += ShowPendingCount;
        void LoadTierRegions(string workspace)
        {
            var regionNames = WorkspaceDataCache.GetLocalizedNames(workspace);
            _tierRegions = WorkspaceDataCache.GetRegionMetadata(workspace)
                .OrderBy(entry => RegionTileAssets.GetReleaseOrder(entry.Key))
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new CustomTruck(entry.Key, entry.Value.Name, RegionTileAssets.GetReleaseLabel(entry.Key),
                    RegionTileAssets.GetMapNames(entry.Key, regionNames)))
                .ToList();
        }
        async Task RefreshSelectedPageAsync()
        {
            var selectedMain = tabs.SelectedTab;
            var selectedGroup = selectedMain?.Controls.OfType<TabControl>().FirstOrDefault();
            var selectedPage = selectedGroup?.SelectedTab ?? selectedMain;
            var workspace = _workspacePath.Text.Trim();
            if (selectedPage is null || !Directory.Exists(workspace))
            {
                ShowPendingCount(0);
                if (selectedPage == unpackPage) await _startChanges.RefreshAsync();
                return;
            }
            try
            {
                if (selectedMain == planningPage || selectedPage == regionProgressionPage)
                    await WorkspaceDataCache.PreloadOwnedAsync(workspace);
                else await WorkspaceDataCache.PreloadAsync(workspace);
                // A slow load must not initialize a page or workspace the user has already left.
                if (IsDisposed || tabs.SelectedTab != selectedMain || (selectedGroup is not null && selectedGroup.SelectedTab != selectedPage)
                    || !workspace.Equals(_workspacePath.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                if (selectedPage == unpackPage) await _startChanges.RefreshAsync();
                else if (selectedPage == ownedContentPage) _ownedContentTab.EnsureLoaded();
                else if (selectedPage == tierListPage || selectedPage == customGroupingPage)
                {
                    _ownedContentTab.EnsureLoaded();
                    if (selectedPage == tierListPage) _tierListTab.RefreshOwnedTrucks();
                    else _customGroupWipTab.RefreshOwnedTrucks();
                }
                else if (selectedPage == regionTierListPage || selectedPage == regionProgressionPage)
                {
                    LoadTierRegions(workspace);
                    if (selectedPage == regionTierListPage) _regionTierListTab.RefreshOwnedTrucks();
                    else _regionProgressionTab.RefreshRegions();
                }
                else if (selectedPage == truckPropertiesPage) await _truckPropertiesTab.EnsureLoadedAsync();
                else if (selectedPage == experiencePage) _experienceTab.EnsureLoaded();
                else if (selectedPage == trucksCargoPage) _trucksCargoTab.EnsureLoaded();
                else if (selectedMain == editorsPage)
                {
                    _ownedContentTab.EnsureLoaded();
                    _truckEditTab.EnsureEditorLoaded(selectedPage);
                }
                else if (selectedPage == regionsTrucksPage) await _regionsTrucksTab.EnsureLoadedAsync();
                else if (selectedPage == mapUpgradesPage) await _mapUpgradesTab.EnsureLoadedAsync();
                RefreshPendingCount();
            }
            catch (Exception ex) { if (!IsDisposed) AppendLog($"Page loading failed: {ex.Message}"); }
        }
        // Each group retains its selected child when leaving and returning to the group.
        tabs.SelectedIndexChanged += async (_, _) => await RefreshSelectedPageAsync();
        foreach (var group in new[] { planningTabs, randomizerTabs, editorTabs })
            group.SelectedIndexChanged += async (_, _) => { if (group.Parent == tabs.SelectedTab) await RefreshSelectedPageAsync(); };
        _workspacePath.TextChanged += async (_, _) => await RefreshSelectedPageAsync();
        Shown += async (_, _) => await RefreshSelectedPageAsync();

        var navigationHost = new Panel { Dock = DockStyle.Fill };
        navigationHost.Controls.Add(tabs);
        var settings = new Button { Name = "SettingsButton", Text = "\uE713", AccessibleName = "Settings", TabStop = true,
            Font = new Font("Segoe MDL2 Assets", 12f), FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        settings.FlatAppearance.BorderSize = 0;
        var settingsTip = new ToolTip(); settingsTip.SetToolTip(settings, "Settings");
        void PositionSettings()
        {
            var size = Math.Max(28, tabs.ItemSize.Height);
            settings.Size = new Size(size, size);
            settings.Location = new Point(navigationHost.ClientSize.Width - size - 4, 0);
        }
        navigationHost.Controls.Add(settings); settings.BringToFront();
        navigationHost.SizeChanged += (_, _) => PositionSettings();
        navigationHost.DpiChangedAfterParent += (_, _) => PositionSettings();
        PositionSettings(); settings.Click += (_, _) => ShowSettings();
        Controls.Add(navigationHost);
        TextAreaStyle.Watch(this);
        var search = new CurrentTabSearch(() => tabs.SelectedTab);
        search.Attach(this);
        Disposed += (_, _) => { search.Dispose(); settingsTip.Dispose(); settings.Font.Dispose(); _truckEditTab.Dispose(); };
        _openButton.Enabled = _packButton.Enabled = Directory.Exists(_workspacePath.Text.Trim());
    }

    private Form CreateSettingsWindow()
    {
        var window = new Form { Text = "Settings", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(850, 700), MinimumSize = new Size(720, 420), ShowInTaskbar = false };
        var settings = new SettingsTab { Dock = DockStyle.Fill };
        window.Controls.Add(settings);
        var search = new CurrentTabSearch(() => settings);
        search.Attach(window);
        window.Disposed += (_, _) => search.Dispose();
        return window;
    }

    private void ShowSettings()
    {
        using var window = CreateSettingsWindow();
        window.ShowDialog(this);
    }

    private void BrowsePak()
    {
        var currentPath = _pakPath.Text.Trim();
        using var dialog = new OpenFileDialog
        {
            Title = "Select SnowRunner PAK",
            Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = File.Exists(currentPath) ? Path.GetDirectoryName(currentPath) : null,
            FileName = File.Exists(currentPath) ? Path.GetFileName(currentPath) : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pakPath.Text = dialog.FileName;
            RememberPaths();
        }
    }

    private void BrowseWorkspace()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a workspace folder. Existing files will be compared before any replacements." };
        if (Directory.Exists(_workspacePath.Text)) dialog.SelectedPath = _workspacePath.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try { _workspacePath.Text = WorkspaceLayout.Migrate(dialog.SelectedPath).Root; }
            catch (Exception ex) { AppendLog("Workspace layout could not be upgraded: " + ex.Message); return; }
            RememberPaths();
            if (_startChanges is not null) _ = _startChanges.RefreshAsync();
        }
    }

    private async Task UnpackAsync()
    {
        var pak = _pakPath.Text.Trim();
        var workspace = _workspacePath.Text.Trim();
        if (!File.Exists(pak)) { MessageBox.Show(this, "Choose an existing initial.pak file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (string.IsNullOrWhiteSpace(workspace)) { MessageBox.Show(this, "Choose a workspace folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        RememberPaths();

        (int Added, int Replaced, int Unchanged) mergeStats = default;
        var succeeded = await RunBusyAsync("Unpacking PAK and comparing workspace files…", () =>
        {
            Directory.CreateDirectory(workspace);
            var staging = Path.Combine(Path.GetTempPath(), $"snowpak-unpack-{Guid.NewGuid():N}");
            var mergeEdits = staging + "-edits";
            Directory.CreateDirectory(staging);
            try
            {
                ExtractPak(pak, staging);
                var blockPath = Path.Combine(staging, CacheBlockFile.InitialCacheBlockName);
                if (!File.Exists(blockPath)) throw new InvalidDataException("The PAK does not contain initial.cache_block.");
                using (var stream = File.OpenRead(blockPath))
                {
                    var reader = new CacheBlockReader(stream);
                    reader.UnpackAll(staging);
                }
                File.Delete(blockPath);
                SeedOriginalFiles(staging, workspace);
                mergeStats = MergeWorkspace(staging, workspace, mergeEdits, ResolveFileConflicts);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                if (Directory.Exists(mergeEdits)) Directory.Delete(mergeEdits, recursive: true);
            }
        });
        _openButton.Enabled = succeeded && Directory.Exists(workspace);
        _packButton.Enabled = succeeded && Directory.Exists(workspace);
        if (succeeded)
        {
            AppendLog($"Workspace merge: {mergeStats.Added} added, {mergeStats.Replaced} replaced, {mergeStats.Unchanged} unchanged.");
            await WorkspaceDataCache.PreloadAsync(workspace, forceReload: true);
            _experienceTab.WorkspaceUpdated();
            _ownedContentTab.WorkspaceUpdated();
            _truckEditTab.WorkspaceUpdated();
            if (_startChanges is not null) await _startChanges.RefreshAsync();
        }
    }

    private async Task PackAsync()
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) { MessageBox.Show(this, "Choose an existing workspace folder first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        RememberPaths();
        using var dialog = new SaveFileDialog
        {
            Title = "Create or replace a PAK",
            Filter = "PAK files (*.pak)|*.pak|All files (*.*)|*.*",
            FileName = "initial_modified.pak",
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "pak"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var output = dialog.FileName;
        if (File.Exists(_pakPath.Text.Trim()) && Path.GetFullPath(output).Equals(Path.GetFullPath(_pakPath.Text.Trim()), StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "Choose a new PAK output file; the input PAK is preserved.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (_experienceTab.PendingChanges(workspace).Any())
        {
            MessageBox.Show(this, "Apply staged rank XP using Start → Apply pending changes before packing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (UpgradeChanges.Pending(workspace).Any())
        {
            MessageBox.Show(this, "Apply staged upgrade changes using Start → Apply pending changes before packing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { SavePendingPakChanges(); }
        catch (Exception ex) { AppendLog(ex.Message); return; }
        await RunBusyAsync("Building cache block and PAK…", () => PackWorkspace(workspace, output));
        if (_startChanges is not null) await _startChanges.RefreshAsync();
    }

    private IReadOnlyList<WorkspaceChange> PendingPakChanges(string workspace) => _experienceTab.PendingChanges(workspace).Concat(_truckEditTab.PendingChanges(workspace)).Concat(_truckPropertiesTab.PendingChanges(workspace)).Concat(UpgradeChanges.Pending(workspace)).Concat(FrameAddonFuelStore.Pending(workspace))
        .Select(change => change with { Target = WorkspaceOriginals.GetRelativePath(workspace, change.Target) }).ToArray();

    private void SavePendingPakChanges()
    {
        var workspace = _workspacePath.Text.Trim();
        if (_experienceTab.PendingChanges(workspace).Any()) _experienceTab.SavePendingChanges();
        if (_truckEditTab.PendingChanges(workspace).Any()) _truckEditTab.SavePendingChanges();
        if (_truckPropertiesTab.PendingChanges(workspace).Any()) _truckPropertiesTab.SavePendingChanges();
        if (UpgradeChanges.Pending(workspace).Any()) UpgradeChanges.Apply(workspace);
        if (FrameAddonFuelStore.Pending(workspace).Any()) _truckEditTab.ApplyFrameAddonFuelChanges(workspace);
        if (PendingPakChanges(workspace).Count > 0) throw new InvalidOperationException("Some PAK edits could not be saved. Review the pending rows and correct invalid values.");
    }

    private async Task ApplyMapChangesAsync()
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) return;
        _ownedContentTab.EnsureLoaded();
        var enabled = _ownedContentTab.GetAvailableCustomGroupingTrucks().Select(truck => truck.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedRegions = WorkspaceDataCache.GetRegionMetadata(workspace)
            .Where(pair => pair.Value.IsBaseGame || _ownedRegionState.IsOwned(pair.Key)).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var succeeded = await RunBusyAsync("Applying pending map replacements and preserving backups…", () =>
        {
            if (PendingTruckReplacements.LoadStaged(workspace).Any(change => !change.ResetOriginal && !enabled.Contains(change.Target.Code)))
                throw new InvalidOperationException("A pending replacement truck is no longer enabled. Review the pending map changes.");
            var randomized = PendingTruckReplacements.LoadAssignments(workspace).Select(item => item.PlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (PendingTruckReplacements.LoadStaged(workspace).Any(change => !change.ResetOriginal && randomized.Contains(change.Original.StablePlacementKey)
                    && !ownedRegions.Contains(change.Original.RegionCode)))
                throw new InvalidOperationException("A region with staged randomization is no longer owned. Restore ownership or reset its staged changes.");
            foreach (var saved in PendingTruckReplacements.ApplyAll(workspace))
                BeginInvoke((Action)(() => AppendLog($"Map saved: {saved.OutputPath} · Original backup: {saved.OriginalBackupPath}")));
        });
        await _regionsTrucksTab.RefreshChangesAsync();
        if (_startChanges is not null) await _startChanges.RefreshAsync();
        if (!succeeded) throw new InvalidOperationException("Map changes could not be applied. See the shared log for the source error.");
    }

    private async Task RandomizeObtainableAsync(IProgress<RandomizationLogEntry> progress)
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("Choose an extracted workspace on Start first.");
        var progression = new RegionProgressionStore().Load();
        if (!progression.IsFinalized) throw new InvalidOperationException("Finalize Region progression before randomizing obtainable trucks.");
        await WorkspaceDataCache.PreloadOwnedAsync(workspace);
        _ownedContentTab.EnsureLoaded();
        var regions = WorkspaceDataCache.GetRegionMetadata(workspace);
        var ownedRegions = regions.Where(pair => pair.Value.IsBaseGame || _ownedRegionState.IsOwned(pair.Key))
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedTrucks = _ownedContentTab.GetAvailableCustomGroupingTrucks();
        var truckTiers = TierListStore.Load();
        var regionTiers = RegionTierListStore.Load();
        var catalog = await _regionsTrucksTab.GetRandomizationCatalogAsync();
        await Task.Run(() => ObtainableTruckRandomizer.Randomize(workspace, catalog, ownedRegions, ownedTrucks, progression, truckTiers, regionTiers, progress,
            tireDifficulty: ProgressionDifficulty.Hard));
        await _regionsTrucksTab.RefreshChangesAsync();
    }

    private async Task<UpgradeTrialCatalog> GetUpgradeTrialCatalogAsync(bool force)
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("Choose an extracted workspace on Start first.");
        if (force) await WorkspaceDataCache.PreloadAsync(workspace, forceReload: true);
        await WorkspaceDataCache.PreloadOwnedAsync(workspace);
        _ownedContentTab.EnsureLoaded();
        var enabled = _ownedContentTab.GetAvailableCustomGroupingTrucks().Select(t => t.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedRegions = WorkspaceDataCache.GetRegionMetadata(workspace).Where(p => p.Value.IsBaseGame || _ownedRegionState.IsOwned(p.Key))
            .Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedDlc = _ownedContentTab.GetOwnedDlcCodes();
        var progression = new RegionProgressionStore().Load();
        var trucks = await _regionsTrucksTab.GetRandomizationCatalogAsync();
        var stamp = workspace + "|" + WorkspaceDataCache.ContentVersion + "|" + string.Join('|', progression.Order)
            + string.Join('|', enabled.Order(StringComparer.Ordinal)) + string.Join('|', ownedRegions.Order(StringComparer.Ordinal))
            + string.Join('|', ownedDlc.Order(StringComparer.Ordinal))
            + string.Join('|', trucks.Entries.Select(t => t.StablePlacementKey + ":" + t.TruckCode + ":" + t.Availability));
        if (!force && _upgradeTrialCatalog is not null && _upgradeTrialStamp == stamp) return _upgradeTrialCatalog;
        var catalog = await Task.Run(() => UpgradeTrialService.Prepare(workspace, trucks, enabled, ownedRegions, ownedDlc, progression.Order));
        _upgradeTrialCatalog = catalog; _upgradeTrialStamp = stamp;
        return catalog;
    }

    private async Task RandomizeUpgradesAsync(IProgress<RandomizationLogEntry> progress)
    {
        if (!new RegionProgressionStore().Load().IsFinalized)
            throw new InvalidOperationException("Finalize Region progression before randomizing upgrades.");
        var catalog = await GetUpgradeTrialCatalogAsync(false);
        progress.Report(new("Using current obtainable truck assignments and direct contract rewards; mission-only trucks are excluded."));
        var trucks = await _regionsTrucksTab.GetRandomizationCatalogAsync();
        var obtainable = trucks.Entries.Where(t => t.Availability.StartsWith("Obtainable", StringComparison.Ordinal)).ToArray();
        progress.Report(new($"Current map-truck preview: {obtainable.Length} obtainable placements; {obtainable.Count(t => t.TruckChanged)} differ from the preserved original."));
        await Task.Run(() => UpgradeTrialService.Generate(catalog, progress));
        await _mapUpgradesTab.RefreshDraftAsync();
    }

    private async Task ApplyUpgradeChangesAsync()
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("Choose an extracted workspace on Start first.");
        await Task.Run(() => UpgradeChanges.Apply(workspace));
        _upgradeTrialCatalog = null;
        _upgradeTrialStamp = null;
        _upgradeRandomizationTab.RefreshStagingState();
        if (_startChanges is not null) await _startChanges.RefreshAsync();
    }

    private async Task StageUpgradeChangesAsync()
    {
        var catalog = await GetUpgradeTrialCatalogAsync(true);
        await Task.Run(() => UpgradeChanges.Stage(catalog));
        await _mapUpgradesTab.RefreshDraftAsync();
        _upgradeRandomizationTab.RefreshStagingState();
        if (_startChanges is not null) await _startChanges.RefreshAsync();
    }

    private async Task RandomizeMissionsAsync(MissionTruckDifficulty difficulty, IProgress<RandomizationLogEntry> progress)
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("Choose an extracted workspace on Start first.");
        await WorkspaceDataCache.PreloadOwnedAsync(workspace);
        _ownedContentTab.EnsureLoaded();
        var ownedRegions = WorkspaceDataCache.GetRegionMetadata(workspace)
            .Where(pair => pair.Value.IsBaseGame || _ownedRegionState.IsOwned(pair.Key)).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledTrucks = _ownedContentTab.GetAvailableCustomGroupingTrucks();
        var regionTiers = RegionTierListStore.Load();
        var catalog = await _regionsTrucksTab.GetRandomizationCatalogAsync();
        await Task.Run(() => MissionTruckRandomizer.Randomize(workspace, catalog, ownedRegions, enabledTrucks, regionTiers, difficulty, progress));
        await _regionsTrucksTab.RefreshChangesAsync();
    }

    private static void ExtractPak(string pakPath, string workspace)
    {
        using var archive = ZipFile.OpenRead(pakPath);
        foreach (var entry in archive.Entries)
        {
            var destination = SafeDestination(workspace, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string SafeDestination(string root, string archiveName)
    {
        var normalized = archiveName.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is ".." or ""))
            throw new InvalidDataException($"Unsafe path in PAK: {archiveName}");
        var fullRoot = Path.GetFullPath(root);
        var fullTarget = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"PAK path escapes workspace: {archiveName}");
        return fullTarget;
    }

    private static (int Added, int Replaced, int Unchanged) MergeWorkspace(
        string staging,
        string workspace,
        string mergeEdits,
        Func<List<FileConflict>, string, bool> resolveConflicts)
    {
        var pendingCopies = new List<(string Source, string Destination, bool IsReplacement)>();
        var conflicts = new List<FileConflict>();
        var unchanged = 0;

        // Resolve every conflict before changing the destination, so canceling leaves the
        // existing workspace untouched.
        foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(staging, source);
            var originalPath = WorkspaceOriginals.GetOriginalPath(workspace, relativePath);
            if (!File.Exists(originalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
                File.Copy(source, originalPath, overwrite: false);
            }
            var destination = WorkspaceOriginals.GetCurrentPath(workspace, relativePath);

            if (FilesAreEqual(source, destination))
            {
                var updatedPath = WorkspaceOriginals.GetUpdatedPath(workspace, relativePath);
                if (File.Exists(updatedPath) && FilesAreEqual(updatedPath, originalPath)) File.Delete(updatedPath);
                unchanged++;
                continue;
            }

            conflicts.Add(new FileConflict(relativePath, destination, source));
        }

        if (conflicts.Count > 0)
        {
            Directory.CreateDirectory(mergeEdits);
            if (!resolveConflicts(conflicts, mergeEdits)) throw new OperationCanceledException("Unpack canceled. The workspace was not changed.");
            foreach (var conflict in conflicts)
            {
                if (conflict.FinalPath is null) throw new InvalidOperationException($"No version was selected for {conflict.RelativePath}.");
                if (!string.Equals(conflict.FinalPath, conflict.ExistingPath, StringComparison.OrdinalIgnoreCase))
                    pendingCopies.Add((conflict.FinalPath, WorkspaceOriginals.GetUpdatedPath(workspace, conflict.RelativePath), true));
            }
        }

        foreach (var item in pendingCopies)
        {
            if (File.Exists(item.Destination) && item.Source == item.Destination) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            File.Copy(item.Source, item.Destination, overwrite: item.IsReplacement);
        }

        var updatedRoot = WorkspaceOriginals.GetUpdatedRoot(workspace);
        foreach (var updatedPath in Directory.Exists(updatedRoot) ? Directory.EnumerateFiles(updatedRoot, "*", SearchOption.AllDirectories) : Array.Empty<string>())
        {
            var relativePath = Path.GetRelativePath(updatedRoot, updatedPath);
            var originalPath = WorkspaceOriginals.GetOriginalPath(workspace, relativePath);
            if (File.Exists(originalPath) && FilesAreEqual(updatedPath, originalPath)) File.Delete(updatedPath);
        }

        return (0, pendingCopies.Count, unchanged);
    }

    private static void SeedOriginalFiles(string staging, string workspace)
    {
        var originalRoot = WorkspaceOriginals.GetOriginalRoot(workspace);
        foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, source);
            if (relative.StartsWith(".snowpaktool" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var original = WorkspaceOriginals.GetOriginalPath(workspace, relative);
            if (File.Exists(original)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(source, original);
        }
    }

    private static bool FilesAreEqual(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length) return false;

        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstBuffer = new byte[64 * 1024];
        var secondBuffer = new byte[firstBuffer.Length];
        while (true)
        {
            var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
            if (firstRead != secondRead) return false;
            if (firstRead == 0) return true;
            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead))) return false;
        }
    }

    private sealed class FileConflict(string relativePath, string existingPath, string incomingPath)
    {
        public string RelativePath { get; } = relativePath;
        public string ExistingPath { get; } = existingPath;
        public string IncomingPath { get; } = incomingPath;
        public string? FinalPath { get; set; }
    }

    private bool ResolveFileConflicts(List<FileConflict> conflicts, string mergeEdits)
    {
        bool ShowDialog()
        {
            using var dialog = new FileConflictReviewDialog(conflicts.Select(conflict =>
                new FileConflictReviewDialog.Item(conflict.RelativePath, conflict.ExistingPath, conflict.IncomingPath)).ToList(),
                mergeEdits);
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            for (var i = 0; i < conflicts.Count; i++) conflicts[i].FinalPath = dialog.Items[i].FinalPath;
            return true;
        }

        return InvokeRequired ? (bool)Invoke((Func<bool>)ShowDialog) : ShowDialog();
    }

    private static void PackWorkspace(string workspace, string output)
    {
        var oldBlock = WorkspaceOriginals.GetCurrentPath(workspace, CacheBlockFile.InitialCacheBlockName);
        if (File.Exists(oldBlock)) throw new IOException("Remove the old initial.cache_block from the workspace first. This app normally removes it after unpacking.");

        var tempBlock = Path.Combine(Path.GetTempPath(), $"snowpak-{Guid.NewGuid():N}.cache_block");
        var outputFullPath = Path.GetFullPath(output);
        var outputDirectory = Path.GetDirectoryName(outputFullPath)!;
        var tempPak = Path.Combine(outputDirectory, $".snowpak-{Guid.NewGuid():N}.tmp.pak");
        var mergedWorkspace = Path.Combine(Path.GetTempPath(), $"snowpak-merged-{Guid.NewGuid():N}");
        try
        {
            BuildMergedWorkspace(workspace, mergedWorkspace);
            var entries = CacheBlockWriter.GetFileEntries(mergedWorkspace, mixed: true)
                .Where(entry => !entry.ExternalName.StartsWith("[ps]\\.snowpaktool\\", StringComparison.OrdinalIgnoreCase)
                                && !entry.ExternalName.StartsWith("[ps_common]\\.snowpaktool\\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.InternalName).ToList();
            using (var stream = new FileStream(tempBlock, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var writer = new CacheBlockWriter(stream);
                writer.Pack(mergedWorkspace, entries);
            }
            var additional = new[] { new KeyValuePair<string, string>(CacheBlockFile.InitialCacheBlockName, tempBlock) };
            ZipPakHelper.CreatePak(mergedWorkspace, tempPak, CacheBlockFile.InitialCacheBlockDirectories.Concat(new[] { ".snowpaktool", "original_files" }), additional);
            File.Move(tempPak, outputFullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempBlock)) File.Delete(tempBlock);
            if (File.Exists(tempPak)) File.Delete(tempPak);
            if (Directory.Exists(mergedWorkspace)) Directory.Delete(mergedWorkspace, recursive: true);
        }
    }

    private static void BuildMergedWorkspace(string workspace, string mergedWorkspace)
    {
        Directory.CreateDirectory(mergedWorkspace);
        foreach (var root in new[] { WorkspaceOriginals.GetOriginalRoot(workspace), WorkspaceOriginals.GetUpdatedRoot(workspace) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, source);
                if (relative.StartsWith("originals" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".snowpaktool" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("region-maps" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("layout-backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("progression-settings.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, "originals", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = SafeDestination(mergedWorkspace, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
    }

    private void RememberPaths()
    {
        var pak = _pakPath.Text.Trim();
        if (!string.IsNullOrWhiteSpace(pak)) UserPreferences.SaveLastPakPath(pak);
        var workspace = _workspacePath.Text.Trim();
        if (!string.IsNullOrWhiteSpace(workspace)) UserPreferences.SaveLastWorkspacePath(workspace);
    }

    private void OpenWorkspace()
    {
        var workspace = _workspacePath.Text.Trim();
        if (!Directory.Exists(workspace)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{workspace}\"") { UseShellExecute = true });
    }

    private async Task<bool> RunBusyAsync(string status, Action work)
    {
        SetBusy(true);
        AppendLog(status);
        try
        {
            await Task.Run(work);
            AppendLog("Done.");
            return true;
        }
        catch (OperationCanceledException ex)
        {
            AppendLog(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        if (_workspacePath.Parent is not null) _workspacePath.Parent.Enabled = !busy;
        if (_startChanges is not null) _startChanges.Enabled = !busy;
        _loadingBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        _loadingBar.MarqueeAnimationSpeed = busy ? 30 : 0;
        _loadingBar.Value = 0;
        _unpackButton.Enabled = !busy;
        _packButton.Enabled = !busy && Directory.Exists(_workspacePath.Text.Trim());
        _openButton.Enabled = !busy && Directory.Exists(_workspacePath.Text.Trim());
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
