using System.Numerics;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Scene;

/// <summary>A proxy placed in a LOD: its named selection, model path, triangle face and placement transform.</summary>
/// <param name="Name">Named selection ("proxy:\dz\...\box_c.001").</param>
/// <param name="Path">Proxy model path from the name, without the index (as stored, e.g. "\dz\...\box_c").</param>
/// <param name="Index">Numeric suffix of the name (-1 when there is none).</param>
/// <param name="Face">The proxy triangle (face index), or -1 when the selection has no triangle (3 selected points).</param>
/// <param name="Transform">Proxy model RAW → host RAW (System.Numerics row vectors: v * Transform).</param>
/// <param name="A">First point of the proxy triangle (host LOD point index).</param>
/// <param name="B">Second point of the proxy triangle.</param>
/// <param name="C">Third point of the proxy triangle.</param>
public readonly record struct PlacedProxy(string Name, string Path, int Index, int Face, Matrix4x4 Transform, uint A = 0, uint B = 0, uint C = 0);

/// <summary>
/// How Object Builder / Buldozer / the game place a proxy model from its triangle (pure).
/// The vertex with the largest angle is the model origin, the short leg (to the vertex with the second largest
/// angle) is the model's +Z (forward), the long leg is +Y (up), X = Y × Z (<see cref="Proxies.ComputeFrame"/>,
/// a3ob utilities_proxy / mrcmodding "proxy coordinates"). The axes are orthonormalized and the size of the
/// triangle is ignored: the proxy model is placed rotated + translated, never scaled or mirrored (binarize stores
/// normalized axes, see armake p3d.c).
/// </summary>
public static class ProxyPlacement
{
    /// <summary>Rows = images of the proxy model's X, Y, Z axes, then the origin (unit scale).</summary>
    public static Matrix4x4 ToMatrix(in ProxyFrame f) => new(
        (float)f.X.X, (float)f.X.Y, (float)f.X.Z, 0,
        (float)f.Y.X, (float)f.Y.Y, (float)f.Y.Z, 0,
        (float)f.Z.X, (float)f.Z.Y, (float)f.Z.Z, 0,
        (float)f.Center.X, (float)f.Center.Y, (float)f.Center.Z, 1);

    /// <summary>Placement transform of a proxy triangle (RAW), or null for a degenerate triangle.</summary>
    public static Matrix4x4? FromTriangle(Vec3 a, Vec3 b, Vec3 c) =>
        Proxies.ComputeFrame(a, b, c) is { } f ? ToMatrix(f) : null;

    /// <summary>
    /// The three points defining a proxy: the first triangle of the selection (Object Builder / binarize use the
    /// first face), else exactly three selected points. Face = -1 in the second case.
    /// </summary>
    public static (int Face, uint A, uint B, uint C)? Triangle(Lod lod, SelectionTag tag)
    {
        int nf = Math.Min(tag.Faces.Length, lod.FaceCount);
        var faces = tag.Faces;
        var counts = lod.FaceVertCount;
        for (int f = 0; f < nf; f++)
        {
            if (faces[f] == 0 || counts[f] != 3) continue;
            uint a = lod.FacePoints[f * 4], b = lod.FacePoints[f * 4 + 1], c = lod.FacePoints[f * 4 + 2];
            if (a < lod.PointCount && b < lod.PointCount && c < lod.PointCount) return (f, a, b, c);
        }
        int np = Math.Min(tag.Points.Length, lod.PointCount);
        Span<uint> sel = stackalloc uint[3];
        int n = 0;
        for (int p = 0; p < np; p++)
        {
            if (tag.Points[p] == 0) continue;
            if (n == 3) return null;
            sel[n++] = (uint)p;
        }
        return n == 3 ? (-1, sel[0], sel[1], sel[2]) : null;
    }

    static Vec3 P(float[] pos, uint i) => new(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);

    /// <summary>
    /// Every placeable proxy of a LOD in tag order (degenerate or triangle-less proxies are skipped).
    /// <paramref name="positions"/> overrides the LOD's positions (RAW, 3 per point) when given.
    /// </summary>
    public static List<PlacedProxy> Find(Lod lod, float[]? positions = null)
    {
        var pos = positions is not null && positions.Length >= lod.PointCount * 3 ? positions : lod.Positions;
        var list = new List<PlacedProxy>();
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s || !Proxies.IsProxyName(s.Name)) continue;
            if (Triangle(lod, s) is not { } tri) continue;
            if (FromTriangle(P(pos, tri.A), P(pos, tri.B), P(pos, tri.C)) is not { } m) continue;
            var (path, index) = Proxies.Parse(s.Name);
            list.Add(new PlacedProxy(s.Name, path, index, tri.Face, m, tri.A, tri.B, tri.C));
        }
        return list;
    }

    /// <summary>True when the LOD has at least one proxy selection (cheap pre-check).</summary>
    public static bool HasProxies(Lod lod)
    {
        foreach (var t in lod.Tags)
            if (t is SelectionTag s && Proxies.IsProxyName(s.Name)) return true;
        return false;
    }

    /// <summary>The LOD drawn for a proxy model: the most detailed resolution LOD (first one on ties), else LOD 0.</summary>
    public static int DisplayLodIndex(P3DModel model)
    {
        int best = -1;
        for (int i = 0; i < model.Lods.Length; i++)
        {
            float r = model.Lods[i].Resolution;
            if (LodNames.TypeOf(r).Type.Id != "visual") continue;
            if (best < 0 || r < model.Lods[best].Resolution) best = i;
        }
        return best >= 0 ? best : model.Lods.Length > 0 ? 0 : -1;
    }

    /// <summary>Axis-aligned bounds of the points of a LOD (RAW), or null when it has none.</summary>
    public static (Vector3 Min, Vector3 Max)? Bounds(Lod lod)
    {
        var p = lod.Positions;
        int n = Math.Min(lod.PointCount, p.Length / 3);
        if (n == 0) return null;
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
            if (z < z0) z0 = z;
            if (z > z1) z1 = z;
        }
        return (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
    }
}
