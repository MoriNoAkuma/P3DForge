using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App;

public partial class MainWindow : Window
{
    static EditorState Ed => AppServices.Editor;
    readonly Dictionary<string, LayoutAnchorable> anchorables = [];
    /// <summary>The panel layout as built at startup, for Window ▸ Reset Layout.</summary>
    string? defaultLayout;

    public MainWindow()
    {
        InitializeComponent();
        Commands.Register(new Command
        {
            Id = "window.resetLayout",
            Label = "_Reset Layout",
            Description = "Puts every panel back where it was at startup: closed panels come back, floating panels dock again",
            Execute = ResetLayout,
        });
        BuildMenu();
        BuildToolbar();
        BuildPanels();
        defaultLayout = SaveLayout();
        PanelRegistry.FocusRequested += FocusPanel;
        Ed.Changed += OnEditorChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            UpdateStatus();
            if (App.StartupFile is not null) FileService.Open(App.StartupFile);
            Autosave.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Autosave.OfferRecovery);
            Automation.Run(this);
        };
        UpdateStatus();
    }

    // ---- menu -----------------------------------------------------------------------------------

    void BuildMenu()
    {
        foreach (var (title, all) in MenuModel.Menus)
        {
            var items = MenuModel.Visible(all, id => Commands.Get(id) is not null);
            if (items.Length == 0) continue;
            var top = new MenuItem { Header = title };
            Fill(top, items);
            MainMenu.Items.Add(top);
        }
    }

    static void Fill(MenuItem parent, MenuEntry[] items)
    {
        foreach (var e in items) parent.Items.Add(Create(e));
        parent.SubmenuOpened += (s, a) =>
        {
            if (!ReferenceEquals(a.OriginalSource, parent)) return;
            Refresh(parent);
        };
    }

    static object Create(MenuEntry e)
    {
        switch (e)
        {
            case SeparatorEntry:
                return new Separator();
            case SubmenuEntry s:
            {
                var m = new MenuItem { Header = Commands.MenuText(s.Title) };
                Fill(m, s.Items);
                return m;
            }
            case DynamicEntry d:
            {
                var m = new MenuItem { Header = d.Title, Tag = d };
                m.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
                m.SubmenuOpened += (_, _) =>
                {
                    m.Items.Clear();
                    foreach (var (label, action) in DynamicMenus.Get(d.Id))
                    {
                        var item = new MenuItem { Header = label.Replace("_", "__") };
                        item.Click += (_, _) => action();
                        m.Items.Add(item);
                    }
                    if (m.Items.Count == 0) m.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
                };
                return m;
            }
            case CommandEntry c:
            {
                var m = new MenuItem { Tag = c };
                m.Click += (_, _) => Commands.Run(c.Command);
                return m;
            }
            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>Updates labels, hotkey hints, enabled and checked states when a menu opens.</summary>
    static void Refresh(MenuItem parent)
    {
        foreach (var o in parent.Items)
        {
            if (o is not MenuItem { Tag: CommandEntry c } mi) continue;
            var cmd = Commands.Get(c.Command);
            mi.Header = Commands.MenuText(c.Label ?? cmd?.Label ?? c.Command);
            mi.InputGestureText = cmd?.Hotkeys.FirstOrDefault() ?? "";
            mi.IsEnabled = Commands.IsEnabled(c.Command);
            mi.IsCheckable = cmd?.IsChecked is not null;
            mi.IsChecked = Commands.IsChecked(c.Command);
            mi.ToolTip = cmd?.Description;
        }
    }

    // ---- toolbar --------------------------------------------------------------------------------

    void BuildToolbar()
    {
        string? group = null;
        foreach (var item in Toolbar.Items.OrderBy(i => i.Order))
        {
            if (group is not null && item.Group != group) MainToolbar.Items.Add(new Separator());
            group = item.Group;
            var cmd = Commands.Get(item.Command);
            var hot = cmd?.Hotkeys.FirstOrDefault();
            var b = new Button
            {
                Content = item.Glyph,
                Style = (Style)FindResource("IconButton"),
                ToolTip = $"{item.Tooltip ?? Commands.PlainText(cmd?.Label ?? item.Command)}{(hot is null ? "" : $" ({hot})")}",
                Tag = item,
            };
            b.Click += (_, _) => Commands.Run(item.Command);
            MainToolbar.Items.Add(b);
        }
        // Search-box look-alike: the obvious entry point when you do not know where a command lives.
        var faint = (Brush)FindResource("Editor.Faint");
        var find = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = "Find a command… (e.g. texture)", VerticalAlignment = VerticalAlignment.Center, Foreground = faint },
                    new TextBlock { Text = "Ctrl+Shift+P", VerticalAlignment = VerticalAlignment.Center, Foreground = faint, FontSize = 11, Margin = new Thickness(18, 0, 0, 0) },
                },
            },
            MinWidth = 300,
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = "Search every command by name and run it (Ctrl+Shift+P or F1)",
        };
        find.Click += (_, _) => Commands.Run("app.commandPalette");
        MainToolbar.Items.Add(new Separator());
        MainToolbar.Items.Add(find);
        RefreshToolbar();
    }

    void RefreshToolbar()
    {
        foreach (var o in MainToolbar.Items)
        {
            if (o is not Button { Tag: ToolbarItem item } b) continue;
            b.IsEnabled = Commands.IsEnabled(item.Command);
            bool on = Commands.IsChecked(item.Command);
            b.Background = on ? new SolidColorBrush(Color.FromArgb(0x40, 0x4F, 0x9C, 0xFF)) : Brushes.Transparent;
        }
    }

    // ---- panels ---------------------------------------------------------------------------------

    void BuildPanels()
    {
        // Panels with TabWith go after every other panel so their host pane exists; hiding happens last so a hidden
        // host still owns its pane.
        var ordered = PanelRegistry.All.OrderBy(p => p.TabWith is null ? 0 : 1).ThenBy(p => p.Order).ToList();
        foreach (var p in ordered)
        {
            var a = new LayoutAnchorable { Title = p.Title, ContentId = p.Id, Content = p.Create(), CanClose = true, CanHide = true };
            anchorables[p.Id] = a;
            if (p.TabWith is { } host && anchorables.TryGetValue(host, out var h) && h.Parent is LayoutAnchorablePane hostPane)
            {
                hostPane.Children.Add(a);
                continue;
            }
            switch (p.Location)
            {
                case PanelLocation.Left:
                    LeftGroup.Children.Add(new LayoutAnchorablePane(a));
                    break;
                case PanelLocation.Right:
                    RightGroup.Children.Add(new LayoutAnchorablePane(a));
                    break;
                default:
                    BottomPane.Children.Add(a);
                    break;
            }
        }
        foreach (var p in ordered)
            if (p.StartHidden) anchorables[p.Id].Hide();
        // Keep the panes' first tab (the host) selected.
        foreach (var pane in new[] { LeftGroup, RightGroup }.SelectMany(g => g.Children.OfType<LayoutAnchorablePane>()))
            if (pane.ChildrenCount > 0) pane.SelectedContentIndex = 0;
        if (BottomPane.ChildrenCount == 0) BottomPane.Parent?.RemoveChild(BottomPane);
        if (RightGroup.ChildrenCount == 0) RightGroup.Parent?.RemoveChild(RightGroup);
        if (LeftGroup.ChildrenCount == 0) LeftGroup.Parent?.RemoveChild(LeftGroup);
    }

    string SaveLayout()
    {
        using var w = new StringWriter();
        new XmlLayoutSerializer(Dock).Serialize(w);
        return w.ToString();
    }

    void ResetLayout()
    {
        if (defaultLayout is null) return;
        // Same content objects, new layout objects: the serializer hands every panel its existing content by ContentId.
        var contents = anchorables.ToDictionary(p => p.Key, p => p.Value.Content);
        var serializer = new XmlLayoutSerializer(Dock);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is { } id && contents.TryGetValue(id, out var c)) e.Content = c;
            else if (e.Model.ContentId == "viewport") e.Content = Viewport;
        };
        using (var r = new StringReader(defaultLayout)) serializer.Deserialize(r);
        anchorables.Clear();
        foreach (var a in Dock.Layout.Descendents().OfType<LayoutAnchorable>())
            if (a.ContentId is { } id) anchorables[id] = a;
        foreach (var a in Dock.Layout.Hidden)
            if (a.ContentId is { } id) anchorables[id] = a;
        Ed.Log("Panel layout reset");
    }

    void FocusPanel(string id)
    {
        if (!anchorables.TryGetValue(id, out var a)) return;
        if (a.IsHidden) a.Show();
        a.IsActive = true;
        a.IsSelected = true;
    }

    // ---- status ---------------------------------------------------------------------------------

    void OnEditorChanged(Change c)
    {
        UpdateStatus();
        RefreshToolbar();
    }

    void UpdateStatus()
    {
        var lod = Ed.Lod;
        Title = Ed.Title;
        StatusLod.Text = LodNames.Name(lod.Resolution);
        StatusCounts.Text = $"{lod.PointCount:N0} pts · {lod.FaceCount:N0} faces";
        var sel = Selection.PeekSelected(lod);
        StatusSelection.Text = $"sel {sel?.PointCount ?? 0:N0} / {sel?.FaceCount ?? 0:N0} ({(Ed.SelectMode == SelectMode.Points ? "points" : "faces")})";
        var last = Ed.LogEntries.Count > 0 ? Ed.LogEntries[^1] : null;
        if (last is not null && (DateTime.Now - last.Time).TotalSeconds < 20)
        {
            StatusMessage.Text = last.Message;
            StatusMessage.Foreground = last.Level switch
            {
                LogLevel.Error => (Brush)FindResource("Editor.Error"),
                LogLevel.Warn => (Brush)FindResource("Editor.Warn"),
                _ => (Brush)FindResource("Editor.Dim"),
            };
        }
    }

    // ---- input ----------------------------------------------------------------------------------

    static bool IsTextInput(object? source) => source is TextBox or PasswordBox or ComboBox { IsEditable: true } || source is DependencyObject d && IsInsideTextBox(d);

    static bool IsInsideTextBox(DependencyObject d)
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is TextBox or PasswordBox) return true;
        return false;
    }

    /// <summary>Keys that keep their native meaning while typing in a text field.</summary>
    static readonly HashSet<string> TextEditing = ["Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Delete", "Backspace", "Home", "End", "Left", "Right", "Up", "Down", "Enter", "Escape", "Tab"];

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var acc = Commands.ToAccelerator(key, Keyboard.Modifiers);
        if (acc is null) return;
        if (IsTextInput(Keyboard.FocusedElement))
        {
            bool modifier = acc.StartsWith("Ctrl+") || acc.StartsWith("Alt+") || (acc.StartsWith('F') && acc.Length <= 3);
            if (!modifier || TextEditing.Contains(acc)) return;
        }
        if (Keyboard.FocusedElement is MenuItem) return;
        var cmd = Commands.ForAccelerator(acc);
        if (cmd is null) return;
        e.Handled = true;
        Commands.Run(cmd.Id);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var path = files[0];
        if (SurfaceFeature.IsTextureFile(path) || SurfaceFeature.IsMaterialFile(path))
        {
            SurfaceFeature.Drop(path); // texture / material onto the selected faces
            return;
        }
        if (ImportExportFeature.IsImportable(path))
        {
            ImportExportFeature.Import(path); // .obj / .fbx into the model
            return;
        }
        if (!path.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase))
        {
            Ed.Log($"Dropped file is not a .p3d, texture or .rvmat: {path}", LogLevel.Warn);
            return;
        }
        if (FileService.ConfirmDiscard("open another file")) FileService.Open(path);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!FileService.ConfirmDiscard("exit")) e.Cancel = true;
    }
}
