using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.Core.Materials;
using IOPath = System.IO.Path;

namespace P3DForge.App.Panels.Materials;

/// <summary>
/// Material library (Window ▸ Material Library): every *.rvmat under a folder (default the project root, "Folder…"
/// changes it, persisted in Settings.Extra["materials.libraryFolder"]) as a grid of preview spheres. The index is built
/// in the background (<see cref="MaterialIndex"/>, cached in %LOCALAPPDATA%\P3DForge\matindex.json with file stamps,
/// so a rescan re-parses only changed files). Search = substring terms on name / path; the grid is virtualized (tiles
/// exist only for visible rows) and previews are CPU-rendered on worker threads for visible tiles only (LRU cache).
/// Double-click assigns the material to the selected faces; right-click: open in editor, select faces, reveal, copy.
/// Automation flags: "materials.libraryFolder" (folder), "materials.librarySearch" (search text).
/// </summary>
public sealed class MaterialLibraryPanel : DockPanel
{
    const double TileW = 112, TileH = 134, Thumb = 96;
    const int PageSize = 400;
    const string FolderKey = "materials.libraryFolder";

    static EditorState Ed => AppServices.Editor;
    static MaterialStore Store => MaterialStore.Shared;

    // shared by panel instances (AvalonDock may re-create the panel)
    static readonly MaterialIndex Index = new(CacheFile());
    static Task? loadTask;
    static CancellationTokenSource? scanCts;
    static Task? scanTask;
    static string? scannedFolder;
    static object? lastProgress; // boxed MaterialScanProgress (written by the scan thread)

    readonly TextBlock folderText, status, searchHint;
    readonly TextBox search;
    readonly Button moreButton;
    readonly ScrollViewer scroller;
    readonly Canvas canvas;
    readonly DispatcherTimer scanTimer, searchTimer;
    readonly Dictionary<int, Tile> active = [];
    readonly Stack<Tile> pool = new();
    List<MaterialIndexEntry> all = [];
    List<MaterialIndexEntry> results = [];
    int limit = PageSize, cols = 1, lastFound = -1;
    long lastRefresh, lastSave = Environment.TickCount64;
    string? selectedPath;
    bool loaded;

    public MaterialLibraryPanel()
    {
        Background = Brushes.Transparent;

        var top = new StackPanel { Margin = new Thickness(6, 6, 6, 2) };
        SetDock(top, Dock.Top);
        Children.Add(top);

        var folderRow = new DockPanel();
        var rescan = IconBtn("", "Rescan the folder (re-parses changed files, refreshes previews)", () => Rescan(force: true));
        var pick = IconBtn("", "Folder… — choose the folder to browse", PickFolder);
        SetDock(rescan, Dock.Right);
        SetDock(pick, Dock.Right);
        folderRow.Children.Add(rescan);
        folderRow.Children.Add(pick);
        folderText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Dlg.Res("Editor.Dim"),
            FontSize = 11.5,
        };
        folderRow.Children.Add(folderText);
        top.Children.Add(folderRow);

        var searchGrid = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        search = new TextBox { Style = (Style)FindResource("CompactTextBox"), ToolTip = "Search: space-separated terms matched in the file name / path" };
        searchHint = new TextBlock
        {
            Text = "Search name or path…",
            IsHitTestVisible = false,
            Foreground = Dlg.Res("Editor.Faint"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        searchGrid.Children.Add(search);
        searchGrid.Children.Add(searchHint);
        top.Children.Add(searchGrid);

        var statusRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        moreButton = new Button
        {
            Content = "Show more",
            Style = (Style)FindResource("CompactButton"),
            Padding = new Thickness(8, 1, 8, 1),
            Visibility = Visibility.Collapsed,
            ToolTip = $"Show {PageSize} more results",
        };
        moreButton.Click += (_, _) =>
        {
            limit += PageSize;
            Reflow(keepTiles: true);
        };
        SetDock(moreButton, Dock.Right);
        statusRow.Children.Add(moreButton);
        status = new TextBlock { Foreground = Dlg.Res("Editor.Dim"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        statusRow.Children.Add(status);
        top.Children.Add(statusRow);

        canvas = new Canvas { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        scroller = new ScrollViewer
        {
            Content = canvas,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new Thickness(2, 0, 0, 0),
        };
        scroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) Layout();
        };
        Children.Add(scroller);

        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            ApplySearch(resetScroll: true);
        };
        search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            searchTimer.Stop();
            searchTimer.Start();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && search.Text.Length > 0)
            {
                search.Text = "";
                e.Handled = true;
            }
        };

        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        scanTimer.Tick += (_, _) => OnScanTick();

        Loaded += (_, _) => OnLoaded();
        Unloaded += (_, _) => OnUnloaded();
    }

    static string CacheFile() =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P3DForge", "matindex.json");

    /// <summary>Folder shown by the library (setting, else the project root).</summary>
    public static string Folder
    {
        get
        {
            var f = AppServices.Settings.Get<string?>(FolderKey, null);
            return string.IsNullOrWhiteSpace(f) ? AppServices.Settings.ProjectRoot : f;
        }
    }

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────────────────────

    void OnLoaded()
    {
        if (loaded) return;
        loaded = true;
        Ed.Changed += OnEditorChanged;
        Store.Changed += OnStoreChanged;
        Previews.Ready += OnPreviewReady;
        folderText.Text = Folder;
        folderText.ToolTip = Folder;
        ApplyAutomationFlags();
        status.Text = "Loading the material index…";
        loadTask ??= Task.Run(() =>
        {
            try
            {
                Index.Load();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => Ed.Log($"Material index cache: {ex.Message}", LogLevel.Warn));
            }
        });
        loadTask.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            RefreshEntries();
            if (scanTask is { IsCompleted: false }) scanTimer.Start();
            else if (!string.Equals(scannedFolder, Folder, StringComparison.OrdinalIgnoreCase)) Rescan(force: false);
        }));
    }

    void OnUnloaded()
    {
        if (!loaded) return;
        loaded = false;
        Ed.Changed -= OnEditorChanged;
        Store.Changed -= OnStoreChanged;
        Previews.Ready -= OnPreviewReady;
        scanTimer.Stop();
        ReleaseAll();
    }

    void OnEditorChanged(Change c)
    {
        if (c.HasFlag(Change.Ui)) ApplyAutomationFlags();
    }

    void ApplyAutomationFlags()
    {
        if (Ed.Flag<string?>("materials.libraryFolder", null) is { } folder)
        {
            Ed.SetFlag("materials.libraryFolder", null);
            SetFolder(folder);
        }
        if (Ed.Flag<string?>("materials.librarySearch", null) is { } text)
        {
            Ed.SetFlag("materials.librarySearch", null);
            search.Text = text;
            searchTimer.Stop();
            ApplySearch(resetScroll: true);
        }
    }

    // ── folder / scan ───────────────────────────────────────────────────────────────────────────────────────

    void PickFolder()
    {
        string? folder;
        if (Dlg.Automated) folder = Dlg.TakeAnswer();
        else
        {
            var dlg = new OpenFolderDialog { Title = "Material library folder", InitialDirectory = Directory.Exists(Folder) ? Folder : "" };
            folder = dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FolderName : null;
        }
        if (!string.IsNullOrWhiteSpace(folder)) SetFolder(folder);
    }

    void SetFolder(string folder)
    {
        folder = IOPath.GetFullPath(folder.Replace('/', '\\'));
        if (!Directory.Exists(folder))
        {
            Ed.Log($"Material library: folder not found: {folder}", LogLevel.Warn);
            return;
        }
        var root = AppServices.Settings.ProjectRoot;
        AppServices.Settings.Set<string?>(FolderKey, string.Equals(folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : folder);
        folderText.Text = folder;
        folderText.ToolTip = folder;
        if (loadTask is { IsCompleted: true })
        {
            RefreshEntries(resetScroll: true);
            Rescan(force: false);
        }
    }

    void Rescan(bool force)
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            status.Text = $"Folder not found: {folder}";
            return;
        }
        if (force)
        {
            Previews.Clear();
            ReleaseAll();
            Layout();
        }
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource();
        scannedFolder = folder;
        lastProgress = null;
        var load = loadTask;
        var progress = new Latest(p => lastProgress = p);
        scanTask = Task.Run(() =>
        {
            load?.Wait();
            try
            {
                return Index.Scan(folder, progress, cts.Token);
            }
            finally
            {
                SaveIndex(wait: true); // parsed summaries survive a cancelled scan (placeholders are never saved)
            }
        }, cts.Token);
        scanTask.ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.IsFaulted) Ed.Log($"Material library scan: {t.Exception!.GetBaseException().Message}", LogLevel.Warn);
            if (cts != scanCts || !loaded) return;
            scanTimer.Stop();
            lastFound = -1;
            RefreshEntries();
        }));
        scanTimer.Start();
        UpdateStatus();
    }

    static int saving;

    /// <summary>Writes the index cache (skipped while another save runs).</summary>
    static void SaveIndex(bool wait = false)
    {
        while (Interlocked.Exchange(ref saving, 1) == 1)
        {
            if (!wait) return;
            Thread.Sleep(20);
        }
        try
        {
            Index.Save();
        }
        catch (Exception)
        {
            // cache only
        }
        finally
        {
            Volatile.Write(ref saving, 0);
        }
    }

    void OnScanTick()
    {
        // long scans: persist progress every ~10 s so an app exit mid-scan keeps the parsed part
        if (Environment.TickCount64 - lastSave > 10000)
        {
            lastSave = Environment.TickCount64;
            if (lastProgress is MaterialScanProgress { Folder: null, Parsed: > 0 }) Task.Run(() => SaveIndex());
        }
        if (scanTask is null || scanTask.IsCompleted)
        {
            scanTimer.Stop();
            return;
        }
        // the index publishes all files once enumerated (Folder == null), then fills in the summaries: refresh the grid
        // then and every ~1.2 s while parsing (entries are small summaries; visible tiles rebind only when changed)
        var now = Environment.TickCount64;
        if (lastProgress is MaterialScanProgress { Folder: null } p && p.Parsed != lastFound && (lastFound < 0 || now - lastRefresh > 1200))
        {
            lastFound = p.Parsed;
            lastRefresh = now;
            RefreshEntries();
        }
        else UpdateStatus();
    }

    sealed class Latest(Action<MaterialScanProgress> report) : IProgress<MaterialScanProgress>
    {
        public void Report(MaterialScanProgress value) => report(value);
    }

    // ── entries / search ────────────────────────────────────────────────────────────────────────────────────

    void RefreshEntries(bool resetScroll = false)
    {
        var folder = Folder;
        Task.Run(() => Index.Entries(folder)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (!loaded || t.IsFaulted || !string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase)) return;
            all = t.Result;
            ApplySearch(resetScroll);
        }));
    }

    void ApplySearch(bool resetScroll)
    {
        results = MaterialIndex.Search(all, search.Text);
        if (resetScroll)
        {
            limit = PageSize;
            scroller.ScrollToVerticalOffset(0);
        }
        Reflow(keepTiles: true);
    }

    void UpdateStatus()
    {
        var shown = Math.Min(results.Count, limit);
        var text = string.IsNullOrWhiteSpace(search.Text)
            ? $"{results.Count:N0} material(s)"
            : $"found {results.Count:N0} of {all.Count:N0}";
        if (results.Count > shown) text += $" (first {shown:N0})";
        if (scanTask is { IsCompleted: false })
            text += lastProgress is MaterialScanProgress p ? $" · scanning… {p.Found:N0} files, {p.Parsed:N0} parsed" : " · scanning…";
        status.Text = text;
        moreButton.Visibility = results.Count > shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── virtualized grid ────────────────────────────────────────────────────────────────────────────────────

    void Reflow(bool keepTiles)
    {
        if (!keepTiles) ReleaseAll();
        UpdateStatus();
        Layout();
    }

    void Layout()
    {
        var width = scroller.ViewportWidth > 0 ? scroller.ViewportWidth : scroller.ActualWidth;
        if (width <= 0) return;
        var newCols = Math.Max(1, (int)(width / TileW));
        if (newCols != cols)
        {
            cols = newCols;
            ReleaseAll();
        }
        var shown = Math.Min(results.Count, limit);
        var rows = (shown + cols - 1) / cols;
        canvas.Width = width;
        canvas.Height = rows * TileH + 4;
        var cell = width / cols;

        var y0 = scroller.VerticalOffset;
        var h = scroller.ViewportHeight > 0 ? scroller.ViewportHeight : scroller.ActualHeight;
        var first = Math.Max(0, (int)(y0 / TileH) * cols);
        var last = Math.Min(shown, ((int)((y0 + h) / TileH) + 1) * cols);

        List<int>? drop = null;
        foreach (var i in active.Keys)
            if (i < first || i >= last || i >= results.Count)
                (drop ??= []).Add(i);
        if (drop is not null)
            foreach (var i in drop)
                Release(i);

        for (var i = first; i < last; i++)
        {
            var e = results[i];
            if (active.TryGetValue(i, out var existing))
            {
                if (ReferenceEquals(existing.Entry, e)) continue;
                Release(i);
            }
            var tile = pool.Count > 0 ? pool.Pop() : NewTile();
            tile.Visibility = Visibility.Visible;
            tile.Bind(e, string.Equals(e.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            Canvas.SetLeft(tile, (i % cols) * cell + (cell - TileW) / 2);
            Canvas.SetTop(tile, (i / cols) * TileH + 2);
            active[i] = tile;
        }
    }

    Tile NewTile()
    {
        var t = new Tile(this);
        canvas.Children.Add(t);
        return t;
    }

    void Release(int index)
    {
        if (!active.Remove(index, out var t)) return;
        t.Unbind();
        t.Visibility = Visibility.Collapsed;
        pool.Push(t);
    }

    void ReleaseAll()
    {
        if (active.Count == 0) return;
        foreach (var i in active.Keys.ToList()) Release(i);
    }

    void OnPreviewReady(string key, BitmapSource? bmp)
    {
        foreach (var t in active.Values)
            if (t.Key == key)
                t.SetPreview(bmp, failed: bmp is null);
    }

    void OnStoreChanged(string storeKey)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStoreChanged(storeKey));
            return;
        }
        if (!loaded) return;
        // an edited (override) or reloaded material: re-render its visible tiles (the key carries the store version)
        foreach (var (i, t) in active.ToList())
            if (t.StoreKey == storeKey && t.Entry is { } e)
            {
                t.Unbind();
                t.Bind(e, string.Equals(e.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
    }

    // ── actions ─────────────────────────────────────────────────────────────────────────────────────────────

    void Select(Tile tile)
    {
        selectedPath = tile.Entry?.Path;
        foreach (var t in active.Values) t.SetSelected(ReferenceEquals(t, tile));
    }

    void ShowMenu(Tile tile)
    {
        if (tile.Entry is not { } e) return;
        Select(tile);
        var modelPath = MaterialActions.ToModelPath(e.Path);
        var hasModel = Ed.Model.Lods.Length > 0;
        var menu = new ContextMenu { PlacementTarget = tile, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Open in Material Editor", () => MaterialActions.OpenInEditor(e.Path)));
        menu.Items.Add(Item("Assign to Selected Faces", () => MaterialActions.AssignToSelectedFaces(e.Path), hasModel));
        menu.Items.Add(Item("Select Faces Using It", () => MaterialActions.SelectFacesUsing(e.Path), hasModel));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reveal in Explorer", () => MaterialActions.RevealInExplorer(e.Path)));
        menu.Items.Add(Item($"Copy Path  ({modelPath})", () => MaterialActions.CopyPath(modelPath)));
        if (!string.Equals(modelPath, e.Path, StringComparison.OrdinalIgnoreCase))
            menu.Items.Add(Item("Copy Full Path", () => MaterialActions.CopyPath(e.Path)));
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action a, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => Dlg.Try(a);
        return mi;
    }

    Button IconBtn(string glyph, string tip, Action a)
    {
        var b = new Button { Content = glyph, Style = (Style)FindResource("IconButton"), ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        b.Click += (_, _) => Dlg.Try(a);
        return b;
    }

    static string Describe(MaterialIndexEntry e)
    {
        var lines = new List<string> { MaterialActions.ToModelPath(e.Path) };
        if (e.Error is not null) lines.Add($"Error: {e.Error}");
        else
        {
            lines.Add($"Shader: {(e.Shader.Length > 0 ? e.Shader : "—")}{(e.Binarized ? "  (binarized)" : "")}");
            if (e.Diffuse is not null) lines.Add($"Colour: {e.Diffuse}");
            if (e.Normal is not null) lines.Add($"Normal: {e.Normal}");
            if (e.Specular is not null) lines.Add($"Specular: {e.Specular}");
        }
        lines.Add("Double-click: assign to the selected faces · right-click: more");
        return string.Join("\n", lines);
    }

    // ── tile ────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class Tile : Border
    {
        readonly MaterialLibraryPanel owner;
        readonly Image image;
        readonly Ellipse placeholder;
        readonly TextBlock name, mark;
        bool selected;

        public MaterialIndexEntry? Entry { get; private set; }
        public string? Key { get; private set; }
        public string? StoreKey { get; private set; }

        public Tile(MaterialLibraryPanel owner)
        {
            this.owner = owner;
            Width = TileW;
            Height = TileH - 4;
            CornerRadius = new CornerRadius(4);
            BorderThickness = new Thickness(1);
            BorderBrush = Brushes.Transparent;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            placeholder = new Ellipse
            {
                Width = Thumb - 8,
                Height = Thumb - 8,
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            image = new Image { Width = Thumb, Height = Thumb, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            mark = new TextBlock
            {
                Text = "!",
                FontWeight = FontWeights.Bold,
                Foreground = Dlg.Res("Editor.Error"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 6, 0),
                Visibility = Visibility.Collapsed,
            };
            var thumb = new Grid { Width = Thumb, Height = Thumb, Margin = new Thickness(0, 4, 0, 2) };
            thumb.Children.Add(placeholder);
            thumb.Children.Add(image);
            name = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(4, 0, 4, 2),
            };
            var stack = new StackPanel();
            stack.Children.Add(thumb);
            stack.Children.Add(name);
            var root = new Grid();
            root.Children.Add(stack);
            root.Children.Add(mark);
            Child = root;
            ToolTipService.SetInitialShowDelay(this, 600);
            ToolTipOpening += (_, _) => ToolTip = Entry is { } e ? Describe(e) : null;
            ToolTip = "";
            MouseEnter += (_, _) => UpdateLook(hover: true);
            MouseLeave += (_, _) => UpdateLook(hover: false);
            MouseLeftButtonDown += (_, e) =>
            {
                owner.Select(this);
                if (e.ClickCount == 2 && Entry is { } en)
                {
                    if (Ed.Model.Lods.Length == 0) Ed.Log("Open a model and select faces to assign the material", LogLevel.Warn);
                    else Dlg.Try(() => MaterialActions.AssignToSelectedFaces(en.Path));
                }
                e.Handled = true;
            };
            MouseRightButtonUp += (_, e) =>
            {
                owner.ShowMenu(this);
                e.Handled = true;
            };
        }

        public void Bind(MaterialIndexEntry e, bool isSelected)
        {
            Entry = e;
            StoreKey = Store.Key(e.Path);
            Key = $"{e.Path.ToLowerInvariant()}|{e.MtimeTicks}|{Store.Version(e.Path)}";
            name.Text = e.Name;
            if (e.Error is null) name.ClearValue(TextBlock.ForegroundProperty);
            else name.Foreground = Dlg.Res("Editor.Error");
            SetSelected(isSelected);
            if (e.Error is not null)
            {
                SetPreview(null, failed: true);
                return;
            }
            if (Previews.TryGet(Key, out var bmp)) SetPreview(bmp, failed: bmp is null);
            else
            {
                SetPreview(null, failed: false);
                Previews.Want(Key, e.Path, Dispatcher);
            }
        }

        public void Unbind()
        {
            if (Key is not null) Previews.Unwant(Key);
            Entry = null;
            Key = null;
            StoreKey = null;
            image.Source = null;
        }

        public void SetPreview(BitmapSource? bmp, bool failed)
        {
            image.Source = bmp;
            placeholder.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
            mark.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetSelected(bool value)
        {
            selected = value;
            UpdateLook(IsMouseOver);
        }

        void UpdateLook(bool hover)
        {
            BorderBrush = selected ? Dlg.Res("Editor.Accent") : Brushes.Transparent;
            Background = selected ? new SolidColorBrush(Color.FromArgb(48, 79, 156, 255))
                : hover ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255))
                : Brushes.Transparent;
        }
    }
}
