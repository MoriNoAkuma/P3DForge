using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Resources;
using P3DForge.App.Editor;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>
/// Resource Library (Shift+F10): textures, materials and proxies used by the model (face counts per LOD; click =
/// select the faces / proxies in the active LOD; context menu: assign to selected faces, rename, reveal, copy path)
/// above a lazy, cached browser of the project root (.paa / .rvmat / .p3d) with PAA thumbnails.
/// </summary>
public sealed class ResourcesPanel : Grid
{
    static EditorState Ed => AppServices.Editor;

    enum RowKind { Texture, Material, Proxy }

    sealed record UsedInfo(RowKind Kind, string Path, int[] PerLod, List<(int Lod, string Name)> ProxyNames);

    readonly ObservableCollection<FileRow> used = [];
    readonly ListBox usedList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox filter;
    readonly TextBlock usedHeader = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly ProjectBrowser browser;
    readonly ComboBox kinds;
    readonly TabControl tabs;
    readonly Image preview = new() { Width = 72, Height = 72, Stretch = Stretch.Uniform };
    readonly TextBlock previewInfo = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    List<UsedInfo> all = [];
    bool stale = true, queued;

    static readonly (string Label, string[] Ext)[] Filters =
    [
        ("Textures, materials & models", [".paa", ".pac", ".tga", ".png", ".rvmat", ".p3d"]),
        ("Textures (.paa .tga .png)", [".paa", ".pac", ".tga", ".png"]),
        ("Materials (.rvmat)", [".rvmat"]),
        ("Models (.p3d)", [".p3d"]),
    ];

    public ResourcesPanel()
    {
        Background = Dlg.Res("Editor.PanelBackground");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 }); // tabs
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                      // preview
        var modelDock = new DockPanel();
        var projDock = new DockPanel();
        tabs = new TabControl { Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        tabs.Items.Add(new TabItem { Header = TabHeader("In model"), Content = modelDock });
        tabs.Items.Add(new TabItem { Header = TabHeader("Project"), Content = projDock });
        tabs.SelectedIndex = Math.Clamp(AppServices.Settings.Get("resources.tab", 0), 0, 1);
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, tabs)) AppServices.Settings.Set("resources.tab", tabs.SelectedIndex);
        };
        SetRow(tabs, 0);
        Children.Add(tabs);

        // ---- used by the model ----
        filter = new TextBox { Style = (Style)Application.Current.FindResource("CompactTextBox"), ToolTip = "Filter by path", Margin = new Thickness(0, 4, 0, 0) };
        filter.TextChanged += (_, _) => Fill();
        var hint = new TextBlock { Text = "Filter…", IsHitTestVisible = false, Foreground = Dlg.Res("Editor.Faint"), Margin = new Thickness(10, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        filter.TextChanged += (_, _) => hint.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || filter.Text.Length == 0) return;
            filter.Text = "";
            e.Handled = true;
        };
        var filterHost = new Grid();
        filterHost.Children.Add(filter);
        filterHost.Children.Add(hint);
        var head = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        head.Children.Add(usedHeader);
        head.Children.Add(filterHost);
        DockPanel.SetDock(head, Dock.Top);
        modelDock.Children.Add(head);

        usedList.ItemsSource = used;
        usedList.ItemTemplate = RowTemplates.Row;
        usedList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        usedList.ItemContainerStyle = (Style)Application.Current.FindResource("CompactListBoxItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(usedList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(usedList, true);
        usedList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(usedList, d) is ListBoxItem { DataContext: FileRow r }) SelectUsers(r);
        };
        usedList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && usedList.SelectedItem is FileRow r)
            {
                SelectUsers(r);
                e.Handled = true;
            }
        };
        usedList.SelectionChanged += (_, _) =>
        {
            if (usedList.SelectedItem is FileRow { Data: UsedInfo u } && u.Kind == RowKind.Texture) ShowPreview(u.Path);
        };
        usedList.ContextMenu = new ContextMenu();
        usedList.ContextMenuOpening += (_, _) => BuildUsedMenu(usedList.ContextMenu);
        modelDock.Children.Add(usedList);

        // ---- project browser ----
        kinds = new ComboBox { FontSize = 11, MinHeight = 0, Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center, ToolTip = "File types" };
        foreach (var f in Filters) kinds.Items.Add(f.Label);
        kinds.SelectedIndex = 0;
        var refresh = new Button { Content = "", Style = (Style)Application.Current.FindResource("IconButton"), ToolTip = "Rescan folders", Margin = new Thickness(4, 0, 0, 0) };
        var projHead = new DockPanel { Margin = new Thickness(6, 4, 6, 2) };
        DockPanel.SetDock(refresh, Dock.Right);
        projHead.Children.Add(refresh);
        DockPanel.SetDock(kinds, Dock.Right);
        projHead.Children.Add(kinds);
        projHead.Children.Add(new TextBlock { Text = AppServices.Settings.ProjectRoot, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = "Project root (Settings)" });
        DockPanel.SetDock(projHead, Dock.Top);
        projDock.Children.Add(projHead);

        browser = new ProjectBrowser(Filters[0].Ext, 0.8);
        kinds.SelectionChanged += (_, _) => browser.Extensions = Filters[Math.Max(0, kinds.SelectedIndex)].Ext;
        refresh.Click += (_, _) => browser.Refresh();
        browser.FileSelected += abs =>
        {
            if (abs is not null && IsTexture(abs)) ShowPreview(abs);
        };
        browser.FileActivated += abs =>
        {
            if (IsTexture(abs) || IsMaterial(abs)) Assign(AppServices.ToProjectPath(abs), IsTexture(abs) ? ResourceKind.Texture : ResourceKind.Material);
        };
        browser.FileList.ContextMenu = new ContextMenu();
        browser.FileList.ContextMenuOpening += (_, _) => BuildFileMenu(browser.FileList.ContextMenu);
        projDock.Children.Add(browser);

        // ---- preview ----
        var prev = new DockPanel { Margin = new Thickness(6, 4, 6, 6) };
        var pb = new Border { Width = 74, Height = 74, BorderBrush = Dlg.Res("Editor.Faint"), BorderThickness = new Thickness(1), Child = preview };
        DockPanel.SetDock(pb, Dock.Left);
        prev.Children.Add(pb);
        prev.Children.Add(previewInfo);
        previewInfo.Foreground = Dlg.Res("Editor.Dim");
        previewInfo.Text = "Select a texture to preview it.";
        SetRow(prev, 1);
        Children.Add(prev);

        Ed.Changed += c =>
        {
            if ((c & Change.Ui) != 0) OnUiFlags();
            if ((c & (Change.Model | Change.Load | Change.Document | Change.ActiveLod)) == 0) return; // ActiveLod: "N here" counts
            stale = true;
            if (IsVisible) Queue();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Queue();
        };
        Queue();
    }

    /// <summary>
    /// Flags (also used by automation): "resources.show" = "model" | "project" switches the tab;
    /// "resources.reveal" = a model or absolute path opens the Project tab at that file (consumed).
    /// </summary>
    void OnUiFlags()
    {
        if (Ed.Flag<string?>("resources.show", null) is { } show)
        {
            Ed.SetFlag("resources.show", null);
            tabs.SelectedIndex = show.StartsWith("proj", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        if (Ed.Flag<string?>("resources.reveal", null) is { } reveal)
        {
            Ed.SetFlag("resources.reveal", null);
            RevealInProject(reveal);
        }
    }

    /// <summary>Shows a file (model path or absolute) in the Project tab.</summary>
    public void RevealInProject(string path)
    {
        var abs = Path.IsPathRooted(path) && path.Length > 2 && path[1] == ':' ? path : AppServices.ResolveProjectPath(path);
        if (abs is null) return;
        tabs.SelectedIndex = 1;
        _ = browser.Reveal(abs);
        if (IsTexture(abs)) ShowPreview(abs);
    }

    static TextBlock TabHeader(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(2, 0, 2, 0) };

    static bool IsTexture(string p) => FolderCache.Matches(p, ResourcePicker.TextureExtensions);
    static bool IsMaterial(string p) => p.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase);

    void Queue()
    {
        if (queued) return;
        queued = true;
        Dispatcher.BeginInvoke(() =>
        {
            queued = false;
            if (!stale) return;
            stale = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    /// <summary>Recomputes the used textures / materials / proxies of the whole model (cheap: one pass over faces + selection names).</summary>
    void Rebuild()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var model = Ed.Model;
        var list = new List<UsedInfo>();
        foreach (var u in Attributes.Usage(model))
            list.Add(new UsedInfo(u.Kind == ResourceKind.Texture ? RowKind.Texture : RowKind.Material, u.Path, u.FacesPerLod, []));
        var proxies = new Dictionary<string, UsedInfo>(StringComparer.OrdinalIgnoreCase);
        for (int li = 0; li < model.Lods.Length; li++)
        {
            foreach (var s in model.Lods[li].Selections)
            {
                if (!Proxies.IsProxyName(s.Name)) continue;
                var (path, _) = Proxies.Parse(s.Name);
                if (!proxies.TryGetValue(path, out var info)) proxies[path] = info = new UsedInfo(RowKind.Proxy, path, new int[model.Lods.Length], []);
                info.PerLod[li]++;
                info.ProxyNames.Add((li, s.Name));
            }
        }
        list.AddRange(proxies.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase));
        all = list;
        Fill();
        if (Dlg.Automated) Ed.Log($"[perf] resource usage rebuild: {sw.ElapsedMilliseconds} ms");
    }

    void Fill()
    {
        var model = Ed.Model;
        var f = filter.Text.Trim();
        var keep = (usedList.SelectedItem as FileRow)?.Data as UsedInfo;
        used.Clear();
        int tex = 0, mat = 0, prx = 0;
        foreach (var u in all)
        {
            switch (u.Kind)
            {
                case RowKind.Texture: tex++; break;
                case RowKind.Material: mat++; break;
                default: prx++; break;
            }
            if (f.Length > 0 && u.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
            int total = 0, lods = 0;
            var tip = new StringBuilder(u.Path);
            for (int li = 0; li < u.PerLod.Length; li++)
            {
                if (u.PerLod[li] == 0) continue;
                total += u.PerLod[li];
                lods++;
                if (li < model.Lods.Length) tip.Append('\n').Append(LodNames.Name(model.Lods[li].Resolution)).Append(": ").Append(u.PerLod[li].ToString("N0")).Append(u.Kind == RowKind.Proxy ? " proxies" : " faces");
            }
            int active = Ed.ActiveLod < u.PerLod.Length ? u.PerLod[Ed.ActiveLod] : 0;
            var row = new FileRow
            {
                Name = u.Path,
                Path = u.Path,
                Data = u,
                WantsThumb = u.Kind == RowKind.Texture,
                ThumbSize = 64,
                Glyph = u.Kind switch { RowKind.Texture => "", RowKind.Material => "", _ => "" },
                Detail = (u.Kind switch { RowKind.Texture => "texture", RowKind.Material => "material", _ => "proxy" })
                         + $" · {total:N0} {(u.Kind == RowKind.Proxy ? "instance" : "face")}{(total == 1 ? "" : "s")} · {lods} LOD{(lods == 1 ? "" : "s")}"
                         + (active > 0 ? $" · {active:N0} here" : ""),
                ToolTip = tip.ToString(),
            };
            used.Add(row);
            if (keep is not null && keep.Kind == u.Kind && Attributes.PathKey(keep.Path) == Attributes.PathKey(u.Path)) usedList.SelectedItem = row;
        }
        usedHeader.Text = $"{tex} textures · {mat} materials · {prx} proxies";
    }

    // ---- actions ------------------------------------------------------------------------------------

    /// <summary>Selects the faces (texture / material) or proxy selections of a row in the active LOD.</summary>
    void SelectUsers(FileRow r)
    {
        if (r.Data is not UsedInfo u) return;
        var lod = Ed.Lod;
        if (u.Kind == RowKind.Proxy)
        {
            var names = u.ProxyNames.Where(p => p.Lod == Ed.ActiveLod).Select(p => p.Name).ToList();
            if (names.Count == 0)
            {
                Ed.Log($"{u.Path}: no proxy in the active LOD", LogLevel.Warn);
                return;
            }
            Ed.Batch(() =>
            {
                for (int i = 0; i < names.Count; i++) SelectionOps.SelectNamed(names[i], i == 0 ? SelectOp.Set : SelectOp.Add);
            });
            Ed.Log($"Selected {names.Count} proxy instance(s) of {u.Path}");
            return;
        }
        var mask = u.Kind == RowKind.Texture ? Attributes.FacesWithTexture(lod, u.Path) : Attributes.FacesWithMaterial(lod, u.Path);
        int n = SelectionMask.CountNonZero(mask);
        if (n == 0)
        {
            Ed.Log($"{u.Path}: not used in the active LOD", LogLevel.Warn);
            return;
        }
        SelectionOps.SelectFaces(mask, null, SelectOp.Set, u.Kind == RowKind.Texture ? "Select faces by texture" : "Select faces by material");
        Ed.Log($"Selected {n:N0} faces using {u.Path}");
    }

    /// <summary>Assigns a texture / material to the selected faces (faces of selected points when none) of the active LOD.</summary>
    public static void Assign(string path, ResourceKind kind)
    {
        var lod = Ed.Lod;
        var sel = Ed.GetSelection();
        var faces = Attributes.EffectiveFaces(lod, sel.Points, sel.Faces);
        int n = SelectionMask.CountNonZero(faces);
        if (n == 0)
        {
            Dlg.Fail("Assign: select faces first.");
            return;
        }
        var what = kind == ResourceKind.Texture ? "texture" : "material";
        bool changed = false;
        Dlg.Try(() => changed = Ed.EditLod($"Assign {what} ({n:N0} faces)", l => kind == ResourceKind.Texture
            ? Attributes.SetFaceTextureMaterial(l, faces, path, null)
            : Attributes.SetFaceTextureMaterial(l, faces, null, path), new EditOptions { KeepNormals = true }));
        Ed.Log(changed ? $"Assigned {what} {path} to {n:N0} faces" : $"The selected faces already use {path}");
    }

    static void Reveal(string modelOrAbsPath)
    {
        var abs = Path.IsPathRooted(modelOrAbsPath) && modelOrAbsPath.Length > 2 && modelOrAbsPath[1] == ':' ? modelOrAbsPath : AppServices.ResolveProjectPath(modelOrAbsPath);
        if (abs is null)
        {
            Dlg.Fail("Not a file path.");
            return;
        }
        try
        {
            if (File.Exists(abs)) Process.Start("explorer.exe", $"/select,\"{abs}\"");
            else if (Path.GetDirectoryName(abs) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
            else Dlg.Fail($"Not found: {abs}");
        }
        catch (Exception ex)
        {
            Dlg.Fail("Explorer: " + ex.Message);
        }
    }

    static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Ed.Log("Copied " + text);
        }
        catch (Exception ex)
        {
            Dlg.Fail("Clipboard: " + ex.Message);
        }
    }

    static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        m.Click += (_, _) => action();
        return m;
    }

    void BuildUsedMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (usedList.SelectedItem is not FileRow { Data: UsedInfo u } r)
        {
            menu.Items.Add(Item("Mass Texture & Material Renaming…", () => MassRenameDialog.Show()));
            return;
        }
        bool hasSel = Selection.PeekSelected(Ed.Lod) is { IsEmpty: false };
        if (u.Kind == RowKind.Proxy)
        {
            menu.Items.Add(Item("Select Proxies (active LOD)", () => SelectUsers(r)));
        }
        else
        {
            var kind = u.Kind == RowKind.Texture ? ResourceKind.Texture : ResourceKind.Material;
            menu.Items.Add(Item("Select Faces (active LOD)", () => SelectUsers(r)));
            menu.Items.Add(Item($"Assign to Selected Faces", () => Assign(u.Path, kind), hasSel));
            menu.Items.Add(Item("Rename…", () => MassRenameDialog.Show(u.Path, kind)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in Project Browser", () => RevealInProject(u.Kind == RowKind.Proxy ? ProxyFile(u.Path) : u.Path)));
        menu.Items.Add(Item("Reveal in Explorer", () => Reveal(u.Kind == RowKind.Proxy ? ProxyFile(u.Path) : u.Path)));
        menu.Items.Add(Item("Copy Path", () => Copy(u.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Mass Texture & Material Renaming…", () => MassRenameDialog.Show()));
    }

    static string ProxyFile(string path) => path.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase) ? path : path + ".p3d";

    void BuildFileMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (browser.SelectedFile is not { } abs)
        {
            menu.Items.Add(Item("Rescan Folders", browser.Refresh));
            return;
        }
        var rel = AppServices.ToProjectPath(abs);
        bool hasSel = Selection.PeekSelected(Ed.Lod) is { IsEmpty: false };
        if (IsTexture(abs)) menu.Items.Add(Item("Assign Texture to Selected Faces", () => Assign(rel, ResourceKind.Texture), hasSel));
        else if (IsMaterial(abs)) menu.Items.Add(Item("Assign Material to Selected Faces", () => Assign(rel, ResourceKind.Material), hasSel));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reveal in Explorer", () => Reveal(abs)));
        menu.Items.Add(Item("Copy Path", () => Copy(rel)));
        menu.Items.Add(Item("Copy Absolute Path", () => Copy(abs)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rescan Folders", browser.Refresh));
    }

    void ShowPreview(string path)
    {
        preview.Source = null;
        previewInfo.Text = "loading…";
        void Done(ImageSource? img)
        {
            preview.Source = img;
            previewInfo.Text = (img is System.Windows.Media.Imaging.BitmapSource b ? $"{b.PixelWidth}×{b.PixelHeight} preview\n" : img is null ? "cannot load\n" : "") + AppServices.ToProjectPath(path);
        }
        if (Path.IsPathRooted(path) && path.Length > 2 && path[1] == ':') Thumbnails.RequestFile(path, 128, Done);
        else Thumbnails.Request(path, 128, Done);
    }
}
