using System.Numerics;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// Pure render maths (System.Numerics only — no WPF / GL types, so tests can call it). Row-vector convention
/// (v * M) like the rest of the renderer; RENDER = (x, y, -z) of RAW.
/// </summary>
public static class RenderMath
{
    /// <summary>
    /// Converts a RAW→RAW transform (e.g. ProxyInstance.Transform) into the RENDER→RENDER transform
    /// S·T·S with S = diag(1, 1, -1, 1): every element with exactly one z row/column changes sign.
    /// </summary>
    public static Matrix4x4 RawToRenderTransform(Matrix4x4 t) => new(
        t.M11, t.M12, -t.M13, t.M14,
        t.M21, t.M22, -t.M23, t.M24,
        -t.M31, -t.M32, t.M33, -t.M34,
        t.M41, t.M42, -t.M43, t.M44);

    /// <summary>Inverse transpose of the upper 3×3 (normals, row vectors: n' = n · N); identity when singular.</summary>
    public static Matrix4x4 NormalMatrix(Matrix4x4 m)
    {
        var a = m with { M14 = 0, M24 = 0, M34 = 0, M41 = 0, M42 = 0, M43 = 0, M44 = 1 };
        if (!Matrix4x4.Invert(a, out var inv)) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(inv);
    }

    /// <summary>Determinant of the upper 3×3: negative when the transform mirrors (front faces flip).</summary>
    public static float Determinant3(Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>
    /// Direction towards the sun in RENDER space. Azimuth in degrees clockwise from north (RAW +Z) seen from
    /// above (90° = east = RAW +X), elevation in degrees above the horizon.
    /// </summary>
    public static Vector3 SunDirection(float azimuthDeg, float elevationDeg)
    {
        float az = azimuthDeg * MathF.PI / 180f, el = Math.Clamp(elevationDeg, -89.9f, 89.9f) * MathF.PI / 180f;
        var raw = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
        return new Vector3(raw.X, raw.Y, -raw.Z);
    }

    /// <summary>
    /// Direction towards a light fixed relative to the camera ("Inventory"): azimuth 0 = from behind the camera,
    /// positive = from the camera's right; elevation = above the camera's horizontal plane.
    /// </summary>
    public static Vector3 CameraLightDirection(float azimuthDeg, float elevationDeg, Vector3 forward, Vector3 up)
    {
        forward = Normalized(forward, -Vector3.UnitZ);
        var right = Vector3.Cross(forward, up);
        right = Normalized(right, Vector3.UnitX);
        var camUp = Vector3.Normalize(Vector3.Cross(right, forward));
        float az = azimuthDeg * MathF.PI / 180f, el = Math.Clamp(elevationDeg, -89.9f, 89.9f) * MathF.PI / 180f;
        var back = -forward;
        var d = (back * MathF.Cos(az) + right * MathF.Sin(az)) * MathF.Cos(el) + camUp * MathF.Sin(el);
        return Normalized(d, Vector3.UnitY);
    }

    /// <summary>Azimuth / elevation (degrees) of a RENDER-space direction towards the sun (inverse of <see cref="SunDirection"/>).</summary>
    public static (float Azimuth, float Elevation) SunAngles(Vector3 renderDir)
    {
        renderDir = Normalized(renderDir, Vector3.UnitY);
        var raw = new Vector3(renderDir.X, renderDir.Y, -renderDir.Z);
        float el = MathF.Asin(Math.Clamp(raw.Y, -1f, 1f)) * 180f / MathF.PI;
        float az = MathF.Atan2(raw.X, raw.Z) * 180f / MathF.PI;
        if (az < 0) az += 360f;
        return (az, el);
    }

    /// <summary>
    /// Orthographic light view-projection (row vectors) for a directional shadow map enclosing the sphere
    /// (<paramref name="center"/>, <paramref name="radius"/>); <paramref name="towardsLight"/> points at the sun.
    /// Depth range covers the sphere with a margin; texel snapping keeps the map stable while the camera moves.
    /// </summary>
    public static Matrix4x4 ShadowViewProjection(Vector3 center, float radius, Vector3 towardsLight, int mapSize = 2048)
    {
        radius = MathF.Max(radius, 1e-3f);
        var dir = Normalized(towardsLight, Vector3.UnitY);
        var up = MathF.Abs(dir.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        var eye = center + dir * (radius * 2f);
        var view = Matrix4x4.CreateLookAt(eye, center, up);
        var proj = Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, radius * 0.5f, radius * 3.5f);
        var vp = view * proj;
        // Snap the projected origin to whole texels (no shimmering when the bounds move slightly).
        var o = Vector4.Transform(new Vector4(0, 0, 0, 1), vp);
        float half = mapSize * 0.5f;
        float sx = MathF.Round(o.X * half) / half - o.X, sy = MathF.Round(o.Y * half) / half - o.Y;
        return vp * Matrix4x4.CreateTranslation(sx, sy, 0);
    }

    /// <summary>Horizontal field of view (radians) for a vertical one and an aspect ratio (width / height).</summary>
    public static float HorizontalFov(float verticalFov, float aspect) => 2f * MathF.Atan(MathF.Tan(verticalFov * 0.5f) * aspect);

    /// <summary>Vertical field of view (radians) for a horizontal one and an aspect ratio (width / height).</summary>
    public static float VerticalFov(float horizontalFov, float aspect) => 2f * MathF.Atan(MathF.Tan(horizontalFov * 0.5f) / MathF.Max(aspect, 1e-6f));

    /// <summary>Linear exposure multiplier for an EV bias (stops).</summary>
    public static float ExposureScale(float ev) => MathF.Pow(2f, ev);

    /// <summary>Bounding sphere (center, radius) of an axis-aligned box.</summary>
    public static (Vector3 Center, float Radius) Sphere(Vector3 min, Vector3 max)
    {
        if (min.X > max.X) return (Vector3.Zero, 1f);
        var c = (min + max) * 0.5f;
        return (c, MathF.Max((max - min).Length() * 0.5f, 1e-3f));
    }

    /// <summary>Axis-aligned bounds of a box (min, max) transformed by <paramref name="m"/> (row vectors).</summary>
    public static (Vector3 Min, Vector3 Max) TransformBounds(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var rmin = new Vector3(float.MaxValue);
        var rmax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var q = Vector3.Transform(p, m);
            rmin = Vector3.Min(rmin, q);
            rmax = Vector3.Max(rmax, q);
        }
        return (rmin, rmax);
    }

    /// <summary>
    /// Fresnel reflectance at normal incidence for a complex index of refraction (n, k) — the parameters of
    /// the rvmat procedural <c>fresnel(n, k)</c> texture.
    /// </summary>
    public static float FresnelF0(float n, float k)
    {
        float num = (n - 1) * (n - 1) + k * k, den = (n + 1) * (n + 1) + k * k;
        return den <= 0 ? 0.04f : Math.Clamp(num / den, 0f, 1f);
    }

    static Vector3 Normalized(Vector3 v, Vector3 fallback)
    {
        float l = v.Length();
        return l > 1e-8f && float.IsFinite(l) ? v / l : fallback;
    }
}
