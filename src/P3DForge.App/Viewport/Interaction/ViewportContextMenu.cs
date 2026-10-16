using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>RMB click (no drag) in a view: context menu of the registered editing commands (missing ids are skipped).</summary>
static class ViewportContextMenu
{
    static readonly string[][] Groups =
    [
        ["edit.undo", "edit.redo"],
        ["edit.cut", "edit.copy", "edit.paste", "edit.delete"],
        ["edit.hideSelection", "edit.unhideAll", "edit.lockSelection", "edit.unlockAll"],
        ["transform.pinToSelection", "transform.usePin", "view.frameSelection"],
        ["surface.setTexture", "surface.setMaterial", "material.editSelected", "uv.editor"],
        ["anim.animateSelection", "tools.animations"],
        ["faces.properties", "points.properties"],
        ["app.commandPalette"],
    ];

    /// <summary>Command ids the menu would show now (registered ones only), "-" between groups.</summary>
    public static List<string> Items()
    {
        var list = new List<string>();
        foreach (var g in Groups)
        {
            var ids = g.Where(id => Commands.Get(id) is not null).ToList();
            if (ids.Count == 0) continue;
            if (list.Count > 0) list.Add("-");
            list.AddRange(ids);
        }
        return list;
    }

    public static void Show(IViewportContext ctx, IViewHandle view, Point p)
    {
        var items = Items();
        if (items.Count == 0) return;
        if (Dlg.Automated)
        {
            AppServices.Editor.Log("[automation] context menu: " + string.Join(" ", items));
            return;
        }
        var menu = new ContextMenu();
        foreach (var id in items)
        {
            if (id == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var cmd = Commands.Get(id)!;
            var mi = new MenuItem
            {
                Header = Commands.MenuText(cmd.Label),
                InputGestureText = cmd.Hotkeys.FirstOrDefault() ?? "",
                IsEnabled = Commands.IsEnabled(id),
            };
            if (cmd.IsChecked is not null)
            {
                mi.IsCheckable = true;
                mi.IsChecked = Commands.IsChecked(id);
            }
            mi.Click += (_, _) => Commands.Run(id);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = ctx.InputElement;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = view.Rect.X + p.X;
        menu.VerticalOffset = view.Rect.Y + p.Y;
        menu.IsOpen = true;
    }
}
