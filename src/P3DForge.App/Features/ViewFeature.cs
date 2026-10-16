using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.Core;

namespace P3DForge.App.Features;

/// <summary>
/// View commands: camera presets per pane, four views (F9), pane cycling, framing, zoom, look-at-pin,
/// shading modes (wire / solid / textured / final) and display toggles (proxies, memory labels, normals, hidden,
/// weights heat map, background LOD ghost).
/// </summary>
public sealed class ViewFeature : IFeature
{
    static EditorState Ed => AppServices.Editor;
    static ViewportView? Vp => ViewportPlugins.Current as ViewportView;

    static Command Preset(string id, string label, ViewKind kind, params string[] keys) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = keys,
        CanExecute = () => Vp is not null,
        IsChecked = () => Vp?.Camera.Kind == kind,
        Execute = () => Vp?.SetViewKind(kind),
    };

    static Command Toggle(string id, string label, string flag, bool fallback, params string[] keys) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = keys,
        IsChecked = () => Ed.Flag(flag, fallback),
        Execute = () => Ed.SetFlag(flag, !Ed.Flag(flag, fallback)),
    };

    static Command Shading(string id, string label, string mode, string key) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = [key],
        IsChecked = () => Ed.Flag("view.shading", "textured") == mode,
        Execute = () => Ed.SetFlag("view.shading", mode),
    };

    static Command ViewCmd(string id, string label, Action<ViewportView> run, params string[] keys) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = keys,
        CanExecute = () => Vp is not null,
        Execute = () =>
        {
            if (Vp is { } vp) run(vp);
        },
    };

    /// <summary>"view.showWeights" holds a selection name (SelectionsFeature), true (= current selection) or null.</summary>
    static bool WeightsOn => Ed.Flag<object?>("view.showWeights", null) switch
    {
        string s => s.Length > 0,
        bool b => b,
        _ => false,
    };

    public void Install()
    {
        Commands.Register(
            Preset("view.front", "_Front", ViewKind.Front, "Num1"),
            Preset("view.back", "_Back", ViewKind.Back, "Ctrl+Num1"),
            Preset("view.left", "_Left", ViewKind.Left, "Ctrl+Num3"),
            Preset("view.right", "_Right", ViewKind.Right, "Num3"),
            Preset("view.top", "_Top", ViewKind.Top, "Num7"),
            Preset("view.bottom", "Bott_om", ViewKind.Bottom, "Ctrl+Num7"),
            Preset("view.projected", "_Projected", ViewKind.Perspective, "Num0"),
            new Command
            {
                Id = "view.togglePerspective",
                Label = "Perspective / Orthographic",
                Hotkeys = ["Num5"],
                CanExecute = () => Vp is not null,
                IsChecked = () => Vp?.Camera.IsOrtho == true,
                Execute = () =>
                {
                    if (Vp is null) return;
                    Vp.Camera.ForceOrtho = !Vp.Camera.ForceOrtho;
                    Vp.SetViewKind(Vp.Camera.Kind);
                },
            },
            ViewCmd("view.frameAll", "Center && Zoom: _Drawing", v => v.FrameAll(false), "Home", "Ctrl+NumMultiply"),
            ViewCmd("view.frameSelection", "Center && Zoom: _Selection", v => v.FrameSelection(), "NumMultiply", "Shift+F"),
            ViewCmd("view.frameAllViews", "Center && Zoom: _All Views", v => v.FrameAll(true), "Alt+NumMultiply"),
            ViewCmd("view.zoomIn", "Zoom _In", v => v.Zoom(0.8f), "NumAdd"),
            ViewCmd("view.zoomOut", "Zoom _Out", v => v.Zoom(1.25f), "NumSubtract"),
            new Command
            {
                Id = "view.lookAtPin",
                Label = "_Look At Pin",
                Hotkeys = ["Space"],
                CanExecute = () => Vp is not null && Ed.Pin is not null,
                Execute = () =>
                {
                    if (Vp is { } v && Ed.Pin is { } pin) v.LookAt(pin);
                },
            },
            new Command
            {
                Id = "view.toggleQuad",
                Label = "_Four Views (F, L, T, 3D)",
                Hotkeys = ["F9"],
                CanExecute = () => Vp is not null,
                IsChecked = () => Vp?.IsQuad == true,
                Execute = () => Vp?.ToggleQuad(),
            },
            ViewCmd("view.nextPane", "_Next View", v => v.CyclePane(1), "Tab"),
            ViewCmd("view.prevPane", "_Previous View", v => v.CyclePane(-1), "Shift+Tab"),
            Shading("view.shadingWire", "_Wireframe", "wire", "Alt+1"),
            Shading("view.shadingSolid", "_Solid", "solid", "Alt+2"),
            Shading("view.shadingTextured", "_Textured", "textured", "Alt+3"),
            Shading("view.shadingFinal", "F_inal (Game-like)", "final", "Alt+4"),
            Toggle("view.backfaceCulling", "Face _Culling", "view.backfaceCulling", false, "F"),
            Toggle("view.showBackfaces", "Highlight _Back Faces", "view.showBackfaces", true),
            Toggle("view.showGrid", "_Grid", "view.showGrid", AppServices.Settings.ShowGrid),
            Toggle("view.showPoints", "Show _Points", "view.showPoints", false),
            Toggle("view.showNormals", "Show _Normals", "view.showNormals", false),
            Toggle("view.showProxies", "Show P_roxies", "view.showProxies", true),
            Toggle("view.showMemoryLabels", "Show _Memory Point Names", "view.showMemoryLabels", true),
            Toggle("view.showHidden", "Show _Hidden", "view.showHidden", false),
            new Command
            {
                Id = "view.showWeights",
                Label = "Show _Weights",
                IsChecked = () => WeightsOn,
                Execute = () => Ed.SetFlag("view.showWeights", WeightsOn ? null : true),
            },
            new Command
            {
                Id = "view.backgroundLod",
                Label = "Show LOD at Bac_kground…",
                CanExecute = () => Ed.Model.Lods.Length > 1,
                IsChecked = () => BackgroundLod >= 0,
                Execute = PickBackgroundLod,
            });

        Toolbar.Add(new ToolbarItem { Command = "view.frameAll", Glyph = "", Group = "view", Order = 40, Tooltip = "Frame all" });
        Toolbar.Add(new ToolbarItem { Command = "view.shadingWire", Glyph = "", Group = "view", Order = 41 });
        Toolbar.Add(new ToolbarItem { Command = "view.shadingSolid", Glyph = "", Group = "view", Order = 42 });
        Toolbar.Add(new ToolbarItem { Command = "view.shadingTextured", Glyph = "", Group = "view", Order = 43 });
        Toolbar.Add(new ToolbarItem { Command = "view.shadingFinal", Glyph = "", Group = "view", Order = 43, Tooltip = "Final shading: rvmat materials, sun, shadows (Alt+4)" });
        Toolbar.Add(new ToolbarItem { Command = "view.backfaceCulling", Glyph = "", Group = "view", Order = 44 });
        Toolbar.Add(new ToolbarItem { Command = "view.toggleQuad", Glyph = "", Group = "view", Order = 45 });
    }

    static int BackgroundLod => LodFeature.EffectiveBackgroundLod;

    /// <summary>Object Builder "Show LOD at Background…": pick a LOD drawn as a yellow frame over the active one.</summary>
    static void PickBackgroundLod()
    {
        var list = new ListBox { MinHeight = 220, Margin = new Thickness(0, 0, 0, 8) };
        list.Items.Add("(none)");
        var lods = Ed.Model.Lods;
        for (int i = 0; i < lods.Length; i++)
            list.Items.Add($"{i}: {LodNames.Name(lods[i].Resolution)}{(i == Ed.ActiveLod ? "  (active)" : "")}");
        list.SelectedIndex = BackgroundLod + 1;
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        var dlg = new Window
        {
            Title = "Show LOD at Background",
            Content = panel,
            Width = 340,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = Application.Current.MainWindow,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        list.MouseDoubleClick += (_, _) => dlg.DialogResult = true;
        if (dlg.ShowDialog() != true) return;
        int sel = list.SelectedIndex - 1;
        if (sel >= 0) Ed.SetFlag(LodFeature.BackgroundFlag, sel);
        else LodFeature.ClearBackground(); // "(none)" also turns off the automatic first-LOD frame
    }
}
