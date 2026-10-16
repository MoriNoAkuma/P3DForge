using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTK.Graphics.OpenGL4;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using OpenTK.Wpf;
using P3DForge.App.Editor;
using P3DForge.App.Scene;
using P3DForge.App.Viewport.Rendering;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using Point = System.Windows.Point;

namespace P3DForge.App.Viewport;

/// <summary>
/// The 3D viewport: an OpenGL surface (GLWpfControl) rendering the active LOD on demand into one view or the
/// Object Builder four-view layout (Front, Left, Top, 3D — F9), each pane with its own camera; textured /
/// solid / wire shading, proxies (colour, triad, path label), memory point labels, background LOD ghost,
/// Object Builder camera navigation, and the <see cref="IViewportContext"/> services for plugins.
/// </summary>
public partial class ViewportView : UserControl, IViewportContext
{
    static EditorState Ed => AppServices.Editor;

    const int MaxProxyLabels = 300, MaxPointLabels = 400;
    static readonly Brush LabelFg = Frozen(Color.FromRgb(0xC8, 0xCC, 0xD4));
    static readonly Brush ActiveFrame = Frozen(Color.FromRgb(0x3D, 0x8F, 0xD1));
    static readonly Brush InactiveFrame = Frozen(Color.FromArgb(0x00, 0, 0, 0));

    // Four-view layout order (top-left, top-right, bottom-left, bottom-right).
    readonly ViewPane[] panes = [new("front", ViewKind.Front), new("left", ViewKind.Left), new("top", ViewKind.Top), new("3d", ViewKind.Perspective)];
    readonly PaneChrome[] chrome;
    int active = 3;
    bool quad;

    readonly Dictionary<int, float[]> previews = [];
    readonly Dictionary<string, IReadOnlyList<OverlayLine>> overlays = [];
    ModelRenderer? renderer, ghost;
    GpuPrograms? programs;
    TextureCache? textures;
    ProxyRenderer? proxyRenderer;
    ShadowMap? shadowMap;
    GpuTimer? gpuTimer;
    DrawContext? drawContext;
    readonly MaterialLibrary materials = new();
    string? texturesChangedSeen;
    readonly Queue<string> textureInvalidations = new();
    Action? detachPlugins;
    bool overlaysDirty;
    bool framePending = true;

    // navigation state
    enum Nav { None, Orbit, Pan, Zoom, Sun }
    Nav nav;
    ViewPane? navPane;
    Point last;

    // timings (DebugInfo, stats overlay)
    long changeStamp, lastRenderStamp;
    double lastFrameMs, lastLatencyMs;
    readonly FrameHistory frameStats = new();

    // live sun drag (Alt+Shift+LMB): angles override the flags until the button is released
    float? sunDragAz, sunDragEl;

    public ViewportView()
    {
        InitializeComponent();
        chrome = [.. panes.Select(_ => new PaneChrome(PaneLayer))];
        // Subscribe before Start(): Start creates the context and raises Ready synchronously.
        Gl.Ready += OnReady;
        Gl.Render += OnRender;
        Gl.Start(new GLWpfControlSettings { MajorVersion = 4, MinorVersion = 1, RenderContinuously = false, Samples = 4 });
        Ed.Changed += OnEditorChanged;
        ProxyScene.Changed += Invalidate;
        Core.Materials.MaterialStore.Shared.Changed += OnMaterialChanged;
        Root.PreviewMouseDown += OnPreviewMouseDown;
        Root.MouseDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseUp += OnMouseUp;
        Root.MouseWheel += OnMouseWheel;
        Root.LostMouseCapture += (_, _) =>
        {
            if (nav == Nav.Sun) EndSunDrag(commit: true);
            nav = Nav.None;
        };
        Root.SizeChanged += (_, _) => Layout();
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) =>
        {
            detachPlugins ??= ViewportPlugins.Attach(this);
            UpdateStatsVisibility(); // flags restored at startup raise no change for this view
        };
        Unloaded += (_, _) =>
        {
            detachPlugins?.Invoke();
            detachPlugins = null;
        };
        Dispatcher.ShutdownStarted += (_, _) =>
        {
            ProxyScene.Changed -= Invalidate;
            Core.Materials.MaterialStore.Shared.Changed -= OnMaterialChanged;
        };
        Layout();
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---- pane chrome & layout ---------------------------------------------------------------------

    /// <summary>Per-pane WPF elements: a clipped canvas (view name + 2D labels) and the active-pane frame.</summary>
    sealed class PaneChrome
    {
        public readonly Canvas Host = new() { ClipToBounds = true, IsHitTestVisible = false };
        public readonly Border Frame = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        public readonly TextBlock Name = new() { FontSize = 12, Foreground = LabelFg, IsHitTestVisible = false };
        public readonly LabelLayer Labels;

        public PaneChrome(Canvas layer)
        {
            Canvas.SetLeft(Name, 8);
            Canvas.SetTop(Name, 6);
            Panel.SetZIndex(Name, 1);
            Host.Children.Add(Name);
            layer.Children.Add(Host);
            layer.Children.Add(Frame);
            Labels = new LabelLayer(Host);
        }

        public void Place(Rect r, bool visible, bool framed)
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            Host.Visibility = vis;
            Frame.Visibility = vis;
            Canvas.SetLeft(Host, r.X);
            Canvas.SetTop(Host, r.Y);
            Host.Width = r.Width;
            Host.Height = r.Height;
            Canvas.SetLeft(Frame, r.X);
            Canvas.SetTop(Frame, r.Y);
            Frame.Width = r.Width;
            Frame.Height = r.Height;
            Frame.BorderBrush = framed ? ActiveFrame : InactiveFrame;
        }
    }

    IEnumerable<ViewPane> VisiblePanes => quad ? panes : [panes[active]];

    bool IsPaneVisible(int i) => quad || i == active;

    /// <summary>Quad rectangles for every pane (also when hidden, so framing uses sane aspects); the active pane fills the control in single mode.</summary>
    void Layout()
    {
        double W = Math.Max(1, Math.Floor(Root.ActualWidth)), H = Math.Max(1, Math.Floor(Root.ActualHeight));
        const double gap = 2;
        double w1 = Math.Max(1, Math.Floor((W - gap) / 2)), h1 = Math.Max(1, Math.Floor((H - gap) / 2));
        double w2 = Math.Max(1, W - w1 - gap), h2 = Math.Max(1, H - h1 - gap);
        panes[0].Rect = new Rect(0, 0, w1, h1);
        panes[1].Rect = new Rect(w1 + gap, 0, w2, h1);
        panes[2].Rect = new Rect(0, h1 + gap, w1, h2);
        panes[3].Rect = new Rect(w1 + gap, h1 + gap, w2, h2);
        if (!quad) panes[active].Rect = new Rect(0, 0, W, H);
        UpdateChrome();
        Invalidate();
    }

    void UpdateChrome()
    {
        for (int i = 0; i < panes.Length; i++)
        {
            chrome[i].Name.Text = panes[i].Label;
            chrome[i].Place(panes[i].Rect, IsPaneVisible(i), quad && i == active);
        }
    }

    // ---- rendering ------------------------------------------------------------------------------

    int renderCount;
    bool readyCalled;

    /// <summary>Diagnostics for automation/tests.</summary>
    public string DebugInfo
    {
        get
        {
            var t = renderer?.Textures;
            return $"ready={readyCalled} renders={renderCount} fb={Gl.FrameBufferWidth}x{Gl.FrameBufferHeight} size={ActualWidth:F0}x{ActualHeight:F0} " +
                $"loaded={IsLoaded} renderer={(renderer is null ? "null" : "ok")} err={ErrorText.Text} quad={quad} active={panes[active].Id}:{panes[active].Kind} " +
                $"build={renderer?.LastBuildMs:F1}ms frame={lastFrameMs:F1}ms change2frame={lastLatencyMs:F1}ms ranges={renderer?.DrawRangeCount} " +
                $"tex={t?.Loaded}/{t?.Count} inflight={t?.InFlight} proxies={renderer?.Proxies.Count} labels={string.Join(",", chrome.Select(c => c.Labels.Count))} [{renderer?.BuildBreakdown}] " +
                $"shading={ShadingMode} proxyModels={proxyRenderer?.Count}/{ProxyScene.Instances.Count} meshes={proxyRenderer?.CachedMeshes} building={proxyRenderer?.Building} " +
                $"shadowRenders={shadowMap?.Renders} draws={frameStats.DrawCalls} tris={frameStats.Triangles} gpu={frameStats.LastGpuMs:F2}ms " +
                $"stats={Ed.Flag(RenderConfig.Stats, false)}/{StatsPanel.Visibility}/{StatsPanel.ActualWidth:F0}";
        }
    }

    void OnReady()
    {
        readyCalled = true;
        try
        {
            programs = new GpuPrograms();
            textures = new TextureCache(() => Dispatcher.BeginInvoke(Invalidate));
            renderer = new ModelRenderer(programs, textures);
            proxyRenderer = new ProxyRenderer(() => Dispatcher.BeginInvoke(Invalidate));
            shadowMap = new ShadowMap();
            gpuTimer = new GpuTimer();
            drawContext = new DrawContext { Programs = programs, Textures = textures, Materials = materials };
        }
        catch (Exception ex)
        {
            ErrorText.Text = "OpenGL initialisation failed: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            Ed.Log(ErrorText.Text, LogLevel.Error);
        }
    }

    static Shading ShadingMode => Ed.Flag("view.shading", "textured") switch
    {
        "wire" => Shading.Wire,
        "solid" => Shading.Solid,
        "final" => Shading.Final,
        _ => Shading.Textured,
    };

    static int FlagInt(string key, int fallback) => Ed.Flag<object?>(key, null) switch
    {
        int i => i,
        double d => (int)d,
        long l => (int)l,
        string s when int.TryParse(s, out int v) => v,
        _ => fallback,
    };

    /// <summary>
    /// Per-point weights for the heat map. Flag "view.showWeights" = a named selection's name (set by the
    /// Selections panel), true (= the current selection's weights) or null/false (off).
    /// </summary>
    static byte[]? WeightsMask(Lod lod) => Ed.Flag<object?>("view.showWeights", null) switch
    {
        string name when name.Length > 0 => lod.Selection(name)?.Points ?? new byte[lod.PointCount],
        true => Selection.PeekSelected(lod)?.Points ?? new byte[lod.PointCount],
        _ => null,
    };

    void OnRender(TimeSpan _)
    {
        long t0 = Stopwatch.GetTimestamp();
        double interval = lastRenderStamp == 0 ? double.NaN : Stopwatch.GetElapsedTime(lastRenderStamp).TotalMilliseconds;
        lastRenderStamp = t0;
        renderCount++;
        var bg = (Color)ColorConverter.ConvertFromString(AppServices.Settings.ViewportBackground);
        int fbW = Gl.FrameBufferWidth, fbH = Gl.FrameBufferHeight;
        GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(0, 0, fbW, fbH);
        if (quad) GL.ClearColor(bg.R / 400f, bg.G / 400f, bg.B / 400f, 1f); // darker gaps between panes
        else GL.ClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (renderer is null && !readyCalled) OnReady();
        if (renderer is null || drawContext is not { } dc || textures is null || proxyRenderer is null || shadowMap is null) return;
        gpuTimer?.Begin();
        dc.Stats.Reset();

        var lod = Ed.Lod;
        while (textureInvalidations.TryDequeue(out var changed))
        {
            int dropped = textures.Invalidate(changed);
            if (dropped > 0) shadowMap.Invalidate();
        }
        renderer.PrepareFrame();
        var weights = WeightsMask(lod);
        // camo / damage preview (Tools ▸ Camo & Damage): a render-only copy with the variant's textures
        renderer.Sync(Panels.VariantSession.RenderLod(lod), previews.GetValueOrDefault(Ed.ActiveLod), Ed.Flag("view.showHidden", false), weights);
        int bgIndex = FlagInt("view.backgroundLod", -1);
        // Object Builder: with no background LOD chosen, the first LOD shows as a yellow frame in the others.
        if (bgIndex < 0 && Ed.Flag("view.firstLodBackground", true) && Ed.Model.Lods.Length > 1) bgIndex = 0;
        bool showGhost = bgIndex >= 0 && bgIndex < Ed.Model.Lods.Length && bgIndex != Ed.ActiveLod;
        if (showGhost)
        {
            ghost ??= new ModelRenderer(programs!, textures);
            ghost.Sync(Ed.Model.Lods[bgIndex], previews.GetValueOrDefault(bgIndex));
        }
        proxyRenderer.Prepare(ProxyScene.Instances, ProxyScene.Enabled, ProxyScene.SelectionTint);
        if (framePending)
        {
            framePending = false;
            foreach (var p in panes) p.FrameBounds(renderer.BoundsMin, renderer.BoundsMax, renderer.RenderPositions);
        }
        if (overlaysDirty)
        {
            overlaysDirty = false;
            renderer.SetOverlay(overlays.Values.SelectMany(x => x));
        }

        var shading = ShadingMode;
        bool final = shading == Shading.Final;
        bool memoryLike = lod.FaceCount == 0;
        var opt = new ModelRenderer.Options(
            Shading: shading,
            Culling: Ed.Flag("view.backfaceCulling", false),
            ShowPoints: memoryLike || Ed.Flag("view.showPoints", false) || (final ? RenderConfig.Bool(RenderConfig.FinalPoints) : Ed.SelectMode == SelectMode.Points),
            ShowGrid: final ? RenderConfig.Bool(RenderConfig.FinalGrid) : Ed.Flag("view.showGrid", AppServices.Settings.ShowGrid),
            BackTint: !Ed.Flag("view.showBackfaces", true) ? 0f : shading == Shading.Textured ? 0.28f : 0.55f,
            PointSize: memoryLike ? 5f : 3f,
            ShowProxies: Ed.Flag("view.showProxies", true),
            ShowNormals: Ed.Flag("view.showNormals", false),
            Weights: weights is not null,
            WireOverlay: final && RenderConfig.Bool(RenderConfig.FinalWire),
            SelectedPointsOnly: final);
        var finalParams = RenderConfig.Final();
        bool shadows = shading != Shading.Wire && (final
            ? (finalParams.Toggles & FinalParams.Shadows) != 0 && finalParams.Stage == 0
            : RenderConfig.Bool(RenderConfig.ShadowAllModes));
        var (sceneMin, sceneMax) = SceneBounds();
        float fovY = RenderConfig.Str(RenderConfig.Fov) == "game" ? RenderConfig.GameFovY : RenderConfig.DefaultFovY;

        double sx = fbW / Math.Max(1, Root.ActualWidth), sy = fbH / Math.Max(1, Root.ActualHeight);
        foreach (var pane in VisiblePanes)
        {
            if (pane.Camera.Kind == ViewKind.Perspective) pane.Camera.FovY = fovY;
            var r = pane.Rect;
            int x = (int)Math.Round(r.X * sx), w = Math.Max(1, (int)Math.Round(r.Width * sx)), h = Math.Max(1, (int)Math.Round(r.Height * sy));
            int y = fbH - (int)Math.Round(r.Y * sy) - h;
            var (_, camUp) = pane.Camera.Axes();
            var sunDir = RenderConfig.SunDirection(pane.Forward, camUp, sunDragAz, sunDragEl);
            ShadowParams shadow = default;
            if (shadows && renderer.HasGeometry)
            {
                int size = RenderConfig.ShadowMapSize;
                var key = (sunDir, sceneMin, sceneMax, renderer.GeometryVersion, proxyRenderer.Version, textures.Uploads, size);
                shadowMap.Update(size, sceneMin, sceneMax, sunDir, key, programs!.Depth, depth =>
                {
                    renderer.DrawDepth(depth);
                    proxyRenderer.DrawDepth(depth, textures, materials);
                }, Gl.Framebuffer, (x, y, w, h));
                shadow = new ShadowParams(shadowMap.Texture, shadowMap.LightViewProj, shadowMap.Size,
                    RenderConfig.Bool(RenderConfig.ShadowSoft) ? 1.6f : 0f, shadowMap.TexelWorld * 1.5f,
                    Math.Clamp(RenderConfig.Float(RenderConfig.ShadowStrength), 0f, 1f));
            }
            GL.Enable(EnableCap.ScissorTest);
            GL.Viewport(x, y, w, h);
            GL.Scissor(x, y, w, h);
            GL.ClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            dc.ViewProj = pane.ViewProjection;
            dc.Eye = pane.Eye;
            dc.Options = opt;
            dc.EditLight = RenderConfig.EditLight(sunDir);
            dc.FinalLight = RenderConfig.FinalLight(sunDir);
            dc.Final = finalParams;
            dc.Shadow = shadow;
            ModelRenderer.BeginPane();
            renderer.DrawGrid(dc);
            if (final || opt.Shading == Shading.Textured)
            {
                // opaque + alpha-tested surfaces of the model and every proxy first, then the blended ones (glass)
                dc.BlendPass = 0;
                renderer.DrawSurface(dc);
                proxyRenderer.DrawSurface(dc);
                dc.BlendPass = 1;
                renderer.DrawSurface(dc);
                proxyRenderer.DrawSurface(dc);
                dc.BlendPass = -1;
            }
            else
            {
                renderer.DrawSurface(dc);
                proxyRenderer.DrawSurface(dc);
            }
            if (opt.WireOverlay) proxyRenderer.DrawEdges(dc, wire: false);
            renderer.DrawLines(dc, lod);
            if (showGhost) ghost!.DrawGhost(dc);
        }
        GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(0, 0, fbW, fbH);
        UpdateLabels(lod, opt.ShowProxies);
        gpuTimer?.End();

        if (changeStamp != 0)
        {
            GL.Finish(); // measure until the frame is really done (once per document/LOD change)
            lastLatencyMs = Stopwatch.GetElapsedTime(changeStamp).TotalMilliseconds;
            changeStamp = 0;
        }
        lastFrameMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        frameStats.Record(lastFrameMs, interval, gpuTimer?.LastMs ?? double.NaN, dc.Stats);
        if (captureCallback is not null) CaptureFrame();
        AfterFrame();
    }

    /// <summary>RENDER bounds of the drawn LOD and the proxy instances (shadow map fit).</summary>
    (Vector3 Min, Vector3 Max) SceneBounds()
    {
        var min = renderer!.BoundsMin;
        var max = renderer.BoundsMax;
        if (proxyRenderer is { Count: > 0 } pr)
        {
            min = Vector3.Min(min, pr.BoundsMin);
            max = Vector3.Max(max, pr.BoundsMax);
        }
        return (min, max);
    }

    // ---- 2D labels (proxies, memory points) --------------------------------------------------------

    Lod? labelLod;
    string?[]? pointLabels;
    int[] labelled = [];
    float[] labelRaw = [], labelProj = [];

    void UpdateLabels(Lod lod, bool showProxies)
    {
        bool memLabels = Ed.Flag("view.showMemoryLabels", true) && (LodNames.IsMemory(lod.Resolution) || lod.FaceCount == 0);
        if (memLabels && !ReferenceEquals(labelLod, lod))
        {
            labelLod = lod;
            pointLabels = SceneData.PointLabels(lod);
            var hidden = Selection.HiddenPoints(lod);
            var idx = new List<int>();
            if (pointLabels is not null)
                for (int i = 0; i < pointLabels.Length; i++)
                    if (pointLabels[i] is not null && hidden[i] == 0) idx.Add(i);
            labelled = [.. idx];
        }
        var raw = previews.GetValueOrDefault(Ed.ActiveLod) ?? lod.Positions;
        int nl = memLabels && pointLabels is not null ? labelled.Length : 0;
        if (nl > 0)
        {
            if (labelRaw.Length < nl * 3) labelRaw = new float[nl * 3];
            if (labelProj.Length < nl * 2) labelProj = new float[nl * 2];
            for (int k = 0; k < nl; k++)
            {
                int p = labelled[k];
                if (p * 3 + 2 >= raw.Length) continue;
                labelRaw[k * 3] = raw[p * 3];
                labelRaw[k * 3 + 1] = raw[p * 3 + 1];
                labelRaw[k * 3 + 2] = raw[p * 3 + 2];
            }
        }
        var proxies = showProxies && renderer is not null ? renderer.Proxies : [];
        // Selected proxies are labelled first and always; the others only where they do not overlap (declutter).
        var selFaces = proxies.Count > 0 ? Selection.PeekSelected(lod)?.Faces : null;
        bool IsSelectedProxy(ProxyInfo px) => selFaces is not null && px.Face >= 0 && px.Face < selFaces.Length && selFaces[px.Face] != 0;
        for (int i = 0; i < panes.Length; i++)
        {
            var layer = chrome[i].Labels;
            layer.Begin();
            if (IsPaneVisible(i))
            {
                var pane = panes[i];
                double w = pane.Rect.Width, h = pane.Rect.Height;
                int count = 0;
                for (int pass = 0; pass < 2; pass++)
                    foreach (var px in proxies)
                    {
                        if (count >= MaxProxyLabels) break;
                        bool sel = IsSelectedProxy(px);
                        if (sel != (pass == 0)) continue;
                        var s = pane.ProjectRender(Coords.RawToRender(px.Anchor));
                        if (!(s.X >= 0 && s.Y >= 0 && s.X <= w && s.Y <= h)) continue;
                        if (layer.Add(s.X + 5, s.Y - 16, px.Label, LabelLayer.ProxyBrush, force: sel)) count++;
                    }
                if (nl > 0)
                {
                    pane.ProjectRaw(labelRaw.AsSpan(0, nl * 3), labelProj.AsSpan(0, nl * 2));
                    var selPts = Selection.PeekSelected(lod)?.Points;
                    count = 0;
                    for (int pass = 0; pass < 2; pass++)
                        for (int k = 0; k < nl && count < MaxPointLabels; k++)
                        {
                            int p = labelled[k];
                            bool sel = selPts is not null && p < selPts.Length && selPts[p] != 0;
                            if (sel != (pass == 0)) continue;
                            float x = labelProj[k * 2], y = labelProj[k * 2 + 1];
                            if (!(x >= 0 && y >= 0 && x <= w && y <= h)) continue;
                            if (layer.Add(x + 5, y - 7, pointLabels![p]!, LabelLayer.MemoryBrush, force: sel)) count++;
                        }
                }
            }
            layer.End();
        }
    }

    // ---- capture (automation/tests) -----------------------------------------------------------------

    Action<BitmapSource>? captureCallback;

    /// <summary>Reads back the next rendered frame (exact GL pixels, MSAA resolved) with the WPF overlays (labels, pane frames, plugin canvas) composited on top.</summary>
    public void RequestCapture(Action<BitmapSource> callback)
    {
        captureCallback = callback;
        Invalidate();
    }

    void CaptureFrame()
    {
        var cb = captureCallback;
        captureCallback = null;
        int w = Gl.FrameBufferWidth, h = Gl.FrameBufferHeight;
        if (cb is null || w <= 0 || h <= 0) return;
        // The control renders into a multisampled FBO: resolve into a plain one before reading.
        int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        GL.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Gl.Framebuffer);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        var px = new byte[w * h * 4];
        GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, px);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Gl.Framebuffer);
        GL.DeleteFramebuffer(fbo);
        GL.DeleteRenderbuffer(rb);
        var flipped = new byte[px.Length];
        for (int y = 0; y < h; y++) System.Buffer.BlockCopy(px, (h - 1 - y) * w * 4, flipped, y * w * 4, w * 4);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, flipped, w * 4);
        bmp.Freeze();
        Dispatcher.BeginInvoke(() => cb(ComposeOverlays(bmp)));
    }

    BitmapSource ComposeOverlays(BitmapSource gl)
    {
        double W = Root.ActualWidth, H = Root.ActualHeight;
        if (W < 1 || H < 1) return gl;
        try
        {
            Root.UpdateLayout();
            var dv = new DrawingVisual();
            var area = new Rect(0, 0, W, H);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(gl, area);
                foreach (var layer in new FrameworkElement[] { PaneLayer, Overlay })
                {
                    if (!layer.IsVisible || VisualTreeHelper.GetChildrenCount(layer) == 0) continue;
                    var brush = new VisualBrush(layer)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = area,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = area,
                    };
                    dc.DrawRectangle(brush, null, area);
                }
                // Floating chrome (stats overlay, sun readout): a RenderTargetBitmap of the element itself keeps its
                // layout offset inside Root, so a Root-sized bitmap holds it at the right place (VisualBrush rendered
                // these aligned panels empty or stale).
                foreach (var panel in new FrameworkElement[] { StatsPanel, SunPanel })
                {
                    if (!panel.IsVisible || panel.ActualWidth < 1 || panel.ActualHeight < 1) continue;
                    var layer = new RenderTargetBitmap(gl.PixelWidth, gl.PixelHeight, 96.0 * gl.PixelWidth / W, 96.0 * gl.PixelHeight / H, PixelFormats.Pbgra32);
                    layer.Render(panel);
                    dc.DrawImage(layer, area);
                }
            }
            var rtb = new RenderTargetBitmap(gl.PixelWidth, gl.PixelHeight, 96.0 * gl.PixelWidth / W, 96.0 * gl.PixelHeight / H, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return gl;
        }
    }

    public void Invalidate() => Gl.InvalidateVisual();

    void OnEditorChanged(Change c)
    {
        if (c.HasFlag(Change.Document)) framePending |= c.HasFlag(Change.ActiveLod) && c.HasFlag(Change.Pin);
        if ((c.HasFlag(Change.Document) || c.HasFlag(Change.ActiveLod)) && changeStamp == 0) changeStamp = Stopwatch.GetTimestamp();
        if (c.HasFlag(Change.Ui) && Ed.Flag("view.quad", false) != quad)
        {
            quad = !quad;
            Layout();
        }
        if (c.HasFlag(Change.ActiveLod) || c.HasFlag(Change.Ui)) UpdateChrome();
        if (c.HasFlag(Change.Ui))
        {
            // Cross-feature signal: a texture was saved → drop it from the cache ("path" or "path|nonce").
            var changed = Ed.Flag<string?>("textures.changed", null);
            if (changed is null) texturesChangedSeen = null; // the textures feature clears it before each announcement
            else if (changed != texturesChangedSeen)
            {
                texturesChangedSeen = changed;
                int bar = changed.IndexOf('|');
                textureInvalidations.Enqueue(bar >= 0 ? changed[..bar] : changed);
            }
            UpdateStatsVisibility();
        }
        Invalidate();
    }

    /// <summary>An rvmat file or its unsaved editor override changed: rebuild materials (live preview) and redraw.</summary>
    void OnMaterialChanged(string key)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMaterialChanged(key));
            return;
        }
        materials.Invalidate();
        shadowMap?.Invalidate();
        Invalidate();
    }

    // ---- stats overlay, screenshots ------------------------------------------------------------

    /// <summary>Timings and counters of recent frames.</summary>
    public FrameHistory Stats => frameStats;

    /// <summary>Raised after every rendered frame (UI thread).</summary>
    public event Action? FrameRendered;

    System.Windows.Threading.DispatcherTimer? statsTimer;

    void UpdateStatsVisibility()
    {
        bool on = Ed.Flag(RenderConfig.Stats, false);
        StatsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // The viewport draws only when something changes; refresh the text so an idle view says so instead of
        // showing the numbers of the last burst of frames.
        if (on && statsTimer is null)
        {
            statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            statsTimer.Tick += (_, _) =>
            {
                if (frameStats.IsIdle) StatsText.Text = frameStats.Format();
            };
        }
        if (on) statsTimer!.Start();
        else statsTimer?.Stop();
    }

    void AfterFrame()
    {
        if (StatsPanel.Visibility == Visibility.Visible)
        {
            StatsText.Text = frameStats.Format();
            const double W = 170, H = 34, budget = 33.3; // graph spans 0..33 ms; dashed line = 16.7 ms (60 FPS)
            StatsBudget.Y1 = StatsBudget.Y2 = H - H * 16.7 / budget;
            StatsLine.Points = Graph(frameStats.Series(0), W, H, budget);
            StatsGpuLine.Points = Graph(frameStats.Series(2), W, H, budget);
            if (Ed.Flag(RenderConfig.StatsContinuous, false))
                Dispatcher.BeginInvoke(Invalidate, System.Windows.Threading.DispatcherPriority.Background);
        }
        FrameRendered?.Invoke();
    }

    static PointCollection Graph(IEnumerable<double> values, double w, double h, double max)
    {
        var pts = new PointCollection();
        int i = 0;
        foreach (var v in values)
        {
            double x = w * i++ / (FrameHistory.Capacity - 1);
            if (double.IsFinite(v)) pts.Add(new Point(x, h - h * Math.Clamp(v / max, 0, 1)));
        }
        return pts;
    }

    /// <summary>Sun direction line + star from the scene centre and the angle readout next to the cursor.</summary>
    void UpdateSunIndicator(Point at)
    {
        if (renderer is null) return;
        var pane = navPane ?? panes[active];
        var (_, up) = pane.Camera.Axes();
        var dir = RenderConfig.SunDirection(pane.Forward, up, sunDragAz, sunDragEl);
        var (min, max) = SceneBounds();
        var (c, rad) = RenderMath.Sphere(min, max);
        var tip = c + dir * rad * 1.3f;
        const uint col = 0xFFC83CFF;
        float st = rad * 0.07f;
        SetOverlayLines("render.sun",
        [
            new OverlayLine(c, tip, col),
            new OverlayLine(tip - Vector3.UnitX * st, tip + Vector3.UnitX * st, col),
            new OverlayLine(tip - Vector3.UnitY * st, tip + Vector3.UnitY * st, col),
            new OverlayLine(tip - Vector3.UnitZ * st, tip + Vector3.UnitZ * st, col),
            new OverlayLine(c, c + new Vector3(dir.X, 0, dir.Z) * rad * 1.3f, 0xFFC83C70),
        ]);
        SunText.Text = $"Sun  azimuth {sunDragAz:F0}°  elevation {sunDragEl:F0}°" + (RenderConfig.CurrentPreset.CameraRelative ? "  (camera)" : "");
        SunPanel.Margin = new Thickness(at.X + 18, at.Y + 14, 0, 0);
        SunPanel.Visibility = Visibility.Visible;
        Invalidate();
    }

    void EndSunDrag(bool commit)
    {
        if (commit && sunDragAz is { } az && sunDragEl is { } el)
        {
            Ed.SetFlag(RenderConfig.Azimuth, Math.Round(az, 1));
            Ed.SetFlag(RenderConfig.Elevation, Math.Round(el, 1));
        }
        sunDragAz = sunDragEl = null;
        SetOverlayLines("render.sun", null);
        SunPanel.Visibility = Visibility.Collapsed;
        Invalidate();
    }

    /// <summary>Saves the next frame (with the WPF overlays) as PNG; <paramref name="done"/> gets the path or null on failure.</summary>
    public void SaveScreenshot(string path, Action<string?>? done = null)
    {
        RequestCapture(bmp =>
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                Ed.Log($"Screenshot saved: {path}");
                done?.Invoke(path);
            }
            catch (Exception ex)
            {
                Ed.Log($"Screenshot failed: {ex.Message}", LogLevel.Warn);
                done?.Invoke(null);
            }
        });
    }

    /// <summary>Label drawn at the control's top-left: the single view's, or the top-left pane's in the four-view layout.</summary>
    public string ViewLabelText => quad ? panes[0].Label : panes[active].Label;

    // ---- camera commands (used by ViewFeature) ----------------------------------------------------

    /// <summary>Camera of the active pane.</summary>
    public ViewCamera Camera => panes[active].Camera;
    public bool IsQuad => quad;
    public ViewPane ActivePane => panes[active];

    public void SetViewKind(ViewKind kind)
    {
        panes[active].Camera.Kind = kind;
        UpdateChrome();
        Invalidate();
    }

    /// <summary>Toggles the four-view layout (Object Builder F9). The state lives in the UI flag "view.quad" so menus/toolbar refresh.</summary>
    public void ToggleQuad() => Ed.SetFlag("view.quad", !quad);

    /// <summary>Activates the next/previous pane (Tab / Shift+Tab); in single mode it shows that pane.</summary>
    public void CyclePane(int dir)
    {
        SetActive((active + dir + panes.Length) % panes.Length);
        if (!quad) Layout();
    }

    void SetActive(int index)
    {
        if (index == active) return;
        active = index;
        if (!quad) Layout();
        else UpdateChrome();
        Invalidate();
    }

    public void FrameAll() => FrameAll(false);

    public void FrameAll(bool allViews)
    {
        if (renderer is { HasGeometry: true })
            foreach (var p in allViews ? panes : [panes[active]]) p.FrameBounds(renderer.BoundsMin, renderer.BoundsMax, renderer.RenderPositions);
        Invalidate();
    }

    public void FrameSelection()
    {
        var lod = Ed.Lod;
        var sel = Selection.PeekSelected(lod);
        if (sel is null || sel.Value.PointCount == 0 || renderer is null)
        {
            FrameAll();
            return;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var rp = renderer.RenderPositions;
        for (int i = 0; i < sel.Value.Points.Length && i * 3 + 2 < rp.Length; i++)
        {
            if (sel.Value.Points[i] == 0) continue;
            var p = new Vector3(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        panes[active].FrameBounds(min, max, rp, sel.Value.Points);
        Invalidate();
    }

    /// <summary>Zooms the active pane (&lt; 1 = in).</summary>
    public void Zoom(float factor)
    {
        panes[active].Zoom(factor);
        Invalidate();
    }

    /// <summary>Centres the active pane on a RAW point (Look At Pin).</summary>
    public void LookAt(Vector3 raw)
    {
        panes[active].Camera.Target = Coords.RawToRender(raw);
        Invalidate();
    }

    // ---- navigation (Object Builder scheme; see ViewportContract input ownership) --------------

    int PaneIndexAt(Point p)
    {
        if (!quad) return active;
        for (int i = 0; i < panes.Length; i++)
            if (panes[i].Rect.Contains(p)) return i;
        return -1;
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        int i = PaneIndexAt(e.GetPosition(Root));
        if (i >= 0) SetActive(i);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool lmb = e.LeftButton == MouseButtonState.Pressed, rmb = e.RightButton == MouseButtonState.Pressed, mmb = e.MiddleButton == MouseButtonState.Pressed;
        var cam = panes[active].Camera;
        Nav next = Nav.None;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (alt && shift && lmb && !rmb && ShadingMode != Shading.Wire) next = Nav.Sun;
        else if (alt && lmb && rmb) next = Nav.Zoom;
        else if (alt && rmb) next = cam.IsOrtho ? Nav.Pan : Nav.Orbit;
        else if (alt && lmb) next = Nav.Pan;
        else if (e.ChangedButton == MouseButton.Middle && mmb)
            next = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || cam.IsOrtho ? Nav.Pan : Nav.Orbit;
        if (next == Nav.None) return;
        if (next == Nav.Sun)
        {
            sunDragAz = RenderConfig.Float(RenderConfig.Azimuth);
            sunDragEl = RenderConfig.Float(RenderConfig.Elevation);
        }
        nav = next;
        navPane = panes[active];
        last = e.GetPosition(Root);
        if (nav == Nav.Sun) UpdateSunIndicator(last);
        Root.CaptureMouse();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (nav == Nav.None || navPane is null) return;
        var p = e.GetPosition(Root);
        var d = p - last;
        last = p;
        var camera = navPane.Camera;
        switch (nav)
        {
            case Nav.Orbit:
                camera.Yaw -= (float)d.X * 0.008f;
                camera.Pitch = Math.Clamp(camera.Pitch + (float)d.Y * 0.008f, -1.55f, 1.55f);
                break;
            case Nav.Pan:
            {
                float upp = camera.UnitsPerPixelAtTarget((float)Math.Max(1, navPane.Rect.Height));
                var (right, up) = camera.Axes();
                camera.Target += (-right * (float)d.X + up * (float)d.Y) * upp;
                break;
            }
            case Nav.Zoom:
                camera.Distance = Math.Clamp(camera.Distance * MathF.Exp((float)d.Y * 0.01f), 1e-3f, 1e6f);
                break;
            case Nav.Sun:
            {
                float az = (sunDragAz ?? 0) + (float)d.X * 0.45f;
                sunDragAz = az - 360f * MathF.Floor(az / 360f);
                sunDragEl = Math.Clamp((sunDragEl ?? 45) - (float)d.Y * 0.35f, -5f, 89.5f);
                UpdateSunIndicator(p);
                break;
            }
        }
        Invalidate();
        e.Handled = true;
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (nav == Nav.None) return;
        if (nav == Nav.Sun)
        {
            EndSunDrag(commit: true);
            nav = Nav.None;
            navPane = null;
            Root.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        bool anyDown = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed;
        if (anyDown && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && navPane is not null)
        {
            nav = e.RightButton == MouseButtonState.Pressed ? (navPane.Camera.IsOrtho ? Nav.Pan : Nav.Orbit) : Nav.Pan;
            return;
        }
        nav = Nav.None;
        navPane = null;
        Root.ReleaseMouseCapture();
        e.Handled = true;
    }

    void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (WheelZoom(e.GetPosition(Root), e.Delta)) e.Handled = true;
    }

    /// <summary>Mouse-wheel zoom at a control point (<paramref name="delta"/> 120 = one notch in). False outside the panes.</summary>
    public bool WheelZoom(Point cp, int delta)
    {
        int i = PaneIndexAt(cp);
        if (i < 0) return false;
        var pane = panes[i];
        var camera = pane.Camera;
        float factor = MathF.Pow(0.85f, delta / 120f);
        var vp = new Point(cp.X - pane.Rect.X, cp.Y - pane.Rect.Y);
        var (o, dir) = pane.RayAt(vp);
        if (!Finite(o) || !Finite(dir)) return true;
        Vector3 under;
        float size = SceneSize();
        float minDistance = Math.Max(1e-3f, size * 1e-3f);
        if (!camera.IsOrtho && SurfaceUnder(pane, vp) is { } hitT && Vector3.Dot(o + dir * hitT - camera.Eye, camera.Forward) is var depth and > 1e-4f)
        {
            // Zoom towards the surface under the cursor: pivot at its depth first (the view does not change), so the
            // camera closes in on that surface and never passes into the model, where every face is culled.
            var eye = camera.Eye;
            camera.Target = eye + camera.Forward * depth;
            camera.Distance = depth;
            under = o + dir * hitT;
        }
        else if (camera.IsOrtho)
        {
            // flat views: towards the cursor at the pivot's depth
            float t = Vector3.Dot(camera.Target - o, camera.Forward) / MathF.Max(1e-6f, Vector3.Dot(dir, camera.Forward));
            under = o + dir * t;
        }
        else
        {
            // nothing under the cursor: towards the pivot (the view centre), so the model does not fly off screen
            under = camera.Target;
            minDistance = Math.Max(minDistance, size * 0.05f);
        }
        minDistance = MathF.Min(minDistance, camera.Distance); // a wheel step in never moves the camera back
        float distance = camera.Distance * factor;
        // Out: at least 0.5 % of the model per notch, so backing away from a close-up takes a few notches, not fifty.
        if (factor > 1 && !camera.IsOrtho) distance = MathF.Max(distance, camera.Distance + size * 0.005f * MathF.Log(factor) / MathF.Log(1 / 0.85f));
        distance = Math.Clamp(distance, minDistance, 1e6f);
        float applied = distance / camera.Distance;
        var target = under + (camera.Target - under) * applied;
        if (!Finite(target) || !float.IsFinite(distance)) return true;
        camera.Target = target;
        camera.Distance = distance;
        Invalidate();
        return true;
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>
    /// Ray distance to the nearest face of the active LOD or a shown proxy model under a view point, else to a point
    /// of the active LOD next to the cursor (LODs without faces, e.g. Memory).
    /// </summary>
    float? SurfaceUnder(ViewPane pane, Point vp)
    {
        float? best = PickFace(pane, vp)?.Distance;
        if (Scene.ProxyViewport.Pick(pane, vp) is { } ph && (best is not { } b || ph.Distance < b)) best = ph.Distance;
        if (best is null && PickPoint(pane, vp, 16) is var pi and >= 0)
        {
            var raw = previews.GetValueOrDefault(Ed.ActiveLod) ?? Ed.Lod.Positions;
            var (o, dir) = pane.RayAt(vp);
            best = Vector3.Dot(Coords.RawToRender(new Vector3(raw[pi * 3], raw[pi * 3 + 1], raw[pi * 3 + 2])) - o, dir);
        }
        return best is { } d && d > 0 && float.IsFinite(d) ? d : null;
    }

    /// <summary>Size (bounding-box diagonal) of the active LOD for zoom limits; 2 m without geometry.</summary>
    float SceneSize()
    {
        var rp = RenderPositions;
        if (rp.Length < 3) return 2f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < rp.Length; i += 3)
        {
            var p = new Vector3(rp[i], rp[i + 1], rp[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        float size = (max - min).Length();
        return float.IsFinite(size) ? Math.Clamp(size, 0.2f, 100f) : 2f;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var pane = panes[active];
        var camera = pane.Camera;
        float step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 60 : 15;
        float upp = camera.UnitsPerPixelAtTarget((float)Math.Max(1, pane.Rect.Height));
        var (right, up) = camera.Axes();
        Vector3 delta = e.Key switch
        {
            Key.Left => -right * step * upp,
            Key.Right => right * step * upp,
            Key.Up => up * step * upp,
            Key.Down => -up * step * upp,
            _ => Vector3.Zero,
        };
        if (delta == Vector3.Zero) return;
        camera.Target += delta;
        Invalidate();
        e.Handled = true;
    }

    // ---- IViewportContext ----------------------------------------------------------------------

    public FrameworkElement InputElement => Root;
    public Canvas OverlayCanvas => Overlay;
    public IReadOnlyList<IViewHandle> Views => quad ? panes : [panes[active]];
    public IViewHandle ActiveView => panes[active];

    public (IViewHandle View, Point ViewPoint)? ViewAt(Point controlPoint)
    {
        if (controlPoint.X < 0 || controlPoint.Y < 0 || controlPoint.X > Root.ActualWidth || controlPoint.Y > Root.ActualHeight) return null;
        int i = PaneIndexAt(controlPoint);
        if (i < 0) return null;
        var r = panes[i].Rect;
        return (panes[i], new Point(controlPoint.X - r.X, controlPoint.Y - r.Y));
    }

    float[] RenderPositions => renderer?.RenderPositions ?? [];

    public FaceHit? PickFace(IViewHandle v, Point viewPoint)
    {
        var lod = Ed.Lod;
        var rp = RenderPositions;
        if (rp.Length != lod.PointCount * 3) return null;
        var (o, dir) = v.RayAt(viewPoint);
        var hidden = Selection.FacesTouching(lod, Selection.HiddenPoints(lod));
        float best = float.MaxValue;
        int bestFace = -1;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (hidden[f] != 0) continue;
            int nv = lod.FaceVertCount[f];
            for (int k = 1; k + 1 < nv; k++)
            {
                float t = RayTriangle(o, dir, P(rp, lod.FacePoints[f * 4]), P(rp, lod.FacePoints[f * 4 + k]), P(rp, lod.FacePoints[f * 4 + k + 1]));
                if (t > 0 && t < best)
                {
                    best = t;
                    bestFace = f;
                }
            }
        }
        if (bestFace < 0) return null;
        var hit = o + dir * best;
        int nearest = (int)lod.FacePoints[bestFace * 4];
        float nd = float.MaxValue;
        for (int k = 0; k < lod.FaceVertCount[bestFace]; k++)
        {
            uint pi = lod.FacePoints[bestFace * 4 + k];
            float dd = Vector3.DistanceSquared(P(rp, pi), hit);
            if (dd < nd)
            {
                nd = dd;
                nearest = (int)pi;
            }
        }
        return new FaceHit(bestFace, nearest, Coords.RenderToRaw(hit), best);
    }

    static Vector3 P(float[] rp, uint i) => new(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]);

    /// <summary>Möller–Trumbore; returns the ray parameter or -1.</summary>
    static float RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return -1;
        float inv = 1 / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        return Vector3.Dot(e2, q) * inv;
    }

    public int PickPoint(IViewHandle v, Point viewPoint, double radius, bool visibleOnly = false)
    {
        var lod = Ed.Lod;
        var raw = previews.GetValueOrDefault(Ed.ActiveLod) ?? lod.Positions;
        var proj = new float[lod.PointCount * 2];
        v.ProjectRaw(raw.AsSpan(0, Math.Min(raw.Length, lod.PointCount * 3)), proj);
        var hidden = Selection.HiddenPoints(lod);
        int best = -1;
        double bestD = radius * radius;
        for (int i = 0; i < lod.PointCount; i++)
        {
            if (hidden[i] != 0 || float.IsNaN(proj[i * 2])) continue;
            double dx = proj[i * 2] - viewPoint.X, dy = proj[i * 2 + 1] - viewPoint.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 > bestD) continue;
            if (visibleOnly && IsOccluded(v, i, raw)) continue;
            bestD = d2;
            best = i;
        }
        return best;
    }

    bool IsOccluded(IViewHandle v, int point, float[] raw)
    {
        var target = Coords.RawToRender(new Vector3(raw[point * 3], raw[point * 3 + 1], raw[point * 3 + 2]));
        var o = v.IsOrthographic ? target - v.Forward * 1e4f : v.Eye;
        var dir = Vector3.Normalize(target - o);
        float dist = Vector3.Distance(o, target);
        var lod = Ed.Lod;
        var rp = RenderPositions;
        if (rp.Length != lod.PointCount * 3) return false;
        float eps = Math.Max(dist * 1e-4f, 1e-4f);
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int nv = lod.FaceVertCount[f];
            for (int k = 1; k + 1 < nv; k++)
            {
                float t = RayTriangle(o, dir, P(rp, lod.FacePoints[f * 4]), P(rp, lod.FacePoints[f * 4 + k]), P(rp, lod.FacePoints[f * 4 + k + 1]));
                if (t > 0 && t < dist - eps) return true;
            }
        }
        return false;
    }

    public int[] PointsInPolygon(IViewHandle v, IReadOnlyList<Point> polygon, bool visibleOnly = false)
    {
        var lod = Ed.Lod;
        var raw = previews.GetValueOrDefault(Ed.ActiveLod) ?? lod.Positions;
        if (raw.Length < lod.PointCount * 3) raw = lod.Positions;
        var proj = new float[lod.PointCount * 2];
        v.ProjectRaw(raw.AsSpan(0, lod.PointCount * 3), proj);
        var hidden = Selection.HiddenPoints(lod);
        // polygon bounding box rejects most points before the exact test
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in polygon)
        {
            minX = Math.Min(minX, q.X);
            minY = Math.Min(minY, q.Y);
            maxX = Math.Max(maxX, q.X);
            maxY = Math.Max(maxY, q.Y);
        }
        var result = new List<int>();
        Occlusion? occ = null;
        for (int i = 0; i < lod.PointCount; i++)
        {
            float x = proj[i * 2], y = proj[i * 2 + 1];
            if (hidden[i] != 0 || float.IsNaN(x) || x < minX || x > maxX || y < minY || y > maxY) continue;
            if (!InPolygon(x, y, polygon)) continue;
            if (visibleOnly)
            {
                occ ??= new Occlusion(v, lod, raw, Selection.FacesTouching(lod, hidden));
                if (occ.IsOccluded(i)) continue;
            }
            result.Add(i);
        }
        return [.. result];
    }

    static bool InPolygon(double x, double y, IReadOnlyList<Point> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public void SetPreviewPositions(int lodIndex, float[]? positions)
    {
        if (positions is null) previews.Remove(lodIndex);
        else previews[lodIndex] = positions;
        Invalidate();
    }

    public void SetOverlayLines(string owner, IReadOnlyList<OverlayLine>? lines)
    {
        if (lines is null) overlays.Remove(owner);
        else overlays[owner] = lines;
        overlaysDirty = true;
        Invalidate();
    }
}
