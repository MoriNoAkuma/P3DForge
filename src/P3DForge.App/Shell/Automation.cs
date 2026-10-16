using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Viewport;
using P3DForge.Core;
using P3DForge.Core.Ops;

namespace P3DForge.App.Shell;

/// <summary>
/// Test/automation hook. When the environment variable P3DFORGE_AUTOMATION points to a JSON script, the app
/// runs its steps after the main window is shown, then exits (unless "keepOpen": true). Example:
/// <code>
/// { "width": 1600, "height": 1000,
///   "steps": [ { "open": "P:\\x\\model.p3d" }, { "wait": 800 }, { "command": "view.front" },
///              { "selectPoints": [0, 1, 2] }, { "setFlag": ["view.shading", "wire"] }, { "lod": 1 },
///              { "screenshot": "testdata\\out\\shot.png" }, { "dump": "testdata\\out\\state.json" } ] }
/// </code>
/// A failing step is recorded in the dump/log (P3DFORGE_AUTOMATION_LOG or &lt;script&gt;.log) and the script continues.
/// </summary>
public static class Automation
{
    public static string? ScriptPath => Environment.GetEnvironmentVariable("P3DFORGE_AUTOMATION");

    static readonly List<string> log = [];

    public static async void Run(Window window)
    {
        var path = ScriptPath;
        if (path is null || !File.Exists(path)) return;
        var logPath = Environment.GetEnvironmentVariable("P3DFORGE_AUTOMATION_LOG") ?? path + ".log";
        JsonNode script;
        try
        {
            script = JsonNode.Parse(File.ReadAllText(path))!;
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, "bad script: " + ex.Message);
            Application.Current.Shutdown(2);
            return;
        }
        if (script["width"] is { } w) window.Width = w.GetValue<double>();
        if (script["height"] is { } h) window.Height = h.GetValue<double>();
        window.WindowState = WindowState.Normal;
        await Delay(500);
        foreach (var step in script["steps"]?.AsArray() ?? [])
        {
            if (step is not JsonObject o || o.Count == 0) continue;
            var (key, value) = o.First();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await RunStep(window, key, value);
                log.Add($"ok   {key} {value?.ToJsonString()} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                log.Add($"FAIL {key} {value?.ToJsonString()}: {ex.Message}");
            }
        }
        File.WriteAllLines(logPath, log);
        if (script["keepOpen"]?.GetValue<bool>() != true)
        {
            AppServices.Editor.Load(Core.Model.P3DModel.Empty(), null); // avoid the unsaved-changes prompt
            Application.Current.Shutdown(0);
        }
    }

    static Task Delay(int ms) => Task.Delay(ms);

    static async Task RunStep(Window window, string key, JsonNode? v)
    {
        var ed = AppServices.Editor;
        switch (key)
        {
            case "open":
                if (!FileService.Open(v!.GetValue<string>())) throw new InvalidOperationException("open failed");
                await Delay(300);
                break;
            case "command":
            {
                var run = System.Diagnostics.Stopwatch.StartNew();
                Commands.Run(v!.GetValue<string>());
                log.Add($"     {v.GetValue<string>()}: {run.Elapsed.TotalMilliseconds:0.0} ms on the UI thread");
                await Delay(150);
                break;
            }
            case "wait":
                await Delay(v!.GetValue<int>());
                break;
            case "lod":
                ed.SetActiveLod(v!.GetValue<int>());
                await Delay(150);
                break;
            case "selectPoints":
                SelectionOps.SelectPoints(null, v!.AsArray().Select(x => x!.GetValue<int>()).ToArray(), SelectOp.Set);
                await Delay(100);
                break;
            case "selectNamed":
                SelectionOps.SelectNamed(v!.GetValue<string>(), SelectOp.Set);
                await Delay(100);
                break;
            case "setFlag":
            {
                var a = v!.AsArray();
                object? val = a[1]?.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => a[1]!.GetValue<double>(),
                    JsonValueKind.String => a[1]!.GetValue<string>(),
                    _ => null,
                };
                ed.SetFlag(a[0]!.GetValue<string>(), val);
                await Delay(100);
                break;
            }
            case "screenshot":
                await Delay(200);
                Save(await Compose(window), v!.GetValue<string>());
                break;
            case "viewportShot":
                Save(await ViewportFrame() ?? throw new InvalidOperationException("no viewport frame"), v!.GetValue<string>());
                break;
            case "click":
            {
                // a visible button / toggle / check box by its caption (first match), clicked like a user would
                var caption = v!.GetValue<string>();
                System.Windows.Controls.Primitives.ButtonBase? hit = null;
                void Find(DependencyObject o)
                {
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o) && hit is null; i++)
                    {
                        var c = VisualTreeHelper.GetChild(o, i);
                        if (c is System.Windows.Controls.Primitives.ButtonBase b && b.IsVisible && b.IsEnabled
                            && string.Equals((b.Content as string)?.Trim(), caption, StringComparison.OrdinalIgnoreCase)) hit = b;
                        else Find(c);
                    }
                }
                Find(window);
                if (hit is null) throw new InvalidOperationException($"no visible, enabled button \"{caption}\"");
                if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(hit)
                        ?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider invoke) invoke.Invoke();
                else if (hit is System.Windows.Controls.Primitives.ToggleButton t) t.IsChecked = !(t.IsChecked ?? false);
                await Delay(200);
                break;
            }
            case "scrollPanels":
            {
                // "end" / "home": scroll every scrollable panel (to see what is below the fold in screenshots)
                bool end = v?.GetValue<string>() != "home";
                void Walk(DependencyObject o)
                {
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
                    {
                        var c = VisualTreeHelper.GetChild(o, i);
                        if (c is System.Windows.Controls.ScrollViewer sv && sv.ScrollableHeight > 0)
                        {
                            if (end) sv.ScrollToEnd();
                            else sv.ScrollToHome();
                        }
                        Walk(c);
                    }
                }
                Walk(window);
                await Delay(200);
                break;
            }
            case "wheel":
            {
                // [x, y, notches]: mouse-wheel zoom at a point given as fractions of the viewport (+ = in)
                var a = v!.AsArray();
                if (ViewportPlugins.Current is not Viewport.ViewportView vv) throw new InvalidOperationException("no viewport");
                var at = new Point(vv.InputElement.ActualWidth * a[0]!.GetValue<double>(), vv.InputElement.ActualHeight * a[1]!.GetValue<double>());
                int n = a[2]!.GetValue<int>();
                for (int k = 0; k < Math.Abs(n); k++)
                {
                    vv.WheelZoom(at, Math.Sign(n) * 120);
                    await Delay(15);
                }
                if (vv.ActiveView is Viewport.ViewPane pane)
                    log.Add($"     camera: distance {pane.Camera.Distance:0.#####} target {pane.Camera.Target:0.###} eye {pane.Camera.Eye:0.###}");
                await Delay(300);
                break;
            }
            case "autosave":
                Services.Autosave.WriteNow();
                await Delay(v?.GetValue<int>() ?? 1500);
                break;
            case "crash":
                // ends the process without the normal exit (autosave recovery tests); the log is written first
                log.Add("crash: Environment.Exit(3)");
                File.WriteAllLines(Environment.GetEnvironmentVariable("P3DFORGE_AUTOMATION_LOG") ?? ScriptPath + ".log", log);
                Environment.Exit(3);
                break;
            case "perf":
                // viewport timings after the next frames: build / frame / change-to-frame latency
                await Delay(v?.GetValue<int>() ?? 300);
                log.Add("perf " + (ViewportPlugins.Current is Viewport.ViewportView vp ? vp.DebugInfo : "(no viewport)"));
                break;
            case "dump":
                File.WriteAllText(v!.GetValue<string>(), Dump().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                break;
            case "save":
                if (!FileService.Save(v!.GetValue<string>())) throw new InvalidOperationException("save failed");
                break;
            default:
                throw new NotSupportedException($"unknown step '{key}'");
        }
    }

    /// <summary>Editor state snapshot for assertions.</summary>
    public static JsonObject Dump()
    {
        var ed = AppServices.Editor;
        var sel = Selection.PeekSelected(ed.Lod);
        return new JsonObject
        {
            ["path"] = ed.Path,
            ["dirty"] = ed.Dirty,
            ["activeLod"] = ed.ActiveLod,
            ["lodName"] = LodNames.Name(ed.Lod.Resolution),
            ["lods"] = new JsonArray(ed.Model.Lods.Select(l => (JsonNode)$"{LodNames.Name(l.Resolution)}: {l.PointCount} pts, {l.FaceCount} faces, {l.Selections.Count()} sels").ToArray()),
            ["selectedPoints"] = sel?.PointCount ?? 0,
            ["selectedFaces"] = sel?.FaceCount ?? 0,
            ["selectMode"] = ed.SelectMode.ToString(),
            ["undo"] = new JsonArray(ed.UndoLabels.Select(s => (JsonNode)s).ToArray()),
            ["commands"] = Commands.All.Count,
            ["viewport"] = (ViewportPlugins.Current as ViewportView)?.DebugInfo,
            ["log"] = new JsonArray(ed.LogEntries.TakeLast(30).Select(e => (JsonNode)$"{e.Level}: {e.Message}").ToArray()),
            ["validation"] = new JsonArray(Panels.ValidationPanel.Results.Take(80).Select(i => (JsonNode)$"{i.Severity} [{i.Rule}] LOD {i.LodIndex}: {i.Message}").ToArray()),
            ["automation"] = new JsonArray(log.Select(s => (JsonNode)s).ToArray()),
        };
    }

    static Task<BitmapSource?> ViewportFrame()
    {
        var tcs = new TaskCompletionSource<BitmapSource?>();
        if (ViewportPlugins.Current is not ViewportView vv)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }
        vv.RequestCapture(b => tcs.TrySetResult(b));
        _ = Task.Delay(3000).ContinueWith(_ => tcs.TrySetResult(null));
        return tcs.Task;
    }

    /// <summary>
    /// Deterministic window capture: the WPF visual tree rendered with RenderTargetBitmap (independent of what
    /// is on screen), with the OpenGL frame composited into the viewport's rectangle.
    /// </summary>
    static async Task<BitmapSource> Compose(Window window)
    {
        var gl = await ViewportFrame();
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var ui = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        ui.Render(root);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawImage(ui, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            if (gl is not null && ViewportPlugins.Current is ViewportView vv && vv.IsVisible)
            {
                var r = vv.TransformToAncestor(root).TransformBounds(new Rect(0, 0, vv.ActualWidth, vv.ActualHeight));
                dc.DrawImage(gl, r); // RequestCapture already composites pane labels and overlays
            }
        }
        var outBmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        return outBmp;
    }

    static void Save(BitmapSource bmp, string pngPath)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }
}
