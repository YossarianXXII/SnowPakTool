using System.Text.Json;

namespace SnowPakTool.Desktop;

using System.Text.RegularExpressions;

/// <summary>Custom truck grouping with image tiles.</summary>
internal sealed class CustomGroupWipTab : UserControl
{
    private const int TileWidth = 150;
    private const int TileHeight = 210;
    private readonly Func<IReadOnlyList<CustomTruck>> _getAvailableTrucks;
    private readonly List<GroupState> _states;
    private readonly bool _tierList;
    private readonly List<GroupView> _groupViews = [];
    private string? _lastTruckSignature;
    private readonly HashSet<string> _availableTruckCodes = new(StringComparer.OrdinalIgnoreCase);
    private TileFlowPanel? _unsortedTiles;
    private ColumnStyle? _unsortedColumnStyle;
private FlowLayoutPanel? _groupList;
    private readonly Label _status = new() { AutoSize = true, Text = "Load a workspace to list trucks." };

    public CustomGroupWipTab(Func<IReadOnlyList<CustomTruck>> getAvailableTrucks, bool tierList = false)
    {
        _tierList = tierList;
        _states = tierList ? TierListStore.Load() : CustomGroupStore.Load();
        _getAvailableTrucks = getAvailableTrucks;
        BuildLayout();
        var wheelFilter = new DirectionalWheelFilter(this);
        Application.AddMessageFilter(wheelFilter);
        Disposed += (_, _) => Application.RemoveMessageFilter(wheelFilter);
        var search = new QuickSearchOverlay(this, FindTruck, SearchTrucks);
        Disposed += (_, _) => search.Dispose();
        RefreshOwnedTrucks();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 3 };
        var unsortedColumn = new ColumnStyle(SizeType.Absolute, 525);
        layout.ColumnStyles.Add(unsortedColumn);
        _unsortedColumnStyle = unsortedColumn;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new Label
        {
            Text = _tierList ? "Drag trucks into tiers or back to Unsorted." : "Here you can make custom classes. These can be used instead of the games classes in some customization.",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold),
            Padding = new Padding(4, 4, 4, 10)
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(4, 0, 4, 8) };
        var addGroup = new Button { Text = "Add group", AutoSize = true };
        var saveGroups = new Button { Text = _tierList ? "Save tiers" : "Save groups", AutoSize = true };
        addGroup.Click += (_, _) => AddGroup();
        saveGroups.Click += (_, _) => SaveGroups();
        if (!_tierList) toolbar.Controls.Add(addGroup);
        toolbar.Controls.Add(saveGroups);
        toolbar.Controls.Add(_status);
        layout.Controls.Add(toolbar, 0, 1);
        layout.SetColumnSpan(toolbar, 2);

        var groupList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = _tierList ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2)
        };
        groupList.SizeChanged += (_, _) => ResizeGroupPanes(groupList);
        _groupList = groupList;

        var allTrucksPanel = CreateTilePanel("Unsorted", "Drag trucks into groups or back here to remove them from a group.");
        _unsortedTiles = allTrucksPanel.Tiles;
        _unsortedTiles.SizeChanged += (_, _) => UpdateUnsortedPaneWidth();
        allTrucksPanel.Tiles.AllowDrop = true;
        allTrucksPanel.Tiles.DragEnter += (_, e) => e.Effect = e.Data?.GetData(typeof(Panel)) is Panel ? DragDropEffects.Move : DragDropEffects.None;
        allTrucksPanel.Tiles.DragOver += (_, e) => UpdateDropPreview(allTrucksPanel.Tiles, groupList, allTrucksPanel.Tiles, e);
        allTrucksPanel.Tiles.DragLeave += (_, _) => ClearDropPreviewIfOutside(allTrucksPanel.Tiles);
        allTrucksPanel.Tiles.DragDrop += (_, e) => CompleteTileDrop(allTrucksPanel.Tiles, e);
        layout.Controls.Add(allTrucksPanel.Panel, 0, 2);

        foreach (var state in _states)
            AddGroupPane(state, groupList);

        layout.Controls.Add(groupList, 1, 2);
        ResizeGroupPanes(groupList);
        Controls.Add(layout);
    }

    private void AddGroupPane(GroupState state, FlowLayoutPanel groupList)
    {
        var section = CreateTilePanel(state.Name, state.Description, isGroup: true);
        section.Panel.Dock = DockStyle.None;
        section.Panel.Size = new Size(380, 350);

        var commands = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var moveLeft = new Button { Text = "◀", Width = 27, Height = 24, Padding = Padding.Empty, Margin = new Padding(1, 0, 1, 0), AccessibleName = "Move group left" };
        var moveRight = new Button { Text = "▶", Width = 27, Height = 24, Padding = Padding.Empty, Margin = new Padding(1, 0, 1, 0), AccessibleName = "Move group right" };
        var edit = new Button { Text = "Edit", AutoSize = true, Height = 24, Margin = new Padding(2, 0, 0, 0) };
        var remove = new Button { Text = "Remove", AutoSize = true, Height = 24, Margin = new Padding(2, 0, 0, 0) };
        moveLeft.Click += (_, _) => MoveGroup(state, -1);
        moveRight.Click += (_, _) => MoveGroup(state, 1);
        edit.Click += (_, _) => EditGroup(state, section.Panel, section.Description);
        remove.Click += (_, _) => RemoveGroup(state);
        commands.Controls.Add(moveLeft);
        commands.Controls.Add(moveRight);
        commands.Controls.Add(edit);
        commands.Controls.Add(remove);
        if (!_tierList) section.Header.Controls.Add(commands, 1, 0);
        else
        {
            section.Tiles.WrapContents = false;
            section.Tiles.Margin = Padding.Empty;
            section.Tiles.Padding = Padding.Empty;
            section.Panel.Text = state.Description;
            var content = (TableLayoutPanel)section.Header.Parent!;
            content.Controls.Remove(section.Header);
            content.Controls.Remove(section.Tiles);
            content.ColumnCount = 2;
            content.ColumnStyles.Clear();
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowCount = 1;
            content.RowStyles.Clear();
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tierHeader = new Label
            {
                Text = state.Name, Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 18f, FontStyle.Bold)
            };
            content.Controls.Add(tierHeader, 0, 0);
            content.Controls.Add(section.Tiles, 1, 0);
            section.Panel.Disposed += (_, _) => section.Header.Dispose();
            section.Header.Visible = false;
            void ApplyTint()
            {
                var c = TierListStore.ColorFor(state.Name);
                var b = AppColors.Get("Surface");
                section.Panel.BackColor = Color.FromArgb((b.R * 4 + c.R) / 5, (b.G * 4 + c.G) / 5, (b.B * 4 + c.B) / 5);
                tierHeader.BackColor = Color.FromArgb((b.R + c.R * 2) / 3, (b.G + c.G * 2) / 3, (b.B + c.B * 2) / 3);
                tierHeader.ForeColor = tierHeader.BackColor.GetBrightness() < 0.45f ? Color.White : Color.Black;
            }
            ApplyTint();
            EventHandler changed = (_, _) => ApplyTint();
            AppColors.Changed += changed;
            section.Panel.Disposed += (_, _) => AppColors.Changed -= changed;
        }

        AttachPaneDropTarget(section.Panel, section.Tiles, groupList);
        section.Tiles.AllowDrop = true;
        section.Tiles.DragEnter += (_, e) => e.Effect = e.Data?.GetData(typeof(Panel)) is Panel ? DragDropEffects.Move : DragDropEffects.None;
        section.Tiles.DragOver += (_, e) => UpdateDropPreview(section.Tiles, groupList, section.Tiles, e);
        section.Tiles.DragLeave += (_, _) => ClearDropPreviewIfOutside(section.Tiles);
        section.Tiles.DragDrop += (_, e) => CompleteTileDrop(section.Tiles, e);

        var view = new GroupView(state, section.Panel, section.Tiles, section.Description, moveLeft, moveRight);
        _groupViews.Add(view);
        groupList.Controls.Add(section.Panel);
        groupList.PerformLayout();
        ResizeGroupPanes(groupList);
        UpdateGroupMoveButtons();
    }

    private void MoveGroup(GroupState state, int direction)
    {
        var currentIndex = _states.IndexOf(state);
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _states.Count)
            return;

        _states.RemoveAt(currentIndex);
        _states.Insert(targetIndex, state);
        var view = _groupViews.Single(item => ReferenceEquals(item.State, state));
        _groupViews.Remove(view);
        _groupViews.Insert(targetIndex, view);
        _groupList!.Controls.SetChildIndex(view.Panel, targetIndex);
        _groupList.PerformLayout();
        UpdateGroupMoveButtons();
        SaveGroups();
    }

    private void UpdateGroupMoveButtons()
    {
        for (var i = 0; i < _groupViews.Count; i++)
        {
            _groupViews[i].MoveLeft.Enabled = i > 0;
            _groupViews[i].MoveRight.Enabled = i < _groupViews.Count - 1;
        }
    }
    public void RefreshOwnedTrucks()
    {
        if (_unsortedTiles is null || IsDisposed)
            return;

        var trucks = _getAvailableTrucks();
        var signature = string.Join("\u001f", trucks
            .OrderBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase)
            .Select(truck => $"{truck.Code}\u001e{truck.Name}\u001e{truck.Class}"));
        if (string.Equals(_lastTruckSignature, signature, StringComparison.Ordinal))
            return;
        _lastTruckSignature = signature;
        var truckByCode = trucks.ToDictionary(truck => truck.Code, StringComparer.OrdinalIgnoreCase);
        _availableTruckCodes.Clear();
        _availableTruckCodes.UnionWith(truckByCode.Keys);
        var groupedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in _groupViews)
        {
            foreach (Control tile in view.Tiles.Controls.Cast<Control>().ToArray())
            {
                view.Tiles.Controls.Remove(tile);
                tile.Dispose();
            }

            view.State.TruckCodes = view.State.TruckCodes
                .Where(code => groupedCodes.Add(code))
                .ToList();
            foreach (var code in view.State.TruckCodes)
                if (truckByCode.TryGetValue(code, out var truck))
                    AddTileTo(view.Tiles, truck);
        }

        foreach (Control tile in _unsortedTiles.Controls.Cast<Control>().ToArray())
        {
            _unsortedTiles.Controls.Remove(tile);
            tile.Dispose();
        }
        foreach (var truck in trucks
                     .Where(truck => !groupedCodes.Contains(truck.Code))
                     .OrderBy(truck => truck.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AddTileTo(_unsortedTiles, truck);
        }
        UpdateUnsortedPaneWidth();
        _status.Text = $"Loaded {trucks.Count} available trucks. Changes save automatically.";
    }

    private void AddTileTo(TileFlowPanel panel, CustomTruck truck)
    {
        var tile = CreateTile(truck);
        AttachTileDragHandlers(tile, tile, _groupList);
        AttachTileDropTargets(tile, _groupList);
        panel.Controls.Add(tile);
    }

    private void AddGroup()
    {
        using var dialog = new GroupNameDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.GroupName))
            return;

        var name = dialog.GroupName.Trim();
        if (_states.Any(group => string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            MessageBox.Show(this, "A group with that name already exists.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var state = new GroupState { Name = name, Description = dialog.Description.Trim() };
        _states.Add(state);
        AddGroupPane(state, _groupList!);
        _lastTruckSignature = null;
        RefreshOwnedTrucks();
        SaveGroups();
    }

    private void EditGroup(GroupState state, GroupBox panel, Label description)
    {
        using var dialog = new GroupNameDialog(state.Name, state.Description);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.GroupName))
            return;

        var name = dialog.GroupName.Trim();
        if (_states.Any(group => !ReferenceEquals(group, state) &&
                                 string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            MessageBox.Show(this, "A group with that name already exists.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        state.Name = name;
        state.Description = dialog.Description.Trim();
        panel.Text = state.Name;
        description.Text = state.Description;
        SaveGroups();
    }

    private void RemoveGroup(GroupState state)
    {
        if (state.TruckCodes.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"This group contains {state.TruckCodes.Count} truck(s). Remove it and move them to Unsorted?",
                "Remove custom group",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _states.Remove(state);
        var view = _groupViews.Single(item => ReferenceEquals(item.State, state));
        _groupViews.Remove(view);
        _groupList!.Controls.Remove(view.Panel);
        view.Panel.Dispose();
        _lastTruckSignature = null;
        RefreshOwnedTrucks();
        SaveGroups();
    }

    private void SyncGroupAssignments()
    {
        foreach (var view in _groupViews)
        {
            // Keep assignments for trucks hidden by ownership/workspace filtering.
            var unavailable = view.State.TruckCodes.Where(code => !_availableTruckCodes.Contains(code)).ToArray();
            view.State.TruckCodes = view.Tiles.Controls.Cast<Control>()
                .Select(tile => tile.Tag as string)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .Concat(unavailable)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        SaveGroups();
    }

    private void SaveGroups()
    {
        try
        {
            if (_tierList) TierListStore.Save(_states);
            else CustomGroupStore.Save(_states);
            _status.Text = _tierList ? "Tiers saved." : "Custom groups saved.";

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Save failed: {ex.Message}";
            MessageBox.Show(this, $"Could not save {(_tierList ? "tiers" : "custom groups")}: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateUnsortedPaneWidth()
    {
        if (_unsortedTiles is null || _unsortedColumnStyle is null)
            return;

        const int tileStride = TileWidth + 1;
        const int fixedPaneSpacing = 57;
        const int defaultColumns = 3;
        var rowsThatFit = Math.Max(1, _unsortedTiles.ClientSize.Height / (TileHeight + 1));
        var tileCount = _unsortedTiles.Controls.Count;
        var columns = tileCount <= rowsThatFit ? 1
            : tileCount <= rowsThatFit * 2 ? 2
            : defaultColumns;
        var desiredWidth = columns * tileStride + fixedPaneSpacing;
        if (Math.Abs(_unsortedColumnStyle.Width - desiredWidth) < 0.5)
            return;

        _unsortedColumnStyle.Width = desiredWidth;
    }
    private void ResizeGroupPanes(FlowLayoutPanel groupList)
    {
        foreach (Control pane in groupList.Controls)
        {
            if (_tierList)
            {
                pane.Height = TileHeight + 1 + SystemInformation.HorizontalScrollBarHeight + 42;
                pane.Width = Math.Max(180, groupList.ClientSize.Width - groupList.Padding.Horizontal - pane.Margin.Horizontal - SystemInformation.VerticalScrollBarWidth);
            }
            else pane.Height = Math.Max(1, groupList.ClientSize.Height - groupList.Padding.Vertical - pane.Margin.Vertical);
        }
    }

    private (GroupBox Panel, TileFlowPanel Tiles, Label Description, TableLayoutPanel Header) CreateTilePanel(string title, string subtitle, bool isGroup = false)
    {
        var panel = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(7), Margin = new Padding(5) };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(2) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var description = new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText
        };
        header.Controls.Add(description, 0, 0);
        var tiles = new TileFlowPanel(isGroup && !_tierList)
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(2)
        };
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(tiles, 0, 1);
        panel.Controls.Add(content);
        return (panel, tiles, description, header);
    }

    private static void AttachTileDragHandlers(Control control, Panel tile, FlowLayoutPanel? groupList)
    {
        Point mouseDown = Point.Empty;
        control.MouseDown += (_, e) => mouseDown = e.Location;
        control.MouseMove += (_, e) =>
        {
            if ((e.Button & MouseButtons.Left) == 0 ||
                Math.Abs(e.X - mouseDown.X) < SystemInformation.DragSize.Width / 2 &&
                Math.Abs(e.Y - mouseDown.Y) < SystemInformation.DragSize.Height / 2)
                return;

            using var preview = new DragPreviewForm(tile);
            tile.Visible = false;
            try
            {
                preview.Show();
                preview.FollowCursor();
                tile.DoDragDrop(tile, DragDropEffects.Move);
            }
            finally
            {
                preview.Close();
                tile.Visible = true;
                (tile.Parent as TileFlowPanel)?.ClearDropIndex();
                if (groupList is not null)
                    ClearDropMarkers(groupList);
            }
        };

        foreach (Control child in control.Controls)
            AttachTileDragHandlers(child, tile, groupList);
    }

    private void AttachTileDropTargets(Control control, FlowLayoutPanel? groupList)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = e.Data?.GetData(typeof(Panel)) is Panel
            ? DragDropEffects.Move
            : DragDropEffects.None;
        control.DragOver += (_, e) =>
        {
            if (FindTilePanel(control) is { } target)
                UpdateDropPreview(target, groupList, control, e);
        };
        control.DragLeave += (_, _) => ClearDropPreviewIfOutside(FindTilePanel(control));
        control.DragDrop += (_, e) =>
        {
            if (FindTilePanel(control) is { } target)
                CompleteTileDrop(target, e);
        };

        foreach (Control child in control.Controls)
            AttachTileDropTargets(child, groupList);
    }

    private void AttachPaneDropTarget(Control control, TileFlowPanel target, FlowLayoutPanel groupList)
    {
        if (control is TileFlowPanel)
            return;

        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = e.Data?.GetData(typeof(Panel)) is Panel
            ? DragDropEffects.Move
            : DragDropEffects.None;
        control.DragOver += (_, e) => UpdateDropPreview(target, groupList, control, e);
        control.DragLeave += (_, _) => ClearDropPreviewIfOutside(target);
        control.DragDrop += (_, e) => CompleteTileDrop(target, e);

        foreach (Control child in control.Controls)
            AttachPaneDropTarget(child, target, groupList);
    }

    private static TileFlowPanel? FindTilePanel(Control control)
    {
        for (var current = control.Parent; current is not null; current = current.Parent)
            if (current is TileFlowPanel tiles && tiles.AllowDrop)
                return tiles;
        return null;
    }

    private static void ClearDropPreviewIfOutside(TileFlowPanel? target)
    {
        if (target is not null && !target.RectangleToScreen(target.ClientRectangle).Contains(Cursor.Position))
            target.ClearDropIndex();
    }

    private static void ClearDropMarkers(Control parent)
    {
        if (parent is TileFlowPanel tiles)
            tiles.ClearDropIndex();
        foreach (Control child in parent.Controls)
            ClearDropMarkers(child);
    }

    private static void UpdateDropPreview(TileFlowPanel target, FlowLayoutPanel? groupList, Control eventSource, DragEventArgs e)
    {
        e.Effect = e.Data?.GetData(typeof(Panel)) is Panel ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effect == DragDropEffects.None)
        {
            target.ClearDropIndex();
            return;
        }

        // DragEventArgs coordinates are already in screen space.
        var screenPoint = new Point(e.X, e.Y);
        if (groupList is not null)
        {
            var listPoint = groupList.PointToClient(screenPoint);
            const int edgeSize = 72;
            var scroll = groupList.HorizontalScroll;
            var maxValue = Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1);
            if (listPoint.X >= 0 && listPoint.X <= groupList.ClientSize.Width &&
                listPoint.X < edgeSize && scroll.Value > scroll.Minimum)
            {
                scroll.Value = Math.Max(scroll.Minimum, scroll.Value - 6);
                groupList.PerformLayout();
            }
            else if (listPoint.X >= 0 && listPoint.X <= groupList.ClientSize.Width &&
                     listPoint.X > groupList.ClientSize.Width - edgeSize && scroll.Value < maxValue)
            {
                scroll.Value = Math.Min(maxValue, scroll.Value + 6);
                groupList.PerformLayout();
            }
        }

        var movingTile = e.Data?.GetData(typeof(Panel)) as Panel;
        var position = target.GetInsertionPosition(target.PointToClient(screenPoint), movingTile);
        target.SetDropPosition(position.Index, movingTile, position.AtPreviousRowEnd);
    }

    private void CompleteTileDrop(TileFlowPanel target, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(Panel)) is not Panel tile || tile.Parent is not TileFlowPanel source)
            return;

        var insertAt = target.DropIndex;
        if (insertAt < 0)
            insertAt = target.GetInsertionPosition(target.PointToClient(new Point(e.X, e.Y)), tile).Index;

        source.Controls.Remove(tile);
        insertAt = Math.Clamp(insertAt, 0, target.Controls.Count);
        target.Controls.Add(tile);
        target.Controls.SetChildIndex(tile, insertAt);
        SyncGroupAssignments();
        source.ClearDropIndex();
        target.ClearDropIndex();
        source.PerformLayout();
        target.PerformLayout();
        UpdateUnsortedPaneWidth();
    }

    private bool FindTruck(string query) => SearchTrucks(query) > 0;

    private int SearchTrucks(string query)
    {
        query = query.Trim();
        var panes = new List<(TileFlowPanel Tiles, Control? Pane)>();
        if (_unsortedTiles is not null) panes.Add((_unsortedTiles, null));
        panes.AddRange(_groupViews.Select(view => (view.Tiles, (Control?)view.Panel)));
        (TileFlowPanel Tiles, Control? Pane, Control Tile)? first = null;
        var count = 0;
        foreach (var (tiles, pane) in panes)
        {
            foreach (var tile in tiles.Controls.OfType<SearchableTruckTile>())
            {
                var matches = query.Length >= 3 &&
                    ((tile.Tag as string)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                     tile.Controls.OfType<Label>().Any(label => label.Text.Contains(query, StringComparison.OrdinalIgnoreCase)));
                tile.SearchMatch = matches;
                if (!matches) continue;
                count++;
                first ??= (tiles, pane, tile);
            }
        }
        if (first is { } found)
        {
            if (found.Pane is not null) _groupList?.ScrollControlIntoView(found.Pane);
            found.Tiles.ScrollControlIntoView(found.Tile);
            _status.Text = $"{count} matching truck{(count == 1 ? "" : "s")}.";
        }
        return count;
    }

    private static Panel CreateTile(CustomTruck truck)
    {
        var tile = new SearchableTruckTile
        {
            Tag = truck.Code,
            Width = 150,
            Height = 210,
            Margin = new Padding(0, 0, 1, 1),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(30, 35, 40)
        };
        var image = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = TruckThumbnailProvider.PlaceholderImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(30, 35, 40)
        };
        _ = LoadTileImageAsync(image, truck);
        var name = new Label
        {
            Text = truck.Name,
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = Color.FromArgb(32, 38, 45),
            ForeColor = Color.White,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };
        var classOverlay = new Label
        {
            Text = truck.Class,
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.FromArgb(32, 38, 45),
            ForeColor = Color.White,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        tile.Controls.Add(image);
        tile.Controls.Add(name);
        tile.Controls.Add(classOverlay);
        name.BringToFront();
        classOverlay.BringToFront();
        void ApplyClassColor()
        {
            var key = truck.Class.Replace("-", "").Replace(" ", "").Replace("_", "").ToLowerInvariant() switch
            {
                "heavy" => "TruckClassHeavy",
                "heavyduty" => "TruckClassHeavyDuty",
                "offroad" => "TruckClassOffroad",
                "scout" => "TruckClassScout",
                "highway" => "TruckClassHighway",
                _ => null
            };
            var background = tile.SearchMatch ? AppColors.Get("EngineMatch") :
                key is null ? Color.FromArgb(32, 38, 45) : AppColors.Get(key);
            name.BackColor = classOverlay.BackColor = background;
            name.ForeColor = classOverlay.ForeColor = background.GetBrightness() < 0.55f ? Color.White : Color.Black;
        }
        tile.SearchMatchChanged += (_, _) => ApplyClassColor();
        ApplyClassColor();
        EventHandler colorsChanged = (_, _) => ApplyClassColor();
        AppColors.Changed += colorsChanged;
        tile.Disposed += (_, _) => AppColors.Changed -= colorsChanged;
        return tile;
    }

    private static async Task LoadTileImageAsync(PictureBox picture, CustomTruck truck)
    {
        try
        {
            var loadedImage = await TruckThumbnailProvider.LoadAsync(truck);
            if (picture.IsDisposed)
                return;
            // The await resumes on the WinForms UI context. The tile may not have a
            // native handle yet, so assigning through BeginInvoke here can drop images.
            picture.Image = loadedImage;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { }
    }


    private sealed record GroupView(GroupState State, GroupBox Panel, TileFlowPanel Tiles, Label Description, Button MoveLeft, Button MoveRight);

    private sealed class GroupNameDialog : Form
    {
        private readonly TextBox _name = new() { Dock = DockStyle.Top, Width = 280 };
        private readonly TextBox _description = new() { Dock = DockStyle.Top, Width = 280 };
        public string GroupName => _name.Text;
        public string Description => _description.Text;

        public GroupNameDialog(string initialName = "", string initialDescription = "")
        {
            Text = "Custom group";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(330, 175);
            _name.Text = initialName;
            _description.Text = initialDescription;
            var nameLabel = new Label { Text = "Name", AutoSize = true, Dock = DockStyle.Top };
            var descriptionLabel = new Label { Text = "Description (max 10 words)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 0) };
            var save = new Button { Text = "OK", AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
            save.Click += (_, _) =>
            {
                if (_description.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 10)
                {
                    MessageBox.Show(this, "Description must be 10 words or fewer.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            Controls.Add(_description);
            Controls.Add(descriptionLabel);
            Controls.Add(_name);
            Controls.Add(nameLabel);
            Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;
        }
    }

    private sealed class DragPreviewForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

        public DragPreviewForm(Control tile)
        {
            var image = new Bitmap(tile.Width, tile.Height);
            tile.DrawToBitmap(image, new Rectangle(Point.Empty, tile.Size));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.88;
            Size = tile.Size;
            BackgroundImage = image;
            BackgroundImageLayout = ImageLayout.Stretch;
            _timer.Tick += (_, _) => FollowCursor();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int WmNcHitTest = 0x0084;
            const int HtTransparent = -1;
            if (message.Msg == WmNcHitTest)
            {
                message.Result = (IntPtr)HtTransparent;
                return;
            }
            base.WndProc(ref message);
        }

        public void FollowCursor()
        {
            Location = new Point(Cursor.Position.X + 14, Cursor.Position.Y + 14);
            if (!_timer.Enabled)
                _timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                BackgroundImage?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}


internal sealed class SearchableTruckTile : Panel
{
    private bool _searchMatch;
    public event EventHandler? SearchMatchChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SearchMatch
    {
        get => _searchMatch;
        set
        {
            if (_searchMatch == value) return;
            _searchMatch = value;
            SearchMatchChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
