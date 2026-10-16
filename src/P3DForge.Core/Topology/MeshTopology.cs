using P3DForge.Core.Model;

namespace P3DForge.Core.Topology;

/// <summary>One connected set of faces (faces sharing at least one point).</summary>
/// <param name="Faces">Face indices, ascending.</param>
/// <param name="Points">Point indices used by those faces, ascending.</param>
public sealed record Component(int[] Faces, int[] Points);

/// <summary>Result of <see cref="MeshTopology.FindComponents"/>.</summary>
public sealed class ComponentsResult
{
    /// <summary>Component of each face; -1 for faces excluded by the mask.</summary>
    public required int[] FaceComponent { get; init; }
    /// <summary>Component of each point; -1 for points not used by any included face.</summary>
    public required int[] PointComponent { get; init; }
    /// <summary>Components ordered by their lowest face index.</summary>
    public required Component[] Components { get; init; }
    public int Count => Components.Length;
}

/// <summary>Result of <see cref="MeshTopology.CheckClosed(Lod, byte[])"/>. Edge lists are flat (a, b) point-index pairs.</summary>
public sealed class ClosedResult
{
    /// <summary>Edges used by exactly one face, in that face's traversal direction.</summary>
    public required int[] OpenEdges { get; init; }
    /// <summary>The face of each open edge.</summary>
    public required int[] OpenEdgeFaces { get; init; }
    /// <summary>Edges used by more than two faces, (min, max).</summary>
    public required int[] NonManifoldEdges { get; init; }
    /// <summary>Edges whose two faces traverse them in the same direction (inconsistent winding), (min, max).</summary>
    public required int[] InconsistentEdges { get; init; }
    /// <summary>Distinct faces that touch an open or non-manifold edge, ascending.</summary>
    public required int[] ProblemFaces { get; init; }
    /// <summary>Distinct faces that touch an inconsistently wound edge (both faces of every such edge), ascending.</summary>
    public int[] InconsistentFaces { get; init; } = [];

    public int OpenEdgeCount => OpenEdges.Length / 2;
    public int NonManifoldEdgeCount => NonManifoldEdges.Length / 2;
    public int InconsistentEdgeCount => InconsistentEdges.Length / 2;
    /// <summary>No open and no non-manifold edges (an empty face set is closed).</summary>
    public bool Closed => OpenEdges.Length == 0 && NonManifoldEdges.Length == 0;
}

/// <summary>
/// Connectivity and edge checks of a LOD (Object Builder "Topology" semantics). Pure, allocation-light, O(n log n).
/// Point indices that are out of range are ignored.
/// </summary>
public static class MeshTopology
{
    /// <summary>All face indices (when <paramref name="faceMask"/> is null) or those with a non-zero mask byte, ascending.</summary>
    public static int[] FaceList(Lod lod, byte[]? faceMask)
    {
        int fc = lod.FaceCount;
        if (faceMask is null)
        {
            var all = new int[fc];
            for (int f = 0; f < fc; f++) all[f] = f;
            return all;
        }
        int n = 0;
        for (int f = 0; f < fc; f++) if (faceMask[f] != 0) n++;
        var o = new int[n];
        for (int f = 0, j = 0; f < fc; f++) if (faceMask[f] != 0) o[j++] = f;
        return o;
    }

    /// <summary>Distinct point indices used by the given faces, ascending. Cost ∝ the face set for small sets.</summary>
    public static int[] PointsOfFaces(Lod lod, ReadOnlySpan<int> faces)
    {
        int pc = lod.PointCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        if ((long)faces.Length * 16 < pc)
        {
            // Small set (a component of a big LOD): sort + unique instead of a LOD-sized mask.
            var buf = new int[faces.Length * 4];
            int m = 0;
            foreach (int f in faces)
                for (int k = 0, n = nv[f]; k < n; k++)
                {
                    uint p = fp[f * 4 + k];
                    if (p < pc) buf[m++] = (int)p;
                }
            Array.Sort(buf, 0, m);
            int u = 0;
            for (int i = 0; i < m; i++)
                if (u == 0 || buf[u - 1] != buf[i]) buf[u++] = buf[i];
            return buf.AsSpan(0, u).ToArray();
        }
        var used = new bool[pc];
        int count = 0;
        foreach (int f in faces)
        {
            for (int k = 0, n = nv[f]; k < n; k++)
            {
                uint p = fp[f * 4 + k];
                if (p < pc && !used[p]) { used[p] = true; count++; }
            }
        }
        var o = new int[count];
        for (int p = 0, j = 0; j < count; p++) if (used[p]) o[j++] = p;
        return o;
    }

    /// <summary>
    /// Splits the faces into connected components: faces sharing at least one point are connected. Components are
    /// ordered by their lowest point index (Object Builder order). Faces with <c>faceMask[f] == 0</c> are ignored. With
    /// <paramref name="includeIsolatedPoints"/> every point that no face of the LOD uses becomes a single-point component
    /// (Object Builder "Find Components" creates ComponentXX for them too).
    /// </summary>
    public static ComponentsResult FindComponents(Lod lod, byte[]? faceMask = null, bool includeIsolatedPoints = false)
    {
        int pc = lod.PointCount, fc = lod.FaceCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        var sets = new DisjointSets(pc);
        var used = new bool[pc];
        var anyFace = includeIsolatedPoints ? new bool[pc] : null;
        var faceRoot = new int[fc];
        Array.Fill(faceRoot, -1);

        for (int f = 0; f < fc; f++)
        {
            bool masked = faceMask is null || faceMask[f] != 0;
            int first = -1;
            for (int k = 0, n = nv[f]; k < n; k++)
            {
                uint p = fp[f * 4 + k];
                if (p >= pc) continue;
                if (anyFace is not null) anyFace[p] = true;
                if (!masked) continue;
                used[p] = true;
                if (first < 0) first = (int)p;
                else sets.Union(first, (int)p);
            }
            if (masked) faceRoot[f] = first; // resolved to a root below
        }

        // Number the components by their lowest point.
        var rootComponent = new int[pc];
        Array.Fill(rootComponent, -1);
        var pointComponent = new int[pc];
        Array.Fill(pointComponent, -1);
        int count = 0;
        for (int p = 0; p < pc; p++)
        {
            if (used[p])
            {
                int root = sets.Find(p);
                int c = rootComponent[root];
                if (c < 0) c = rootComponent[root] = count++;
                pointComponent[p] = c;
            }
            else if (anyFace is not null && !anyFace[p]) pointComponent[p] = count++;
        }

        var faceComponent = new int[fc];
        Array.Fill(faceComponent, -1);
        for (int f = 0; f < fc; f++)
            if (faceRoot[f] >= 0) faceComponent[f] = rootComponent[sets.Find(faceRoot[f])];

        var faces = Bucket(faceComponent, count);
        var points = Bucket(pointComponent, count);
        var comps = new Component[count];
        for (int c = 0; c < count; c++) comps[c] = new Component(faces[c], points[c]);
        return new ComponentsResult { FaceComponent = faceComponent, PointComponent = pointComponent, Components = comps };
    }

    /// <summary>Counting sort of indices by label (labels &lt; 0 dropped); ascending within each bucket.</summary>
    internal static int[][] Bucket(int[] labels, int count)
    {
        var sizes = new int[count];
        foreach (int c in labels) if (c >= 0) sizes[c]++;
        var o = new int[count][];
        for (int c = 0; c < count; c++) o[c] = new int[sizes[c]];
        Array.Clear(sizes);
        for (int i = 0; i < labels.Length; i++)
        {
            int c = labels[i];
            if (c >= 0) o[c][sizes[c]++] = i;
        }
        return o;
    }

    /// <summary>
    /// Classifies the edges of a face set (all faces when <paramref name="faces"/> is null). Edges of collapsed faces
    /// whose endpoints coincide are ignored. Open = used by one face; non-manifold = used by more than two faces;
    /// inconsistent = two faces traverse the edge in the same direction.
    /// </summary>
    public static ClosedResult CheckClosed(Lod lod, ReadOnlySpan<int> faces)
    {
        int lodPoints = lod.PointCount;
        var fp = lod.FacePoints;
        var nv = lod.FaceVertCount;
        // Small face sets (one component of a big LOD) use a local, order-preserving point numbering so the cost is
        // proportional to the set, not to the LOD.
        int[]? local = (long)faces.Length * 16 < lodPoints ? PointsOfFaces(lod, faces) : null;
        int pc = local?.Length ?? lodPoints;
        if (local is not null)
        {
            var lfp = new uint[faces.Length * 4];
            var lnv = new byte[faces.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                int f = faces[i], n = nv[f];
                lnv[i] = (byte)n;
                for (int k = 0; k < n; k++)
                {
                    uint p = fp[f * 4 + k];
                    lfp[i * 4 + k] = p < lodPoints ? (uint)Array.BinarySearch(local, (int)p) : uint.MaxValue;
                }
            }
            var faceArr = faces.ToArray();
            var r = CheckClosedCore(lfp, lnv, pc, Enumerable.Range(0, faces.Length).ToArray());
            // Back to LOD indices.
            int[] P(int[] a) { for (int i = 0; i < a.Length; i++) a[i] = local[a[i]]; return a; }
            int[] F(int[] a) { for (int i = 0; i < a.Length; i++) a[i] = faceArr[a[i]]; Array.Sort(a); return a; }
            return new ClosedResult
            {
                OpenEdges = P(r.OpenEdges),
                OpenEdgeFaces = F(r.OpenEdgeFaces),
                NonManifoldEdges = P(r.NonManifoldEdges),
                InconsistentEdges = P(r.InconsistentEdges),
                ProblemFaces = F(r.ProblemFaces),
                InconsistentFaces = F(r.InconsistentFaces),
            };
        }
        return CheckClosedCore(fp, nv, pc, faces);
    }

    private static ClosedResult CheckClosedCore(uint[] fp, byte[] nv, int pc, ReadOnlySpan<int> faces)
    {

        // CSR buckets of edge uses keyed by the lower point index; key = 2 * higher + (1 if walked low → high).
        var start = new int[pc + 1];
        foreach (int f in faces)
        {
            int n = nv[f], b = f * 4;
            for (int k = 0; k < n; k++)
            {
                uint a = fp[b + k], c = fp[b + (k + 1 == n ? 0 : k + 1)];
                if (a == c || a >= pc || c >= pc) continue;
                start[(a < c ? a : c) + 1]++;
            }
        }
        for (int p = 0; p < pc; p++) start[p + 1] += start[p];
        var keys = new long[start[pc]];
        var owner = new int[start[pc]];
        var fill = start.AsSpan(0, pc).ToArray();
        foreach (int f in faces)
        {
            int n = nv[f], b = f * 4;
            for (int k = 0; k < n; k++)
            {
                uint a = fp[b + k], c = fp[b + (k + 1 == n ? 0 : k + 1)];
                if (a == c || a >= pc || c >= pc) continue;
                int i;
                if (a < c) { i = fill[a]++; keys[i] = (long)c * 2 + 1; }
                else { i = fill[c]++; keys[i] = (long)a * 2; }
                owner[i] = f;
            }
        }

        var open = new List<int>();
        var openFaces = new List<int>();
        var nonManifold = new List<int>();
        var inconsistent = new List<int>();
        var problem = new HashSet<int>();
        var flipped = new HashSet<int>();
        for (int lo = 0; lo < pc; lo++)
        {
            int s = start[lo], e = start[lo + 1];
            if (e - s > 1) Array.Sort(keys, owner, s, e - s);
            for (int i = s; i < e;)
            {
                long hi = keys[i] >> 1;
                int j = i + 1;
                while (j < e && (keys[j] >> 1) == hi) j++;
                int uses = j - i;
                if (uses == 1)
                {
                    if ((keys[i] & 1) == 1) { open.Add(lo); open.Add((int)hi); }
                    else { open.Add((int)hi); open.Add(lo); }
                    openFaces.Add(owner[i]);
                    problem.Add(owner[i]);
                }
                else if (uses > 2)
                {
                    nonManifold.Add(lo); nonManifold.Add((int)hi);
                    for (int t = i; t < j; t++) problem.Add(owner[t]);
                }
                else if (keys[i] == keys[i + 1])
                {
                    inconsistent.Add(lo); inconsistent.Add((int)hi);
                    flipped.Add(owner[i]); flipped.Add(owner[i + 1]);
                }
                i = j;
            }
        }
        var pf = problem.ToArray();
        Array.Sort(pf);
        var inf = flipped.ToArray();
        Array.Sort(inf);
        return new ClosedResult
        {
            OpenEdges = open.ToArray(),
            OpenEdgeFaces = openFaces.ToArray(),
            NonManifoldEdges = nonManifold.ToArray(),
            InconsistentEdges = inconsistent.ToArray(),
            ProblemFaces = pf,
            InconsistentFaces = inf,
        };
    }

    /// <summary><see cref="CheckClosed(Lod, ReadOnlySpan{int})"/> over every face (or the masked ones).</summary>
    public static ClosedResult CheckClosed(Lod lod, byte[]? faceMask = null) => CheckClosed(lod, FaceList(lod, faceMask));
}
