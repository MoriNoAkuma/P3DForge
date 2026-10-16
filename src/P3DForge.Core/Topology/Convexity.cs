using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Topology;

/// <summary>Convexity of one face set (normally a component).</summary>
public sealed class ConvexityResult
{
    /// <summary>Faces that do not lie on the convex hull of the set's points, ascending.</summary>
    public required int[] NonConvexFaces { get; init; }
    /// <summary>Points strictly inside the hull (deeper than the tolerance), ascending.</summary>
    public required int[] InteriorPoints { get; init; }
    /// <summary>Edges whose two faces meet at an interior angle above 180° (flat pairs a, b).</summary>
    public required int[] ConcaveEdges { get; init; }
    /// <summary>Largest depth of a point below the hull surface (model units, ≥ 0).</summary>
    public double MaxViolation { get; init; }
    /// <summary>Classification tolerance used.</summary>
    public double Tolerance { get; init; }
    /// <summary>Hull build failure (Coplanar = flat set, convex by definition; Degenerate/Colinear = no area).</summary>
    public HullFailure HullFailure { get; init; }
    public bool Convex => NonConvexFaces.Length == 0 && ConcaveEdges.Length == 0;
}

/// <summary>Convexity tests: faces on / off the convex hull and locally concave edges.</summary>
public static class Convexity
{
    /// <summary>
    /// Checks the convexity of a face set: builds the convex hull of its points and reports the faces that are not on
    /// it, the interior points and the concave edges. <paramref name="tolerance"/> ≤ 0 selects
    /// <see cref="Measure.ClassifyTolerance"/> of the set (0.01 % of its size).
    /// </summary>
    public static ConvexityResult Check(Lod lod, int[] faces, double tolerance = 0)
    {
        var points = MeshTopology.PointsOfFaces(lod, faces);
        var P = lod.Positions;
        double tol = tolerance > 0 ? tolerance : Measure.ClassifyTolerance(Measure.Scale(P, points));
        var concave = ConcaveEdges(lod, faces, tol);
        var hull = ConvexHull.Build(P, points);
        if (!hull.Ok)
            return new ConvexityResult
            {
                NonConvexFaces = [], InteriorPoints = [], ConcaveEdges = concave, Tolerance = tol, HullFailure = hull.Failure,
            };

        // Hull vertex → incident hull polygons (CSR over the ascending HullPoints).
        var hv = hull.HullPoints;
        int planes = hull.PolygonCount;
        var pl = hull.Planes;
        var incStart = new int[hv.Length + 1];
        var pp = hull.PolyPoints;
        for (int i = 0; i < pp.Length; i++) incStart[Array.BinarySearch(hv, pp[i]) + 1]++;
        for (int i = 0; i < hv.Length; i++) incStart[i + 1] += incStart[i];
        var inc = new int[pp.Length];
        var fill = incStart.AsSpan(0, hv.Length).ToArray();
        for (int poly = 0; poly < planes; poly++)
            for (int k = hull.PolyStart[poly]; k < hull.PolyStart[poly + 1]; k++)
                inc[fill[Array.BinarySearch(hv, pp[k])]++] = poly;

        // Depth of every point below the hull surface: min over planes of (d − n·x); hull vertices are on it.
        // On the surface ⇔ depth ≤ tol (early exit), so only interior points scan every plane.
        int pc = lod.PointCount;
        var depth = new Dictionary<int, double>(points.Length);
        double maxViolation = 0;
        var interior = new List<int>();
        foreach (int p in points)
        {
            if (Array.BinarySearch(hv, p) >= 0) { depth[p] = 0; continue; }
            double x = P[p * 3], y = P[p * 3 + 1], z = P[p * 3 + 2];
            double best = double.PositiveInfinity;
            for (int h = 0; h < planes; h++)
            {
                double dd = pl[h * 4 + 3] - (pl[h * 4] * x + pl[h * 4 + 1] * y + pl[h * 4 + 2] * z);
                if (dd < best) { best = dd; if (best <= tol) break; }
            }
            if (best > tol) interior.Add(p);
            if (best > maxViolation && best > tol) maxViolation = best;
            depth[p] = best;
        }

        // A face is on the hull when none of its points is interior and all of them lie on one hull plane. Candidate
        // planes are the polygons incident to the face's hull vertices (every plane only when it has none).
        var nonConvex = new List<int>();
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        foreach (int f in faces)
        {
            int n = nv[f], b = f * 4;
            bool inside = false, anyVertex = false, onPlane = false;
            for (int k = 0; k < n && !inside; k++)
            {
                uint p = fp[b + k];
                if (p < pc && depth[(int)p] > tol) inside = true;
            }
            if (!inside)
            {
                for (int k = 0; k < n && !onPlane; k++)
                {
                    int v = Array.BinarySearch(hv, (int)fp[b + k]);
                    if (v < 0) continue;
                    anyVertex = true;
                    for (int i = incStart[v]; i < incStart[v + 1] && !onPlane; i++)
                        onPlane = FaceOnPlane(P, fp, b, n, pl, inc[i], tol);
                }
                if (!anyVertex) onPlane = OnSomePlane(P, fp, b, n, pl, planes, tol);
                // A non-planar quad is on the hull when one of its two triangulations is (the engine splits it).
                if (!onPlane && n == 4)
                    onPlane = SplitOnHull(P, fp, b, 0, hv, incStart, inc, pl, planes, tol)
                              || SplitOnHull(P, fp, b, 1, hv, incStart, inc, pl, planes, tol);
            }
            if (inside || !onPlane) nonConvex.Add(f);
        }
        return new ConvexityResult
        {
            NonConvexFaces = nonConvex.ToArray(),
            InteriorPoints = interior.ToArray(),
            ConcaveEdges = concave,
            MaxViolation = maxViolation,
            Tolerance = tol,
            HullFailure = HullFailure.None,
        };
    }

    /// <summary>Split 0 = triangles (0,1,2)+(0,2,3), split 1 = (0,1,3)+(1,2,3); true when both lie on hull planes.</summary>
    private static bool SplitOnHull(float[] P, uint[] fp, int b, int split, int[] hv, int[] incStart, int[] inc,
        double[] pl, int planes, double tol)
    {
        int[] t = split == 0 ? Split0 : Split1;
        return TriangleOnHull(P, fp, b, t, 0, hv, incStart, inc, pl, planes, tol)
            && TriangleOnHull(P, fp, b, t, 3, hv, incStart, inc, pl, planes, tol);
    }

    static readonly int[] Split0 = [0, 1, 2, 0, 2, 3];
    static readonly int[] Split1 = [0, 1, 3, 1, 2, 3];

    private static bool TriangleOnHull(float[] P, uint[] fp, int b, int[] t, int o, int[] hv, int[] incStart, int[] inc,
        double[] pl, int planes, double tol)
    {
        bool anyVertex = false;
        for (int j = 0; j < 3; j++)
        {
            int v = Array.BinarySearch(hv, (int)fp[b + t[o + j]]);
            if (v < 0) continue;
            anyVertex = true;
            for (int i = incStart[v]; i < incStart[v + 1]; i++)
                if (TriangleOnPlane(P, fp, b, t, o, pl, inc[i], tol)) return true;
        }
        if (anyVertex) return false;
        for (int h = 0; h < planes; h++)
            if (TriangleOnPlane(P, fp, b, t, o, pl, h, tol)) return true;
        return false;
    }

    private static bool TriangleOnPlane(float[] P, uint[] fp, int b, int[] t, int o, double[] pl, int h, double tol)
    {
        double nx = pl[h * 4], ny = pl[h * 4 + 1], nz = pl[h * 4 + 2], d = pl[h * 4 + 3];
        for (int j = 0; j < 3; j++)
        {
            long q = fp[b + t[o + j]] * 3L;
            if (Math.Abs(nx * P[q] + ny * P[q + 1] + nz * P[q + 2] - d) > tol) return false;
        }
        return true;
    }

    private static bool FaceOnPlane(float[] P, uint[] fp, int b, int n, double[] pl, int h, double tol)
    {
        double nx = pl[h * 4], ny = pl[h * 4 + 1], nz = pl[h * 4 + 2], d = pl[h * 4 + 3];
        for (int k = 0; k < n; k++)
        {
            long o = fp[b + k] * 3L;
            if (Math.Abs(nx * P[o] + ny * P[o + 1] + nz * P[o + 2] - d) > tol) return false;
        }
        return true;
    }

    private static bool OnSomePlane(float[] P, uint[] fp, int b, int n, double[] pl, int planes, double tol)
    {
        for (int h = 0; h < planes; h++)
        {
            double nx = pl[h * 4], ny = pl[h * 4 + 1], nz = pl[h * 4 + 2], d = pl[h * 4 + 3];
            bool all = true;
            for (int k = 0; k < n; k++)
            {
                long o = fp[b + k] * 3L;
                if (Math.Abs(nx * P[o] + ny * P[o + 1] + nz * P[o + 2] - d) > tol) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    /// <summary>
    /// Edges shared by exactly two consistently wound faces whose interior angle is above 180°: a vertex of one face lies
    /// more than <paramref name="tol"/> outside the other face's plane. Returns flat (a, b) pairs.
    /// </summary>
    public static int[] ConcaveEdges(Lod lod, int[] faces, double tol)
    {
        int pc = lod.PointCount;
        var P = lod.Positions;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        // Edge uses keyed by (min, max); value = first face * 8 + slot * 2 + direction (−1 after a third use).
        var first = new Dictionary<long, int>(faces.Length * 2, EdgeKey.Comparer);
        var o = new List<int>();
        foreach (int f in faces)
        {
            int n = nv[f], b = f * 4;
            for (int k = 0; k < n; k++)
            {
                uint a = fp[b + k], c = fp[b + (k + 1 == n ? 0 : k + 1)];
                if (a == c || a >= pc || c >= pc) continue;
                long key = EdgeKey.Of(a, c);
                int dir = a < c ? 1 : 0;
                if (!first.TryGetValue(key, out int prev))
                {
                    first[key] = f * 8 + k * 2 + dir;
                    continue;
                }
                if (prev < 0) continue; // third use: non-manifold, skip
                first[key] = -1;
                int g = prev >> 3, kg = (prev >> 1) & 3;
                if ((prev & 1) == dir) continue; // inconsistent winding: undefined
                if (Concave(P, fp, nv, f, k, g, tol) || Concave(P, fp, nv, g, kg, f, tol))
                {
                    o.Add((int)Math.Min(a, c));
                    o.Add((int)Math.Max(a, c));
                }
            }
        }
        return o.ToArray();
    }

    /// <summary>
    /// True when a vertex of <paramref name="g"/> (other than the shared edge's) lies more than tol outside the plane
    /// of <paramref name="f"/>'s corner triangle at edge slot <paramref name="k"/> (the whole face's plane when that
    /// corner is degenerate). Using the corner keeps slightly non-planar quads from creating false concave edges.
    /// </summary>
    private static bool Concave(float[] P, uint[] fp, byte[] nv, int f, int k, int g, double tol)
    {
        int n = nv[f], b = f * 4;
        uint ea = fp[b + k], eb = fp[b + (k + 1) % n], ec = fp[b + (k + 2) % n];
        long a3 = ea * 3L, b3 = eb * 3L, c3 = ec * 3L;
        double ux = P[b3] - P[a3], uy = P[b3 + 1] - P[a3 + 1], uz = P[b3 + 2] - P[a3 + 2];
        double vx = P[c3] - P[a3], vy = P[c3 + 1] - P[a3 + 1], vz = P[c3 + 2] - P[a3 + 2];
        // The stored (right-handed) normal points into the mesh: outward = −(u × v).
        double nx = -(uy * vz - uz * vy), ny = -(uz * vx - ux * vz), nz = -(ux * vy - uy * vx);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        double ox = P[a3], oy = P[a3 + 1], oz = P[a3 + 2];
        double scale = Math.Sqrt((ux * ux + uy * uy + uz * uz) * (vx * vx + vy * vy + vz * vz));
        if (len <= 1e-9 * scale || len == 0)
        {
            // Degenerate corner: whole-face Newell plane through the centroid.
            nx = ny = nz = 0;
            ox = oy = oz = 0;
            for (int j = 0; j < n; j++)
            {
                long p = fp[b + j] * 3L, q = fp[b + (j + 1 == n ? 0 : j + 1)] * 3L;
                nx += (P[p + 1] - P[q + 1]) * (double)(P[p + 2] + P[q + 2]);
                ny += (P[p + 2] - P[q + 2]) * (double)(P[p] + P[q]);
                nz += (P[p] - P[q]) * (double)(P[p + 1] + P[q + 1]);
                ox += P[p]; oy += P[p + 1]; oz += P[p + 2];
            }
            len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len <= 0) return false;
            nx = -nx; ny = -ny; nz = -nz;
            ox /= n; oy /= n; oz /= n;
        }
        nx /= len; ny /= len; nz /= len;
        int m = nv[g], gb = g * 4;
        for (int j = 0; j < m; j++)
        {
            uint pv = fp[gb + j];
            if (pv == ea || pv == eb) continue;
            long v = pv * 3L;
            if (nx * (P[v] - ox) + ny * (P[v + 1] - oy) + nz * (P[v + 2] - oz) > tol) return true;
        }
        return false;
    }
}
