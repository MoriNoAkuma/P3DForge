using System.Numerics;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// Orbit camera in RENDER space. Perspective views orbit a target; orthographic views look along a fixed
/// axis (Object Builder's Front/Left/Top…) and pan/zoom only.
/// </summary>
public sealed class ViewCamera
{
    public ViewKind Kind { get; set; } = ViewKind.Perspective;
    public Vector3 Target { get; set; }
    /// <summary>Distance from target (perspective) — also drives the ortho half-height.</summary>
    public float Distance { get; set; } = 5;
    /// <summary>Radians around +Y; 0 looks from +Z (RENDER) towards the target.</summary>
    public float Yaw { get; set; } = MathF.PI * 0.80f;
    public float Pitch { get; set; } = 0.42f;
    public float FovY { get; set; } = MathF.PI / 4;
    /// <summary>Perspective "projected" view can be switched to orthographic (Num5).</summary>
    public bool ForceOrtho { get; set; }

    public bool IsOrtho => Kind != ViewKind.Perspective || ForceOrtho;

    /// <summary>Direction from the target to the eye.</summary>
    public Vector3 EyeDirection => Kind switch
    {
        // Model front faces RAW +Z = RENDER -Z: the Front view looks at it from RENDER -Z.
        ViewKind.Front => new(0, 0, -1),
        ViewKind.Back => new(0, 0, 1),
        ViewKind.Left => new(-1, 0, 0),
        ViewKind.Right => new(1, 0, 0),
        ViewKind.Top => new(0, 1, 0),
        ViewKind.Bottom => new(0, -1, 0),
        _ => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw)),
    };

    Vector3 Up => Kind switch
    {
        ViewKind.Top => new(0, 0, -1),
        ViewKind.Bottom => new(0, 0, 1),
        _ => Vector3.UnitY,
    };

    public Vector3 Eye => Target + EyeDirection * Distance;
    public Vector3 Forward => -EyeDirection;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Up);

    public Matrix4x4 Projection(float aspect)
    {
        float far = Distance * 200f + 1000f;
        if (IsOrtho)
        {
            float h = Distance * MathF.Tan(FovY / 2) * 2;
            return Matrix4x4.CreateOrthographic(h * aspect, h, -far, far);
        }
        // far / near stays ≤ 1e6: beyond ~1e7 float rounding puts the far plane at infinity (depth and picking break
        // in close-ups), so a close-up sees 1e6 × near (≥ 10 m) instead of a kilometre
        float near = MathF.Max(Distance / 1000f, 1e-5f);
        return Matrix4x4.CreatePerspectiveFieldOfView(FovY, aspect, near, MathF.Min(far, near * 1e6f));
    }

    /// <summary>Camera-space right and up axes (RENDER) for panning.</summary>
    public (Vector3 Right, Vector3 Up) Axes()
    {
        var f = Forward;
        var r = Vector3.Normalize(Vector3.Cross(f, Up));
        if (!float.IsFinite(r.X)) r = Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(r, f));
        return (r, u);
    }

    /// <summary>World units per pixel at the target depth for a viewport of <paramref name="heightPx"/> pixels.</summary>
    public float UnitsPerPixelAtTarget(float heightPx) => Distance * MathF.Tan(FovY / 2) * 2 / MathF.Max(1, heightPx);

    /// <summary>Frames a bounding sphere (RENDER).</summary>
    public void Frame(Vector3 center, float radius, float aspect = 1)
    {
        if (!float.IsFinite(radius) || radius <= 0) radius = 1;
        Target = center;
        Distance = radius / MathF.Sin(FovY / 2) * 1.1f;
        // narrow panes: fit the width as well
        if (aspect > 0 && aspect < 1) Distance /= aspect;
    }

    /// <summary>
    /// Frames points (RENDER xyz, only those with mask[i] != 0 when a mask is given) tightly: the target is the centre
    /// of <paramref name="min"/>..<paramref name="max"/> and the distance is the smallest one at which every point
    /// projects inside the view (with a small margin) for the current direction. O(n), no allocations.
    /// </summary>
    public void FramePoints(Vector3 min, Vector3 max, float[] rp, byte[]? mask, float aspect = 1, float margin = 1.06f)
    {
        var center = (min + max) / 2;
        var half = (max - min) / 2;
        if (!float.IsFinite(half.X + half.Y + half.Z) || half.Length() < 1e-4f)
        {
            Frame(center, Math.Max(half.Length(), 0.05f), aspect);
            return;
        }
        if (!(aspect > 0)) aspect = 1;
        Target = center;
        var (right, up) = Axes();
        var toEye = EyeDirection;
        float tanY = MathF.Tan(FovY / 2), tanX = tanY * aspect;
        bool ortho = IsOrtho;
        float need = 0;
        int n = rp.Length / 3;
        for (int i = 0; i < n; i++)
        {
            if (mask is not null && (i >= mask.Length || mask[i] == 0)) continue;
            var c = new Vector3(rp[i * 3] - center.X, rp[i * 3 + 1] - center.Y, rp[i * 3 + 2] - center.Z);
            float x = MathF.Abs(Vector3.Dot(c, right)) * margin, y = MathF.Abs(Vector3.Dot(c, up)) * margin;
            need = ortho
                ? MathF.Max(need, MathF.Max(y, x / aspect) / tanY)
                : MathF.Max(need, Vector3.Dot(c, toEye) + MathF.Max(x / tanX, y / tanY));
        }
        Distance = Math.Clamp(need, 1e-3f, 1e6f);
    }

    /// <summary>
    /// Frames an axis-aligned box (RENDER) tightly: the target is the box centre and the distance is the smallest one
    /// at which all 8 corners project inside the view (with a small margin) for the current direction.
    /// </summary>
    public void FrameBox(Vector3 min, Vector3 max, float aspect = 1, float margin = 1.08f)
    {
        var center = (min + max) / 2;
        var half = (max - min) / 2;
        if (!float.IsFinite(half.X + half.Y + half.Z) || half.Length() < 1e-4f)
        {
            Frame(center, Math.Max(half.Length(), 0.05f), aspect);
            return;
        }
        if (!(aspect > 0)) aspect = 1;
        Target = center;
        var (right, up) = Axes();
        var toEye = EyeDirection;
        float tanY = MathF.Tan(FovY / 2), tanX = tanY * aspect;
        float need = 0;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
            float x = MathF.Abs(Vector3.Dot(c, right)) * margin, y = MathF.Abs(Vector3.Dot(c, up)) * margin;
            float z = Vector3.Dot(c, toEye); // towards the eye
            need = IsOrtho
                ? MathF.Max(need, MathF.Max(y, x / aspect) / tanY)
                : MathF.Max(need, z + MathF.Max(x / tanX, y / tanY));
        }
        Distance = Math.Clamp(need, 1e-3f, 1e6f);
    }
}
