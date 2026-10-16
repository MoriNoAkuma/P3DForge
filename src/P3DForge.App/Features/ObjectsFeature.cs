using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Scene;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;

namespace P3DForge.App.Features;

/// <summary>
/// Proxy models as objects: <see cref="ProxySceneHost"/> (instances of the active LOD for the render core), the Objects
/// window (Shift+F9), proxy picking + selection boxes in the viewport, Reload Proxies, Show Proxy Models.
/// Options persist in Settings.Extra["objects.enabled" | "objects.selectionBox" | "objects.selectionTint"].
/// </summary>
public sealed class ObjectsFeature : IFeature
{
    public int Order => 60;
    public const string PanelId = "objects";
    public const string ReloadProxiesId = "objects.reloadProxies";
    public const string ToggleProxiesId = "objects.toggleProxies";
    public const string FileReloadId = "file.reload";
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        var s = AppServices.Settings;
        ProxyScene.Enabled = s.Get("objects.enabled", true);
        ProxyScene.SelectionBox = s.Get("objects.selectionBox", true);
        ProxyScene.SelectionTint = s.Get("objects.selectionTint", true);
        ProxySceneHost.Install();
        if (System.Windows.Application.Current is { } app) app.Activated += (_, _) => OnAppActivated();

        PanelRegistry.Register(new PanelDescriptor { Id = PanelId, Title = "Objects", Location = PanelLocation.Right, Order = 15, Create = () => new ObjectsPanel() });
        ViewportPlugins.Register(ProxyViewport.Attach);

        Commands.Register(
            new Command { Id = "window.objects", Label = "_Objects", Hotkeys = ["Shift+F9"], Execute = () => PanelRegistry.Focus(PanelId), Description = "Proxy models of the active LOD: visibility, selection, open" },
            new Command
            {
                Id = ToggleProxiesId,
                Label = "Show Proxy _Models",
                Description = "Draw the models referenced by proxies in the viewport",
                Execute = () => SetOption("enabled", !ProxyScene.Enabled),
                IsChecked = () => ProxyScene.Enabled,
            },
            new Command
            {
                Id = "objects.logInstances",
                Label = "Log Proxy Instances",
                Description = "Writes the proxy instances of the active LOD (status, position, screen point) to the log",
                Execute = LogInstances,
            },
            new Command
            {
                Id = "objects.hideSelected",
                Label = "Hide Selected Proxy Models",
                Description = "Hides the selected proxy models in the viewport (display only; the model is not changed)",
                Execute = () => Ed.Log(ProxySceneHost.HideSelected() is var n and > 0 ? $"Hidden {n} proxy model{(n == 1 ? "" : "s")}" : "No visible proxy model selected"),
            },
            new Command
            {
                Id = "objects.showAll",
                Label = "Show All Proxy Models",
                Description = "Shows the proxy models hidden in the Objects window",
                Execute = ProxySceneHost.ShowAll,
            },
            new Command
            {
                Id = "objects.frameSelected",
                Label = "Center && Zoom: Selected Proxy Model",
                Description = "Centers the view on the selected proxy model and zooms to its size",
                Execute = () =>
                {
                    var list = ProxyScene.Instances;
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Selected && list[i].Depth == 0)
                        {
                            ProxyViewport.Frame(i);
                            return;
                        }
                    Ed.Log("No proxy model selected");
                },
            },
            new Command
            {
                Id = "objects.pickTest",
                Label = "Pick Proxy at Point (automation)",
                Description = "Clicks the proxy model at flag 'objects.pickAt' (\"x,y\" viewport pixels, as logged by Log Proxy Instances)",
                Execute = PickTest,
            },
            new Command
            {
                Id = ReloadProxiesId,
                Label = "Reload _Proxies",
                Description = "Reloads the proxy model files that changed on disk (and missing ones that appeared)",
                Execute = ReloadProxies,
            });
    }

    /// <summary>Sets and persists an option ("enabled" | "selectionBox" | "selectionTint").</summary>
    public static void SetOption(string key, bool value)
    {
        switch (key)
        {
            case "enabled":
                ProxyScene.Enabled = value;
                break;
            case "selectionBox":
                ProxyScene.SelectionBox = value;
                break;
            case "selectionTint":
                ProxyScene.SelectionTint = value;
                break;
            default:
                return;
        }
        AppServices.Settings.Set("objects." + key, value); // saves
        Ed.Notify(Change.Ui); // menus / toolbar re-query IsChecked
    }

    /// <summary>objects.logInstances: summary + one line per instance (also used by automation checks).</summary>
    static void LogInstances()
    {
        var list = ProxyScene.Instances;
        Ed.Log(ProxySceneHost.Summary() is { Length: > 0 } s ? s : "Proxies: none in the active LOD");
        var view = ViewportPlugins.Current?.ActiveView;
        Span<float> raw = stackalloc float[3];
        Span<float> px = stackalloc float[2];
        for (int i = 0; i < list.Count && i < 60; i++)
        {
            var inst = list[i];
            var t = inst.Transform.Translation;
            var status = inst.Model.Model is not null ? $"ok {inst.Model.Lod?.FaceCount ?? 0} faces" : inst.Model.Error ?? "loading";
            string screen = "";
            if (view is not null)
            {
                var c = t;
                if (ProxyLibrary.BoundsOf(inst.Model) is { } b) c = System.Numerics.Vector3.Transform((b.Min + b.Max) / 2, inst.Transform);
                raw[0] = c.X;
                raw[1] = c.Y;
                raw[2] = c.Z;
                view.ProjectRaw(raw, px);
                if (float.IsFinite(px[0])) screen = $" screen {view.Rect.X + px[0]:0},{view.Rect.Y + px[1]:0}";
            }
            Ed.Log(FormattableString.Invariant($"[{i}] {inst.Name} | d{inst.Depth} {(inst.Visible ? "visible" : "hidden")}{(inst.Selected ? " selected" : "")} | {status} | at {t.X:0.###},{t.Y:0.###},{t.Z:0.###}{screen}"));
        }
    }

    /// <summary>objects.pickTest: the viewport click path (pick + select) at a logged screen point, for automation.</summary>
    static void PickTest()
    {
        var view = ViewportPlugins.Current?.ActiveView;
        var at = Ed.Flag<string>("objects.pickAt", "");
        if (view is not null && at.StartsWith('#') && int.TryParse(at[1..], out int n) && ScreenPoint(view, n) is { } sp)
            at = FormattableString.Invariant($"{sp.X},{sp.Y}"); // "#N": the screen point of instance N
        var parts = at.Split(',');
        if (view is null || parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y))
        {
            Ed.Log("objects.pickTest: set flag objects.pickAt = \"x,y\" first", LogLevel.Warn);
            return;
        }
        var p = new System.Windows.Point(x - view.Rect.X, y - view.Rect.Y);
        if (ProxyViewport.Pick(view, p) is not { } hit)
        {
            Ed.Log($"objects.pickTest: no proxy model at {x:0},{y:0}");
            return;
        }
        bool ok = ProxySceneHost.Select(hit.Index, SelectOp.Set);
        Ed.Log(FormattableString.Invariant($"objects.pickTest: [{hit.Index}] {ProxyScene.Instances[hit.Index].Name} at distance {hit.Distance:0.###}{(ok ? " selected" : " (not selectable)")}"));
    }

    /// <summary>Viewport-control pixel of an instance's model centre (bounds centre), or null when off screen.</summary>
    static System.Windows.Point? ScreenPoint(IViewHandle view, int instance)
    {
        var list = ProxyScene.Instances;
        if (instance < 0 || instance >= list.Count) return null;
        var inst = list[instance];
        var c = inst.Transform.Translation;
        if (ProxyLibrary.BoundsOf(inst.Model) is { } b) c = System.Numerics.Vector3.Transform((b.Min + b.Max) / 2, inst.Transform);
        Span<float> raw = [c.X, c.Y, c.Z];
        Span<float> px = stackalloc float[2];
        view.ProjectRaw(raw, px);
        return float.IsFinite(px[0]) ? new System.Windows.Point(view.Rect.X + px[0], view.Rect.Y + px[1]) : null;
    }

    static long lastActivationCheck;

    /// <summary>Back from another program (e.g. after saving a proxy model in Object Builder): reload proxy files
    /// whose write time / size changed. The stamps are checked on a background thread; at most every 2 s.</summary>
    static void OnAppActivated()
    {
        long now = Environment.TickCount64;
        if (now - lastActivationCheck < 2000 || ProxyLibrary.Entries.Count == 0) return;
        lastActivationCheck = now;
        ProxyLibrary.Refresh(done: n =>
        {
            if (n > 0) Ed.Log($"Reloading {n} proxy model{(n == 1 ? "" : "s")} changed on disk");
        });
    }

    public static void ReloadProxies() =>
        ProxyLibrary.Refresh(done: n => Ed.Log(n == 0 ? "Proxy models are up to date" : $"Reloading {n} proxy model{(n == 1 ? "" : "s")} changed on disk"));
}
