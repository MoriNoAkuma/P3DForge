using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Features;
using P3DForge.App.Viewport;
using P3DForge.App.Viewport.Rendering;

namespace P3DForge.App.Panels.Render;

/// <summary>
/// Render panel (window.render, Shift+F12): view mode and FOV, Final-mode render toggles and stage view, light
/// (preset, sun azimuth / elevation, colours, intensity), shadows, exposure and live frame statistics. Every control
/// edits a UI flag (see <see cref="RenderConfig"/>), so menus, automation and the viewport stay in sync.
/// </summary>
public sealed class RenderPanel : DockPanel
{
    static EditorState Ed => AppServices.Editor;
    static ViewportView? Vp => ViewportPlugins.Current as ViewportView;

    readonly List<Action> syncers = [];
    readonly TextBlock statsText;
    readonly TextBlock fovNote;
    bool syncing;
    long lastStatsTick;
    ViewportView? hookedViewport;

    public RenderPanel()
    {
        Background = Brushes.Transparent;
        var stack = new StackPanel { Margin = new Thickness(6, 2, 6, 8) };
        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Children.Add(scroll);

        // ---- Model ----
        stack.Children.Add(Header("Model"));
        stack.Children.Add(Combo("View mode",
            [("wire", "Wireframe (Alt+1)"), ("solid", "Solid (Alt+2)"), ("textured", "Textured (Alt+3)"), ("final", "Final — game-like (Alt+4)")],
            () => Ed.Flag("view.shading", "textured"), v => Ed.SetFlag("view.shading", v),
            "Final = rvmat materials, sun, shadows, tone mapping. The editing modes stay the default for modelling."));
        stack.Children.Add(Combo("FOV", [("default", "Default (45°)"), ("game", "Game (43°)")],
            () => RenderConfig.Str(RenderConfig.Fov), v => Ed.SetFlag(RenderConfig.Fov, v),
            "Perspective view field of view. Game = DayZ / Arma camera initFov 0.75 rad (43° vertical)."));
        fovNote = new TextBlock { FontSize = 11, Foreground = Dlg.Res("Editor.Dim"), Margin = new Thickness(96, 0, 0, 4) };
        stack.Children.Add(fovNote);

        // ---- Final render toggles ----
        stack.Children.Add(Header("Final render"));
        var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        toggles.Children.Add(Check("Diffuse", RenderConfig.FinalDiffuse, "Sun (diffuse) lighting"));
        toggles.Children.Add(Check("Specular", RenderConfig.FinalSpecular, "Specular highlights (material specular × _smdi)"));
        toggles.Children.Add(Check("Ambient", RenderConfig.FinalAmbient, "Hemispheric sky / ground ambient × _as"));
        toggles.Children.Add(Check("Env. reflection", RenderConfig.FinalEnv, "Environment reflection (procedural sky) × fresnel"));
        toggles.Children.Add(Check("Emissive", RenderConfig.FinalEmissive, "Material emmisive colour"));
        toggles.Children.Add(Check("Shadows", RenderConfig.FinalShadows, "Directional shadow map (model + proxies)"));
        toggles.Children.Add(Check("Wireframe", RenderConfig.FinalWire, "Edges over the shaded model"));
        toggles.Children.Add(Check("Grid", RenderConfig.FinalGrid, "Ground grid in Final mode"));
        toggles.Children.Add(Check("Points", RenderConfig.FinalPoints, "Show vertices in Final mode (selected faces stay tinted)"));
        toggles.Children.Add(Check("Flip normal Y", RenderConfig.FinalFlipGreen, "Flip the green channel of _nohq normal maps"));
        stack.Children.Add(toggles);
        stack.Children.Add(Combo("Back faces", [("auto", "Auto (cull opaque)"), ("show", "Show"), ("cull", "Cull")],
            () => RenderConfig.Str(RenderConfig.FinalBackFaces), v => Ed.SetFlag(RenderConfig.FinalBackFaces, v),
            "Auto culls back faces of opaque materials like the game; alpha-tested / two-sided materials stay visible."));
        var stages = RenderConfig.Stages.Select((s, i) => (s, RenderConfig.StageLabels[i])).ToArray();
        stack.Children.Add(Combo("Stage view", stages, () => RenderConfig.Str(RenderConfig.FinalStage), v =>
        {
            Ed.SetFlag(RenderConfig.FinalStage, v);
            if (v != "final" && Ed.Flag("view.shading", "textured") != "final") Ed.SetFlag("view.shading", "final");
        }, "Shows a single material channel (debugging): textures as stored, fresnel term, environment."));

        // ---- Light ----
        stack.Children.Add(Header("Light"));
        stack.Children.Add(Combo("Preset", [.. RenderConfig.Presets.Select(p => (p.Id, p.Label))],
            () => RenderConfig.CurrentPreset.Id, RenderConfig.ApplyPreset,
            "Buldozer = world sun + readable ambient; In-game = warmer sun, darker ambient; Inventory = light fixed to the camera; Headlight = classic editor look."));
        stack.Children.Add(SliderRow("Sun azimuth", RenderConfig.Azimuth, 0, 360, 1, "0°", "Clockwise from north (+Z). Alt+Shift+LMB drag in a view moves the sun."));
        stack.Children.Add(SliderRow("Sun elevation", RenderConfig.Elevation, -5, 90, 1, "0°", "Height above the horizon"));
        stack.Children.Add(SliderRow("Intensity", RenderConfig.Intensity, 0, 3, 0.05, "0.00", "Sun intensity"));
        stack.Children.Add(ColorRow("Sun colour", RenderConfig.SunColor));
        stack.Children.Add(SliderRow("Ambient", RenderConfig.Ambient, 0, 2.5, 0.05, "0.00", "Hemispheric ambient strength"));
        stack.Children.Add(ColorRow("Sky colour", RenderConfig.SkyColor));
        stack.Children.Add(ColorRow("Ground colour", RenderConfig.GroundColor));
        stack.Children.Add(SliderRow("Specular (edit)", RenderConfig.Specular, 0, 1, 0.01, "0.00", "Specular highlight of the Solid / Textured modes"));
        stack.Children.Add(Hint("Alt+Shift+LMB drag in a view: move the sun."));

        // ---- Shadows ----
        stack.Children.Add(Header("Shadows"));
        var sh = new WrapPanel();
        sh.Children.Add(Check("Enabled (Final)", RenderConfig.FinalShadows, "Shadow map in Final mode"));
        sh.Children.Add(Check("Solid / Textured too", RenderConfig.ShadowAllModes, "Also shadow the editing modes"));
        sh.Children.Add(Check("Soft (PCF)", RenderConfig.ShadowSoft, "12-tap percentage-closer filtering"));
        stack.Children.Add(sh);
        stack.Children.Add(Combo("Map size", [("1024", "1024"), ("2048", "2048"), ("4096", "4096")],
            () => RenderConfig.ShadowMapSize.ToString(CultureInfo.InvariantCulture),
            v => Ed.SetFlag(RenderConfig.ShadowSize, double.Parse(v, CultureInfo.InvariantCulture)), "Shadow map resolution"));
        stack.Children.Add(SliderRow("Strength", RenderConfig.ShadowStrength, 0, 1, 0.05, "0.00", "How dark shadowed areas get"));

        // ---- Post ----
        stack.Children.Add(Header("Post"));
        stack.Children.Add(SliderRow("Exposure (EV)", RenderConfig.Exposure, -4, 4, 0.1, "+0.0;-0.0;0.0", "Exposure bias in stops (Final mode, before ACES tone mapping)"));

        // ---- Stats ----
        stack.Children.Add(Header("Statistics"));
        var st = new WrapPanel();
        st.Children.Add(Check("Overlay", RenderConfig.Stats, "FPS, frame / CPU / GPU ms, draw calls, triangles, frame-time graph in the viewport"));
        st.Children.Add(Check("Redraw continuously", RenderConfig.StatsContinuous, "Benchmark: redraw every frame while the overlay is shown"));
        stack.Children.Add(st);
        statsText = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = Dlg.Res("Editor.Dim"), Margin = new Thickness(2, 2, 0, 4), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(statsText);
        var shot = new Button { Content = "Save screenshot…", Style = (Style)FindResource("CompactButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        shot.Click += (_, _) => RenderFeature.SaveScreenshot();
        stack.Children.Add(shot);

        Loaded += (_, _) =>
        {
            Ed.Changed += OnChanged;
            Hook();
            Sync();
        };
        Unloaded += (_, _) =>
        {
            Ed.Changed -= OnChanged;
            if (hookedViewport is not null) hookedViewport.FrameRendered -= OnFrame;
            hookedViewport = null;
        };
    }

    void Hook()
    {
        if (Vp is { } vp && !ReferenceEquals(vp, hookedViewport))
        {
            if (hookedViewport is not null) hookedViewport.FrameRendered -= OnFrame;
            hookedViewport = vp;
            vp.FrameRendered += OnFrame;
        }
    }

    void OnChanged(Change c)
    {
        if (c.HasFlag(Change.Ui)) Sync();
    }

    void OnFrame()
    {
        long now = Environment.TickCount64;
        if (now - lastStatsTick < 250 || hookedViewport is null) return;
        lastStatsTick = now;
        statsText.Text = hookedViewport.Stats.Format();
        UpdateFovNote();
    }

    void UpdateFovNote()
    {
        float fov = RenderConfig.Str(RenderConfig.Fov) == "game" ? RenderConfig.GameFovY : RenderConfig.DefaultFovY;
        float aspect = Vp?.ActivePane.Aspect ?? 16f / 9f;
        float h = RenderMath.HorizontalFov(fov, aspect) * 180f / MathF.PI;
        fovNote.Text = $"{fov * 180f / MathF.PI:F0}° vertical (horiz. {h:F0}° in the active view)";
    }

    void Sync()
    {
        syncing = true;
        try
        {
            foreach (var s in syncers) s();
            UpdateFovNote();
        }
        finally
        {
            syncing = false;
        }
    }

    // ---- builders ----

    TextBlock Header(string text) => new() { Text = text, Style = (Style)FindResource("PanelHeaderText"), Margin = new Thickness(0, 10, 0, 4) };

    static TextBlock Hint(string text) => new() { Text = text, FontSize = 11, Foreground = Dlg.Res("Editor.Faint"), Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };

    static Grid Row(string label, UIElement content, UIElement? trailing = null, string? tooltip = null)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), ToolTip = tooltip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        g.Children.Add(l);
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            g.Children.Add(trailing);
        }
        return g;
    }

    CheckBox Check(string label, string flag, string tooltip)
    {
        var cb = new CheckBox { Content = label, FontSize = 12, Margin = new Thickness(0, 2, 12, 2), ToolTip = tooltip, MinWidth = 0 };
        cb.Checked += (_, _) =>
        {
            if (!syncing) Ed.SetFlag(flag, true);
        };
        cb.Unchecked += (_, _) =>
        {
            if (!syncing) Ed.SetFlag(flag, false);
        };
        syncers.Add(() => cb.IsChecked = RenderConfig.Bool(flag));
        return cb;
    }

    Grid Combo(string label, (string Value, string Text)[] items, Func<string> get, Action<string> set, string tooltip)
    {
        var combo = new ComboBox { FontSize = 12, MinHeight = 24, Padding = new Thickness(6, 2, 6, 2) };
        foreach (var (value, text) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        combo.SelectionChanged += (_, _) =>
        {
            if (syncing || combo.SelectedItem is not ComboBoxItem { Tag: string v }) return;
            set(v);
        };
        syncers.Add(() =>
        {
            var cur = get();
            foreach (ComboBoxItem it in combo.Items)
                if ((string)it.Tag == cur)
                {
                    combo.SelectedItem = it;
                    return;
                }
            combo.SelectedIndex = -1;
        });
        return Row(label, combo, null, tooltip);
    }

    Grid SliderRow(string label, string flag, double min, double max, double step, string format, string tooltip)
    {
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        var value = new TextBox { Width = 52, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("NumberTextBox"), HorizontalContentAlignment = HorizontalAlignment.Right };
        string Fmt(double v) => v.ToString(format, CultureInfo.InvariantCulture);
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            double v = Math.Round(e.NewValue / step) * step;
            value.Text = Fmt(v);
            Ed.SetFlag(flag, Math.Round(v, 4));
        };
        void Commit()
        {
            var t = value.Text.Trim().TrimEnd('°');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                Ed.SetFlag(flag, Math.Round(Math.Clamp(v, min, max), 4));
            else Sync();
        }
        value.LostKeyboardFocus += (_, _) => Commit();
        value.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Commit();
        };
        syncers.Add(() =>
        {
            double v = RenderConfig.Float(flag);
            slider.Value = Math.Clamp(v, min, max);
            if (!value.IsKeyboardFocusWithin) value.Text = Fmt(v);
        });
        return Row(label, slider, value, tooltip);
    }

    Grid ColorRow(string label, string flag)
    {
        var swatch = new Border { Width = 22, Height = 18, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Dlg.Res("Editor.Faint"), Margin = new Thickness(0, 0, 6, 0) };
        var text = new TextBox { FontSize = 12, Style = (Style)FindResource("CompactTextBox"), FontFamily = (FontFamily)FindResource("MonoFont") };
        var panel = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Left);
        panel.Children.Add(swatch);
        panel.Children.Add(text);
        var popup = MakePalette(c => Ed.SetFlag(flag, c));
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            popup.PlacementTarget = swatch;
            popup.IsOpen = true;
        };
        swatch.Cursor = System.Windows.Input.Cursors.Hand;
        void Commit()
        {
            var t = text.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            var c = RenderConfig.ParseColor(t, new Vector3(-1));
            if (c.X >= 0) Ed.SetFlag(flag, RenderConfig.FormatColor(c));
            else Sync();
        }
        text.LostKeyboardFocus += (_, _) => Commit();
        text.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) Commit();
        };
        syncers.Add(() =>
        {
            var s = RenderConfig.Str(flag);
            var c = RenderConfig.ParseColor(s, Vector3.One);
            swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));
            if (!text.IsKeyboardFocusWithin) text.Text = RenderConfig.FormatColor(c);
        });
        return Row(label, panel, null, "Hex colour #RRGGBB (click the swatch for a palette)");
    }

    /// <summary>Small swatch palette (warm / neutral / cool light colours) in a popup.</summary>
    static Popup MakePalette(Action<string> pick)
    {
        string[] colors =
        [
            "#FFFFFF", "#FFF4E5", "#FFE6C4", "#FFD9A0", "#FFC37A", "#FFAA5A",
            "#E6EEFF", "#C8D8FF", "#A8C0F0", "#8E9BB0", "#7C90B4", "#5A6E96",
            "#A0A4AC", "#808080", "#5C564E", "#4A4237", "#3A3630", "#202020",
        ];
        var wrap = new WrapPanel { Width = 6 * 24 + 8, Margin = new Thickness(4) };
        var popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
        foreach (var c in colors)
        {
            var v = RenderConfig.ParseColor(c, Vector3.One);
            var b = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(2), CornerRadius = new CornerRadius(2), ToolTip = c,
                Background = new SolidColorBrush(Color.FromRgb((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255))),
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.MouseLeftButtonUp += (_, _) =>
            {
                pick(c);
                popup.IsOpen = false;
            };
            wrap.Children.Add(b);
        }
        popup.Child = new Border { Background = Dlg.Res("Editor.PanelBackground"), BorderBrush = Dlg.Res("Editor.Faint"), BorderThickness = new Thickness(1), Child = wrap };
        return popup;
    }
}
