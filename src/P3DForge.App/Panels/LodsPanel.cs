using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.Core;
using P3DForge.Core.Model;

namespace P3DForge.App.Panels;

/// <summary>
/// LOD list (Object Builder "LODs" window): category glyph, name, point/face counts; click activates, double-click
/// opens the LOD properties, context menu with New/Duplicate/Delete/Show at Background/Properties….
/// </summary>
public sealed class LodsPanel : ListBox
{
    static EditorState Ed => AppServices.Editor;
    bool syncing;
    P3DModel? shownModel;
    int shownBackground = -2;

    public LodsPanel()
    {
        ItemContainerStyle = MakeItemStyle();
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        SelectionChanged += (_, _) =>
        {
            if (!syncing && SelectedIndex >= 0) Ed.SetActiveLod(SelectedIndex);
        };
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => BuildMenu(ContextMenu);
        PanelKeys.Register(this, OnKey);
        Ed.Changed += c =>
        {
            if ((c & (Change.Model | Change.ActiveLod | Change.Document | Change.Ui)) != 0) Refresh();
        };
        Refresh();
    }

    Style MakeItemStyle()
    {
        var s = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource("CompactListBoxItem"));
        s.Setters.Add(new EventSetter(MouseDoubleClickEvent, new MouseButtonEventHandler((sender, e) =>
        {
            if (sender is ListBoxItem { Tag: int index })
            {
                e.Handled = true;
                Ed.SetActiveLod(index);
                Commands.Run("lod.properties");
            }
        })));
        return s;
    }

    static (string Glyph, string Brush) GlyphOf(float resolution)
    {
        var (type, _) = LodNames.TypeOf(resolution);
        return type.Category switch
        {
            LodCategory.Visual => ("", "Editor.Accent"), // View (eye)
            LodCategory.View => ("", "Editor.Dim"), // Camera
            LodCategory.Shadow => ("", "Editor.Faint"), // moon
            LodCategory.Geometry => ("", "Editor.Ok"), // cube
            LodCategory.Edit => ("", "Editor.Dim"), // pencil
            _ => type.Id == "unknown" ? ("", "Editor.Error") : ("", "Editor.Warn"), // pin (memory, paths, ...)
        };
    }

    void Refresh()
    {
        var model = Ed.Model;
        int bg = LodFeature.EffectiveBackgroundLod;
        if (!ReferenceEquals(model, shownModel) || bg != shownBackground)
        {
            shownModel = model;
            shownBackground = bg;
            syncing = true;
            Items.Clear();
            var mono = (FontFamily)Application.Current.FindResource("MonoFont");
            var iconFont = (FontFamily)Application.Current.FindResource("IconFont");
            for (int i = 0; i < model.Lods.Length; i++)
            {
                var lod = model.Lods[i];
                var (glyph, brush) = GlyphOf(lod.Resolution);
                var (type, _) = LodNames.TypeOf(lod.Resolution);
                var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0) };
                var icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = iconFont,
                    FontSize = 13,
                    Width = 20,
                    Foreground = (Brush)Application.Current.FindResource(brush),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                var counts = new TextBlock
                {
                    Text = $"{lod.PointCount,7:N0} {lod.FaceCount,7:N0}",
                    FontFamily = mono,
                    Foreground = (Brush)Application.Current.FindResource("Editor.Dim"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(counts, Dock.Right);
                row.Children.Add(counts);
                if (i == bg)
                {
                    var bgMark = new TextBlock
                    {
                        Text = "",
                        FontFamily = iconFont,
                        FontSize = 12,
                        Margin = new Thickness(4, 0, 6, 0),
                        Foreground = (Brush)Application.Current.FindResource("Editor.Selection"),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Shown at background",
                    };
                    DockPanel.SetDock(bgMark, Dock.Right);
                    row.Children.Add(bgMark);
                }
                row.Children.Add(new TextBlock { Text = LodNames.Name(lod.Resolution), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                int sels = 0, props = 0;
                foreach (var t in lod.Tags)
                    if (t is SelectionTag) sels++;
                    else if (t is PropertyTag) props++;
                Items.Add(new ListBoxItem
                {
                    Content = row,
                    Tag = i,
                    ToolTip = $"{LodNames.Name(lod.Resolution)} — {type.Name} ({type.Category})\nresolution {((double)lod.Resolution).ToString("G9", System.Globalization.CultureInfo.InvariantCulture)}\n" +
                              $"{lod.PointCount:N0} points, {lod.FaceCount:N0} faces, {sels} named selections, {props} properties",
                });
            }
            syncing = false;
        }
        if (SelectedIndex != Ed.ActiveLod)
        {
            syncing = true;
            SelectedIndex = Ed.ActiveLod;
            syncing = false;
        }
    }

    bool OnKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (key)
        {
            case Key.Delete:
                Commands.Run("lod.delete");
                return true;
            case Key.F2 or Key.Enter:
                Commands.Run("lod.properties");
                return true;
            case Key.Up:
                Ed.SetActiveLod(Math.Max(0, Ed.ActiveLod - 1));
                return true;
            case Key.Down:
                Ed.SetActiveLod(Math.Min(Ed.Model.Lods.Length - 1, Ed.ActiveLod + 1));
                return true;
            case Key.Home:
                Ed.SetActiveLod(0);
                return true;
            case Key.End:
                Ed.SetActiveLod(Ed.Model.Lods.Length - 1);
                return true;
        }
        return false;
    }

    static MenuItem Cmd(string id, string? header = null, string? glyph = null)
    {
        var c = Commands.Get(id);
        var mi = new MenuItem
        {
            Header = header ?? c?.Label ?? id,
            IsEnabled = Commands.IsEnabled(id),
            InputGestureText = c?.Hotkeys.FirstOrDefault() ?? "",
        };
        if (glyph is not null) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 13 };
        if (c?.IsChecked is not null)
        {
            mi.IsCheckable = true;
            mi.IsChecked = Commands.IsChecked(id);
        }
        mi.Click += (_, _) => Commands.Run(id);
        return mi;
    }

    static void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Cmd("lod.new", "_New…", ""));
        menu.Items.Add(Cmd("lod.duplicate", "D_uplicate", ""));
        menu.Items.Add(Cmd("lod.delete", "_Delete", ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("lod.showAtBackground", "Show at _Background"));
        menu.Items.Add(Cmd("lod.copySelectionTo", "_Copy Selection to LODs…"));
        menu.Items.Add(Cmd("lod.sort", "_Sort LODs"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("lod.properties", "_Properties…", ""));
    }
}
