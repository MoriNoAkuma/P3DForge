using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using P3DForge.App.Editor;

namespace P3DForge.App.Dialogs.Resources;

/// <summary>One folder listing (sub-folders + matching files), produced on a background thread.</summary>
public sealed record FolderListing(string Path, string[] Dirs, (string Name, long Size)[] Files, string? Error, int Skipped);

/// <summary>
/// Lazy, cached folder enumeration for the project browser. Folders are listed one at a time on the thread pool
/// (P:\ can hold hundreds of thousands of files — it is never walked recursively); results are cached per
/// (folder, extension filter) until <see cref="Clear"/>.
/// </summary>
public static class FolderCache
{
    const int MaxFiles = 5000;
    static readonly Dictionary<string, Task<FolderListing>> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UI thread: the listing of <paramref name="dir"/> (cached task).</summary>
    public static Task<FolderListing> List(string dir, string[] extensions)
    {
        var key = string.Join("|", extensions) + "|" + dir;
        if (cache.TryGetValue(key, out var t) && !t.IsFaulted) return t;
        return cache[key] = Task.Run(() => Enumerate(dir, extensions));
    }

    public static void Clear() => cache.Clear();

    static FolderListing Enumerate(string dir, string[] extensions)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            var dirs = new List<string>();
            foreach (var d in di.EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                dirs.Add(d.Name);
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            var files = new List<(string, long)>();
            int skipped = 0;
            foreach (var f in di.EnumerateFiles())
            {
                if (!Matches(f.Name, extensions)) continue;
                if (files.Count >= MaxFiles)
                {
                    skipped++;
                    continue;
                }
                files.Add((f.Name, f.Length));
            }
            files.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            return new FolderListing(dir, [.. dirs], [.. files], null, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FolderListing(dir, [], [], ex.Message, 0);
        }
    }

    public static bool Matches(string name, string[] extensions)
    {
        foreach (var e in extensions)
            if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>A row of the file list / used-resources list; its thumbnail is requested the first time a bound
/// (= realized, visible) row reads it, so virtualized lists only decode what is on screen.</summary>
public sealed class FileRow : INotifyPropertyChanged
{
    ImageSource? thumb;
    bool requested;

    public required string Name { get; init; }
    /// <summary>Absolute path (project files) or model path (used resources).</summary>
    public required string Path { get; init; }
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uE8A5";
    public string? ToolTip { get; init; }
    public bool WantsThumb { get; init; }
    public int ThumbSize { get; init; } = 64;
    /// <summary>Free payload for the owner (e.g. the ResourceUse).</summary>
    public object? Data { get; init; }

    public ImageSource? Thumb
    {
        get
        {
            if (!requested && WantsThumb)
            {
                requested = true;
                Thumbnails.Request(Path, ThumbSize, img =>
                {
                    thumb = img;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
                });
            }
            return thumb;
        }
    }

    public Visibility GlyphVisibility => thumb is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}

/// <summary>Data templates built from XAML strings (bindings + virtualization without a .xaml file).</summary>
public static class RowTemplates
{
    const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    static DataTemplate? row;

    /// <summary>Thumbnail (or glyph), name and a dim detail line.</summary>
    public static DataTemplate Row => row ??= (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate {{Ns}}>
          <Grid Margin="0,1,0,1" ToolTip="{Binding ToolTip}">
            <Grid.ColumnDefinitions><ColumnDefinition Width="30"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
            <Border Width="26" Height="26" Background="#20FFFFFF" CornerRadius="2">
              <Grid>
                <Image Source="{Binding Thumb}" Stretch="Uniform" RenderOptions.BitmapScalingMode="HighQuality"/>
                <TextBlock Text="{Binding Glyph}" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="14" Opacity="0.7"
                           HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="{Binding GlyphVisibility}"/>
              </Grid>
            </Border>
            <StackPanel Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center">
              <TextBlock Text="{Binding Name}" FontSize="12" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Detail}" FontSize="10.5" Opacity="0.6" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
          </Grid>
        </DataTemplate>
        """);
}

/// <summary>
/// Lazy browser of the project root (Settings.ProjectRoot, default P:\): a folder tree (each folder enumerated on a
/// background task when first expanded) above a virtualized file list of the selected folder with PAA thumbnails.
/// </summary>
public sealed class ProjectBrowser : Grid
{
    readonly TreeView tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly ListBox files = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBlock status = new() { FontSize = 11, Margin = new Thickness(4, 2, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ObservableCollection<FileRow> rows = [];
    string[] extensions;
    string root = "";
    string? currentDir;
    int loadSeq;

    /// <summary>A file was selected (absolute path) or the selection was cleared (null).</summary>
    public event Action<string?>? FileSelected;
    /// <summary>Double-click / Enter on a file (absolute path).</summary>
    public event Action<string>? FileActivated;

    public ListBox FileList => files;
    public string? SelectedFile => (files.SelectedItem as FileRow)?.Path;
    public string? CurrentFolder => currentDir;

    public ProjectBrowser(string[] extensions, double treeShare = 0.8)
    {
        this.extensions = extensions;
        // tree : files = treeShare : 1 (star rows so a short docked panel still shows both; the splitter adjusts)
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(treeShare, GridUnitType.Star), MinHeight = 60 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // no virtualization for the folder tree: levels are small and TreeViewItem.BringIntoView needs realized items
        SetRow(tree, 0);
        Children.Add(tree);
        var split = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Dlg.Res("Editor.Faint"), Opacity = 0.5 };
        SetRow(split, 1);
        Children.Add(split);

        files.ItemsSource = rows;
        files.ItemTemplate = RowTemplates.Row;
        files.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(files, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(files, true);
        VirtualizingPanel.SetVirtualizationMode(files, VirtualizationMode.Recycling);
        files.ItemContainerStyle = CompactItem();
        files.SelectionChanged += (_, _) => FileSelected?.Invoke(SelectedFile);
        files.MouseDoubleClick += (_, e) =>
        {
            if (SelectedFile is { } f && e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(files, d) is not null) FileActivated?.Invoke(f);
        };
        files.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && SelectedFile is { } f)
            {
                FileActivated?.Invoke(f);
                e.Handled = true;
            }
        };
        SetRow(files, 2);
        Children.Add(files);
        status.Foreground = Dlg.Res("Editor.Dim");
        SetRow(status, 3);
        Children.Add(status);

        tree.SelectedItemChanged += (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: string dir }) ShowFolder(dir);
        };
        SetRoot(AppServices.Settings.ProjectRoot);
    }

    public string[] Extensions
    {
        get => extensions;
        set
        {
            extensions = value;
            if (currentDir is not null) ShowFolder(currentDir);
        }
    }

    static Style CompactItem()
    {
        var s = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource("CompactListBoxItem"));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
        return s;
    }

    /// <summary>Re-roots the tree (e.g. after the project root setting changed).</summary>
    public void SetRoot(string newRoot)
    {
        root = newRoot;
        tree.Items.Clear();
        rows.Clear();
        currentDir = null;
        if (!Directory.Exists(root))
        {
            status.Text = $"Project root {root} not found (Settings › project root)";
            return;
        }
        var item = FolderItem(root, root.TrimEnd('\\') + "\\");
        tree.Items.Add(item);
        item.IsExpanded = true;
        status.Text = root;
    }

    /// <summary>Clears the folder cache and reloads the visible folders.</summary>
    public void Refresh()
    {
        FolderCache.Clear();
        Thumbnails.Clear();
        var dir = currentDir;
        SetRoot(AppServices.Settings.ProjectRoot); // picks up a changed project root
        if (dir is not null) _ = RevealFolder(dir);
    }

    TreeViewItem FolderItem(string path, string label)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "\uE8B7", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        header.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = header, Tag = path, Padding = new Thickness(1), MinHeight = 0 };
        item.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "loading…", FontSize = 11, Opacity = 0.6 }, IsEnabled = false });
        item.Expanded += async (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, item)) return;
            await EnsureLoaded(item);
        };
        return item;
    }

    /// <summary>Fills a folder node's children (once).</summary>
    async Task EnsureLoaded(TreeViewItem item)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { IsEnabled: false } placeholder) return;
        var path = (string)item.Tag;
        var listing = await FolderCache.List(path, extensions);
        if (!item.Items.Contains(placeholder)) return; // filled by a concurrent call
        item.Items.Clear();
        foreach (var d in listing.Dirs) item.Items.Add(FolderItem(Path.Combine(path, d), d));
        if (listing.Error is not null) item.ToolTip = listing.Error;
    }

    async void ShowFolder(string dir)
    {
        currentDir = dir;
        int seq = ++loadSeq;
        status.Text = "listing " + dir + " …";
        var listing = await FolderCache.List(dir, extensions);
        if (seq != loadSeq) return;
        rows.Clear();
        foreach (var (name, size) in listing.Files)
        {
            var full = Path.Combine(dir, name);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            rows.Add(new FileRow
            {
                Name = name,
                Path = full,
                Detail = FormatSize(size),
                Glyph = ext switch { ".rvmat" => "\uE790", ".p3d" => "\uF158", _ => "\uEB9F" },
                WantsThumb = ext is ".paa" or ".pac" or ".png" or ".jpg",
                ToolTip = AppServices.ToProjectPath(full),
            });
        }
        status.Text = listing.Error ?? $"{AppServices.ToProjectPath(dir)}  —  {listing.Files.Length:N0} file(s){(listing.Skipped > 0 ? $", {listing.Skipped:N0} more not shown" : "")}";
        if (rows.Count > 0) files.ScrollIntoView(rows[0]);
    }

    static string FormatSize(long b) => b >= 1 << 20 ? (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>Expands the tree down to <paramref name="absFile"/>'s folder and selects the file when listed.</summary>
    public async Task Reveal(string absFile)
    {
        var dir = Path.GetDirectoryName(absFile);
        if (dir is null) return;
        if (!await RevealFolder(dir)) return;
        // wait for the file list of that folder
        await FolderCache.List(dir, extensions);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        var row = rows.FirstOrDefault(r => string.Equals(r.Path, absFile, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        files.SelectedItem = row;
        files.ScrollIntoView(row);
    }

    async Task<bool> RevealFolder(string dir)
    {
        var rootFull = root.TrimEnd('\\') + "\\";
        if (!(dir.TrimEnd('\\') + "\\").StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || tree.Items.Count == 0) return false;
        var node = (TreeViewItem)tree.Items[0];
        var rel = dir.Length > rootFull.Length ? dir[rootFull.Length..] : "";
        foreach (var seg in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            await EnsureLoaded(node);
            node.IsExpanded = true;
            var next = node.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is string p && string.Equals(Path.GetFileName(p), seg, StringComparison.OrdinalIgnoreCase));
            if (next is null) break;
            node = next;
        }
        node.IsSelected = true;
        var target = node;
        _ = Dispatcher.BeginInvoke(() =>
        {
            tree.UpdateLayout();
            target.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
        ShowFolder((string)node.Tag);
        return true;
    }
}
