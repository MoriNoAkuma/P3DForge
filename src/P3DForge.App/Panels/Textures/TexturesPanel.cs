using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Controls;
using AvalonDock.Layout;
using System.Windows.Threading;
using Microsoft.Win32;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Textures;
using P3DForge.App.Editor;
using P3DForge.Core.Imaging;
using P3DForge.Core.Paa;

namespace P3DForge.App.Panels.Textures;

/// <summary>
/// Texture tools (window.textures): open a .paa / image (button, file dialog or UI flag "textures.openPath"), info
/// (format, size, mips, suffix rules), preview with channel isolation (RGB, R, G, B, A, RGBA over a checkerboard) and
/// per-channel adjustments (brightness, contrast, gamma, levels, invert) with suffix-aware channel names
/// (_smdi: G = specular, B = glossiness; _nohq: normal X / Y; …). Save overwrites the .paa in its format (.bak when
/// Settings.BackupOnSave); Save as writes .paa (suffix rules) / .png / .tga. A save sets the UI flag
/// "textures.changed" = saved path so viewports reload it.
/// Automation flags: "textures.channel" (rgb|r|g|b|a|rgba), "textures.adjust" ("g:brightness=0.2,invert=1;a:gamma=2"),
/// "textures.saveAsPath" (target of textures.saveAs; only testdata/out/textures may be written).
/// </summary>
public sealed class TexturesPanel : DockPanel
{
    public const string OpenFlag = "textures.openPath";
    public const string ChangedFlag = "textures.changed";
    const int PreviewEdge = 1024;

    static EditorState Ed => AppServices.Editor;

    /// <summary>The live panel (null until the panel was first shown).</summary>
    public static TexturesPanel? Current { get; private set; }

    readonly TextBlock title, info, rules, status, adjustHeader;
    readonly Image image;
    readonly Border imageHost;
    readonly ScrollViewer scroller, outer;
    readonly Grid grid;
    readonly Expander levelsExpander;
    readonly FrameworkElement[] autoRows;
    const double PreviewMinHeight = 220;
    readonly CheckBox actualSize;
    readonly ToggleButton[] viewButtons;
    readonly Slider brightness, contrast, gamma, inBlack, inWhite, outBlack, outWhite;
    readonly CheckBox invert;
    readonly Button saveButton, saveAsButton, resetButton, floatButton;

    LoadedTexture? texture;
    RgbaImage? preview;
    readonly ChannelAdjust[] adjust = [ChannelAdjust.None, ChannelAdjust.None, ChannelAdjust.None, ChannelAdjust.None];
    int view = -1; // -1 RGB, 0..3 R,G,B,A, -2 RGBA
    bool syncing, busy;
    int renderVersion;
    string? lastOpenFlag, lastAdjustFlag, lastChannelFlag, lastShotFlag;
    bool? lastFloatFlag;
    readonly DispatcherTimer renderTimer;

    static readonly (string Key, string Label, int Channel)[] Views = [("rgb", "RGB", -1), ("r", "R", 0), ("g", "G", 1), ("b", "B", 2), ("a", "A", 3), ("rgba", "RGBA", -2)];

    public TexturesPanel()
    {
        Current = this;
        Background = Brushes.Transparent;
        renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        renderTimer.Tick += (_, _) =>
        {
            renderTimer.Stop();
            Render();
        };

        // Layout: a grid inside a vertical scroller. The grid is at least as tall as the pane; when the pane is short
        // (stacked with other right-hand panels) it grows to keep a usable preview and the pane scrolls instead.
        var grid = new Grid();
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = r == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        outer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid };
        outer.SizeChanged += (_, _) => FitGrid();
        Children.Add(outer);
        this.grid = grid;

        // row 0: toolbar + info
        var head = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 2) };
        bar.Children.Add(MakeButton("Open…", "Open a .paa / .png / .tga / .jpg texture", OpenWithDialog));
        saveButton = MakeButton("Save", "Overwrite the texture (.paa keeps its format; a .bak is kept when backups are on)", () => Save(null));
        saveAsButton = MakeButton("Save as…", "Save as .paa (format by suffix), .png or .tga", SaveAs);
        resetButton = MakeButton("Reset", "Discard all channel adjustments", ResetAll);
        bar.Children.Add(saveButton);
        bar.Children.Add(saveAsButton);
        bar.Children.Add(resetButton);
        floatButton = MakeButton("Float", "Pop the texture tools out into their own resizable window (again: dock back)", ToggleFloat);
        bar.Children.Add(floatButton);
        head.Children.Add(bar);
        title = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(6, 4, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, Text = "No texture" };
        info = new TextBlock { FontSize = 11.5, Margin = new Thickness(6, 2, 6, 0), TextWrapping = TextWrapping.Wrap, Foreground = Dlg.Res("Editor.Dim") };
        rules = new TextBlock { FontSize = 11.5, Margin = new Thickness(6, 1, 6, 2), TextWrapping = TextWrapping.Wrap, Foreground = Dlg.Res("Editor.Dim") };
        head.Children.Add(title);
        head.Children.Add(info);
        head.Children.Add(rules);
        Grid.SetRow(head, 0);
        grid.Children.Add(head);

        // row 1: channel view
        var views = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        viewButtons = new ToggleButton[Views.Length];
        for (int i = 0; i < Views.Length; i++)
        {
            int index = i;
            var b = new ToggleButton { Content = Views[i].Label, MinWidth = 38, Height = 24, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2), FontSize = 12 };
            b.Click += (_, _) => SetView(Views[index].Channel);
            viewButtons[i] = b;
            views.Children.Add(b);
        }
        actualSize = new CheckBox { Content = "1:1", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, ToolTip = $"Actual pixels of the preview (limited to {PreviewEdge} px); off = fit" };
        actualSize.Click += (_, _) => ApplyZoom();
        views.Children.Add(actualSize);
        Grid.SetRow(views, 1);
        grid.Children.Add(views);

        // row 2: preview
        image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        imageHost = new Border { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        scroller = new ScrollViewer { Content = imageHost, Margin = new Thickness(6, 2, 6, 2) };
        var frame = new Border { Child = scroller, BorderThickness = new Thickness(1), BorderBrush = Dlg.Res("Editor.Faint"), Margin = new Thickness(4, 2, 4, 2) };
        Grid.SetRow(frame, 2);
        grid.Children.Add(frame);
        ApplyZoom();

        // row 3: adjustments
        var adjustPanel = new StackPanel { Margin = new Thickness(6, 2, 6, 4) };
        adjustHeader = new TextBlock { Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(0, 4, 0, 2) };
        adjustPanel.Children.Add(adjustHeader);
        brightness = AddSlider(adjustPanel, "Brightness", -1, 1, 0, 0.01);
        contrast = AddSlider(adjustPanel, "Contrast", -1, 1, 0, 0.01);
        gamma = AddSlider(adjustPanel, "Gamma", 0.2, 5, 1, 0.01);
        var levels = new StackPanel();
        inBlack = AddSlider(levels, "In black", 0, 255, 0, 1);
        inWhite = AddSlider(levels, "In white", 0, 255, 255, 1);
        outBlack = AddSlider(levels, "Out black", 0, 255, 0, 1);
        outWhite = AddSlider(levels, "Out white", 0, 255, 255, 1);
        levelsExpander = new Expander { Header = "Levels", Content = levels, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), IsExpanded = AppServices.Settings.Get("textures.levelsOpen", false) };
        levelsExpander.Expanded += (_, _) => AppServices.Settings.Set("textures.levelsOpen", true);
        levelsExpander.Collapsed += (_, _) => AppServices.Settings.Set("textures.levelsOpen", false);
        adjustPanel.Children.Add(levelsExpander);
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        invert = new CheckBox { Content = "Invert", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        invert.Click += (_, _) => FromControls();
        var resetChannel = MakeButton("Reset channel", "Reset the adjustments of the edited channel(s)", ResetChannel);
        DockPanel.SetDock(resetChannel, Dock.Right);
        row.Children.Add(resetChannel);
        row.Children.Add(invert);
        adjustPanel.Children.Add(row);
        status = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Dlg.Res("Editor.Dim") };
        adjustPanel.Children.Add(status);
        Grid.SetRow(adjustPanel, 3);
        grid.Children.Add(adjustPanel);
        autoRows = [head, views, adjustPanel];
        foreach (var el in autoRows) el.SizeChanged += (_, e) => { if (e.HeightChanged) FitGrid(); };

        Ed.Changed += OnChanged;
        UpdateUi();
        OnChanged(Change.Ui);
    }

    // ---- UI construction helpers ---------------------------------------------------------------------------

    Button MakeButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("CompactButton"), Margin = new Thickness(2), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    Slider AddSlider(Panel parent, string label, double min, double max, double value, double step)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var name = new TextBlock { Text = label, Width = 70, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Width = 38, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("MonoFont") };
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, IsSnapToTickEnabled = step >= 1, TickFrequency = step, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, _) =>
        {
            val.Text = step >= 1 ? s.Value.ToString("0", CultureInfo.InvariantCulture) : s.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (!syncing) FromControls();
        };
        s.MouseDoubleClick += (_, _) => s.Value = value; // double-click resets the slider
        val.Text = step >= 1 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(val);
        row.Children.Add(s);
        parent.Children.Add(row);
        return s;
    }

    static readonly Brush Checker = MakeChecker();

    static Brush MakeChecker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var dark = new GeometryGroup();
        dark.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        dark.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)), null, dark));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    /// <summary>Grid height = pane height, but never less than the auto rows + a usable preview (the pane scrolls then).</summary>
    void FitGrid()
    {
        double h = outer.ViewportHeight > 0 ? outer.ViewportHeight : outer.ActualHeight;
        if (h <= 0) return;
        double autos = 0;
        foreach (var el in autoRows) autos += el.DesiredSize.Height;
        double target = Math.Max(h, autos + PreviewMinHeight);
        if (Math.Abs(grid.Height - target) > 0.5 || double.IsNaN(grid.Height)) grid.Height = target;
    }

    void ApplyZoom()
    {
        bool oneToOne = actualSize.IsChecked == true;
        image.Stretch = oneToOne ? Stretch.None : Stretch.Uniform;
        scroller.HorizontalScrollBarVisibility = oneToOne ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        scroller.VerticalScrollBarVisibility = oneToOne ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    // ---- state -----------------------------------------------------------------------------------------------

    TextureHint Hint => texture is null ? TextureHints.Default : TextureHints.ForPath(texture.Path);

    /// <summary>Source channels edited by the sliders (RGB / RGBA views edit R, G and B together).</summary>
    int[] EditedChannels => view switch
    {
        >= 0 => [view],
        _ => [0, 1, 2],
    };

    string ChannelName(int c)
    {
        var name = Hint.Channels[c];
        string letter = "RGBA"[c].ToString();
        return name.Equals(letter switch { "R" => "Red", "G" => "Green", "B" => "Blue", _ => "Alpha" }, StringComparison.Ordinal) ? letter : $"{letter} · {name}";
    }

    void UpdateUi()
    {
        bool has = texture is not null;
        saveButton.IsEnabled = saveAsButton.IsEnabled = resetButton.IsEnabled = has && !busy;
        for (int i = 0; i < Views.Length; i++)
        {
            viewButtons[i].IsChecked = Views[i].Channel == view;
            int c = Views[i].Channel;
            viewButtons[i].Content = c >= 0 ? ChannelName(c) : Views[i].Label;
        }
        imageHost.Background = view == -2 ? Checker : Brushes.Transparent;
        var edited = EditedChannels;
        adjustHeader.Text = "Adjust " + (edited.Length == 1 ? ChannelName(edited[0]) : "R, G, B together");
        syncing = true;
        var a = adjust[edited[0]];
        brightness.Value = a.Brightness;
        contrast.Value = a.Contrast;
        gamma.Value = a.Gamma;
        inBlack.Value = a.InBlack;
        inWhite.Value = a.InWhite;
        outBlack.Value = a.OutBlack;
        outWhite.Value = a.OutWhite;
        invert.IsChecked = a.Invert;
        syncing = false;
        bool modified = adjust.Any(x => !x.IsIdentity);
        title.Text = texture is null ? "No texture — Open… or pick one in the material editor" : Path.GetFileName(texture.Path) + (modified ? " (modified)" : "");
    }

    void FromControls()
    {
        if (syncing || texture is null) return;
        var a = new ChannelAdjust
        {
            Brightness = Math.Round(brightness.Value, 3),
            Contrast = Math.Round(contrast.Value, 3),
            Gamma = Math.Round(gamma.Value, 3),
            InBlack = (int)inBlack.Value,
            InWhite = (int)inWhite.Value,
            OutBlack = (int)outBlack.Value,
            OutWhite = (int)outWhite.Value,
            Invert = invert.IsChecked == true,
        };
        foreach (int c in EditedChannels) adjust[c] = a;
        UpdateUi();
        renderTimer.Stop();
        renderTimer.Start();
    }

    void SetView(int channel)
    {
        view = channel;
        UpdateUi();
        Render();
    }

    void ResetChannel()
    {
        foreach (int c in EditedChannels) adjust[c] = ChannelAdjust.None;
        UpdateUi();
        Render();
    }

    void ResetAll()
    {
        for (int c = 0; c < 4; c++) adjust[c] = ChannelAdjust.None;
        UpdateUi();
        Render();
    }

    // ---- flags -----------------------------------------------------------------------------------------------

    void OnChanged(Change c)
    {
        if ((c & Change.Ui) == 0) return;
        var open = Ed.Flag<string?>(OpenFlag, null);
        if (!string.IsNullOrWhiteSpace(open) && open != lastOpenFlag)
        {
            lastOpenFlag = open;
            Open(open);
        }
        var channel = Ed.Flag<string?>("textures.channel", null);
        if (channel is not null && channel != lastChannelFlag)
        {
            lastChannelFlag = channel;
            var v = Views.FirstOrDefault(x => x.Key.Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (v.Key is not null) SetView(v.Channel);
        }
        var fl = Ed.Flag<bool?>("textures.float", null);
        if (fl is { } wantFloat && wantFloat != lastFloatFlag)
        {
            lastFloatFlag = wantFloat;
            if (wantFloat != (Anchorable()?.IsFloating == true)) ToggleFloat();
        }
        var shot = Ed.Flag<string?>("textures.shot", null);
        if (!string.IsNullOrWhiteSpace(shot) && shot != lastShotFlag)
        {
            lastShotFlag = shot;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => CapturePanel(shot));
        }
        var adj = Ed.Flag<string?>("textures.adjust", null);
        if (adj is not null && adj != lastAdjustFlag)
        {
            lastAdjustFlag = adj;
            ApplyAdjustSpec(adj);
        }
    }

    /// <summary>"g:brightness=0.2,invert=1;a:gamma=2" (automation / scripting).</summary>
    void ApplyAdjustSpec(string spec)
    {
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon < 0) continue;
            var chans = part[..colon].Trim().ToLowerInvariant() switch
            {
                "r" => new[] { 0 },
                "g" => [1],
                "b" => [2],
                "a" => [3],
                "rgb" => [0, 1, 2],
                _ => [],
            };
            foreach (int ch in chans)
            {
                var a = adjust[ch];
                foreach (var kv in part[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = kv.Split('=', 2);
                    if (p.Length != 2 || !Dlg.TryParse(p[1], out double d)) continue;
                    a = p[0].Trim().ToLowerInvariant() switch
                    {
                        "brightness" => a with { Brightness = d },
                        "contrast" => a with { Contrast = d },
                        "gamma" => a with { Gamma = d },
                        "inblack" => a with { InBlack = (int)d },
                        "inwhite" => a with { InWhite = (int)d },
                        "outblack" => a with { OutBlack = (int)d },
                        "outwhite" => a with { OutWhite = (int)d },
                        "invert" => a with { Invert = d != 0 },
                        _ => a,
                    };
                }
                adjust[ch] = a;
            }
        }
        UpdateUi();
        Render();
    }

    // ---- open ------------------------------------------------------------------------------------------------

    public void OpenWithDialog()
    {
        if (Dlg.Automated) return;
        var start = texture is not null ? Path.GetDirectoryName(texture.Path) : AppServices.Settings.Get("textures.lastFolder", AppServices.Settings.ProjectRoot);
        var dlg = new OpenFileDialog { Title = "Open texture", Filter = TextureIO.TextureFilter, InitialDirectory = Directory.Exists(start) ? start : AppServices.Settings.ProjectRoot };
        if (dlg.ShowDialog(TextureIO.Owner) != true) return;
        AppServices.Settings.Set("textures.lastFolder", Path.GetDirectoryName(dlg.FileName) ?? "");
        Open(dlg.FileName);
    }

    /// <summary>Loads a texture (absolute or project-relative path) on a background thread.</summary>
    public async void Open(string path)
    {
        var abs = Path.IsPathRooted(path) ? path : AppServices.ResolveProjectPath(path) ?? path;
        if (!File.Exists(abs))
        {
            Ed.Log($"Textures: {path} not found", LogLevel.Warn);
            return;
        }
        busy = true;
        status.Text = $"Loading {Path.GetFileName(abs)}…";
        UpdateUi();
        try
        {
            var sw = Stopwatch.StartNew();
            var (tex, prev) = await Task.Run(() =>
            {
                var t = TextureIO.Load(abs);
                return (t, TextureIO.Fit(t.Image, PreviewEdge));
            });
            texture = tex;
            preview = prev;
            for (int c = 0; c < 4; c++) adjust[c] = ChannelAdjust.None;
            if (view == 3 && tex.Image.IsOpaque()) view = -1;
            info.Text = tex.Describe();
            rules.Text = $"Suffix rules: {Hint.Describe(!tex.Image.IsOpaque())}";
            status.Text = $"Loaded in {sw.ElapsedMilliseconds} ms";
            Ed.Log($"Textures: opened {abs}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or FileFormatException)
        {
            Ed.Log($"Textures: cannot open {Path.GetFileName(abs)}: {ex.Message}", LogLevel.Warn);
            status.Text = ex.Message;
        }
        finally
        {
            busy = false;
            UpdateUi();
            Render();
        }
    }

    // ---- preview ---------------------------------------------------------------------------------------------

    async void Render()
    {
        if (preview is null)
        {
            image.Source = null;
            return;
        }
        int version = ++renderVersion;
        var src = preview;
        var adj = adjust.ToArray();
        int v = view;
        var bmp = await Task.Run(() => TextureIO.ToBitmap(ImageOps.Isolate(ImageOps.Adjust(src, adj), v)));
        if (version != renderVersion) return;
        image.Source = bmp;
    }

    // ---- save ------------------------------------------------------------------------------------------------

    void SaveAs()
    {
        if (texture is null) return;
        string? target;
        if (Dlg.Automated) target = Ed.Flag<string?>("textures.saveAsPath", null);
        else
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save texture as",
                Filter = "PAA texture (*.paa)|*.paa|PNG image (*.png)|*.png|TGA image (*.tga)|*.tga",
                FileName = Path.GetFileNameWithoutExtension(texture.Path),
                InitialDirectory = Path.GetDirectoryName(texture.Path),
                AddExtension = true,
            };
            if (dlg.ShowDialog(TextureIO.Owner) != true) return;
            target = dlg.FileName;
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            Dlg.Fail("Textures: no target path");
            return;
        }
        Save(target);
    }

    /// <summary>Writes the adjusted full-resolution texture; null = overwrite the opened file (PAA keeps its format).</summary>
    public async void Save(string? target)
    {
        if (texture is null || busy) return;
        var tex = texture;
        bool overwrite = target is null;
        target ??= TextureIO.IsPaa(tex.Path) ? tex.Path : null;
        if (target is null)
        {
            SaveAs(); // images are never overwritten in place by "Save": pick a .paa / .png / .tga
            return;
        }
        if (!TextureIO.MayWrite(target))
        {
            Ed.Log($"Textures: automation may only write into {TextureIO.AutomationOutDir()} (refused {target})", LogLevel.Warn);
            return;
        }
        PaaFormat? format = overwrite && tex.Header is { } h ? PaaFormats.FromHeaderFormat(h.Format) : null;
        int resizeW = 0, resizeH = 0;
        if (TextureIO.IsPaa(target) && !tex.Image.HasPowerOfTwoSize)
        {
            resizeW = Math.Max(4, RgbaImage.NearestPowerOfTwo(tex.Image.Width));
            resizeH = Math.Max(4, RgbaImage.NearestPowerOfTwo(tex.Image.Height));
            if (!Dlg.Confirm("Save texture", $"PAA textures need power-of-two sizes; {Path.GetFileName(tex.Path)} is {tex.Image.Width}×{tex.Image.Height}. Resize to {resizeW}×{resizeH} and save?"))
            {
                status.Text = $"Not saved: {tex.Image.Width}×{tex.Image.Height} is not a power-of-two size";
                return;
            }
        }
        var adj = adjust.ToArray();
        busy = true;
        status.Text = $"Saving {Path.GetFileName(target)}…";
        UpdateUi();
        try
        {
            var sw = Stopwatch.StartNew();
            var saved = await Task.Run(() =>
            {
                var img = ImageOps.Adjust(tex.Image, adj);
                if (resizeW > 0) img = ImageOps.Resize(img, resizeW, resizeH);
                if (TextureIO.IsPaa(target) && PaaWriter.CheckSize(img.Width, img.Height, format ?? TextureHints.ForPath(target).ResolveFormat(!img.IsOpaque())) is { } error)
                    throw new InvalidOperationException(error);
                TextureIO.Write(target, TextureIO.Encode(img, target, format));
                return TextureIO.IsPaa(target) ? TextureIO.Load(target) : new LoadedTexture(target, img, null);
            });
            status.Text = $"Saved {Path.GetFileName(target)} in {sw.ElapsedMilliseconds} ms";
            Ed.Log($"Textures: saved {target} ({saved.Describe()}) in {sw.ElapsedMilliseconds} ms");
            TextureIO.NotifyChanged(target);
            if (TextureIO.IsPaa(target))
            {
                // continue editing the written file (its decoded pixels, adjustments baked in)
                texture = saved;
                preview = await Task.Run(() => TextureIO.Fit(saved.Image, PreviewEdge));
                for (int c = 0; c < 4; c++) adjust[c] = ChannelAdjust.None;
                info.Text = saved.Describe();
                rules.Text = $"Suffix rules: {Hint.Describe(!saved.Image.IsOpaque())}";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            status.Text = ex.Message;
            Ed.Log($"Textures: cannot save {Path.GetFileName(target)}: {ex.Message}", LogLevel.Warn);
        }
        finally
        {
            busy = false;
            UpdateUi();
            Render();
        }
    }

    // ---- docking ---------------------------------------------------------------------------------------------

    LayoutAnchorable? Anchorable()
    {
        for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is LayoutAnchorableControl c) return c.Model as LayoutAnchorable;
        return null;
    }

    /// <summary>Floats the panel in its own window (sized for a usable preview) or docks it back.</summary>
    void ToggleFloat()
    {
        var a = Anchorable();
        if (a is null) return;
        if (a.IsFloating)
        {
            a.Dock();
            floatButton.Content = "Float";
            return;
        }
        var main = TextureIO.Owner;
        a.FloatingWidth = 720;
        a.FloatingHeight = 860;
        if (main is not null)
        {
            a.FloatingLeft = main.Left + Math.Max(0, main.ActualWidth - 720 - 380);
            a.FloatingTop = main.Top + 90;
        }
        a.Float();
        floatButton.Content = "Dock";
    }

    /// <summary>Automation: renders the panel into P3DFORGE_DIALOG_SHOTS/&lt;name&gt;.png.</summary>
    void CapturePanel(string name)
    {
        var dir = Environment.GetEnvironmentVariable("P3DFORGE_DIALOG_SHOTS");
        if (string.IsNullOrEmpty(dir) || ActualWidth < 1 || ActualHeight < 1) return;
        try
        {
            int w = (int)Math.Ceiling(ActualWidth), h = (int)Math.Ceiling(ActualHeight);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(dir);
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ed.Log($"Textures: capture failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>For commands: true when a texture is loaded and nothing is running.</summary>
    public bool CanSave => texture is not null && !busy;

    public void SaveAsCommand() => SaveAs();
    public void ResetCommand() => ResetAll();
}
