using P3DForge.Core.Ops;

namespace P3DForge.Core.Topology;

/// <summary>Result of <see cref="ConvexHull.Build"/>. Every index refers to the ORIGINAL point indices.</summary>
public sealed class HullResult
{
    public HullFailure Failure { get; init; }
    public bool Ok => Failure == HullFailure.None;
    /// <summary>Distance tolerance the hull was built with (model units).</summary>
    public double Tolerance { get; init; }
    /// <summary>Hull vertices, ascending.</summary>
    public int[] HullPoints { get; init; } = [];
    /// <summary>Start of each hull polygon in <see cref="PolyPoints"/>; length = polygon count + 1.</summary>
    public int[] PolyStart { get; init; } = [0];
    /// <summary>Polygon vertex loops in P3D winding (outward hull ⇒ negative right-handed signed volume on RAW coords).</summary>
    public int[] PolyPoints { get; init; } = [];
    /// <summary>Outward unit normal and offset per polygon (nx, ny, nz, d): a point x is outside when n·x − d &gt; tol.</summary>
    public double[] Planes { get; init; } = [];
    /// <summary>Faces ready for a LOD (3|4 per face): triangles and planar quads, P3D winding.</summary>
    public byte[] FaceVertCount { get; init; } = [];
    /// <summary>Stride 4 (slot 3 = slot 0 for triangles).</summary>
    public int[] FacePoints { get; init; } = [];

    public int PolygonCount => PolyStart.Length - 1;
    public int FaceCount => FaceVertCount.Length;

    /// <summary>Signed distance of <paramref name="x"/> outside polygon <paramref name="poly"/>'s plane.</summary>
    public double Distance(int poly, Vec3 x) =>
        Planes[poly * 4] * x.X + Planes[poly * 4 + 1] * x.Y + Planes[poly * 4 + 2] * x.Z - Planes[poly * 4 + 3];

    /// <summary>Human-readable reason of a failure.</summary>
    public string Describe() => Failure switch
    {
        HullFailure.None => $"{HullPoints.Length} points, {FaceCount} faces",
        HullFailure.TooFewPoints => "at least 4 points are needed",
        HullFailure.Degenerate => "all points coincide",
        HullFailure.Colinear => "the points are colinear",
        HullFailure.Coplanar => "the points are coplanar (flat)",
        _ => "the hull could not be built (numerical problem)",
    };
}

/// <summary>Convex hull of a point set on RAW coordinates, robust to coplanar / duplicate / degenerate input.</summary>
public static class ConvexHull
{
    /// <summary>
    /// Builds the convex hull of <paramref name="points"/> (indices into <paramref name="positions"/>, xyz per point).
    /// <paramref name="tolerance"/> ≤ 0 selects <see cref="Measure.DefaultTolerance"/> of the point set; on an internal
    /// failure the build is retried with 10× and 100× the tolerance. Coplanar hull facets are merged into polygons;
    /// polygons with 4 vertices become quads when <paramref name="quads"/>, larger ones are fan-triangulated.
    /// </summary>
    public static HullResult Build(float[] positions, ReadOnlySpan<int> points, double tolerance = 0, bool quads = true)
    {
        int n = points.Length;
        if (n < 4) return new HullResult { Failure = HullFailure.TooFewPoints };
        var scale = Measure.Scale(positions, points);
        double tol = tolerance > 0 ? tolerance : Measure.DefaultTolerance(scale);

        // Centre the coordinates (bounding-box centre) for precision.
        double minX = double.PositiveInfinity, minY = minX, minZ = minX, maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        foreach (int p in points)
        {
            double x = positions[p * 3], y = positions[p * 3 + 1], z = positions[p * 3 + 2];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, cz = (minZ + maxZ) / 2;
        var coords = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            int o = points[i] * 3;
            coords[i * 3] = positions[o] - cx;
            coords[i * 3 + 1] = positions[o + 1] - cy;
            coords[i * 3 + 2] = positions[o + 2] - cz;
        }

        HullFailure fail = HullFailure.Broken;
        int[] faceStart = [], loops = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            fail = QuickHull.Build(coords, tol, out faceStart, out loops);
            if (fail != HullFailure.Broken) break;
            tol *= 10;
        }
        if (fail != HullFailure.None) return new HullResult { Failure = fail, Tolerance = tol };

        int polyCount = faceStart.Length - 1;
        var polyPoints = new int[loops.Length];
        var planes = new double[polyCount * 4];
        var used = new HashSet<int>();
        int faceCount = 0;
        for (int f = 0; f < polyCount; f++)
        {
            int s = faceStart[f], e = faceStart[f + 1], len = e - s;
            // Outward plane from the right-handed CCW loop (Newell normal on centred coordinates).
            double nx = 0, ny = 0, nz = 0, sx = 0, sy = 0, sz = 0;
            for (int k = 0; k < len; k++)
            {
                int a = loops[s + k] * 3, b = loops[s + (k + 1) % len] * 3;
                nx += (coords[a + 1] - coords[b + 1]) * (coords[a + 2] + coords[b + 2]);
                ny += (coords[a + 2] - coords[b + 2]) * (coords[a] + coords[b]);
                nz += (coords[a] - coords[b]) * (coords[a + 1] + coords[b + 1]);
                sx += coords[a]; sy += coords[a + 1]; sz += coords[a + 2];
            }
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 0) { nx /= nl; ny /= nl; nz /= nl; }
            planes[f * 4] = nx;
            planes[f * 4 + 1] = ny;
            planes[f * 4 + 2] = nz;
            planes[f * 4 + 3] = nx * (sx / len + cx) + ny * (sy / len + cy) + nz * (sz / len + cz);
            // P3D winding = reversed right-handed CCW loop.
            for (int k = 0; k < len; k++)
            {
                int orig = points[loops[e - 1 - k]];
                polyPoints[s + k] = orig;
                used.Add(orig);
            }
            faceCount += len == 4 && quads ? 1 : len - 2;
        }

        var vc = new byte[faceCount];
        var fp = new int[faceCount * 4];
        int d = 0;
        for (int f = 0; f < polyCount; f++)
        {
            int s = faceStart[f], len = faceStart[f + 1] - s;
            if (len == 4 && quads)
            {
                vc[d] = 4;
                for (int k = 0; k < 4; k++) fp[d * 4 + k] = polyPoints[s + k];
                d++;
                continue;
            }
            for (int k = 1; k + 1 < len; k++)
            {
                vc[d] = 3;
                fp[d * 4] = polyPoints[s];
                fp[d * 4 + 1] = polyPoints[s + k];
                fp[d * 4 + 2] = polyPoints[s + k + 1];
                fp[d * 4 + 3] = polyPoints[s];
                d++;
            }
        }
        var hullPoints = used.ToArray();
        Array.Sort(hullPoints);
        return new HullResult
        {
            Failure = HullFailure.None,
            Tolerance = tol,
            HullPoints = hullPoints,
            PolyStart = faceStart,
            PolyPoints = polyPoints,
            Planes = planes,
            FaceVertCount = vc,
            FacePoints = fp,
        };
    }
}
