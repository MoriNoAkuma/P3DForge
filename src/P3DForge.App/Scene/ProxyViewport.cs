using System.Numerics;
using System.Windows;
using P3DForge.App.Viewport;
using P3DForge.Core.Scene;

namespace P3DForge.App.Scene;

/// <summary>Viewport side of the proxy scene: CPU picking of the displayed proxy models and the selection boxes.</summary>
public static class ProxyViewport
{
    public const string OverlayOwner = "objects";
    /// <summary>Selection box colour (0xRRGGBBAA), the selection rubber-band amber.</summary>
    public const uint BoxColor = 0xF2C94CFF;
    /// <summary>Box drawn around selected proxies without a loaded model (missing / ODOL / loading), half size in metres.</summary>
    const float PlaceholderHalf = 0.25f;

    /// <summary>
    /// Nearest visible, loaded proxy model under a VIEW point: (instance index, ray distance in the units of
    /// <see cref="FaceHit.Distance"/>), or null. Bounding boxes reject first; nothing is allocated per face.
    /// </summary>
    public static (int Index, float Distance)? Pick(IViewHandle view, Point viewPoint)
    {
        if (!ProxyScene.Enabled) return null;
        var list = ProxyScene.Instances;
        if (list.Count == 0) return null;
        var (o, d) = view.RayAt(viewPoint);
        var ro = Coords.RenderToRaw(o);
        var rd = Coords.RenderToRaw(d);
        int best = -1;
        float bestT = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var inst = list[i];
            if (!inst.Visible || inst.Model.Lod is not { } lod) continue;
            if (ProxyLibrary.BoundsOf(inst.Model) is not { } b) continue;
            if (ProxyRay.RayPlaced(lod, b, inst.Transform, ro, rd) is { } t && t > 0 && t < bestT)
            {
                bestT = t;
                best = i;
            }
        }
        return best >= 0 ? (best, bestT) : null;
    }

    /// <summary>Distance along the pick ray of a RAW point of the active LOD (to compare with a proxy hit).</summary>
    public static float RayDistance(IViewHandle view, Point viewPoint, Vector3 raw)
    {
        var (o, d) = view.RayAt(viewPoint);
        return Vector3.Dot(Coords.RawToRender(raw) - o, d);
    }

    /// <summary>
    /// Centers the active view on an instance's model and zooms so that it fills about half the view (Objects:
    /// double-click / Center &amp; Zoom). Falls back to the proxy triangle origin with a 1 m radius for unloaded models.
    /// </summary>
    public static bool Frame(int instance)
    {
        var list = ProxyScene.Instances;
        if (instance < 0 || instance >= list.Count || ViewportPlugins.Current is not ViewportView vv || vv.ActiveView is not { } view) return false;
        var inst = list[instance];
        var (min, max) = ProxyLibrary.BoundsOf(inst.Model) is { } b ? b : (new Vector3(-1), new Vector3(1));
        var wmin = new Vector3(float.MaxValue);
        var wmax = new Vector3(float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var p = Vector3.Transform(new Vector3((k & 1) != 0 ? max.X : min.X, (k & 2) != 0 ? max.Y : min.Y, (k & 4) != 0 ? max.Z : min.Z), inst.Transform);
            wmin = Vector3.Min(wmin, p);
            wmax = Vector3.Max(wmax, p);
        }
        var center = (wmin + wmax) / 2;
        float radius = MathF.Max((wmax - wmin).Length() / 2, 0.05f);
        vv.LookAt(center);
        float visible = view.UnitsPerPixel(Coords.RawToRender(center)) * (float)Math.Min(view.Rect.Width, view.Rect.Height);
        if (visible > 0 && float.IsFinite(visible)) vv.Zoom(Math.Clamp(2.6f * radius / visible, 1e-3f, 1e3f));
        return true;
    }

    /// <summary>Viewport plugin: selection boxes of the selected instances ("objects" overlay lines).</summary>
    public static Action Attach(IViewportContext ctx)
    {
        void Update()
        {
            ctx.SetOverlayLines(OverlayOwner, BuildBoxes());
            ctx.Invalidate();
        }
        ProxyScene.Changed += Update;
        Update();
        return () =>
        {
            ProxyScene.Changed -= Update;
            ctx.SetOverlayLines(OverlayOwner, null);
        };
    }

    static List<OverlayLine>? BuildBoxes()
    {
        if (!ProxyScene.Enabled || !ProxyScene.SelectionBox) return null;
        List<OverlayLine>? lines = null;
        var list = ProxyScene.Instances;
        for (int i = 0; i < list.Count; i++)
        {
            var inst = list[i];
            if (!inst.Selected || !inst.Visible) continue;
            var b = inst.Model.Lod is not null ? ProxyLibrary.BoundsOf(inst.Model) : null;
            if (b is null && inst.Depth > 0) continue; // unloaded nested models: the parent box is enough
            var (min, max) = b ?? (new Vector3(-PlaceholderHalf), new Vector3(PlaceholderHalf));
            AddBox(lines ??= [], min, max, inst.Transform, BoxColor);
        }
        return lines;
    }

    static readonly int[] Edges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];

    /// <summary>12 edges of a model-space box placed by <paramref name="m"/> (RAW), in RENDER coordinates.</summary>
    public static void AddBox(List<OverlayLine> lines, Vector3 min, Vector3 max, Matrix4x4 m, uint rgba)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int k = 0; k < 8; k++)
        {
            var p = new Vector3((k & 1) != 0 ? max.X : min.X, (k & 2) != 0 ? max.Y : min.Y, (k & 4) != 0 ? max.Z : min.Z);
            c[k] = Coords.RawToRender(Vector3.Transform(p, m));
        }
        for (int e = 0; e < Edges.Length; e += 2) lines.Add(new OverlayLine(c[Edges[e]], c[Edges[e + 1]], rgba));
    }
}
