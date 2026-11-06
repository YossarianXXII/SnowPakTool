namespace SnowPakTool.Desktop;

internal sealed class RegionProgressionTab : UserControl
{
    private readonly Func<IReadOnlyList<CustomTruck>> _getRegions;
    private readonly Func<List<GroupState>> _getTiers;
    private readonly RegionProgressionStore _store;
    private RegionProgressionState _state;
    private RegionProgressionState? _previous;
    private readonly bool _loadFailed;
    private IReadOnlyList<CustomTruck> _regions = [];
    private readonly Dictionary<string, string> _tiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _randomize = new() { Text = "Randomize", AutoSize = true, Enabled = false };
    private readonly Button _restore = new() { Text = "Previous randomization", AutoSize = true, Enabled = false };
    private readonly Button _finalize = new() { Text = "Finalize", AutoSize = true, Enabled = false };
    private readonly List<RadioButton> _difficultyOptions = [];
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
    private readonly TileFlowPanel _tiles = new(false)
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(3), AllowDrop = true
    };
    private readonly System.Windows.Forms.Timer _dragScroll = new() { Interval = 60 };
    private Panel? _dragTile;

    public RegionProgressionTab(Func<IReadOnlyList<CustomTruck>> getRegions,
        RegionProgressionStore? store = null, Func<List<GroupState>>? getTiers = null)
    {
        _getRegions = getRegions;
        _getTiers = getTiers ?? RegionTierListStore.Load;
        _store = store ?? new();
        try { _state = _store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _state = new();
            _loadFailed = true;
            _status.Text = "Could not load saved progression: " + ex.Message;
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Start in the easiest ranked tier, then gradually favor harder regions with some variation. Hard brings tougher regions forward; Unsorted goes last. Drag tiles to adjust the saved order.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        toolbar.Controls.Add(new Label { Text = "Difficulty", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        foreach (var difficulty in new[] { ProgressionDifficulty.Easy, ProgressionDifficulty.Normal, ProgressionDifficulty.Hard })
        {
            var radio = new RadioButton
            {
                Text = difficulty.ToString(), AutoSize = true, Checked = _state.Difficulty == difficulty,
                Margin = new Padding(0, 5, 12, 0), Enabled = !_loadFailed
            };
            toolbar.Controls.Add(radio);
            _difficultyOptions.Add(radio);
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked || _state.Difficulty == difficulty || _state.IsFinalized) return;
                if (Commit(Copy(_state, difficulty: difficulty)))
                    _status.Text = "Difficulty saved. The current order stays unchanged until you randomize.";
                else
                    foreach (var option in toolbar.Controls.OfType<RadioButton>())
                        option.Checked = option.Text == _state.Difficulty.ToString();
            };
        }
        toolbar.Controls.Add(_randomize);
        toolbar.Controls.Add(_restore);
        toolbar.Controls.Add(_finalize);
        layout.Controls.Add(toolbar, 0, 1);
        var pane = new GroupBox { Text = "Suggested play order — left to right, then next row", Dock = DockStyle.Fill, Padding = new Padding(7) };
        pane.Controls.Add(_tiles);
        layout.Controls.Add(pane, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        Controls.Add(layout);
        _randomize.Click += (_, _) => Randomize();
        _restore.Click += (_, _) => RestorePrevious();
        _finalize.Click += (_, _) => ToggleFinalized();
        AttachDropTarget(_tiles);
        _dragScroll.Tick += (_, _) => ScrollDuringDrag();
        var wheel = new DirectionalWheelFilter(this);
        Application.AddMessageFilter(wheel);
        Disposed += (_, _) => { Application.RemoveMessageFilter(wheel); _dragScroll.Dispose(); };
        if (!_loadFailed) _status.Text = "Open an extracted workspace to list regions.";
        RefreshActions();
    }

    public void RefreshRegions()
    {
        if (IsDisposed || _loadFailed) return;
        _regions = _getRegions();
        ReadTiers();
        var order = RegionProgressionService.AppendNew(_state.Order, _regions.Select(region => region.Code));
        if (!order.SequenceEqual(_state.Order, StringComparer.OrdinalIgnoreCase))
        {
            // New regions can be displayed without persisting a changed plan.
            if (PresetBrowseMode.IsReadOnly(this)) _state = Copy(_state, order);
            else if (!Commit(Copy(_state, order))) return;
        }
        RenderTiles();
        RefreshActions();
        _status.Text = _regions.Count == 0 ? "Open an extracted workspace to list regions." :
            _state.IsFinalized ? $"{_regions.Count} regions. Finalized play order. Unlock to randomize or move tiles." :
            $"{_regions.Count} regions. {(_state.HasRandomized ? "Saved play order." : "Choose a difficulty and randomize to generate a play order.")} Changes save automatically.";
    }

    private void ReadTiers()
    {
        _tiers.Clear();
        foreach (var tier in _getTiers())
            foreach (var code in tier.TruckCodes) _tiers.TryAdd(code, tier.Name);
    }

    private void Randomize()
    {
        if (_regions.Count == 0 || _loadFailed || _state.IsFinalized) return;
        ReadTiers();
        var seed = Random.Shared.Next();
        var order = RegionProgressionService.Generate(_regions.Select(region => new ProgressionRegion(region.Code,
            RegionProgressionService.TierDifficulty(_tiers.GetValueOrDefault(region.Code, "Unsorted")))).ToList(), _state.Difficulty, seed);
        var available = _regions.Select(region => region.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        order.AddRange(_state.Order.Where(code => !available.Contains(code)));
        var candidate = Copy(_state, order);
        candidate.HasRandomized = true;
        candidate.Seed = seed;
        var previous = _state.HasRandomized ? Copy(_state) : null;
        if (!Commit(candidate)) return;
        _previous = previous;
        RefreshActions();
        RenderTiles();
        _status.Text = "New play order saved. Unsorted regions are at the end.";
    }

    private void RestorePrevious()
    {
        if (_previous is null || _state.IsFinalized) return;
        var restored = Copy(_previous, RegionProgressionService.AppendNew(_previous.Order, _state.Order), _state.Difficulty);
        if (!Commit(restored)) return;
        _previous = null;
        _restore.Enabled = false;
        RenderTiles();
        _status.Text = "Previous play order restored and saved.";
    }

    private void ToggleFinalized()
    {
        if (_loadFailed || (!_state.IsFinalized && _regions.Count == 0)) return;
        var candidate = Copy(_state);
        candidate.IsFinalized = !candidate.IsFinalized;
        if (!Commit(candidate)) return;
        StopDrag();
        RefreshActions();
        _status.Text = _state.IsFinalized ? "Play order finalized and saved. Unlock to make changes." :
            "Play order unlocked. You can randomize, restore the previous order, or move tiles.";
    }

    private void RefreshActions()
    {
        var editable = !_loadFailed && !_state.IsFinalized && !PresetBrowseMode.IsReadOnly(this);
        _randomize.Enabled = editable && _regions.Count > 0;
        _restore.Enabled = editable && _regions.Count > 0 && _previous is not null;
        _finalize.Enabled = !_loadFailed && (_state.IsFinalized || _regions.Count > 0);
        _finalize.Text = _state.IsFinalized ? "Unlock" : "Finalize";
        foreach (var option in _difficultyOptions) option.Enabled = editable;
        void SetDropEnabled(Control control)
        {
            control.AllowDrop = editable;
            foreach (Control child in control.Controls) SetDropEnabled(child);
        }
        SetDropEnabled(_tiles);
    }

    private bool Commit(RegionProgressionState candidate)
    {
        if (_loadFailed || PresetBrowseMode.IsReadOnly(this)) return false;
        try { _store.Save(candidate); _state = candidate; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status.Text = "Could not save progression: " + ex.Message;
            return false;
        }
    }

    private static RegionProgressionState Copy(RegionProgressionState state, List<string>? order = null, ProgressionDifficulty? difficulty = null) => new()
    {
        Order = order is null ? [.. state.Order] : [.. order], Difficulty = difficulty ?? state.Difficulty,
        HasRandomized = state.HasRandomized, Seed = state.Seed, IsFinalized = state.IsFinalized
    };

    private void RenderTiles()
    {
        _tiles.SuspendLayout();
        foreach (var control in _tiles.Controls.Cast<Control>().ToArray()) control.Dispose();
        var regions = _regions.ToDictionary(region => region.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var code in _state.Order)
        {
            if (!regions.TryGetValue(code, out var region)) continue;
            var tile = CustomGroupWipTab.CreateTile(region, region: true, regionTier: _tiers.GetValueOrDefault(code, "Unsorted"));
            var header = tile.Controls.OfType<Label>().First(label => label.Dock == DockStyle.Top);
            var badge = new RegionOrderBadge();
            header.Controls.Add(badge);
            void SizeBadge()
            {
                badge.Size = new Size(header.Height, header.Height);
                header.Padding = new Padding(header.Height + 4, 0, 4, 0);
            }
            header.SizeChanged += (_, _) => SizeBadge();
            SizeBadge();
            CustomGroupWipTab.AttachTileDragHandlers(tile, tile, null, () => !_state.IsFinalized && !_loadFailed);
            tile.QueryContinueDrag += (_, e) => { if (e.Action != DragAction.Continue) StopDrag(); };
            AttachDropTarget(tile);
            _tiles.Controls.Add(tile);
        }
        UpdateCaptions();
        _tiles.ResumeLayout(true);
        RefreshActions();
    }

    private void UpdateCaptions()
    {
        var regions = _regions.ToDictionary(region => region.Code, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _tiles.Controls.Count; index++)
        {
            var tile = _tiles.Controls[index];
            var region = regions[(string)tile.Tag!];
            var tier = _tiers.GetValueOrDefault(region.Code, "Unsorted");
            var header = tile.Controls.OfType<Label>().First(label => label.Dock == DockStyle.Top);
            header.Text = $"{region.Name}{Environment.NewLine}{region.Class} · {tier}";
            var badge = header.Controls.OfType<RegionOrderBadge>().Single();
            badge.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            badge.AccessibleName = $"Play position {index + 1}";
            tile.AccessibleName = $"{index + 1}. {region.Name}, {tier}";
        }
    }

    private void AttachDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => PreviewDrop(control, e);
        control.DragOver += (_, e) => PreviewDrop(control, e);
        control.DragLeave += (_, _) =>
        {
            if (!_tiles.RectangleToScreen(_tiles.ClientRectangle).Contains(Cursor.Position)) StopDrag();
        };
        control.DragDrop += (_, e) => CompleteDrop(e);
        foreach (Control child in control.Controls) AttachDropTarget(child);
    }

    private void PreviewDrop(Control source, DragEventArgs e)
    {
        if (_state.IsFinalized || e.Data?.GetData(typeof(Panel)) is not Panel tile || tile.Parent != _tiles)
        { e.Effect = DragDropEffects.None; StopDrag(); return; }
        _dragTile = tile;
        _dragScroll.Start();
        CustomGroupWipTab.UpdateDropPreview(_tiles, null, source, e);
    }

    private void CompleteDrop(DragEventArgs e)
    {
        if (_state.IsFinalized) { StopDrag(); return; }
        if (e.Data?.GetData(typeof(Panel)) is not Panel tile || tile.Parent != _tiles) return;
        var insert = _tiles.DropIndex >= 0 ? _tiles.DropIndex :
            _tiles.GetInsertionPosition(_tiles.PointToClient(new Point(e.X, e.Y)), tile).Index;
        var visible = _tiles.Controls.Cast<Control>().Select(control => (string)control.Tag!).ToList();
        var order = RegionProgressionService.Move(_state.Order, visible, (string)tile.Tag!, insert);
        if (Commit(Copy(_state, order)))
        {
            _tiles.Controls.Remove(tile);
            _tiles.Controls.Add(tile);
            _tiles.Controls.SetChildIndex(tile, Math.Clamp(insert, 0, _tiles.Controls.Count - 1));
            UpdateCaptions();
            _status.Text = "Manual play order saved.";
        }
        StopDrag();
    }

    private void ScrollDuringDrag()
    {
        if (_dragTile is null || _dragTile.IsDisposed || _dragTile.Parent != _tiles || !Visible)
        { StopDrag(); return; }
        var point = _tiles.PointToClient(Cursor.Position);
        if (!_tiles.ClientRectangle.Contains(point)) { StopDrag(); return; }
        var offset = _tiles.AutoScrollPosition;
        var maximum = Math.Max(0, _tiles.VerticalScroll.Maximum - _tiles.VerticalScroll.LargeChange + 1);
        var delta = point.Y < 48 ? -16 : point.Y > _tiles.ClientSize.Height - 48 ? 16 : 0;
        _tiles.AutoScrollPosition = new Point(-offset.X, Math.Clamp(-offset.Y + delta, 0, maximum));
        var position = _tiles.GetInsertionPosition(_tiles.PointToClient(Cursor.Position), _dragTile);
        _tiles.SetDropPosition(position.Index, _dragTile, position.AtPreviousRowEnd);
    }

    private void StopDrag() { _dragScroll.Stop(); _dragTile = null; _tiles.ClearDropIndex(); }
}

/// <summary>A numeric badge supports every position without depending on circled-number font glyphs.</summary>
internal sealed class RegionOrderBadge : Control
{
    public RegionOrderBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
        TabStop = false;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var diameter = Math.Min(Width, Height);
        if (diameter < 4) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var circle = new RectangleF(1, 1, diameter - 2, diameter - 2);
        using var fill = new SolidBrush(Color.FromArgb(42, 47, 55));
        using var outline = new Pen(Color.FromArgb(160, 170, 185));
        e.Graphics.FillEllipse(fill, circle);
        e.Graphics.DrawEllipse(outline, circle);
        using var font = new Font(Font.FontFamily, diameter * (Text.Length > 2 ? 0.38f : 0.54f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var glyph = new System.Drawing.Drawing2D.GraphicsPath();
        glyph.AddString(Text, font.FontFamily, (int)font.Style, font.Size, circle, format);
        using var weight = new Pen(Color.White, 0.65f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        e.Graphics.DrawPath(weight, glyph);
        e.Graphics.FillPath(text, glyph);
    }
}
