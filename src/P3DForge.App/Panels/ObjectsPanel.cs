using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.App.Scene;
using P3DForge.App.Services;
using P3DForge.Core;
using P3DForge.Core.Ops;
using P3DForge.Core.Scene;

namespace P3DForge.App.Panels;

/// <summary>
/// Objects window (Shift+F9): the edited model and every proxy instance of the active LOD (nested ones indented).
/// "Proxies (N)" master switch, selection box / tint switches; checkbox = visible in the viewport; click = select the
/// proxy triangle (Ctrl/Shift add, Ctrl+Shift remove); double-click = select + Center &amp; Zoom; context menu = open
/// the proxy model, show in Explorer, copy paths, show only / show all, reload.
/// </summary>
public sealed class ObjectsPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    readonly CheckBox master, boxes, tint;
    readonly StackPanel rows;
    readonly TextBlock mainName, mainInfo, emptyText, selectionText;
    readonly Border mainRow;
    readonly List<Row> rowList = [];
    bool syncing;
    bool stale = true;

    static readonly Brush SelectedBack = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x9D, 0x3B)));
    static readonly Brush HoverBack = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    sealed class Row : Border
    {
        public int Index;
        public string InstanceName = "";
        public bool IsSelected;
        public readonly CheckBox Check = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Focusable = false,
            ToolTip = "Visible in the viewport",
        };
        public readonly TextBlock Label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        public readonly TextBlock Status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(6, 0, 2, 0) };
    }

    public ObjectsPanel()
    {
        Background = Brushes.Transparent;

        var header = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
        master = Toggle("Proxies", "Draw the proxy models (the files referenced by proxy:… selections) in the viewport");
        boxes = Toggle("Selection box", "Draw a box around selected proxy models");
        tint = Toggle("Selection tint", "Tint selected proxy models");
        master.FontWeight = FontWeights.SemiBold;
        header.Children.Add(master);
        header.Children.Add(boxes);
        header.Children.Add(tint);
        var reload = new Button
        {
            Content = "",
            Style = (Style)FindResource("IconButton"),
            ToolTip = "Reload proxy models changed on disk",
            Margin = new Thickness(4, 0, 0, 0),
        };
        reload.Click += (_, _) => Commands.Run(ObjectsFeature.ReloadProxiesId);
        header.Children.Add(reload);
        // Quick choice of what to show: "None" then tick the wanted proxies, or "All" back.
        var all = new Button { Content = "Show all", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 2, 0, 2), FontSize = 12, ToolTip = "Show every proxy model" };
        var none = new Button { Content = "Hide all", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 2, 0, 2), FontSize = 12, ToolTip = "Hide every proxy model — then tick the ones you want to see" };
        all.Click += (_, _) => ProxySceneHost.ShowAll();
        none.Click += (_, _) => ProxySceneHost.HideAll();
        header.Children.Add(all);
        header.Children.Add(none);
        master.Click += (_, _) => ObjectsFeature.SetOption("enabled", master.IsChecked == true);
        boxes.Click += (_, _) => ObjectsFeature.SetOption("selectionBox", boxes.IsChecked == true);
        tint.Click += (_, _) => ObjectsFeature.SetOption("selectionTint", tint.IsChecked == true);
        SetDock(header, Dock.Top);
        Children.Add(header);

        selectionText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Editor.Dim"),
            Margin = new Thickness(8, 0, 8, 4),
        };
        SetDock(selectionText, Dock.Top);
        Children.Add(selectionText);

        // main model row
        mainName = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        mainInfo = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Editor.Dim"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Editor.Accent") };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(mainName);
        mainGrid.Children.Add(left);
        Grid.SetColumn(mainInfo, 1);
        mainGrid.Children.Add(mainInfo);
        mainRow = new Border { Child = mainGrid, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        mainRow.ContextMenu = MainMenu();
        SetDock(mainRow, Dock.Top);
        Children.Add(mainRow);

        emptyText = new TextBlock
        {
            Text = "No proxies in this LOD.\nProxies are named selections \"proxy:<path>.NNN\" on a triangle (Create ▸ Proxy…).",
            Foreground = (Brush)FindResource("Editor.Faint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(8, 6, 8, 6),
        };
        SetDock(emptyText, Dock.Top);
        Children.Add(emptyText);

        rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Children.Add(scroll);

        ProxyScene.Changed += OnSceneChanged;
        Ed.Changed += OnEditorChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Refresh();
        };
        Refresh();
    }

    static CheckBox Toggle(string text, string tip) => new()
    {
        Content = text,
        FontSize = 12,
        Margin = new Thickness(0, 2, 10, 2),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip,
        Focusable = false,
    };

    bool refreshQueued;

    /// <summary>Coalesced: many scene changes in one dispatcher turn (LOD switches, loads finishing) refresh once,
    /// after the viewport had its turn.</summary>
    void OnSceneChanged()
    {
        if (!IsVisible)
        {
            stale = true;
            return;
        }
        if (refreshQueued) return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            refreshQueued = false;
            if (IsVisible) Refresh();
            else stale = true;
        });
    }

    void OnEditorChanged(Change c)
    {
        if ((c & (Change.Load | Change.Document | Change.ActiveLod | Change.Model)) == 0) return;
        if (!IsVisible)
        {
            stale = true;
            return;
        }
        RefreshMain();
    }

    void RefreshMain()
    {
        var lod = Ed.Lod;
        mainName.Text = Ed.FileName;
        mainInfo.Text = $"{LodNames.Name(lod.Resolution)} · {lod.PointCount:N0} pts · {lod.FaceCount:N0} faces";
        mainRow.ToolTip = Ed.Path ?? "Untitled (not saved yet)";
    }

    /// <summary>Re-reads the scene: rows are rebuilt when the instance names changed, else updated in place.</summary>
    Row? newlySelected;
    readonly Dictionary<string, int> fileCounts = new(StringComparer.OrdinalIgnoreCase);

    void Refresh()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RefreshCore();
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > 25) Ed.Log($"[perf] Objects panel refresh: {ms:0} ms ({rowList.Count} rows)");
    }

    void RefreshCore()
    {
        stale = false;
        syncing = true;
        try
        {
            RefreshMain();
            var list = ProxyScene.Instances;
            master.Content = $"Proxies ({list.Count(i => i.Depth == 0)}{(list.Any(i => i.Depth > 0) ? $" + {list.Count(i => i.Depth > 0)} nested" : "")})";
            master.IsChecked = ProxyScene.Enabled;
            boxes.IsChecked = ProxyScene.SelectionBox;
            tint.IsChecked = ProxyScene.SelectionTint;
            emptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            selectionText.Text = SelectionInfo(list);

            bool same = rowList.Count == list.Count;
            for (int i = 0; same && i < list.Count; i++) same = string.Equals(rowList[i].InstanceName, list[i].Name, StringComparison.Ordinal);
            if (!same)
            {
                // reuse the row controls (creating 100+ rows on every LOD switch is what costs time)
                while (rowList.Count > list.Count)
                {
                    rowList.RemoveAt(rowList.Count - 1);
                    rows.Children.RemoveAt(rows.Children.Count - 1);
                }
                for (int i = 0; i < list.Count; i++)
                {
                    if (i < rowList.Count)
                    {
                        rowList[i].InstanceName = list[i].Name;
                        rowList[i].Padding = new Thickness(6 + list[i].Depth * 16, 1, 6, 1);
                        continue;
                    }
                    var r = MakeRow(i, list[i]);
                    rowList.Add(r);
                    rows.Children.Add(r);
                }
            }
            fileCounts.Clear();
            for (int i = 0; i < list.Count; i++)
                fileCounts[list[i].ProxyPath] = fileCounts.GetValueOrDefault(list[i].ProxyPath) + 1;
            newlySelected = null;
            for (int i = 0; i < list.Count; i++)
            {
                rowList[i].Index = i;
                Update(rowList[i], list[i]);
            }
            // picked in the viewport: scroll the list to it (not while the click came from the list itself)
            if (newlySelected is { } ns && !ns.IsMouseOver) ns.BringIntoView();
        }
        finally
        {
            syncing = false;
        }
    }

    /// <summary>The "Selection" summary: the selected proxies (top level) with file, model size and position.</summary>
    static string SelectionInfo(IReadOnlyList<ProxyInstance> list)
    {
        if (list.Count == 0) return "";
        var sel = new List<ProxyInstance>();
        foreach (var i in list)
            if (i.Selected && i.Depth == 0) sel.Add(i);
        if (sel.Count == 0) return "Nothing selected — click a proxy model in the viewport or a row below.";
        if (sel.Count > 1)
            return $"{sel.Count} proxies selected: " + string.Join(", ", sel.Take(6).Select(i => Label(i.Name))) + (sel.Count > 6 ? ", …" : "");
        var one = sel[0];
        var m = one.Model;
        var t = one.Transform.Translation;
        var what = m.Model is not null && m.Lod is { } l ? $"{l.FaceCount:N0} faces, {l.PointCount:N0} points" : m.Error ?? "loading…";
        return FormattableString.Invariant($"{Label(one.Name)} — {what}\n{ProxyLibrary.Get(one.ProxyPath).Source}\nat {t.X:0.###}, {t.Y:0.###}, {t.Z:0.###}");
    }

    /// <summary>"btr_wheel.001" from "proxy:\btr\proxy\btr_wheel.001" (nested: the last part).</summary>
    static string Label(string name)
    {
        int gt = name.LastIndexOf(" > ", StringComparison.Ordinal);
        var (path, index) = Proxies.Parse(gt >= 0 ? name[(gt + 3)..] : name);
        var file = Path.GetFileName(path.Replace('/', '\\'));
        return index >= 0 ? $"{file}.{index:D3}" : file;
    }

    Row MakeRow(int index, ProxyInstance inst)
    {
        var r = new Row { Index = index, InstanceName = inst.Name, Padding = new Thickness(6 + inst.Depth * 16, 1, 6, 1), Background = Brushes.Transparent };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(r.Check);
        Grid.SetColumn(r.Label, 1);
        g.Children.Add(r.Label);
        Grid.SetColumn(r.Status, 2);
        g.Children.Add(r.Status);
        r.Child = g;
        r.Check.Click += (_, _) =>
        {
            if (syncing) return;
            ProxySceneHost.SetVisible(r.InstanceName, r.Check.IsChecked == true);
        };
        r.MouseEnter += (_, _) =>
        {
            if (!r.IsSelected) r.Background = HoverBack;
        };
        r.MouseLeave += (_, _) => r.Background = r.IsSelected ? SelectedBack : Brushes.Transparent;
        r.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var mods = Keyboard.Modifiers;
            var op = mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift) ? SelectOp.Subtract
                : mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Shift) ? SelectOp.Add
                : SelectOp.Set;
            if (e.ClickCount >= 2)
            {
                ProxySceneHost.Select(r.Index, SelectOp.Set);
                ProxyViewport.Frame(r.Index);
                return;
            }
            if (!ProxySceneHost.Select(r.Index, op)) Ed.Log($"Cannot select {r.InstanceName}: the proxy selection is not in the active LOD", LogLevel.Warn);
        };
        r.ContextMenu = rowMenu ??= RowMenu(); // one menu for every row (creating ~10 items per row is slow)
        r.ContextMenuOpening += (_, _) => menuRow = r;
        return r;
    }

    void Update(Row r, ProxyInstance inst)
    {
        r.Check.IsChecked = !ProxySceneHost.IsHiddenByUser(inst.Name);
        // Models that cannot be drawn (binarized / missing) have nothing to show or hide.
        bool drawable = inst.Model.Model is not null || inst.Model.Error is null;
        r.Check.IsEnabled = drawable;
        r.Check.ToolTip = drawable ? "Visible in the viewport" : "This proxy model cannot be drawn (see the tooltip of the row)";
        r.Label.Text = Label(inst.Name);
        bool hiddenInEditor = ProxySceneHost.HiddenInEditor(r.Index) && inst.Depth == 0;
        if (!inst.Visible) r.Label.Foreground = (Brush)FindResource("Editor.Faint");
        else r.Label.ClearValue(TextBlock.ForegroundProperty); // theme text colour
        var m = inst.Model;
        string status;
        Brush statusBrush;
        if (m.Model is not null)
        {
            status = m.Lod is { } lod ? $"{lod.FaceCount:N0} f" : "";
            if (m.Binarized) status = "game ODOL · " + status;
            statusBrush = (Brush)FindResource("Editor.Faint");
        }
        else if (m.Error is null)
        {
            status = "loading…";
            statusBrush = (Brush)FindResource("Editor.Dim");
        }
        else if (m.Error == ProxyFiles.OdolError)
        {
            status = "ODOL · unreadable";
            statusBrush = (Brush)FindResource("Editor.Warn");
        }
        else if (m.Error.StartsWith("file not found", StringComparison.Ordinal))
        {
            status = "missing";
            statusBrush = (Brush)FindResource("Editor.Error");
        }
        else
        {
            status = "error";
            statusBrush = (Brush)FindResource("Editor.Error");
        }
        if (hiddenInEditor) status = "△ hidden · " + status;
        var entry = ProxyLibrary.Get(inst.ProxyPath);
        var tip = $"{inst.Name}\n{entry.Source}";
        if (entry.LoadedFrom is not null)
        {
            tip += $"\nSource copy next to the model — {entry.File} is binarized or missing";
            status += " · src";
        }
        r.Status.Text = status;
        r.Status.Foreground = statusBrush;
        if (fileCounts.GetValueOrDefault(inst.ProxyPath) is var uses and > 1) tip += $"\n{uses} instances of this model in the LOD";
        if (m.Error is not null)
        {
            tip += $"\n{m.Error}";
            if (m.Error == ProxyFiles.OdolError)
                tip += "\nThis binarized file has a layout the reader does not know. A source (MLOD) copy next to the edited"
                    + "\nmodel is used automatically (e.g. <model folder>\\proxy\\name.p3d for \\prefix\\proxy\\name).";
        }
        else if (m.Model is not null && m.Lod is { } l)
        {
            tip += $"\nLOD {LodNames.Name(l.Resolution)}: {l.PointCount:N0} points, {l.FaceCount:N0} faces";
            if (m.Binarized) tip += "\nBinarized game model (ODOL): drawn with its textures and materials, but it cannot be opened for editing";
        }
        if (inst.Depth > 0) tip += $"\nNested proxy (depth {inst.Depth}) — select / move its parent";
        if (hiddenInEditor) tip += "\nOnly the proxy triangle is hidden in the editor (Edit ▸ Unhide All shows it); the model is still drawn";
        r.ToolTip = tip;
        if (inst.Selected && !r.IsSelected && newlySelected is null) newlySelected = r;
        r.IsSelected = inst.Selected;
        r.Background = inst.Selected ? SelectedBack : r.IsMouseOver ? HoverBack : Brushes.Transparent;
    }

    ContextMenu MainMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => UpdateEnabled(menu);
        menu.Items.Add(Item("Show in Explorer", () => Commands.Run("file.reveal"), () => Ed.Path is not null));
        menu.Items.Add(Item("Reload from Disk", () => Commands.Run(ObjectsFeature.FileReloadId), () => Ed.Path is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show All Proxies", ProxySceneHost.ShowAll, () => true));
        menu.Items.Add(Item("Reload Proxies", () => Commands.Run(ObjectsFeature.ReloadProxiesId), () => true));
        return menu;
    }

    ContextMenu? rowMenu;
    Row menuRow = new();

    ContextMenu RowMenu()
    {
        string File() => ProxyLibrary.Get(Instance(menuRow)?.ProxyPath ?? "").Source;
        ProxyInstance? Inst() => Instance(menuRow);
        var menu = new ContextMenu();
        menu.Opened += (_, _) => UpdateEnabled(menu);
        menu.Items.Add(Item("Open Proxy Model", () => OpenModel(File()), () => System.IO.File.Exists(File())));
        menu.Items.Add(Item("Show in Explorer", () => Reveal(File()), () => System.IO.File.Exists(File()) || Directory.Exists(Path.GetDirectoryName(File()))));
        menu.Items.Add(Item("Copy File Path", () => CopyText(File()), () => true));
        menu.Items.Add(Item("Copy Proxy Path", () => CopyText(Inst()?.ProxyPath ?? ""), () => Inst() is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select", () => ProxySceneHost.Select(menuRow.Index, SelectOp.Set), () => true));
        menu.Items.Add(Item("Center & Zoom", () =>
        {
            ProxySceneHost.Select(menuRow.Index, SelectOp.Set);
            ProxyViewport.Frame(menuRow.Index);
        }, () => true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show Only This Proxy (hide the others)", () => ProxySceneHost.ShowOnly(menuRow.InstanceName), () => true));
        menu.Items.Add(Item("Show All Proxies", ProxySceneHost.ShowAll, () => true));
        menu.Items.Add(Item("Show All Copies of This Model", () => ProxySceneHost.SetVisibleByModel(ProxyScene.Instances[menuRow.Index].ProxyPath, true), () => true));
        menu.Items.Add(Item("Hide All Copies of This Model", () => ProxySceneHost.SetVisibleByModel(ProxyScene.Instances[menuRow.Index].ProxyPath, false), () => true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reload Proxies", () => Commands.Run(ObjectsFeature.ReloadProxiesId), () => true));
        return menu;
    }

    static ProxyInstance? Instance(Row r)
    {
        var list = ProxyScene.Instances;
        return r.Index >= 0 && r.Index < list.Count && list[r.Index].Name == r.InstanceName ? list[r.Index] : null;
    }

    static MenuItem Item(string header, Action run, Func<bool> enabled)
    {
        var mi = new MenuItem { Header = header, Tag = enabled };
        mi.Click += (_, _) => run();
        return mi;
    }

    static void UpdateEnabled(ContextMenu menu)
    {
        foreach (var o in menu.Items)
            if (o is MenuItem { Tag: Func<bool> enabled } mi) mi.IsEnabled = enabled();
    }

    /// <summary>Opens a proxy model as the document (asks to save the current changes first).</summary>
    public static void OpenModel(string file)
    {
        if (!File.Exists(file))
        {
            Ed.Log($"Proxy model not found: {file}", LogLevel.Warn);
            return;
        }
        if (FileService.ConfirmDiscard("open the proxy model")) FileService.Open(file);
    }

    static void Reveal(string file)
    {
        try
        {
            if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (Path.GetDirectoryName(file) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot open Explorer: {ex.Message}", LogLevel.Warn);
        }
    }

    static void CopyText(string text)
    {
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            Ed.Log($"Copied {text}");
        }
        catch (Exception ex)
        {
            Ed.Log($"Clipboard: {ex.Message}", LogLevel.Warn);
        }
    }
}
