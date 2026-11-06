namespace SnowPakTool.Desktop;

/// <summary>A double tap of Shift opens an immediately focused text search.</summary>
internal sealed class QuickSearchOverlay : IMessageFilter, IDisposable
{
    private readonly Control _root;
    private readonly Func<string, bool> _find;
    private readonly Func<string, int> _liveSearch;
    private readonly Panel _overlay;
    private readonly TextBox _input;
    private readonly Label _hint;
    private long _lastShiftRelease = -1000;
    private bool _shiftOnly;
    private bool _isOpen;
    private Control? _previousFocus;
    private Form? _hostForm;

    public QuickSearchOverlay(Control root, Func<string, bool> find, Func<string, int> liveSearch)
    {
        _root = root;
        _find = find;
        _liveSearch = liveSearch;
        _overlay = new Panel
        {
            Size = new Size(340, 76), BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Control, Visible = false, Padding = new Padding(8)
        };
        _input = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search any text..." };
        _hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, Text = "Type 3+ characters    Esc: close",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _overlay.Controls.Add(_input);
        _overlay.Controls.Add(_hint);
        root.Controls.Add(_overlay);
        root.SizeChanged += PositionOverlay;
        root.VisibleChanged += RootVisibilityChanged;
        root.ParentChanged += HostChanged;
        root.HandleCreated += HostChanged;
        _input.TextChanged += (_, _) =>
        {
            var ready = _input.Text.Trim().Length >= CurrentTabSearch.MinimumLength;
            var count = _liveSearch(ready ? _input.Text : "");
            _hint.Text = !ready ? "Type at least 3 characters to search." :
                count == 0 ? "No matching text in the current view." :
                $"{count} match{(count == 1 ? "" : "es")}    Tab / Shift+Tab: navigate";
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = e.Handled = true;
            if (e.KeyCode == Keys.Escape) Close();
            else Confirm();
        };
        Application.AddMessageFilter(this);
    }

    public bool PreFilterMessage(ref Message message)
    {
        const int KeyDown = 0x100, KeyUp = 0x101, SystemKeyDown = 0x104;
        if (_root.IsDisposed || !_root.Visible) return false;
        if (_isOpen)
        {
            // Handle dialog keys before the form/grid consumes them, even if
            // scrolling or editing moved focus away from the textbox.
            if (message.Msg is KeyDown or SystemKeyDown && (Keys)message.WParam.ToInt32() == Keys.Escape)
            { Close(); return true; }
            if (message.Msg is KeyDown or SystemKeyDown && (Keys)message.WParam.ToInt32() == Keys.Enter && _input.ContainsFocus)
            { Confirm(); return true; }
            if (message.Msg is 0x201 or 0x204 or 0x207 or 0x20B or 0xA1 or 0xA4 or 0xA7 or 0xAB)
            {
                var target = Control.FromChildHandle(message.HWnd);
                if (target is null || (target != _overlay && !_overlay.Contains(target)))
                    Close(restoreFocus: false); // Preserve the original click and focus target.
            }
        }
        if (!(_root.ContainsFocus || _root.FindForm()?.ContainsFocus == true)) return false;
        if (message.Msg is not (KeyDown or KeyUp or SystemKeyDown)) return false;
        var key = (Keys)message.WParam.ToInt32();
        var shift = key is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey;
        if (!shift)
        {
            _shiftOnly = false;
            _lastShiftRelease = -1000;
            return false;
        }
        if (message.Msg == KeyDown)
        {
            if ((message.LParam.ToInt64() & (1L << 30)) != 0) return false;
            if (Environment.TickCount64 - _lastShiftRelease <= 350)
            {
                _lastShiftRelease = -1000;
                _shiftOnly = false;
                Open();
                return true;
            }
            _shiftOnly = true;
        }
        else if (message.Msg == KeyUp && _shiftOnly)
        {
            _lastShiftRelease = Environment.TickCount64;
            _shiftOnly = false;
        }
        return false;
    }

    private void Open()
    {
        HostChanged(null, EventArgs.Empty);
        if (!_isOpen) _previousFocus = FindFocused(_root.FindForm() ?? _root);
        _isOpen = true;
        _input.Clear();
        _hint.Text = "Type 3+ characters    Esc: close";
        PositionOverlay(null, EventArgs.Empty);
        _overlay.Show();
        _overlay.BringToFront();
        _input.Focus();
    }

    private void Close(bool restoreFocus = true)
    {
        _isOpen = false;
        _lastShiftRelease = -1000;
        _shiftOnly = false;
        _overlay.Hide();
        _liveSearch(string.Empty);
        if (restoreFocus && _previousFocus is { IsDisposed: false, CanFocus: true }) _previousFocus.Focus();
    }

    private void Confirm()
    {
        if (_input.Text.Trim().Length < CurrentTabSearch.MinimumLength) { _hint.Text = "Type at least 3 characters to search."; return; }
        var query = _input.Text;
        Close();
        if (_find(query)) return;
        Open();
        _input.Text = query;
        _input.SelectionStart = query.Length;
        _hint.Text = "No matching text in the current view.";
    }

    private void HostChanged(object? sender, EventArgs e)
    {
        var form = _root.FindForm();
        if (form == _hostForm) return;
        if (_hostForm is not null) _hostForm.Deactivate -= HostDeactivated;
        _hostForm = form;
        if (_hostForm is not null) _hostForm.Deactivate += HostDeactivated;
    }

    private void HostDeactivated(object? sender, EventArgs e) { if (_isOpen) Close(restoreFocus: false); }
    public void Dismiss() => Close(restoreFocus: false);

    private static Control? FindFocused(Control root)
    {
        if (root.Focused) return root;
        foreach (Control child in root.Controls)
            if (child.ContainsFocus) return FindFocused(child);
        return null;
    }

    private void PositionOverlay(object? sender, EventArgs e)
    {
        _overlay.Width = Math.Min(340, Math.Max(100, _root.ClientSize.Width - 24));
        _overlay.Location = new Point(Math.Max(0, (_root.ClientSize.Width - _overlay.Width) / 2), 12);
    }

    private void RootVisibilityChanged(object? sender, EventArgs e)
    {
        HostChanged(sender, e);
        if (!_root.Visible && _isOpen) Close(restoreFocus: false);
    }

    public void Dispose()
    {
        Application.RemoveMessageFilter(this);
        _root.SizeChanged -= PositionOverlay;
        _root.VisibleChanged -= RootVisibilityChanged;
        _root.ParentChanged -= HostChanged;
        _root.HandleCreated -= HostChanged;
        if (_hostForm is not null) _hostForm.Deactivate -= HostDeactivated;
        _overlay.Dispose();
    }
}
