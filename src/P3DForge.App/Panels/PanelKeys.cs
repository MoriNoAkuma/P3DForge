using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace P3DForge.App.Panels;

/// <summary>
/// Lets a panel own a few plain keys (Delete, F2, Enter, …) while keyboard focus is inside it.
/// <para>
/// MainWindow dispatches global hotkeys from its PreviewKeyDown instance handler (tunnelling, so it runs before any
/// panel handler and would e.g. turn Delete into "delete geometry"). A class handler registered for
/// <see cref="Window"/> runs before instance handlers, so focused panels get the first chance. Text inputs inside
/// the panel are left alone.
/// </para>
/// </summary>
public static class PanelKeys
{
    static readonly List<(FrameworkElement Panel, Func<KeyEventArgs, bool> Handler)> panels = [];
    static bool registered;

    public static void Register(FrameworkElement panel, Func<KeyEventArgs, bool> handler)
    {
        panels.Add((panel, handler));
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
    }

    static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.FocusedElement is not DependencyObject focused) return;
        if (focused is TextBox or PasswordBox || focused is ComboBox { IsEditable: true } || InsideTextBox(focused)) return;
        foreach (var (panel, handler) in panels)
        {
            if (!IsDescendant(panel, focused)) continue;
            if (handler(e)) e.Handled = true;
            return;
        }
    }

    static bool InsideTextBox(DependencyObject d)
    {
        for (var p = d; p is not null; p = Parent(p))
            if (p is TextBox or PasswordBox) return true;
        return false;
    }

    static DependencyObject? Parent(DependencyObject d) =>
        (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);

    static bool IsDescendant(DependencyObject root, DependencyObject d)
    {
        for (var p = d; p is not null; p = Parent(p))
            if (ReferenceEquals(p, root)) return true;
        return false;
    }
}
