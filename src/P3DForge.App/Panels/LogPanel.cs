using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using P3DForge.App.Editor;
using P3DForge.App.Shell;

namespace P3DForge.App.Panels;

/// <summary>Registers the Log panel (bottom dock) and its command window.log.</summary>
public sealed class LogFeature : IFeature
{
    public const string PanelId = "log";

    public int Order => 71;

    public void Install()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Log", Location = PanelLocation.Bottom, Order = 50, Create = () => new LogPanel(), StartHidden = true });
        Commands.Register(new Command
        {
            Id = "window.log",
            Label = "_Log",
            Execute = Show,
            Description = "Editor log: messages, warnings and errors with level filter, search and copy",
        });
    }

    static DockingManager? manager;

    /// <summary>
    /// Shows the panel. Work-around: the shell removes the empty bottom pane at startup, so a hidden bottom panel is
    /// re-shown into a detached pane (invisible); then the panel gets a new pane under the viewport.
    /// </summary>
    static void Show()
    {
        manager ??= FindManager(Application.Current.MainWindow);
        var a = manager?.Layout.Hidden.FirstOrDefault(x => x.ContentId == PanelId)
                ?? manager?.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(x => x.ContentId == PanelId);
        PanelRegistry.Focus(PanelId);
        if (manager is null || a is null || a.Root is not null) return;
        (a.Parent as ILayoutContainer)?.RemoveChild(a);
        var column = manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault()?.Parent as LayoutPanel;
        if (column is { Orientation: Orientation.Vertical }) column.Children.Add(new LayoutAnchorablePane(a) { DockHeight = new GridLength(200) });
        else a.AddToLayout(manager, AnchorableShowStrategy.Bottom);
        a.IsActive = true;
        a.IsSelected = true;
    }

    static DockingManager? FindManager(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is DockingManager m) return m;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindManager(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}

/// <summary>
/// Read-only view of <see cref="EditorState.LogEntries"/>: level filter (info / warnings / errors), text search, copy
/// (selected rows or everything visible), "Clear" (hides what is there now — the editor log itself is untouched) and
/// follow-the-tail auto-scroll. Updates incrementally on <see cref="Change.Log"/>.
/// Automation flag: "log.search" (search text).
/// </summary>
public sealed class LogPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>One visible row (brushes / strings precomputed for cheap virtualised templates).</summary>
    public sealed record Row(LogEntry Entry, string Time, string Level, Brush LevelBrush, string Message);

    readonly ObservableCollection<Row> rows = [];
    readonly ListBox list;
    readonly TextBox search;
    readonly ToggleButton showInfo, showWarn, showError, follow;
    readonly TextBlock count;
    readonly DispatcherTimer refreshTimer;

    LogEntry? lastSeen;  // last log entry already considered (reference)
    LogEntry? clearedAt; // entries up to this one are hidden by "Clear"
    bool rebuild = true;
    string? lastSearchFlag;

    static readonly Brush InfoBrush = Frozen(Color.FromRgb(0x9A, 0xA1, 0xAD));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public LogPanel()
    {
        Background = Brushes.Transparent;
        var s = AppServices.Settings;

        // ---- toolbar
        var bar = new DockPanel { Margin = new Thickness(4, 4, 4, 2), LastChildFill = true };
        showInfo = Toggle("Info", "Show information messages", s.Get("log.info", true));
        showWarn = Toggle("Warnings", "Show warnings", s.Get("log.warn", true));
        showError = Toggle("Errors", "Show errors", s.Get("log.error", true));
        follow = Toggle("Follow", "Keep the newest message in view", s.Get("log.follow", true));
        var copy = Button("Copy", "Copy the selected rows (or every visible row when nothing is selected)", Copy);
        var clear = Button("Clear", "Hide the messages logged so far (the log itself is kept)", Clear);
        var show = Button("Show all", "Show the messages hidden by Clear", () =>
        {
            clearedAt = null;
            Rebuild();
        });
        count = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var e in new UIElement[] { showInfo, showWarn, showError, Separator(), follow, copy, clear, show }) left.Children.Add(e);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(count, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(count);

        search = new TextBox { Style = (Style)FindResource("CompactTextBox"), ToolTip = "Search messages (case-insensitive; several words = all must match)", Margin = new Thickness(8, 0, 0, 0), MinWidth = 160 };
        var hint = new TextBlock { Text = "Search…", IsHitTestVisible = false, Foreground = Res("Editor.Faint"), Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var searchHost = new Grid();
        searchHost.Children.Add(search);
        searchHost.Children.Add(hint);
        search.TextChanged += (_, _) =>
        {
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rebuild();
        };
        bar.Children.Add(searchHost);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        // ---- list (virtualised)
        list = new ListBox
        {
            ItemsSource = rows,
            SelectionMode = SelectionMode.Extended,
            ItemTemplate = MakeTemplate(),
            ItemContainerStyle = MakeItemStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 2),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Copy()));
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copy", Copy));
        menu.Items.Add(MenuItem("Copy all visible", () => CopyRows(rows)));
        menu.Items.Add(MenuItem("Select all", list.SelectAll));
        list.ContextMenu = menu;
        Children.Add(list);

        foreach (var t in new[] { showInfo, showWarn, showError })
            t.Click += (_, _) =>
            {
                SaveOptions();
                Rebuild();
            };
        follow.Click += (_, _) =>
        {
            SaveOptions();
            if (follow.IsChecked == true) ScrollToEnd();
        };

        refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            Refresh();
        };
        Ed.Changed += OnChanged;
        Refresh();
    }

    // ---- construction helpers --------------------------------------------------------------------------------

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    ToggleButton Toggle(string text, string tip, bool on) =>
        new() { Content = text, IsChecked = on, ToolTip = tip, MinHeight = 24, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0), FontSize = 12 };

    Button Button(string text, string tip, Action click)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(2, 0, 2, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    static UIElement Separator() => new Border { Width = 1, Margin = new Thickness(6, 3, 6, 3), Background = Res("Editor.Faint") };

    static MenuItem MenuItem(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    DataTemplate MakeTemplate()
    {
        var mono = (FontFamily)FindResource("MonoFont");
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var time = new FrameworkElementFactory(typeof(TextBlock));
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Time)));
        time.SetValue(TextBlock.FontFamilyProperty, mono);
        time.SetValue(TextBlock.ForegroundProperty, Res("Editor.Faint"));
        time.SetValue(FrameworkElement.WidthProperty, 92.0);
        time.SetValue(DockPanel.DockProperty, Dock.Left);
        var level = new FrameworkElementFactory(typeof(TextBlock));
        level.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Level)));
        level.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Row.LevelBrush)));
        level.SetValue(TextBlock.FontFamilyProperty, mono);
        level.SetValue(FrameworkElement.WidthProperty, 48.0);
        level.SetValue(DockPanel.DockProperty, Dock.Left);
        var message = new FrameworkElementFactory(typeof(TextBlock));
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Message)));
        message.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(time);
        panel.AppendChild(level);
        panel.AppendChild(message);
        return new DataTemplate { VisualTree = panel };
    }

    Style MakeItemStyle()
    {
        var st = new Style(typeof(ListBoxItem), (Style)FindResource("CompactListBoxItem"));
        st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 0, 4, 0)));
        st.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 20.0));
        return st;
    }

    void SaveOptions()
    {
        var s = AppServices.Settings;
        s.Extra["log.info"] = showInfo.IsChecked == true;
        s.Extra["log.warn"] = showWarn.IsChecked == true;
        s.Extra["log.error"] = showError.IsChecked == true;
        s.Extra["log.follow"] = follow.IsChecked == true;
        s.Save();
    }

    // ---- updates ---------------------------------------------------------------------------------------------

    void OnChanged(Change c)
    {
        if ((c & Change.Ui) != 0)
        {
            var q = Ed.Flag<string?>("log.search", null);
            if (q is not null && q != lastSearchFlag)
            {
                lastSearchFlag = q;
                search.Text = q; // TextChanged → Rebuild
            }
        }
        if ((c & Change.Log) == 0) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnChanged(Change.Log));
            return;
        }
        if (!refreshTimer.IsEnabled) refreshTimer.Start(); // coalesce bursts (batch conversions log per file)
    }

    void Rebuild()
    {
        rebuild = true;
        Refresh();
    }

    bool Visible(LogEntry e, string[] terms)
    {
        bool levelOn = e.Level switch
        {
            LogLevel.Warn => showWarn.IsChecked == true,
            LogLevel.Error => showError.IsChecked == true,
            _ => showInfo.IsChecked == true,
        };
        if (!levelOn) return false;
        foreach (var t in terms)
            if (e.Message.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    static Row ToRow(LogEntry e) => new(
        e,
        e.Time.ToString("HH:mm:ss.fff"),
        e.Level switch { LogLevel.Warn => "WARN", LogLevel.Error => "ERROR", _ => "info" },
        e.Level switch { LogLevel.Warn => Res("Editor.Warn"), LogLevel.Error => Res("Editor.Error"), _ => InfoBrush },
        e.Message);

    /// <summary>Appends new entries (or rebuilds after a filter change / log trim).</summary>
    void Refresh()
    {
        var log = Ed.LogEntries;
        var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int start = 0;
        if (!rebuild && lastSeen is not null)
        {
            int at = IndexOf(log, lastSeen);
            if (at < 0) rebuild = true; // trimmed away: start over
            else start = at + 1;
        }
        if (rebuild)
        {
            rows.Clear();
            start = 0;
            if (clearedAt is not null)
            {
                int at = IndexOf(log, clearedAt);
                start = at < 0 ? 0 : at + 1;
            }
        }
        var added = new List<Row>();
        for (int i = start; i < log.Count; i++)
            if (Visible(log[i], terms)) added.Add(ToRow(log[i]));
        foreach (var r in added) rows.Add(r);
        // drop rows whose entries the editor trimmed (keeps memory bounded on long sessions)
        while (rows.Count > 2000) rows.RemoveAt(0);
        lastSeen = log.Count > 0 ? log[^1] : null;
        bool wasRebuild = rebuild;
        rebuild = false;
        int warn = 0, err = 0;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Level == LogLevel.Warn) warn++;
            else if (log[i].Level == LogLevel.Error) err++;
        }
        count.Text = $"{rows.Count} shown · {log.Count} total · {warn} warn · {err} err";
        if ((added.Count > 0 || wasRebuild) && follow.IsChecked == true) ScrollToEnd();
    }

    static int IndexOf(IReadOnlyList<LogEntry> log, LogEntry e)
    {
        for (int i = log.Count - 1; i >= 0; i--)
            if (ReferenceEquals(log[i], e)) return i;
        return -1;
    }

    void ScrollToEnd()
    {
        if (rows.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (rows.Count > 0) list.ScrollIntoView(rows[^1]);
        });
    }

    void Clear()
    {
        var log = Ed.LogEntries;
        clearedAt = log.Count > 0 ? log[^1] : null;
        Rebuild();
    }

    // ---- copy ------------------------------------------------------------------------------------------------

    void Copy()
    {
        var selected = list.SelectedItems.Cast<Row>().OrderBy(rows.IndexOf).ToList();
        CopyRows(selected.Count > 0 ? selected : rows);
    }

    static void CopyRows(IEnumerable<Row> source)
    {
        var sb = new StringBuilder();
        foreach (var r in source) sb.Append(r.Entry.Time.ToString("HH:mm:ss.fff")).Append(" [").Append(r.Entry.Level).Append("] ").AppendLine(r.Message);
        if (sb.Length == 0) return;
        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy (another app holds it): ignore, the user can retry
        }
    }
}
