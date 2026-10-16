using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>Result of a point merge.</summary>
/// <param name="Lod">The new LOD (the input instance when nothing was merged).</param>
/// <param name="RemovedPoints">Points removed by the merge.</param>
/// <param name="RemovedFaces">Faces removed because they became degenerate.</param>
/// <param name="CollapsedQuads">Quads that became triangles.</param>
/// <param name="Groups">Number of groups merged (each into one point).</param>
public sealed record MergeResult(Lod Lod, int RemovedPoints, int RemovedFaces, int CollapsedQuads, int Groups);

/// <summary>Direction for <see cref="PointOps.Navigate"/> (Points › Select First / Next / Previous).</summary>
public enum PointNavigation
{
    First,
    Next,
    Previous,
}

/// <summary>
/// Point operations: merge (into one point), merge near (spatial hash), flatten onto a plane, snap to grid, isolated
/// points and first/next/previous navigation. Pure: never writes into arrays of the input LOD.
/// </summary>
public static class PointOps
{
    /// <summary>
    /// Merges groups of points. <c>rep[i]</c> is the representative of point i (rep[i] = i for points that stay alone;
    /// every group's representative must be its lowest index and map to itself). The representative moves to the
    /// group's centroid (or <paramref name="target"/> for every group); named-selection weights become the group maximum,
    /// masses are summed, animation frames use the frame centroid. Faces that lose vertices drop duplicate corners
    /// (quad → triangle) and are removed when fewer than 3 distinct points remain. Merged-away points are deleted.
    /// </summary>
    public static MergeResult MergeGroups(Lod lod, int[] rep, Vec3? target = null)
    {
        int n = lod.PointCount;
        if (rep.Length != n) throw new ArgumentException("rep must have one entry per point", nameof(rep));
        var size = new int[n];
        for (int i = 0; i < n; i++)
        {
            int r = rep[i];
            if (r < 0 || r > i || rep[r] != r) throw new ArgumentException($"MergeGroups: invalid representative {r} for point {i}");
            size[r]++;
        }
        int groups = 0, removedPoints = 0;
        for (int i = 0; i < n; i++)
        {
            if (rep[i] != i) removedPoints++;
            else if (size[i] > 1) groups++;
        }
        if (removedPoints == 0) return new MergeResult(lod, 0, 0, 0, 0);

        var P = lod.Positions;
        var acc = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            int r = rep[i];
            if (size[r] < 2) continue;
            acc[r * 3] += P[i * 3];
            acc[r * 3 + 1] += P[i * 3 + 1];
            acc[r * 3 + 2] += P[i * 3 + 2];
        }
        var positions = (float[])P.Clone();
        for (int i = 0; i < n; i++)
        {
            if (rep[i] != i || size[i] < 2) continue;
            if (target is { } t)
            {
                positions[i * 3] = (float)t.X;
                positions[i * 3 + 1] = (float)t.Y;
                positions[i * 3 + 2] = (float)t.Z;
            }
            else
                for (int c = 0; c < 3; c++) positions[i * 3 + c] = (float)(acc[i * 3 + c] / size[i]);
        }

        byte[] MergeBytes(byte[] bytes)
        {
            var o = (byte[])bytes.Clone();
            for (int i = 0; i < n; i++)
            {
                int r = rep[i];
                if (r != i) o[r] = MeshMath.MaxWeightByte(o[r], bytes[i]);
            }
            return o;
        }

        var tags = lod.Tags.Select(tag =>
        {
            switch (tag)
            {
                case SelectionTag s:
                    return s with { Points = MergeBytes(s.Points) };
                case StateTag s:
                    return s with { Points = MergeBytes(s.Points) };
                case MassTag m:
                {
                    var mass = (float[])m.Mass.Clone();
                    for (int i = 0; i < n; i++)
                        if (rep[i] != i) mass[rep[i]] += m.Mass[i];
                    return m with { Mass = mass };
                }
                case AnimationTag a:
                {
                    var sum = new double[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        int r = rep[i];
                        if (size[r] < 2) continue;
                        for (int c = 0; c < 3; c++) sum[r * 3 + c] += a.Positions[i * 3 + c];
                    }
                    var pos = (float[])a.Positions.Clone();
                    for (int i = 0; i < n; i++)
                    {
                        if (rep[i] != i || size[i] < 2) continue;
                        for (int c = 0; c < 3; c++)
                            pos[i * 3 + c] = target is { } t
                                ? (float)(t[c] + (a.Positions[i * 3 + c] - P[i * 3 + c]))
                                : (float)(sum[i * 3 + c] / size[i]);
                    }
                    return a with { Positions = pos };
                }
                case SharpEdgesTag e:
                {
                    var o = new List<uint>(e.Edges.Length);
                    var seen = new HashSet<long>();
                    for (int i = 0; i + 1 < e.Edges.Length; i += 2)
                    {
                        uint a0 = e.Edges[i], b0 = e.Edges[i + 1];
                        uint a = a0 < n ? (uint)rep[a0] : a0, b = b0 < n ? (uint)rep[b0] : b0;
                        if (a == b) continue;
                        long key = a < b ? (long)a * n + b : (long)b * n + a;
                        if (!seen.Add(key)) continue;
                        o.Add(a);
                        o.Add(b);
                    }
                    return (Tag)(e with { Edges = o.ToArray() });
                }
                default:
                    return tag;
            }
        }).ToImmutableArray();

        int fc = lod.FaceCount;
        var fp = lod.FacePoints;
        var keepSlots = new sbyte[fc * 4];
        Array.Fill(keepSlots, (sbyte)-1);
        var newNv = new byte[fc];
        int removedFaces = 0, collapsedQuads = 0;
        Span<int> mapped = stackalloc int[4];
        for (int f = 0; f < fc; f++)
        {
            int nv = lod.FaceVertCount[f];
            bool touched = false;
            for (int k = 0; k < nv; k++)
            {
                mapped[k] = rep[fp[f * 4 + k]];
                if (mapped[k] != fp[f * 4 + k]) touched = true;
            }
            if (!touched)
            {
                newNv[f] = (byte)nv;
                for (int k = 0; k < nv; k++) keepSlots[f * 4 + k] = (sbyte)k;
                continue;
            }
            int m = 0;
            for (int k = 0; k < nv; k++)
            {
                if (k > 0 && mapped[k] == mapped[k - 1]) continue;
                if (k == nv - 1 && mapped[k] == mapped[0]) continue;
                keepSlots[f * 4 + m++] = (sbyte)k;
            }
            bool distinct = m >= 3;
            for (int x = 0; distinct && x < m; x++)
                for (int y = x + 1; y < m; y++)
                    if (mapped[keepSlots[f * 4 + x]] == mapped[keepSlots[f * 4 + y]])
                    {
                        distinct = false;
                        break;
                    }
            if (!distinct)
            {
                newNv[f] = 0;
                removedFaces++;
                continue;
            }
            newNv[f] = (byte)m;
            if (nv == 4 && m == 3) collapsedQuads++;
        }
        var b = new FaceBuild(fc - removedFaces);
        int d = 0;
        for (int f = 0; f < fc; f++)
        {
            int nv = newNv[f];
            if (nv == 0) continue;
            b.VertCount[d] = (byte)nv;
            b.Src[d] = f;
            for (int k = 0; k < nv; k++)
            {
                int s = keepSlots[f * 4 + k];
                b.Points[d * 4 + k] = (uint)rep[fp[f * 4 + s]];
                b.SlotFace[d * 4 + k] = f;
                b.SlotIdx[d * 4 + k] = (sbyte)s;
            }
            d++;
        }
        var next = MeshMath.RebuildFaces(lod with { Positions = positions, Tags = tags }, b);
        var keepPoints = new byte[n];
        for (int i = 0; i < n; i++) keepPoints[i] = rep[i] == i ? (byte)1 : (byte)0;
        next = Structure.Compact(next, keepPoints, MeshMath.Ones(next.FaceCount));
        return new MergeResult(next, removedPoints, removedFaces, collapsedQuads, groups);
    }

    /// <summary>Merges every point in <paramref name="mask"/> into one point at their centroid (or at <paramref name="target"/>).</summary>
    public static MergeResult Merge(Lod lod, byte[] mask, Vec3? target = null)
    {
        int n = lod.PointCount, first = -1;
        var rep = new int[n];
        for (int i = 0; i < n; i++)
        {
            rep[i] = i;
            if (mask[i] == 0) continue;
            if (first < 0) first = i;
            rep[i] = first;
        }
        return MergeGroups(lod, rep, target);
    }

    // Multipliers of the per-axis cell hash; the murmur3 finalizer (Fmix) mixes high bits into the low bits used by the
    // power-of-two table (plain multiplicative hashes keep the low-bit structure of grid-aligned coordinates).
    const int HX = unchecked((int)0x9e3779b1), HY = unchecked((int)0x85ebca77), HZ = unchecked((int)0xc2b2ae3d);

    static uint Fmix(int h0)
    {
        uint h = (uint)h0;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }

    static int Cell(double v) => unchecked((int)(long)Math.Floor(v));

    /// <summary>Uniform hash grid over candidate points (CSR buckets); callers always check real distances.</summary>
    sealed class HashGrid
    {
        public int Mask;
        public int[] Start = [], Items = [], Hx = [], Hy = [], Hz = [];
        public sbyte[] Side = [];
    }

    static HashGrid BuildGrid(float[] positions, int[] cand, double cell)
    {
        int m = cand.Length;
        int T = (int)Math.Max(2, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, m)));
        var g = new HashGrid { Mask = T - 1, Hx = new int[m], Hy = new int[m], Hz = new int[m], Side = new sbyte[m * 3], Start = new int[T + 1], Items = new int[m] };
        var bucketOf = new int[m];
        double inv = 1 / cell;
        for (int j = 0; j < m; j++)
        {
            int i = cand[j];
            double fx = positions[i * 3] * inv, fy = positions[i * 3 + 1] * inv, fz = positions[i * 3 + 2] * inv;
            double ix = Math.Floor(fx), iy = Math.Floor(fy), iz = Math.Floor(fz);
            g.Side[j * 3] = (sbyte)(fx - ix < 0.5 ? -1 : 1);
            g.Side[j * 3 + 1] = (sbyte)(fy - iy < 0.5 ? -1 : 1);
            g.Side[j * 3 + 2] = (sbyte)(fz - iz < 0.5 ? -1 : 1);
            g.Hx[j] = unchecked(Cell(fx) * HX);
            g.Hy[j] = unchecked(Cell(fy) * HY);
            g.Hz[j] = unchecked(Cell(fz) * HZ);
            int h = (int)(Fmix(unchecked(g.Hx[j] + g.Hy[j] + g.Hz[j])) & (uint)g.Mask);
            bucketOf[j] = h;
            g.Start[h + 1]++;
        }
        for (int h = 0; h < T; h++) g.Start[h + 1] += g.Start[h];
        var fill = g.Start.AsSpan(0, T).ToArray();
        for (int j = 0; j < m; j++) g.Items[fill[bucketOf[j]]++] = j;
        return g;
    }

    static int[] Candidates(int n, byte[]? mask)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
            if (mask is null || mask[i] != 0) m++;
        var cand = new int[m];
        int j = 0;
        for (int i = 0; i < n; i++)
            if (mask is null || mask[i] != 0) cand[j++] = i;
        return cand;
    }

    static int Probe(HashGrid g, int j, int c) => (int)(Fmix(unchecked(
        g.Hx[j] + ((c & 1) != 0 ? g.Side[j * 3] * HX : 0) +
        g.Hy[j] + ((c & 2) != 0 ? g.Side[j * 3 + 1] * HY : 0) +
        g.Hz[j] + ((c & 4) != 0 ? g.Side[j * 3 + 2] * HZ : 0))) & (uint)g.Mask);

    /// <summary>
    /// Groups the points of <paramref name="mask"/> (all when null) whose distance to a group's first point is ≤
    /// <paramref name="distance"/> (greedy in index order, ~linear). Returns the representative array for
    /// <see cref="MergeGroups"/>. Grid cells are 2·distance wide, so only the 8 cells on the point's side of each axis
    /// can hold points within the distance.
    /// </summary>
    public static int[] ClusterNear(float[] positions, byte[]? mask, double distance)
    {
        int n = positions.Length / 3;
        var rep = new int[n];
        for (int i = 0; i < n; i++) rep[i] = i;
        var cand = Candidates(n, mask);
        int m = cand.Length;
        if (m < 2) return rep;
        double d = Math.Max(0, distance), d2 = d * d;
        var g = BuildGrid(positions, cand, d > 0 ? 2 * d * (1 + 1e-6) : 1);
        var assigned = new bool[m];
        for (int j = 0; j < m; j++)
        {
            if (assigned[j]) continue;
            assigned[j] = true;
            int i = cand[j];
            double px = positions[i * 3], py = positions[i * 3 + 1], pz = positions[i * 3 + 2];
            for (int c = 0; c < 8; c++)
            {
                if (d == 0 && c > 0) break; // exact duplicates share a cell
                int h = Probe(g, j, c);
                for (int s = g.Start[h], e = g.Start[h + 1]; s < e; s++)
                {
                    int o = g.Items[s];
                    if (assigned[o]) continue;
                    int q = cand[o];
                    double ex = positions[q * 3] - px, ey = positions[q * 3 + 1] - py, ez = positions[q * 3 + 2] - pz;
                    if (ex * ex + ey * ey + ez * ez <= d2)
                    {
                        assigned[o] = true;
                        rep[q] = i;
                    }
                }
            }
        }
        return rep;
    }

    /// <summary>Merges points of <paramref name="mask"/> (all when null) closer than <paramref name="distance"/> to each other.</summary>
    public static MergeResult MergeNear(Lod lod, byte[]? mask, double distance)
    {
        if (!(distance >= 0) || !double.IsFinite(distance)) throw new ArgumentException("Distance must be a number ≥ 0", nameof(distance));
        return MergeGroups(lod, ClusterNear(lod.Positions, mask, distance));
    }

    /// <summary>
    /// Smallest distance &gt; 0 between two points of <paramref name="mask"/> (all when null) — "Detect" in the Merge Near
    /// dialog. Exact: a grid with search radius r finds every pair closer than r; r grows until a pair is found.
    /// Null for fewer than two distinct positions.
    /// </summary>
    public static double? ClosestPointDistance(float[] positions, byte[]? mask)
    {
        int n = positions.Length / 3;
        var cand = Candidates(n, mask);
        int m = cand.Length;
        if (m < 2) return null;
        var bounds = MeshMath.Bounds(positions, mask);
        if (bounds is not { } bb) return null;
        double diag = (bb.Max - bb.Min).Length;
        if (!(diag > 0)) return null;
        double r = diag / Math.Max(1, Math.Cbrt(m)) / 2;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            var g = BuildGrid(positions, cand, 2 * r * (1 + 1e-6));
            double best = double.PositiveInfinity;
            for (int j = 0; j < m; j++)
            {
                int i = cand[j];
                double px = positions[i * 3], py = positions[i * 3 + 1], pz = positions[i * 3 + 2];
                for (int c = 0; c < 8; c++)
                {
                    int h = Probe(g, j, c);
                    for (int s = g.Start[h], e = g.Start[h + 1]; s < e; s++)
                    {
                        int o = g.Items[s];
                        if (o <= j) continue;
                        int q = cand[o];
                        double ex = positions[q * 3] - px, ey = positions[q * 3 + 1] - py, ez = positions[q * 3 + 2] - pz;
                        double dd = ex * ex + ey * ey + ez * ez;
                        if (dd > 0 && dd < best) best = dd;
                    }
                }
            }
            // every pair closer than r lies within the probed cells, so a result below r is exact
            if (best <= r * r) return Math.Sqrt(best);
            if (double.IsFinite(best)) r = Math.Sqrt(best) * 1.001;
            else if (r > diag) return null;
            else r *= 4;
        }
        return null;
    }

    /// <summary>Projects the points of <paramref name="mask"/> onto the plane with <paramref name="normal"/> through <paramref name="origin"/> (their centroid when null).</summary>
    public static Lod Flatten(Lod lod, byte[] mask, Vec3 normal, Vec3? origin = null)
    {
        if (normal.Normalized() is not { } nn) throw new ArgumentException("Invalid plane normal", nameof(normal));
        if ((origin ?? MeshMath.Centroid(lod.Positions, mask)) is not { } o) return lod;
        var positions = (float[])lod.Positions.Clone();
        for (int i = 0; i < lod.PointCount; i++)
        {
            if (mask[i] == 0) continue;
            double x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            double dd = (x - o.X) * nn.X + (y - o.Y) * nn.Y + (z - o.Z) * nn.Z;
            positions[i * 3] = (float)(x - dd * nn.X);
            positions[i * 3 + 1] = (float)(y - dd * nn.Y);
            positions[i * 3 + 2] = (float)(z - dd * nn.Z);
        }
        return lod with { Positions = positions };
    }

    /// <summary>Rounds the coordinates of the points in <paramref name="mask"/> to multiples of <paramref name="step"/> (relative to <paramref name="origin"/>).</summary>
    public static (Lod Lod, int Moved) SnapToGrid(Lod lod, byte[] mask, double step, Vec3 origin = default)
    {
        if (!(step > 0) || !double.IsFinite(step)) throw new ArgumentException("Grid step must be a positive number", nameof(step));
        var positions = (float[])lod.Positions.Clone();
        int moved = 0;
        for (int i = 0; i < lod.PointCount; i++)
        {
            if (mask[i] == 0) continue;
            bool changed = false;
            for (int c = 0; c < 3; c++)
            {
                float v = positions[i * 3 + c];
                float s = (float)(Math.Floor((v - origin[c]) / step + 0.5) * step + origin[c]);
                if (s != v)
                {
                    positions[i * 3 + c] = s;
                    changed = true;
                }
            }
            if (changed) moved++;
        }
        return (moved > 0 ? lod with { Positions = positions } : lod, moved);
    }

    /// <summary>Points that no face uses (memory points, stray vertices), excluding <paramref name="blocked"/>.</summary>
    public static byte[] Isolated(Lod lod, byte[]? blocked = null)
    {
        var used = Structure.PointsUsedByFaces(lod);
        var o = new byte[lod.PointCount];
        for (int i = 0; i < o.Length; i++) o[i] = used[i] == 0 && !(blocked is not null && blocked[i] != 0) ? (byte)1 : (byte)0;
        return o;
    }

    /// <summary>
    /// Point navigation: the first selectable point, the next one after the highest selected point, or the previous one
    /// before the lowest selected point; -1 when there is none.
    /// </summary>
    public static int Navigate(byte[] selected, byte[]? blocked, PointNavigation dir)
    {
        int n = selected.Length;
        bool Ok(int i) => blocked is null || blocked[i] == 0;
        if (dir == PointNavigation.First)
        {
            for (int i = 0; i < n; i++)
                if (Ok(i)) return i;
            return -1;
        }
        int lo = -1, hi = -1;
        for (int i = 0; i < n; i++)
        {
            if (selected[i] == 0) continue;
            if (lo < 0) lo = i;
            hi = i;
        }
        if (dir == PointNavigation.Next)
        {
            for (int i = hi + 1; i < n; i++)
                if (Ok(i)) return i;
            return -1;
        }
        if (lo < 0) lo = n;
        for (int i = lo - 1; i >= 0; i--)
            if (Ok(i)) return i;
        return -1;
    }
}
