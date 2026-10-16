using System.Windows;

namespace P3DForge.App.Shell;

/// <summary>
/// A feature module. Every non-abstract class implementing this interface in the app assembly is
/// discovered at startup and <see cref="Install"/> is called once (ascending <see cref="Order"/>), so
/// features never need to edit shared startup code. Register commands, panels, toolbar items here.
/// </summary>
public interface IFeature
{
    int Order => 100;
    void Install();
}

public enum PanelLocation
{
    Left,
    Right,
    Bottom,
}

/// <summary>A dockable panel (AvalonDock anchorable).</summary>
public sealed class PanelDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public PanelLocation Location { get; init; } = PanelLocation.Left;
    /// <summary>Lower first inside its dock.</summary>
    public int Order { get; init; } = 100;
    /// <summary>Creates the panel content (called once, on the UI thread).</summary>
    public required Func<FrameworkElement> Create { get; init; }
    /// <summary>Hidden until the user opens it (Window menu / focus command).</summary>
    public bool StartHidden { get; init; }
    /// <summary>Opens as a tab in the pane of this panel id (same dock side) instead of in a pane of its own.</summary>
    public string? TabWith { get; init; }
}

/// <summary>A toolbar button bound to a command.</summary>
public sealed class ToolbarItem
{
    public required string Command { get; init; }
    /// <summary>Icon glyph from the "Segoe Fluent Icons" font (e.g. "\uE74E" = Save), see https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font.</summary>
    public required string Glyph { get; init; }
    public required string Group { get; init; }
    public int Order { get; init; } = 100;
    public string? Tooltip { get; init; }
}

public static class PanelRegistry
{
    static readonly List<PanelDescriptor> list = [];
    public static IReadOnlyList<PanelDescriptor> All => list;

    /// <summary>Raised when a panel should be shown/activated (Window menu, hotkeys).</summary>
    public static event Action<string>? FocusRequested;

    public static void Register(PanelDescriptor p)
    {
        list.RemoveAll(x => x.Id == p.Id);
        list.Add(p);
    }

    public static void Focus(string id) => FocusRequested?.Invoke(id);
}

public static class Toolbar
{
    static readonly List<ToolbarItem> items = [];
    public static IReadOnlyList<ToolbarItem> Items => items;
    public static void Add(ToolbarItem item) => items.Add(item);
}

/// <summary>Providers for dynamic menu entries (e.g. the recent files list).</summary>
public static class DynamicMenus
{
    static readonly Dictionary<string, Func<IEnumerable<(string Label, Action Action)>>> providers = [];
    public static void Register(string id, Func<IEnumerable<(string Label, Action Action)>> provider) => providers[id] = provider;

    public static IEnumerable<(string Label, Action Action)> Get(string id)
    {
        try
        {
            return providers.TryGetValue(id, out var p) ? p().ToList() : [];
        }
        catch
        {
            return [];
        }
    }
}
