using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Topology;

/// <summary>Bounding-box diagonal and largest absolute coordinate of a point set.</summary>
public readonly record struct PointSetScale(double Diagonal, double MaxAbs);

/// <summary>Per-face and per-set measurements on RAW P3D coordinates.</summary>
public static class Measure
{
    /// <summary>Relative size of float32 rounding noise: one float32 ulp at 1.0.</summary>
    public const double Float32Epsilon = 1.0 / (1 << 23);

    /// <summary>
    /// Right-handed signed volume Σ dot(A, cross(B, C)) / 6 over the faces (quads split (0,1,2),(0,2,3)) on RAW
    /// coordinates in stored order. An outward-facing closed P3D mesh gives a NEGATIVE value (docs/ARCHITECTURE.md §2).
    /// Only meaningful for closed face sets.
    /// </summary>
    public static double SignedVolume(Lod lod, ReadOnlySpan<int> faces)
    {
        var P = lod.Positions;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        double sum = 0;
        foreach (int f in faces)
        {
            int b = f * 4;
            var a = Vec3.At(P, fp[b]);
            var c = Vec3.At(P, fp[b + 2]);
            sum += Vec3.Dot(a, Vec3.Cross(Vec3.At(P, fp[b + 1]), c));
            if (nv[f] == 4) sum += Vec3.Dot(a, Vec3.Cross(c, Vec3.At(P, fp[b + 3])));
        }
        return sum / 6;
    }

    /// <summary>Signed volume of every face (or the masked ones).</summary>
    public static double SignedVolume(Lod lod, byte[]? faceMask = null) => SignedVolume(lod, MeshTopology.FaceList(lod, faceMask));

    /// <summary>
    /// Face normal in the P3D stored convention: normalize(cross(P1 − P0, P2 − P0)) on RAW coordinates (points INTO a
    /// correctly wound closed mesh). Zero for a degenerate face.
    /// </summary>
    public static Vec3 FaceNormalRaw(Lod lod, int f)
    {
        var P = lod.Positions;
        int b = f * 4;
        var a = Vec3.At(P, lod.FacePoints[b]);
        var n = Vec3.Cross(Vec3.At(P, lod.FacePoints[b + 1]) - a, Vec3.At(P, lod.FacePoints[b + 2]) - a);
        double len = Math.Sqrt(Vec3.Dot(n, n));
        return len > 0 ? n / len : Vec3.Zero;
    }

    /// <summary>Area of a face (quads as the two triangles (0,1,2),(0,2,3)).</summary>
    public static double FaceArea(Lod lod, int f)
    {
        var P = lod.Positions;
        int b = f * 4;
        var fp = lod.FacePoints;
        var a = Vec3.At(P, fp[b]);
        var c = Vec3.At(P, fp[b + 2]) - a;
        double area = Vec3.Cross(Vec3.At(P, fp[b + 1]) - a, c).Length;
        if (lod.FaceVertCount[f] == 4) area += Vec3.Cross(c, Vec3.At(P, fp[b + 3]) - a).Length;
        return area / 2;
    }

    /// <summary>
    /// Non-planarity of a quad: distance of the 4th point from the plane of the first three (falls back to the distance
    /// of the 2nd point from the plane of points 0, 2, 3 when the first three are collinear). 0 for triangles.
    /// </summary>
    public static double QuadPlanarity(Lod lod, int f)
    {
        if (lod.FaceVertCount[f] != 4) return 0;
        var P = lod.Positions;
        var fp = lod.FacePoints;
        int b = f * 4;
        var p0 = Vec3.At(P, fp[b]);
        var p1 = Vec3.At(P, fp[b + 1]);
        var p2 = Vec3.At(P, fp[b + 2]);
        var p3 = Vec3.At(P, fp[b + 3]);
        var n = Vec3.Cross(p1 - p0, p2 - p0);
        double len = n.Length;
        if (len > 1e-12) return Math.Abs(Vec3.Dot(n, p3 - p0)) / len;
        n = Vec3.Cross(p2 - p0, p3 - p0);
        len = n.Length;
        return len > 1e-12 ? Math.Abs(Vec3.Dot(n, p1 - p0)) / len : 0;
    }

    /// <summary>Longest diagonal / edge of a face (its characteristic size).</summary>
    public static double FaceSize(Lod lod, int f)
    {
        var P = lod.Positions;
        var fp = lod.FacePoints;
        int n = lod.FaceVertCount[f], b = f * 4;
        double best = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double d = (Vec3.At(P, fp[b + i]) - Vec3.At(P, fp[b + j])).Length;
                if (d > best) best = d;
            }
        return best;
    }

    /// <summary>Scale of the given points (all points when <paramref name="points"/> is null).</summary>
    public static PointSetScale Scale(float[] positions, ReadOnlySpan<int> points, bool all = false)
    {
        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        int count = all ? positions.Length / 3 : points.Length;
        for (int i = 0; i < count; i++)
        {
            int o = (all ? i : points[i]) * 3;
            double x = positions[o], y = positions[o + 1], z = positions[o + 2];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        if (count == 0) return new PointSetScale(0, 0);
        double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
        double maxAbs = Math.Max(Math.Max(Math.Max(-minX, maxX), Math.Max(-minY, maxY)), Math.Max(-minZ, maxZ));
        return new PointSetScale(Math.Sqrt(dx * dx + dy * dy + dz * dz), maxAbs);
    }

    /// <summary>
    /// Default distance tolerance for a point set: 1e-6 of its size, but never below a few float32 ulps of its largest
    /// coordinate (data far from the origin is quantised coarser).
    /// </summary>
    public static double DefaultTolerance(PointSetScale s) => Math.Max(s.Diagonal * 1e-6, s.MaxAbs * 4 * Float32Epsilon);

    /// <summary>
    /// Looser tolerance for classification (convexity, planarity): 1e-4 of the set size (0.1 mm on a 1 m box), at least
    /// 64 float32 ulps of the largest coordinate and 1e-5 model units.
    /// </summary>
    public static double ClassifyTolerance(PointSetScale s) =>
        Math.Max(Math.Max(s.Diagonal * 1e-4, s.MaxAbs * 64 * Float32Epsilon), 1e-5);
}
