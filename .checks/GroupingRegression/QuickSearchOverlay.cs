namespace SnowPakTool.Desktop;

/// <summary>A double tap of Shift opens an immediately focused search field.</summary>
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
    private Control? _previousFocus;

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
        _input = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search truck name, code or class..." };
        _hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, Text = "Type 3+ characters to search    Esc: close",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _overlay.Controls.Add(_input);
        _overlay.Controls.Add(_hint);
        root.Controls.Add(_overlay);
        root.SizeChanged += PositionOverlay;
        root.VisibleChanged += RootVisibilityChanged;
        _input.TextChanged += (_, _) =>
        {
            var count = _liveSearch(_input.Text);
            _hint.Text = _input.Text.Trim().Length < 3 ? "Type at least 3 characters to search." :
                count == 0 ? "No matching truck in the available panes." :
                $"{count} match{(count == 1 ? "" : "es")}    Enter: confirm    Esc: close";
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Escape) Close();
            else if (_input.Text.Trim().Length < 3) _hint.Text = "Type at least 3 characters to search.";
            else
            {
                var query = _input.Text;
                // Restore focus before scrolling so focus restoration cannot undo the result.
                Close();
                if (!_find(query))
                {
                    Open();
                    _input.Text = query;
                    _input.SelectionStart = query.Length;
                    _hint.Text = "No matching truck in the available panes.";
                }
            }
        };
        Application.AddMessageFilter(this);
    }

    public bool PreFilterMessage(ref Message message)
    {
        const int KeyDown = 0x100, KeyUp = 0x101, SystemKeyDown = 0x104;
        if (!_root.Visible || _root.IsDisposed || !(_root.ContainsFocus || _root.FindForm()?.ContainsFocus == true)) return false;
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
        if (!_overlay.Visible) _previousFocus = FindFocused(_root.FindForm() ?? _root);
        _input.Clear();
        _hint.Text = "Type 3+ characters to search    Esc: close";
        PositionOverlay(null, EventArgs.Empty);
        _overlay.Show();
        _overlay.BringToFront();
        _input.Focus();
    }

    private void Close()
    {
        _overlay.Hide();
        _liveSearch(string.Empty);
        if (_previousFocus is { IsDisposed: false, CanFocus: true }) _previousFocus.Focus();
    }

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
        if (!_root.Visible) { _overlay.Hide(); _liveSearch(string.Empty); _lastShiftRelease = -1000; _shiftOnly = false; }
    }

    public void Dispose()
    {
        Application.RemoveMessageFilter(this);
        _root.SizeChanged -= PositionOverlay;
        _root.VisibleChanged -= RootVisibilityChanged;
        _overlay.Dispose();
    }
}
