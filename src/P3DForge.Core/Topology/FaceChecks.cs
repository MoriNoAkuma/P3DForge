using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Topology;

/// <summary>Result of <see cref="FaceChecks.Check"/>; every list is ascending.</summary>
public sealed class FaceCheckResult
{
    /// <summary>Faces that use the same point twice, have no area or are slivers.</summary>
    public required int[] Degenerate { get; init; }
    /// <summary>Quads whose 4th point is off the plane of the first three by more than the tolerance.</summary>
    public required int[] NonPlanar { get; init; }
    /// <summary>Faces with the same points and orientation as an earlier face (the later copies).</summary>
    public required int[] Duplicate { get; init; }
    /// <summary>Faces with the same points as an earlier face but the opposite winding (back-to-back pairs, the later face).</summary>
    public required int[] Reversed { get; init; }
    /// <summary>Faces referencing a point index outside the point list.</summary>
    public required int[] Invalid { get; init; }
    /// <summary>Largest non-planarity found (model units).</summary>
    public double MaxNonPlanarity { get; init; }

    public int ProblemCount => Degenerate.Length + NonPlanar.Length + Duplicate.Length + Invalid.Length;
}

/// <summary>Face sanity checks and repairs (Object Builder "Check Faces", "Remove Duplicate Faces", "Degenerated Faces").</summary>
public static class FaceChecks
{
    /// <summary>Relative sliver threshold: area / longest edge² below this is degenerate.</summary>
    public const double SliverRatio = 1e-6;
    /// <summary>Default non-planarity threshold relative to the quad size (0.5 %).</summary>
    public const double PlanarityRatio = 0.005;

    /// <summary>
    /// Classifies the (masked) faces. A quad is non-planar when its planarity distance exceeds
    /// max(<paramref name="planarityRatio"/> × its size, <paramref name="planarityAbs"/>).
    /// </summary>
    public static FaceCheckResult Check(Lod lod, byte[]? faceMask = null, double planarityRatio = PlanarityRatio, double planarityAbs = 1e-4)
    {
        int fc = lod.FaceCount, pc = lod.PointCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        List<int> degenerate = [], nonPlanar = [], invalid = [];
        double maxNp = 0;
        for (int f = 0; f < fc; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            int n = nv[f], b = f * 4;
            if (n is < 3 or > 4) { invalid.Add(f); continue; }
            bool bad = false;
            for (int k = 0; k < n; k++) if (fp[b + k] >= pc) bad = true;
            if (bad) { invalid.Add(f); continue; }
            if (IsDegenerate(lod, f)) { degenerate.Add(f); continue; }
            if (n == 4)
            {
                double d = Measure.QuadPlanarity(lod, f);
                if (d > Math.Max(planarityRatio * Measure.FaceSize(lod, f), planarityAbs))
                {
                    nonPlanar.Add(f);
                    if (d > maxNp) maxNp = d;
                }
            }
        }
        var (dup, rev) = Duplicates(lod, faceMask);
        return new FaceCheckResult
        {
            Degenerate = degenerate.ToArray(),
            NonPlanar = nonPlanar.ToArray(),
            Duplicate = dup,
            Reversed = rev,
            Invalid = invalid.ToArray(),
            MaxNonPlanarity = maxNp,
        };
    }

    /// <summary>Same point used twice, zero area, or a sliver (area below <see cref="SliverRatio"/> × longest edge²).</summary>
    public static bool IsDegenerate(Lod lod, int f)
    {
        int n = lod.FaceVertCount[f], b = f * 4;
        var fp = lod.FacePoints;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (fp[b + i] == fp[b + j]) return true;
        double size = Measure.FaceSize(lod, f);
        if (size <= 0) return true;
        return Measure.FaceArea(lod, f) <= SliverRatio * size * size;
    }

    /// <summary>
    /// Duplicate faces (same point set and orientation as an earlier face) and reversed duplicates (same set, opposite
    /// orientation), the later face of each pair.
    /// </summary>
    public static (int[] Duplicate, int[] Reversed) Duplicates(Lod lod, byte[]? faceMask = null)
    {
        var (dupOf, revOf) = DuplicateSources(lod, faceMask);
        List<int> d = [], r = [];
        for (int f = 0; f < dupOf.Length; f++)
        {
            if (dupOf[f] >= 0) d.Add(f);
            else if (revOf[f] >= 0) r.Add(f);
        }
        return (d.ToArray(), r.ToArray());
    }

    /// <summary>For each face the earlier identical face (or -1), and the earlier reversed face (or -1).</summary>
    internal static (int[] DupOf, int[] RevOf) DuplicateSources(Lod lod, byte[]? faceMask)
    {
        int fc = lod.FaceCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        var dupOf = new int[fc];
        var revOf = new int[fc];
        Array.Fill(dupOf, -1);
        Array.Fill(revOf, -1);
        var seen = new Dictionary<FaceKey, int>(fc);
        Span<uint> s = stackalloc uint[4];
        for (int f = 0; f < fc; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            int n = nv[f], b = f * 4;
            if (n is < 3 or > 4) continue;
            var key = Canonical(fp.AsSpan(b, n), reverse: false, s);
            if (seen.TryGetValue(key, out int g)) { dupOf[f] = g; continue; }
            var rkey = Canonical(fp.AsSpan(b, n), reverse: true, s);
            if (seen.TryGetValue(rkey, out g)) revOf[f] = g;
            seen.TryAdd(key, f);
        }
        return (dupOf, revOf);
    }

    private readonly record struct FaceKey(uint A, uint B, uint C, uint D);

    /// <summary>Cyclic order starting at the smallest index (reversed first when asked); D = uint.MaxValue for triangles.</summary>
    private static FaceKey Canonical(ReadOnlySpan<uint> pts, bool reverse, Span<uint> s)
    {
        int n = pts.Length;
        for (int k = 0; k < n; k++) s[k] = reverse ? pts[n - 1 - k] : pts[k];
        int m = 0;
        for (int k = 1; k < n; k++) if (s[k] < s[m]) m = k;
        uint a = s[m], b = s[(m + 1) % n], c = s[(m + 2) % n], d = n == 4 ? s[(m + 3) % n] : uint.MaxValue;
        return new FaceKey(a, b, c, d);
    }

    /// <summary>
    /// Removes faces identical to an earlier face (same points, same orientation; with <paramref name="includeReversed"/>
    /// also back-to-back copies). The kept face inherits the named-selection membership of its first removed copy.
    /// Points are unchanged.
    /// </summary>
    public static Lod RemoveDuplicateFaces(Lod lod, byte[]? faceMask, bool includeReversed, out int removed)
    {
        var (dupOf, revOf) = DuplicateSources(lod, faceMask);
        int fc = lod.FaceCount;
        var drop = new bool[fc];
        var merged = new int[fc];
        Array.Fill(merged, -1);
        removed = 0;
        for (int f = 0; f < fc; f++)
        {
            int src = dupOf[f] >= 0 ? dupOf[f] : includeReversed ? revOf[f] : -1;
            if (src < 0 || drop[src]) continue;
            drop[f] = true;
            removed++;
            if (merged[src] < 0) merged[src] = f;
        }
        if (removed == 0) return lod;
        var b = new FaceBuild(fc - removed);
        var src2 = new int[fc - removed];
        int d = 0;
        for (int f = 0; f < fc; f++)
        {
            if (drop[f]) continue;
            b.CopyFace(d, lod, f);
            src2[d] = merged[f];
            d++;
        }
        b.Src2 = src2;
        return MeshMath.RebuildFaces(lod, b);
    }

    /// <summary>
    /// Repairs degenerate faces: repeated point indices are collapsed (a quad with one repeated point becomes a
    /// triangle); faces left with fewer than 3 distinct points or without area are deleted. Points are unchanged.
    /// </summary>
    public static Lod RepairDegenerate(Lod lod, byte[]? faceMask, out int fixedCount, out int deleted)
    {
        int fc = lod.FaceCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        fixedCount = 0;
        deleted = 0;
        var order = new int[fc][];
        Span<int> slots = stackalloc int[4];
        for (int f = 0; f < fc; f++)
        {
            int n = nv[f], b = f * 4;
            if ((faceMask is not null && faceMask[f] == 0) || !IsDegenerate(lod, f)) continue;
            int m = 0;
            for (int k = 0; k < n; k++)
            {
                bool dup = false;
                for (int j = 0; j < m; j++) if (fp[b + slots[j]] == fp[b + k]) dup = true;
                if (!dup) slots[m++] = k;
            }
            if (m < 3 || (m == n) || TriangleArea(lod, f, slots[..m]) <= 0)
            {
                order[f] = [];
                deleted++;
            }
            else
            {
                order[f] = slots[..m].ToArray();
                if (IsSliver(lod, f, order[f])) { order[f] = []; deleted++; }
                else fixedCount++;
            }
        }
        if (fixedCount == 0 && deleted == 0) return lod;
        var bld = new FaceBuild(fc - deleted);
        int d = 0;
        for (int f = 0; f < fc; f++)
        {
            var o = order[f];
            if (o is null) bld.CopyFace(d++, lod, f);
            else if (o.Length >= 3) bld.CopyFace(d++, lod, f, o);
        }
        return MeshMath.RebuildFaces(lod, bld);
    }

    private static double TriangleArea(Lod lod, int f, ReadOnlySpan<int> slots)
    {
        var P = lod.Positions;
        var fp = lod.FacePoints;
        int b = f * 4;
        var a = Vec3.At(P, fp[b + slots[0]]);
        double area = 0;
        for (int k = 1; k + 1 < slots.Length; k++)
            area += Vec3.Cross(Vec3.At(P, fp[b + slots[k]]) - a, Vec3.At(P, fp[b + slots[k + 1]]) - a).Length / 2;
        return area;
    }

    private static bool IsSliver(Lod lod, int f, int[] slots)
    {
        var P = lod.Positions;
        var fp = lod.FacePoints;
        int b = f * 4;
        double size = 0;
        for (int i = 0; i < slots.Length; i++)
            for (int j = i + 1; j < slots.Length; j++)
                size = Math.Max(size, (Vec3.At(P, fp[b + slots[i]]) - Vec3.At(P, fp[b + slots[j]])).Length);
        return size <= 0 || TriangleArea(lod, f, slots) <= SliverRatio * size * size;
    }
}
