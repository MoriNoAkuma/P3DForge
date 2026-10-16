using System.IO;
using System.Windows.Threading;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Panels.Render;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.App.Viewport.Rendering;

namespace P3DForge.App.Features;

/// <summary>
/// Game-like rendering: the Render panel (window.render, Shift+F12), light presets, Final-mode toggles, stats overlay
/// (view.stats), screenshots (view.saveScreenshot) and Game FOV. Options are UI flags persisted by <see cref="RenderConfig"/>.
/// </summary>
public sealed class RenderFeature : IFeature
{
    public const string PanelId = "render";
    static EditorState Ed => AppServices.Editor;
    static ViewportView? Vp => ViewportPlugins.Current as ViewportView;

    DispatcherTimer? saveTimer;

    static Command Toggle(string id, string label, string flag, params string[] keys) => new()
    {
        Id = id,
        Label = label,
        Hotkeys = keys,
        IsChecked = () => RenderConfig.Bool(flag),
        Execute = () => Ed.SetFlag(flag, !RenderConfig.Bool(flag)),
    };

    static Command PresetCmd(LightPreset p) => new()
    {
        Id = "view.light." + p.Id,
        Label = p.Label,
        IsChecked = () => RenderConfig.CurrentPreset.Id == p.Id,
        Execute = () => RenderConfig.ApplyPreset(p.Id),
    };

    public void Install()
    {
        RenderConfig.Load();
        Ed.Changed += c =>
        {
            if (c.HasFlag(Change.Ui)) ScheduleSave();
        };

        PanelRegistry.Register(new PanelDescriptor
        {
            Id = PanelId,
            Title = "Render",
            Location = PanelLocation.Right,
            TabWith = "objects",
            Order = 40,
            Create = () => new RenderPanel(),
            StartHidden = true,
        });

        Commands.Register(
            new Command { Id = "window.render", Label = "_Render", Hotkeys = ["Shift+F12"], Execute = () => PanelRegistry.Focus(PanelId) },
            Toggle("view.stats", "Render _Statistics", RenderConfig.Stats),
            new Command
            {
                Id = "view.saveScreenshot",
                Label = "Save Screens_hot…",
                CanExecute = () => Vp is not null,
                Execute = SaveScreenshot,
            },
            new Command
            {
                Id = "view.gameFov",
                Label = "_Game FOV (43°)",
                IsChecked = () => RenderConfig.Str(RenderConfig.Fov) == "game",
                Execute = () => Ed.SetFlag(RenderConfig.Fov, RenderConfig.Str(RenderConfig.Fov) == "game" ? "default" : "game"),
            },
            Toggle("view.final.shadows", "Final: _Shadows", RenderConfig.FinalShadows),
            Toggle("view.final.wire", "Final: _Wireframe Overlay", RenderConfig.FinalWire),
            Toggle("view.final.grid", "Final: _Grid", RenderConfig.FinalGrid),
            Toggle("view.final.points", "Final: _Points", RenderConfig.FinalPoints),
            new Command
            {
                Id = "view.final.nextStage",
                Label = "Final: Next _Stage View",
                Execute = () =>
                {
                    int i = Array.IndexOf(RenderConfig.Stages, RenderConfig.Str(RenderConfig.FinalStage));
                    Ed.SetFlag(RenderConfig.FinalStage, RenderConfig.Stages[(i + 1) % RenderConfig.Stages.Length]);
                    if (Ed.Flag("view.shading", "textured") != "final") Ed.SetFlag("view.shading", "final");
                },
            });
        foreach (var p in RenderConfig.Presets) Commands.Register(PresetCmd(p));
    }

    /// <summary>Saves the render options shortly after the last UI change (slider drags change flags continuously).</summary>
    void ScheduleSave()
    {
        if (saveTimer is null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (_, _) =>
            {
                saveTimer.Stop();
                try
                {
                    RenderConfig.SaveIfChanged();
                }
                catch (Exception ex)
                {
                    Ed.Log("Could not save render options: " + ex.Message, LogLevel.Warn);
                }
            };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>PNG of the viewport (all panes + overlays). Under automation the path comes from "automation.answer".</summary>
    public static void SaveScreenshot()
    {
        if (Vp is not { } vp) return;
        string? path;
        var baseName = Path.GetFileNameWithoutExtension(Ed.Path ?? "viewport");
        if (string.IsNullOrEmpty(baseName)) baseName = "viewport";
        var name = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        if (Dlg.Automated)
        {
            path = Dlg.TakeAnswer();
            if (path is null)
            {
                Ed.Log("[automation] screenshot cancelled (no automation.answer)", LogLevel.Warn);
                return;
            }
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Screenshot",
                Filter = "PNG image (*.png)|*.png",
                FileName = name,
                DefaultExt = ".png",
                AddExtension = true,
            };
            if (Ed.Path is { } doc && Path.GetDirectoryName(doc) is { Length: > 0 } dir) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
            path = dlg.FileName;
        }
        vp.SaveScreenshot(path);
    }
}
