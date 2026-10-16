using System.Numerics;
using P3DForge.Core.Model;

namespace P3DForge.Core.Scene;

/// <summary>Ray casting against placed proxy models (pure, RAW coordinates, no allocation per face).</summary>
public static class ProxyRay
{
    /// <summary>Slab test: entry distance of a ray into a box (0 when the origin is inside), or null when it misses.</summary>
    public static float? RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float t0 = 0, t1 = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z;
            float hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(da) < 1e-12f)
            {
                if (oa < lo || oa > hi) return null;
                continue;
            }
            float inv = 1 / da;
            float n = (lo - oa) * inv, f = (hi - oa) * inv;
            if (n > f) (n, f) = (f, n);
            if (n > t0) t0 = n;
            if (f < t1) t1 = f;
            if (t0 > t1) return null;
        }
        return t0;
    }

    /// <summary>Möller–Trumbore, both sides; the ray parameter of the hit (&gt; 0) or -1.</summary>
    public static float RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
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
        float t = Vector3.Dot(e2, q) * inv;
        return t > 0 ? t : -1;
    }

    /// <summary>Nearest hit (ray parameter) of a ray with the faces of a LOD in its own RAW space, or null.</summary>
    public static float? RayLod(Lod lod, Vector3 o, Vector3 d)
    {
        var pos = lod.Positions;
        var fp = lod.FacePoints;
        var counts = lod.FaceVertCount;
        int np = Math.Min(lod.PointCount, pos.Length / 3);
        float best = float.MaxValue;
        for (int f = 0; f < counts.Length; f++)
        {
            int nv = counts[f];
            uint i0 = fp[f * 4];
            if (i0 >= np) continue;
            var a = new Vector3(pos[i0 * 3], pos[i0 * 3 + 1], pos[i0 * 3 + 2]);
            for (int k = 1; k + 1 < nv; k++)
            {
                uint i1 = fp[f * 4 + k], i2 = fp[f * 4 + k + 1];
                if (i1 >= np || i2 >= np) continue;
                float t = RayTriangle(o, d,
                    a,
                    new Vector3(pos[i1 * 3], pos[i1 * 3 + 1], pos[i1 * 3 + 2]),
                    new Vector3(pos[i2 * 3], pos[i2 * 3 + 1], pos[i2 * 3 + 2]));
                if (t > 0 && t < best) best = t;
            }
        }
        return best < float.MaxValue ? best : null;
    }

    /// <summary>
    /// Nearest hit of a host-space ray (RAW, normalized direction) with a placed proxy model, as a host-space ray
    /// parameter. <paramref name="bounds"/> (model space) is checked first.
    /// </summary>
    public static float? RayPlaced(Lod lod, (Vector3 Min, Vector3 Max) bounds, Matrix4x4 transform, Vector3 o, Vector3 d)
    {
        if (!Matrix4x4.Invert(transform, out var inv)) return null;
        var lo = Vector3.Transform(o, inv);
        var ld = Vector3.TransformNormal(d, inv);
        float len = ld.Length();
        if (!(len > 1e-12f)) return null;
        ld /= len; // the placement is rigid, but stay correct for any affine transform
        if (RayBox(lo, ld, bounds.Min, bounds.Max) is null) return null;
        if (RayLod(lod, lo, ld) is not { } t) return null;
        var hit = Vector3.Transform(lo + ld * t, transform);
        return Vector3.Dot(hit - o, d);
    }
}
