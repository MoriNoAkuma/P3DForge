using System.Collections.Immutable;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Topology;

/// <summary>Outcome of a component / hull operation (for the log).</summary>
public sealed record HullOpStats(int Components, int Hulls, int Failed, int FacesBefore, int FacesAfter, int PointsRemoved, string? FirstFailure);

/// <summary>
/// Structure-menu operations built on the topology library (pure: never write into arrays of an input Lod):
/// ComponentXX selections, convex hull of the selection, convex hull of every component.
/// </summary>
public static class HullOps
{
    /// <summary>
    /// Object Builder "Find Components": deletes every existing ComponentXX selection and creates Component01..NN, one per
    /// connected piece of the (masked) faces, ordered by lowest point index like Object Builder, holding the piece's
    /// points and faces. Proxy triangles never form a component. Points no face uses become single-point components
    /// (Object Builder does that too) unless <paramref name="isolatedPoints"/> is false. Pieces that are not closed
    /// (open or non-manifold edges) are counted in <paramref name="open"/> and skipped when <paramref name="closedOnly"/>.
    /// Returns the input LOD when the ComponentXX selections would not change.
    /// </summary>
    public static Lod CreateComponentSelections(Lod lod, byte[]? faceMask, out int count, out int open, bool closedOnly = true, bool isolatedPoints = true)
    {
        var original = lod;
        lod = DeleteComponentSelections(lod);
        var comps = MeshTopology.FindComponents(lod, ComponentAnalysis.ExcludeProxies(lod, faceMask), isolatedPoints);
        count = 0;
        open = 0;
        // Build every tag first, then insert them in one go (keeps this O(n) for hundreds of components).
        var tags = new List<Tag>(comps.Count);
        foreach (var comp in comps.Components)
        {
            if (comp.Faces.Length > 0 && !MeshTopology.CheckClosed(lod, comp.Faces).Closed)
            {
                open++;
                if (closedOnly) continue;
            }
            var pts = new byte[lod.PointCount];
            var fcs = new byte[lod.FaceCount];
            foreach (int p in comp.Points) pts[p] = 1;
            foreach (int f in comp.Faces) fcs[f] = 1;
            tags.Add(new SelectionTag(1, ComponentAnalysis.ComponentName(count++), pts, fcs));
        }
        if (SameComponents(original, tags)) return original; // nothing changes: no undo step
        return tags.Count == 0 ? lod : lod with { Tags = lod.Tags.InsertRange(NamedSelections.InsertionIndex(lod), tags) };
    }

    /// <summary><see cref="CreateComponentSelections(Lod, byte[], out int, out int, bool, bool)"/> with Object Builder defaults.</summary>
    public static Lod CreateComponentSelections(Lod lod, byte[]? faceMask, out int count) =>
        CreateComponentSelections(lod, faceMask, out count, out _);

    /// <summary>True when the ComponentXX selections of <paramref name="lod"/> are exactly <paramref name="tags"/> (same order).</summary>
    private static bool SameComponents(Lod lod, List<Tag> tags)
    {
        int i = 0;
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s || !ComponentAnalysis.IsComponentName(s.Name)) continue;
            if (i >= tags.Count || tags[i] is not SelectionTag n || n.Name != s.Name || n.Active != s.Active
                || !n.Points.AsSpan().SequenceEqual(s.Points) || !n.Faces.AsSpan().SequenceEqual(s.Faces)) return false;
            i++;
        }
        return i == tags.Count;
    }

    /// <summary>Removes every named selection called ComponentNN.</summary>
    public static Lod DeleteComponentSelections(Lod lod)
    {
        var b = ImmutableArray.CreateBuilder<Tag>(lod.Tags.Length);
        foreach (var t in lod.Tags)
            if (!(t is SelectionTag s && ComponentAnalysis.IsComponentName(s.Name))) b.Add(t);
        return b.Count == lod.Tags.Length ? lod : lod with { Tags = b.ToImmutable() };
    }

    /// <summary>
    /// Replaces the selection by its convex hull: the faces of the selection are deleted, hull faces built on the hull
    /// vertices (existing points, so their named-selection membership and mass stay) are added, and selected points that
    /// end up inside the hull and unused by any other face are deleted. New faces join the named selections that contain
    /// all of their points, take the texture/material of the first replaced face and become the current selection.
    /// Returns the unchanged LOD (and a failed <paramref name="hull"/>) when the hull cannot be built.
    /// </summary>
    public static Lod ReplaceSelectionWithHull(Lod lod, SelectionMask sel, out HullResult hull, out HullOpStats stats) =>
        ReplaceSelectionWithHull(lod, sel, null, out hull, out stats);

    /// <summary>
    /// <see cref="ReplaceSelectionWithHull(Lod, SelectionMask, out HullResult, out HullOpStats)"/> with the hull built on
    /// <paramref name="hullPoints"/> only (a thinned subset of the selection: a simpler hull); the other selected points
    /// are deleted like interior ones.
    /// </summary>
    public static Lod ReplaceSelectionWithHull(Lod lod, SelectionMask sel, int[]? hullPoints, out HullResult hull, out HullOpStats stats)
    {
        var pointSet = (byte[])sel.Points.Clone();
        Selection.PointsFromFaces(lod, sel.Faces, pointSet);
        var points = hullPoints ?? NamedSelections.Indices(pointSet);
        hull = ConvexHull.Build(lod.Positions, points);
        if (!hull.Ok)
        {
            stats = new HullOpStats(1, 0, 1, lod.FaceCount, lod.FaceCount, 0, hull.Describe());
            return lod;
        }
        var groups = new List<(int[] Faces, HullResult Hull)> { (MeshTopology.FaceList(lod, sel.Faces), hull) };
        var result = ReplaceGroups(lod, groups, pointSet, out int removedPoints);
        stats = new HullOpStats(1, 1, 0, lod.FaceCount, result.FaceCount, removedPoints, null);
        return result;
    }

    /// <summary>
    /// Object Builder "Component Convex Hull": every connected component of the (masked) faces is replaced by its convex
    /// hull (interior points unused elsewhere are deleted) and the ComponentXX selections are recreated. Components whose
    /// hull cannot be built (flat, colinear) are kept unchanged and counted in <see cref="HullOpStats.Failed"/>.
    /// </summary>
    public static Lod ComponentHulls(Lod lod, byte[]? faceMask, out HullOpStats stats)
    {
        var comps = MeshTopology.FindComponents(lod, ComponentAnalysis.ExcludeProxies(lod, faceMask));
        var groups = new List<(int[] Faces, HullResult Hull)>(comps.Count);
        var pointSet = new byte[lod.PointCount];
        int failed = 0;
        string? firstFailure = null;
        foreach (var c in comps.Components)
        {
            var h = ConvexHull.Build(lod.Positions, c.Points);
            if (!h.Ok)
            {
                failed++;
                firstFailure ??= h.Describe();
                continue;
            }
            groups.Add((c.Faces, h));
            foreach (int p in c.Points) pointSet[p] = 1;
        }
        var result = groups.Count == 0 ? lod : ReplaceGroups(lod, groups, pointSet, out _);
        int removed = lod.PointCount - result.PointCount;
        var mask = faceMask is null ? null : RemapMaskAfterHull(lod, result, faceMask, groups);
        result = CreateComponentSelections(result, mask, out int count, out _);
        stats = new HullOpStats(count, groups.Count, failed, lod.FaceCount, result.FaceCount, removed, firstFailure);
        return result;
    }

    /// <summary>
    /// The face mask after <see cref="ReplaceGroups"/>: faces that were not replaced keep their relative order and mask
    /// value; the appended hull faces are masked.
    /// </summary>
    private static byte[] RemapMaskAfterHull(Lod before, Lod after, byte[] faceMask, List<(int[] Faces, HullResult Hull)> groups)
    {
        var dropped = new bool[before.FaceCount];
        foreach (var (faces, _) in groups)
            foreach (int f in faces) dropped[f] = true;
        var m = new byte[after.FaceCount];
        int d = 0;
        for (int f = 0; f < before.FaceCount && d < after.FaceCount; f++)
            if (!dropped[f]) m[d++] = faceMask[f] != 0 ? (byte)1 : (byte)0;
        for (; d < after.FaceCount; d++) m[d] = 1;
        return m;
    }

    /// <summary>
    /// Deletes the faces of every group, appends each group's hull faces, then deletes points of
    /// <paramref name="candidates"/> that no face uses any more.
    /// </summary>
    private static Lod ReplaceGroups(Lod lod, List<(int[] Faces, HullResult Hull)> groups, byte[] candidates, out int removedPoints)
    {
        int fc = lod.FaceCount;
        var drop = new bool[fc];
        int dropped = 0, added = 0;
        foreach (var (faces, hull) in groups)
        {
            foreach (int f in faces) if (!drop[f]) { drop[f] = true; dropped++; }
            added += hull.FaceCount;
        }
        var b = new FaceBuild(fc - dropped + added);
        var tex = new uint[b.Count];
        var mat = new uint[b.Count];
        var flg = new uint[b.Count];
        int d = 0;
        for (int f = 0; f < fc; f++)
            if (!drop[f]) b.CopyFace(d++, lod, f);
        int firstNew = d;
        var newOwner = new int[added]; // group index of every new face
        int gi = 0;
        foreach (var (faces, hull) in groups)
        {
            int model = faces.Length > 0 ? faces[0] : -1;
            for (int h = 0; h < hull.FaceCount; h++)
            {
                int n = hull.FaceVertCount[h];
                b.VertCount[d] = (byte)n;
                for (int k = 0; k < 4; k++) b.Points[d * 4 + k] = (uint)hull.FacePoints[h * 4 + (k < n ? k : 0)];
                if (model >= 0)
                {
                    tex[d] = lod.FaceTexture[model];
                    mat[d] = lod.FaceMaterial[model];
                    flg[d] = lod.FaceFlags[model];
                }
                newOwner[d - firstNew] = gi;
                d++;
            }
            gi++;
        }
        b.Texture = tex;
        b.Material = mat;
        b.Flags = flg;
        var rebuilt = MeshMath.RebuildFaces(lod, b);

        // Named-selection face membership of new faces: every selection that holds all their points.
        rebuilt = AssignNewFaceMembership(rebuilt, firstNew);

        // New selection = the hulls (their faces and points).
        var selPts = new byte[rebuilt.PointCount];
        var selFaces = new byte[rebuilt.FaceCount];
        for (int f = firstNew; f < rebuilt.FaceCount; f++)
        {
            selFaces[f] = 1;
            for (int k = 0; k < rebuilt.FaceVertCount[f]; k++) selPts[rebuilt.FacePoints[f * 4 + k]] = 1;
        }
        rebuilt = Selection.WithSelected(rebuilt, new SelectionMask(selPts, selFaces));

        // Delete candidate points no face uses any more.
        var used = Structure.PointsUsedByFaces(rebuilt);
        var keepPoints = new byte[rebuilt.PointCount];
        removedPoints = 0;
        for (int p = 0; p < keepPoints.Length; p++)
        {
            bool remove = candidates[p] != 0 && used[p] == 0;
            keepPoints[p] = remove ? (byte)0 : (byte)1;
            if (remove) removedPoints++;
        }
        if (removedPoints == 0) return rebuilt;
        var keepFaces = new byte[rebuilt.FaceCount];
        Array.Fill(keepFaces, (byte)1);
        return Structure.Compact(rebuilt, keepPoints, keepFaces);
    }

    private static Lod AssignNewFaceMembership(Lod lod, int firstNew)
    {
        int fc = lod.FaceCount;
        if (firstNew >= fc) return lod;
        var tags = lod.Tags;
        var b = tags.ToBuilder();
        bool changed = false;
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i] is not SelectionTag s) continue;
            byte[]? faces = null;
            for (int f = firstNew; f < fc; f++)
            {
                int n = lod.FaceVertCount[f];
                byte w = 255;
                for (int k = 0; k < n && w != 0; k++)
                {
                    byte pw = s.Points[lod.FacePoints[f * 4 + k]];
                    if (pw == 0) w = 0;
                }
                if (w == 0) continue;
                faces ??= (byte[])s.Faces.Clone();
                faces[f] = 1;
            }
            if (faces is null) continue;
            b[i] = s with { Faces = faces };
            changed = true;
        }
        return changed ? lod with { Tags = b.ToImmutable() } : lod;
    }
}
