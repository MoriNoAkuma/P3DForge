using System.Numerics;
using System.Windows;
using P3DForge.App.Viewport.Rendering;
using Point = System.Windows.Point;

namespace P3DForge.App.Viewport;

/// <summary>One pane of the viewport (single view or one quarter of the four-view layout) with its own camera.</summary>
public sealed class ViewPane(string id, ViewKind kind) : IViewHandle
{
    public ViewCamera Camera { get; } = new() { Kind = kind };
    public string Id { get; } = id;
    public ViewKind Kind => Camera.Kind;
    public bool IsOrthographic => Camera.IsOrtho;
    /// <summary>Rectangle in VIEW units relative to the viewport control (set by the layout).</summary>
    public Rect Rect { get; set; } = new(0, 0, 1, 1);
    /// <summary>The kind the pane had when created (four-view defaults).</summary>
    public ViewKind DefaultKind { get; } = kind;

    double W => Math.Max(1, Rect.Width);
    double H => Math.Max(1, Rect.Height);
    public float Aspect => (float)(W / H);

    public Matrix4x4 ViewProjection => Camera.View * Camera.Projection(Aspect);
    public Vector3 Eye => Camera.IsOrtho ? Camera.Target + Camera.EyeDirection * Camera.Distance * 50 : Camera.Eye;
    public Vector3 Forward => Camera.Forward;

    public string Label => Camera.Kind == ViewKind.Perspective
        ? Camera.ForceOrtho ? "Projected (ortho)" : "Projected"
        : Camera.Kind.ToString();

    public void ProjectRaw(ReadOnlySpan<float> positions, Span<float> output)
    {
        var m = ViewProjection;
        double w = W, h = H;
        int n = positions.Length / 3;
        for (int i = 0; i < n; i++)
        {
            var c = Vector4.Transform(new Vector4(positions[i * 3], positions[i * 3 + 1], -positions[i * 3 + 2], 1), m);
            if (c.W <= 1e-9f || c.Z < 0 || c.Z > c.W)
            {
                output[i * 2] = output[i * 2 + 1] = float.NaN;
                continue;
            }
            output[i * 2] = (float)((c.X / c.W * 0.5 + 0.5) * w);
            output[i * 2 + 1] = (float)((0.5 - c.Y / c.W * 0.5) * h);
        }
    }

    /// <summary>Projects one RENDER point to VIEW-local pixels (NaN when clipped).</summary>
    public Point ProjectRender(Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), ViewProjection);
        if (c.W <= 1e-9f || c.Z < 0 || c.Z > c.W) return new Point(double.NaN, double.NaN);
        return new Point((c.X / c.W * 0.5 + 0.5) * W, (0.5 - c.Y / c.W * 0.5) * H);
    }

    public (Vector3 Origin, Vector3 Direction) RayAt(Point p)
    {
        float nx = (float)(p.X / W * 2 - 1), ny = (float)(1 - p.Y / H * 2);
        if (!Camera.IsOrtho)
        {
            // from the camera itself: inverting the projection loses the far plane in close-ups (NaN rays)
            float tan = MathF.Tan(Camera.FovY / 2);
            var (right, up) = Camera.Axes();
            return (Camera.Eye, Vector3.Normalize(Camera.Forward + right * (nx * tan * Aspect) + up * (ny * tan)));
        }
        Matrix4x4.Invert(ViewProjection, out var inv);
        var a = Vector4.Transform(new Vector4(nx, ny, 0, 1), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    public float UnitsPerPixel(Vector3 renderPoint)
    {
        float h = (float)H;
        if (IsOrthographic) return Camera.UnitsPerPixelAtTarget(h);
        float depth = Vector3.Dot(renderPoint - Camera.Eye, Camera.Forward);
        return MathF.Max(depth, 1e-4f) * MathF.Tan(Camera.FovY / 2) * 2 / h;
    }

    /// <summary>Frames a RENDER-space bounding box; with <paramref name="renderPositions"/> the points themselves (tighter).</summary>
    public void FrameBounds(Vector3 min, Vector3 max, float[]? renderPositions = null, byte[]? mask = null)
    {
        if (renderPositions is not null) Camera.FramePoints(min, max, renderPositions, mask, Aspect);
        else Camera.FrameBox(min, max, Aspect);
    }

    /// <summary>Zooms by <paramref name="factor"/> (&lt; 1 = closer) keeping the target.</summary>
    public void Zoom(float factor) => Camera.Distance = Math.Clamp(Camera.Distance * factor, 1e-3f, 1e6f);
}
