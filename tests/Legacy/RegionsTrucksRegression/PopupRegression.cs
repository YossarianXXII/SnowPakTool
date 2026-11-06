using System.Reflection;
using SnowPakTool.Desktop;

internal static class PopupRegression
{
    internal static void Run()
    {
        using var root = new Panel { Size = new Size(600, 400) };
        using var outside = new Button { Text = "Outside" };
        root.Controls.Add(outside);
        root.CreateControl();
        var lastQuery = "";
        var confirmed = "";
        var canFind = true;
        using var search = new QuickSearchOverlay(root, query => { confirmed = query; return canFind; }, query => { lastQuery = query; return 1; });
        var input = Field<TextBox>(search, "_input");
        var popup = Field<Panel>(search, "_overlay");

        Open(); input.Text = "x";
        var escape = Message.Create(outside.Handle, 0x100, (nint)Keys.Escape, 0);
        Check(search.PreFilterMessage(ref escape) && !IsOpen() && !popup.Visible && lastQuery == "", "Esc closes the popup even when targeted at another control");
        Open();
        var insideClick = Message.Create(input.Handle, 0x201, 0, 0);
        Check(!search.PreFilterMessage(ref insideClick) && IsOpen(), "Click inside the popup keeps it open");
        input.Text = "any string";
        var outsideClick = Message.Create(outside.Handle, 0x201, 0, 0);
        Check(!search.PreFilterMessage(ref outsideClick) && !IsOpen() && lastQuery == "" && outsideClick.HWnd == outside.Handle,
            "Outside click dismisses search without consuming the click");
        Open();
        var titleClick = Message.Create(root.Handle, 0xA1, 0, 0);
        Check(!search.PreFilterMessage(ref titleClick) && !IsOpen(), "Nonclient click dismisses popup");
        Open(); input.Text = "a";
        Invoke("Confirm");
        Check(IsOpen() && confirmed == "" && lastQuery == "", "Short searches are neither run nor confirmed");
        input.Text = "abc"; Invoke("Confirm");
        Check(!IsOpen() && confirmed == "abc", "Three-character searches can be confirmed");
        canFind = false;
        Open(); input.Text = "no matches"; Invoke("Confirm");
        Check(IsOpen() && Field<Label>(search, "_hint").Text.Contains("text"), "Missing results reopen with a generic text message");
        Invoke("HostDeactivated", null, EventArgs.Empty);
        Check(!IsOpen() && lastQuery == "", "Window deactivation dismisses popup");
        Open(); root.Visible = false;
        Check(!IsOpen(), "Hiding the search page closes the popup");
        Console.WriteLine("PASS: popup dismissal and the shared three-character minimum.");

        bool IsOpen() => Field<bool>(search, "_isOpen");
        void Open() => Invoke("Open");
        void Invoke(string name, params object?[] args) => typeof(QuickSearchOverlay).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(search, args);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
