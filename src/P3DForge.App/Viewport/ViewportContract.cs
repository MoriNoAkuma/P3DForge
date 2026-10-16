using System.Numerics;
using System.Windows;

namespace P3DForge.App.Viewport;

/*
 * Contract between the render core (Viewport/Rendering/**, ViewportView) and code that builds on the
 * viewport: interaction (selection, OB mouse transforms, gizmos), animation preview, UV editor, features.
 *
 * Coordinate spaces:
 *   RAW    — P3D file coordinates (left-handed, +X right, +Y up, +Z forward). Core/Editor use RAW only.
 *   RENDER — OpenGL world coordinates = (x, y, -z) of RAW. The z-mirror turns the stored P3D winding
 *            into counter-clockwise-from-outside, so standard back-face culling works.
 *   VIEW   — device-independent pixels (WPF units) relative to the top-left of a view's rectangle.
 *
 * INPUT OWNERSHIP (all handlers listen on IViewportContext.InputElement):
 *   - Camera navigation (render core) owns the wheel, the middle button and every gesture that starts
 *     with Alt held (Alt+RMB orbit, Alt+LMB pan, Alt+LMB+RMB zoom), plus arrow keys.
 *   - Interaction plugins own LMB and RMB gestures without Alt (selection, OB mouse transforms,
 *     gizmos, context menu). They must ignore events with Alt held or the middle button.
 */

public static class Coords
{
    public static Vector3 RawToRender(Vector3 raw) => new(raw.X, raw.Y, -raw.Z);
    public static Vector3 RenderToRaw(Vector3 r) => new(r.X, r.Y, -r.Z);
}

public enum ViewKind
{
    Perspective,
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>One 3D view (a camera drawn into a rectangle of the shared viewport surface).</summary>
public interface IViewHandle
{
    string Id { get; }
    ViewKind Kind { get; }
    bool IsOrthographic { get; }
    /// <summary>Rectangle in VIEW units relative to the viewport control.</summary>
    Rect Rect { get; }
    /// <summary>World(RENDER)→clip matrix, System.Numerics row-vector convention (v * ViewProjection).</summary>
    Matrix4x4 ViewProjection { get; }
    /// <summary>Camera position (RENDER).</summary>
    Vector3 Eye { get; }
    /// <summary>Normalized view direction (RENDER).</summary>
    Vector3 Forward { get; }
    /// <summary>
    /// Projects RAW points (xyz per point) to VIEW pixels: output[2i], output[2i+1]. Points behind the
    /// camera or outside the clip volume get NaN. <paramref name="output"/> length ≥ 2·pointCount.
    /// </summary>
    void ProjectRaw(ReadOnlySpan<float> positions, Span<float> output);
    /// <summary>World(RENDER) ray through a VIEW pixel: (origin, normalized direction).</summary>
    (Vector3 Origin, Vector3 Direction) RayAt(Point viewPoint);
    /// <summary>World units per VIEW pixel at a RENDER-space point (constant-size gizmos, pick tolerances).</summary>
    float UnitsPerPixel(Vector3 renderPoint);
}

/// <summary>A face hit by a pick ray.</summary>
public readonly record struct FaceHit(int Face, int NearestPoint, Vector3 RawPosition, float Distance);

/// <summary>A line segment overlay (RENDER coordinates) drawn on top of the model in every view.</summary>
public readonly record struct OverlayLine(Vector3 A, Vector3 B, uint Rgba);

/// <summary>Services the render core offers to plugins.</summary>
public interface IViewportContext
{
    /// <summary>Element receiving mouse/keyboard input for all views.</summary>
    FrameworkElement InputElement { get; }
    /// <summary>WPF canvas above the 3D surface for 2D overlays (rubber band, labels). VIEW = canvas units.</summary>
    System.Windows.Controls.Canvas OverlayCanvas { get; }
    IReadOnlyList<IViewHandle> Views { get; }
    /// <summary>View under a point of <see cref="InputElement"/> (control coordinates), with the VIEW-local point.</summary>
    (IViewHandle View, Point ViewPoint)? ViewAt(Point controlPoint);
    /// <summary>The view the user interacted with last (target for view commands).</summary>
    IViewHandle ActiveView { get; }
    /// <summary>Schedules a redraw. Rendering is on demand.</summary>
    void Invalidate();
    /// <summary>Nearest visible (non-hidden) face of the active LOD under a VIEW point.</summary>
    FaceHit? PickFace(IViewHandle view, Point viewPoint);
    /// <summary>Nearest visible point of the active LOD within <paramref name="radius"/> VIEW units, or -1.</summary>
    int PickPoint(IViewHandle view, Point viewPoint, double radius, bool visibleOnly = false);
    /// <summary>Indices of non-hidden points whose projection lies inside a VIEW-space polygon.</summary>
    int[] PointsInPolygon(IViewHandle view, IReadOnlyList<Point> polygon, bool visibleOnly = false);
    /// <summary>
    /// Displays <paramref name="positions"/> (RAW, 3 per point) instead of the LOD's stored positions without
    /// touching the document (live transform/animation previews). null restores the stored positions.
    /// </summary>
    void SetPreviewPositions(int lodIndex, float[]? positions);
    /// <summary>Overlay line sets keyed by owner (e.g. "gizmo"); replace with null to remove.</summary>
    void SetOverlayLines(string owner, IReadOnlyList<OverlayLine>? lines);
}

/// <summary>A plugin is attached to every viewport instance; returns a dispose action.</summary>
public delegate Action ViewportPlugin(IViewportContext ctx);

public static class ViewportPlugins
{
    static readonly List<ViewportPlugin> plugins = [];
    static readonly Dictionary<IViewportContext, List<Action>> live = [];

    /// <summary>The current viewport (null before it is created).</summary>
    public static IViewportContext? Current { get; private set; }

    /// <summary>Registers a plugin; it is attached to existing and future viewports.</summary>
    public static void Register(ViewportPlugin plugin)
    {
        plugins.Add(plugin);
        foreach (var (ctx, list) in live) list.Add(plugin(ctx));
    }

    /// <summary>Called by the render core when a viewport is created.</summary>
    public static Action Attach(IViewportContext ctx)
    {
        Current = ctx;
        var disposers = plugins.Select(p => p(ctx)).ToList();
        live[ctx] = disposers;
        return () =>
        {
            foreach (var d in disposers) d();
            live.Remove(ctx);
            if (ReferenceEquals(Current, ctx)) Current = null;
        };
    }
}
