using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using P3DForge.App.Dialogs.Faces;
using P3DForge.App.Editor;
using P3DForge.Core.Ops;

namespace P3DForge.App.Dialogs.Resources;

/// <summary>
/// Texture / material picker used by Face Properties "Browse…": the paths already used by the model (with face
/// counts and thumbnails) next to the lazy project-root browser. Returns a model path ("dz\…\x_co.paa").
/// <para>Automation: captured as "resource-picker"; returns "automation.answer" (or null when unset).</para>
/// </summary>
public static class ResourcePicker
{
    public static string[] TextureExtensions => [".paa", ".pac", ".tga", ".png"];
    public static string[] MaterialExtensions => [".rvmat"];

    public static string? Pick(Window? owner, ResourceKind kind, string current)
    {
        var ed = AppServices.Editor;
        bool tex = kind == ResourceKind.Texture;
        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Put(UIElement e, int row, int col, int colSpan = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, colSpan);
            root.Children.Add(e);
        }

        // ---- used by the model ----
        var used = Attributes.Usage(ed.Model).Where(u => u.Kind == kind).ToList();
        var usedRows = new ObservableCollection<FileRow>(used.Select(u => new FileRow
        {
            Name = u.Path,
            Path = u.Path,
            Detail = $"{u.TotalFaces:N0} faces · {u.LodCount} LOD{(u.LodCount == 1 ? "" : "s")}",
            Glyph = tex ? "" : "",
            WantsThumb = tex,
            ToolTip = u.Path,
        }));
        Put(new TextBlock { Text = $"Used in this model ({usedRows.Count})", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }, 0, 0);
        var usedList = new ListBox
        {
            ItemsSource = usedRows,
            ItemTemplate = RowTemplates.Row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = (Style)Application.Current.FindResource("CompactListBoxItem"),
            BorderBrush = Dlg.Res("Editor.Faint"),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(usedList, ScrollBarVisibility.Disabled);
        Put(usedList, 1, 0);

        // ---- project browser ----
        Put(new TextBlock { Text = $"Project ({AppServices.Settings.ProjectRoot})", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }, 0, 2);
        var browser = new ProjectBrowser(tex ? TextureExtensions : MaterialExtensions, 0.7);
        var browserBorder = new Border { Child = browser, BorderBrush = Dlg.Res("Editor.Faint"), BorderThickness = new Thickness(1) };
        Put(browserBorder, 1, 2);
        Put(new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent }, 1, 1);

        // ---- path + preview ----
        var path = Ui.Mono(current, 520);
        path.HorizontalAlignment = HorizontalAlignment.Stretch;
        path.Width = double.NaN;
        var preview = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform };
        var info = new TextBlock { FontSize = 11, Foreground = Dlg.Res("Editor.Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previewBorder = new Border { Width = 66, Height = 66, BorderBrush = Dlg.Res("Editor.Faint"), BorderThickness = new Thickness(1), Child = preview, Visibility = tex ? Visibility.Visible : Visibility.Collapsed };
        bottom.Children.Add(previewBorder);
        var pathPanel = new StackPanel { Margin = new Thickness(tex ? 10 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        pathPanel.Children.Add(new TextBlock { Text = tex ? "Texture path (relative to the project root)" : "Material path (relative to the project root)", FontSize = 11, Foreground = Dlg.Res("Editor.Dim"), Margin = new Thickness(0, 0, 0, 3) });
        pathPanel.Children.Add(path);
        pathPanel.Children.Add(info);
        Grid.SetColumn(pathPanel, 1);
        bottom.Children.Add(pathPanel);
        Put(bottom, 2, 0, 3);

        var w = Dlg.MakeWindow(tex ? "Select Texture" : "Select Material", root);
        w.SizeToContent = SizeToContent.Manual;
        w.ResizeMode = ResizeMode.CanResizeWithGrip;
        w.Width = 960;
        w.Height = 620;
        if (owner is not null && owner.IsVisible) w.Owner = owner;
        var (buttons, ok) = Dlg.OkCancel(w, () => path.Text.Trim().Length > 0, "Select");
        Put(buttons, 3, 0, 3);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        void UpdateInfo()
        {
            var p = path.Text.Trim();
            ok.IsEnabled = p.Length > 0;
            preview.Source = null;
            info.Foreground = Dlg.Res("Editor.Dim");
            if (p.Length == 0)
            {
                info.Text = "";
                return;
            }
            if (p.StartsWith('#'))
            {
                info.Text = "procedural texture";
                preview.Source = Thumbnails.Procedural(p);
                return;
            }
            var abs = AppServices.ResolveProjectPath(p);
            if (abs is null || !File.Exists(abs))
            {
                info.Text = "file not found under the project root — wrong first folder? Surfaces ▸ Fix Missing Texture Paths can find it";
                info.Foreground = Dlg.Res("Editor.Warn");
                return;
            }
            info.Text = abs;
            if (tex)
                Thumbnails.RequestFile(abs, 128, img =>
                {
                    if (string.Equals(path.Text.Trim(), p, StringComparison.Ordinal)) preview.Source = img;
                });
        }
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateInfo();
        };
        path.TextChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        w.Closed += (_, _) => timer.Stop();

        usedList.SelectionChanged += (_, _) =>
        {
            if (usedList.SelectedItem is FileRow r) path.Text = r.Path;
        };
        usedList.MouseDoubleClick += (_, _) =>
        {
            if (usedList.SelectedItem is FileRow r)
            {
                path.Text = r.Path;
                w.DialogResult = true;
            }
        };
        browser.FileSelected += abs =>
        {
            if (abs is not null) path.Text = AppServices.ToProjectPath(abs);
        };
        browser.FileActivated += abs =>
        {
            path.Text = AppServices.ToProjectPath(abs);
            w.DialogResult = true;
        };
        w.Loaded += async (_, _) =>
        {
            UpdateInfo();
            var cur = current.Trim();
            var match = usedRows.FirstOrDefault(r => Attributes.PathKey(r.Path) == Attributes.PathKey(cur));
            if (match is not null)
            {
                usedList.SelectedItem = match;
                usedList.ScrollIntoView(match);
                path.Text = cur; // keep the exact spelling
            }
            var abs = cur.Length > 0 ? AppServices.ResolveProjectPath(cur) : null;
            if (abs is not null && File.Exists(abs))
            {
                await browser.Reveal(abs);
                path.Text = cur;
            }
            path.Focus();
            path.CaretIndex = path.Text.Length;
        };

        if (Dlg.Automated)
        {
            w.ShowActivated = false;
            w.Show();
            Pump(1500);
            Dlg.Capture(w, tex ? "resource-picker-texture" : "resource-picker-material");
            var a = Dlg.TakeAnswer();
            ed.Log($"[automation] resource picker → {a ?? "(cancel)"}");
            return a is null ? null : AppServices.NormalizeGamePath(a);
        }
        return w.ShowDialog() == true ? AppServices.NormalizeGamePath(path.Text) : null; // never "P:\..." in the model
    }

    /// <summary>Automation only: keeps the dispatcher running for <paramref name="ms"/> (async loads, thumbnails).</summary>
    public static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            frame.Continue = false;
        };
        t.Start();
        Dispatcher.PushFrame(frame);
    }
}
