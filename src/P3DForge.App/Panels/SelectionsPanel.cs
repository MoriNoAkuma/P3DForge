using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels;

/// <summary>One row of the Named Selections list (immutable except for <see cref="Contains"/>).</summary>
public sealed class SelRow : INotifyPropertyChanged
{
    public required int TagIndex { get; init; }
    public required SelectionTag Tag { get; init; }
    public required string Counts { get; init; }
    public required bool Weighted { get; init; }
    public required bool Empty { get; init; }
    public string Name => Tag.Name;
    public string Tip { get; init; } = "";

    bool contains;

    /// <summary>True when the selection contains the whole current (editing) selection.</summary>
    public bool Contains
    {
        get => contains;
        set
        {
            if (contains == value) return;
            contains = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Contains)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Named Selections window (Object Builder parity): filterable, virtualized list of the active LOD's named
/// selections with point/face counts; rows containing the whole current selection are highlighted.
/// Click = select, Ctrl+click = add, Ctrl+Shift+click = subtract, double-click/F2 = rename, Delete = delete.
/// </summary>
public sealed class SelectionsPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>The panel instance (created once by the panel registry), or null before it exists.</summary>
    public static SelectionsPanel? Instance { get; private set; }

    /// <summary>Flag: set to a selection name to select/reveal that row (other features, automation).</summary>
    public const string FocusFlag = "selections.focus";

    readonly ListBox list;
    readonly TextBox filter;
    readonly TextBlock status;
    List<SelRow> rows = [];
    int shownLod = -1;
    P3DModel? shownModel;
    bool stale = true;
    double? restoreOffset;

    sealed class Info(int count, bool weighted)
    {
        public readonly int Count = count;
        public readonly bool Weighted = weighted;
    }

    /// <summary>Non-zero count + "has weights" per byte array instance (arrays are immutable, so this never goes stale).</summary>
    static readonly ConditionalWeakTable<byte[], Info> infoCache = [];

    static Info InfoOf(byte[] a) => infoCache.GetValue(a, static b =>
    {
        var s = b.AsSpan();
        return new Info(s.Length - s.Count((byte)0), s.IndexOfAnyExcept((byte)0, (byte)1) >= 0);
    });

    const string TemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DockPanel ToolTip="{Binding Tip}" Background="Transparent" Margin="0,0,10,0">
            <TextBlock DockPanel.Dock="Right" Text="{Binding Counts}" FontFamily="{DynamicResource MonoFont}"
                       Foreground="{DynamicResource Editor.Dim}" Margin="8,0,0,0" VerticalAlignment="Center" />
            <TextBlock x:Name="W" DockPanel.Dock="Right" Text="w" Visibility="Collapsed" FontWeight="SemiBold"
                       Foreground="{DynamicResource Editor.Accent}" Margin="6,0,0,0" VerticalAlignment="Center" />
            <TextBlock x:Name="N" Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
          </DockPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding Weighted}" Value="True">
              <Setter TargetName="W" Property="Visibility" Value="Visible" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Empty}" Value="True">
              <Setter TargetName="N" Property="Foreground" Value="{DynamicResource Editor.Faint}" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Contains}" Value="True">
              <Setter TargetName="N" Property="Foreground" Value="{DynamicResource Editor.Selection}" />
              <Setter TargetName="N" Property="FontWeight" Value="SemiBold" />
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """;

    public SelectionsPanel()
    {
        Instance = this;
        Background = Brushes.Transparent;

        // ---- top bar: filter + new + count
        var top = new DockPanel { Margin = new Thickness(4, 4, 4, 2) };
        var add = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "New named selection from the current selection" };
        add.Click += (_, _) => NamedSelectionActions.New();
        DockPanel.SetDock(add, Dock.Right);
        status = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        DockPanel.SetDock(status, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(status);
        filter = new TextBox { Style = (Style)FindResource("CompactTextBox"), ToolTip = "Filter by name" };
        var hint = new TextBlock
        {
            Text = "Filter…",
            IsHitTestVisible = false,
            Foreground = (Brush)FindResource("Editor.Faint"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        filter.TextChanged += (_, _) =>
        {
            hint.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        };
        filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && filter.Text.Length > 0)
            {
                filter.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Down && list!.Items.Count > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                FocusSelectedItem();
                e.Handled = true;
            }
        };
        var filterHost = new Grid();
        filterHost.Children.Add(filter);
        filterHost.Children.Add(hint);
        top.Children.Add(filterHost);
        SetDock(top, Dock.Top);
        Children.Add(top);

        // ---- list (virtualized, recycling)
        list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = (DataTemplate)XamlReader.Parse(TemplateXaml),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource("CompactListBoxItem"));
        itemStyle.Setters.Add(new EventSetter(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnItemMouseDown)));
        itemStyle.Setters.Add(new EventSetter(PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(OnItemRightDown)));
        list.ItemContainerStyle = itemStyle;
        list.ContextMenu = new ContextMenu();
        list.ContextMenuOpening += (_, _) => BuildMenu(list.ContextMenu);
        Children.Add(list);

        PanelKeys.Register(this, OnKey);
        Ed.Changed += OnChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && stale) Refresh();
        };
        Refresh();
    }

    /// <summary>The row the context menu / commands act on (list selection, validated against the active LOD).</summary>
    public SelRow? Current
    {
        get
        {
            if (list.SelectedItem is not SelRow r || shownLod != Ed.ActiveLod) return null;
            var tags = Ed.Lod.Tags;
            return r.TagIndex < tags.Length && ReferenceEquals(tags[r.TagIndex], r.Tag) ? r : null;
        }
    }

    // ---- refresh ---------------------------------------------------------------------------------

    void OnChanged(Change c)
    {
        if ((c & (Change.Model | Change.ActiveLod | Change.Document | Change.Selection)) != 0)
        {
            if (!IsVisible)
            {
                stale = true;
                return;
            }
            Refresh();
        }
        if ((c & Change.Ui) != 0 && Ed.Flag<string?>(FocusFlag, null) is { } name)
        {
            Ed.SetFlag(FocusFlag, null);
            Reveal(name);
        }
    }

    static readonly bool perfLog = Environment.GetEnvironmentVariable("P3DFORGE_PANELS_PERF") == "1";

    void Refresh()
    {
        stale = false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RebuildIfNeeded();
        UpdateContains();
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (perfLog || ms > 100) Ed.Log($"[perf] Named Selections refresh: {rows.Count} rows in {ms:0.0} ms", ms > 100 ? LogLevel.Warn : LogLevel.Info);
    }

    bool SameRows(Lod lod)
    {
        if (shownLod != Ed.ActiveLod) return false;
        int k = 0;
        var tags = lod.Tags;
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i] is not SelectionTag s) continue;
            if (k >= rows.Count || !ReferenceEquals(rows[k].Tag, s) || rows[k].TagIndex != i) return false;
            k++;
        }
        return k == rows.Count;
    }

    void RebuildIfNeeded()
    {
        var model = Ed.Model;
        var lod = Ed.Lod;
        if (ReferenceEquals(model, shownModel) && shownLod == Ed.ActiveLod) return;
        if (SameRows(lod))
        {
            shownModel = model;
            return;
        }
        var keep = list.SelectedItem as SelRow;
        var sv = FindScrollViewer(list);
        double offset = sv?.VerticalOffset ?? 0;
        bool sameLod = shownLod == Ed.ActiveLod;

        var next = new List<SelRow>(rows.Count + 4);
        var tags = lod.Tags;
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i] is not SelectionTag s) continue;
            var p = InfoOf(s.Points);
            var f = InfoOf(s.Faces);
            next.Add(new SelRow
            {
                TagIndex = i,
                Tag = s,
                Counts = $"{p.Count,6:N0} {f.Count,6:N0}",
                Weighted = p.Weighted,
                Empty = p.Count == 0 && f.Count == 0,
                Tip = $"{s.Name}\n{p.Count:N0} points, {f.Count:N0} faces{(p.Weighted ? " (weighted)" : "")}",
            });
        }
        rows = next;
        shownLod = Ed.ActiveLod;
        shownModel = model;
        ApplyFilter();
        if (!sameLod) sv?.ScrollToTop();
        else
        {
            if (keep is not null)
            {
                var again = rows.FirstOrDefault(r => r.TagIndex == keep.TagIndex && string.Equals(r.Name, keep.Name, StringComparison.OrdinalIgnoreCase))
                    ?? rows.FirstOrDefault(r => string.Equals(r.Name, keep.Name, StringComparison.OrdinalIgnoreCase));
                if (again is not null && list.Items.Contains(again)) list.SelectedItem = again;
            }
            if (sv is not null)
            {
                restoreOffset = offset;
                Dispatcher.BeginInvoke(() =>
                {
                    if (restoreOffset is { } o) sv.ScrollToVerticalOffset(o);
                    restoreOffset = null;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    void ApplyFilter()
    {
        var text = filter.Text.Trim();
        var keep = list.SelectedItem;
        List<SelRow> visible = text.Length == 0
            ? rows
            : rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        list.ItemsSource = visible;
        if (keep is not null && visible.Contains(keep)) list.SelectedItem = keep;
        status.Text = visible.Count == rows.Count ? $"{rows.Count}" : $"{visible.Count}/{rows.Count}";
    }

    void UpdateContains()
    {
        var lod = Ed.Lod;
        var sel = Selection.PeekSelected(lod);
        int[] idx = [];
        bool faces = false;
        if (sel is { } m && rows.Count > 0)
        {
            idx = NamedSelections.Indices(m.Points);
            if (idx.Length == 0)
            {
                idx = NamedSelections.Indices(m.Faces);
                faces = true;
            }
        }
        foreach (var r in rows)
            r.Contains = idx.Length > 0 && NamedSelections.ContainsAll(faces ? r.Tag.Faces : r.Tag.Points, idx);
    }

    static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    /// <summary>Selects and scrolls to the row named <paramref name="name"/> (clears the filter if it hides it).</summary>
    public void Reveal(string name)
    {
        if (stale) Refresh();
        var row = rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        if (!list.Items.Contains(row)) filter.Text = "";
        restoreOffset = null; // a pending scroll restore from a rebuild must not undo the reveal
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(list.SelectedItem, row)) list.ScrollIntoView(row);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void FocusSelectedItem()
    {
        if (list.SelectedItem is null) return;
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem)?.Focus();
    }

    // ---- input -----------------------------------------------------------------------------------

    void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SelRow row } item) return;
        e.Handled = true;
        list.SelectedItem = row;
        item.Focus();
        if (e.ClickCount >= 2)
        {
            NamedSelectionActions.Rename(row);
            return;
        }
        var mods = Keyboard.Modifiers;
        var op = (mods & ModifierKeys.Control) == 0 ? SelectOp.Set : (mods & ModifierKeys.Shift) != 0 ? SelectOp.Subtract : SelectOp.Add;
        NamedSelectionActions.Select(row, op);
    }

    void OnItemRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SelRow row } item)
        {
            list.SelectedItem = row;
            item.Focus();
        }
    }

    bool OnKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var row = Current;
        switch (key)
        {
            case Key.Delete when mods == ModifierKeys.None:
                if (row is not null) NamedSelectionActions.Delete(row, confirm: true);
                return true;
            case Key.F2 when mods == ModifierKeys.None:
                if (row is not null) NamedSelectionActions.Rename(row);
                return true;
            case Key.Enter or Key.Space:
                if (row is not null)
                    NamedSelectionActions.Select(row, (mods & ModifierKeys.Control) == 0 ? SelectOp.Set : (mods & ModifierKeys.Shift) != 0 ? SelectOp.Subtract : SelectOp.Add);
                return true;
            case Key.C when mods == ModifierKeys.Control:
                if (row is not null) NamedSelectionActions.CopyName(row);
                return true;
            case Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown when mods == ModifierKeys.None:
            {
                int n = list.Items.Count;
                if (n == 0) return true;
                int i = list.SelectedIndex;
                i = key switch
                {
                    Key.Up => Math.Max(0, i - 1),
                    Key.Down => Math.Min(n - 1, i + 1),
                    Key.Home => 0,
                    Key.End => n - 1,
                    Key.PageUp => Math.Max(0, i - 15),
                    _ => Math.Min(n - 1, i + 15),
                };
                list.SelectedIndex = i;
                FocusSelectedItem();
                return true;
            }
            case Key.F when mods == ModifierKeys.Control:
                filter.Focus();
                filter.SelectAll();
                return true;
        }
        return false;
    }

    // ---- context menu ----------------------------------------------------------------------------

    static MenuItem Item(string header, Action action, bool enabled = true, string? glyph = null, string? gesture = null, bool? isChecked = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
        if (glyph is not null) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 13 };
        if (isChecked is { } c)
        {
            mi.IsCheckable = true;
            mi.IsChecked = c;
        }
        mi.Click += (_, _) => action();
        return mi;
    }

    void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var row = Current;
        bool has = row is not null;
        bool hasSel = Selection.PeekSelected(Ed.Lod) is { IsEmpty: false };
        menu.Items.Add(Item("_New…", NamedSelectionActions.New, glyph: ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("_Delete", () => NamedSelectionActions.Delete(row!, confirm: false), has, "", "Del"));
        menu.Items.Add(Item("Delete (_All LODs)", () => NamedSelectionActions.DeleteAllLods(row!), has));
        menu.Items.Add(Item("_Rename…", () => NamedSelectionActions.Rename(row!), has, "", "F2"));
        menu.Items.Add(Item("Rename (All _LODs)…", () => NamedSelectionActions.RenameAllLods(row!), has));
        menu.Items.Add(Item("Delete _Empty", NamedSelectionActions.DeleteEmpty, rows.Any(r => r.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rede_fine", () => NamedSelectionActions.Redefine(row!), has));
        menu.Items.Add(Item("Add _Selected", () => NamedSelectionActions.AddSelected(row!), has && hasSel));
        menu.Items.Add(Item("Re_move Selected", () => NamedSelectionActions.RemoveSelected(row!), has && hasSel));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("_Weights…", () => NamedSelectionActions.Weights(row!), has && hasSel));
        menu.Items.Add(Item("Show _Weights", () => NamedSelectionActions.ToggleShowWeights(row!), has, isChecked: has && NamedSelectionActions.ShowingWeights(row!.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("_Copy Name", () => NamedSelectionActions.CopyName(row!), has, "", "Ctrl+C"));
    }
}
