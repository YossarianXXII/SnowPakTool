using System.Runtime.CompilerServices;

namespace SnowPakTool.Desktop;

internal static class TextAreaStyle
{
    private static readonly ConditionalWeakTable<Control, object> Marked = new();

    public static void Mark(Control control)
    {
        Marked.GetValue(control, _ => new());
        Apply(control);
    }

    private static void Apply(Control control)
    {
        if (control is TextBox { Multiline: true } || Marked.TryGetValue(control, out _))
        {
            control.BackColor = AppColors.Get("TextArea");
            control.ForeColor = SystemColors.WindowText;
        }
    }

    public static void Watch(Control root)
    {
        var watched = new HashSet<Control>();
        void Added(object? sender, ControlEventArgs e) { if (e.Control is not null) Visit(e.Control); }
        void Visit(Control control)
        {
            if (!watched.Add(control)) return;
            Apply(control);
            control.ControlAdded += Added;
            foreach (Control child in control.Controls) Visit(child);
        }
        void Refresh(object? sender, EventArgs e)
        {
            foreach (var control in watched) if (!control.IsDisposed) Apply(control);
        }
        Visit(root);
        AppColors.Changed += Refresh;
        root.Disposed += (_, _) =>
        {
            AppColors.Changed -= Refresh;
            foreach (var control in watched) control.ControlAdded -= Added;
            watched.Clear();
        };
    }
}
