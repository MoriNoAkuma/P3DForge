using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class SelectToolsTests
{
    static SelectionMask M(int np, int nf, int[] points, int[]? faces = null) => new(MaskOf(np, points), MaskOf(nf, faces ?? []));

    [Fact]
    public void Grow_points_by_one_ring_and_faces_by_shared_points()
    {
        var g = GridLod(4, 4); // 25 points, 16 quads; centre point 12
        var s1 = SelectTools.Grow(g, M(25, 16, [12]), facesMode: false);
        Assert.Equal(9, Count(s1.Points));
        Assert.Equal(4, Count(s1.Faces));
        var s2 = SelectTools.Grow(g, s1, false);
        Assert.Equal(25, Count(s2.Points));
        Assert.Equal(16, Count(s2.Faces));
        var f = SelectTools.Grow(g, M(25, 16, [], [5]), facesMode: true);
        Assert.Equal([0, 1, 2, 4, 5, 6, 8, 9, 10], Indices(f.Faces));
        Assert.Equal(16, Count(f.Points));
    }

    [Fact]
    public void Shrink_undoes_grow_on_interior_regions()
    {
        var g = GridLod(4, 4);
        var s1 = SelectTools.Grow(g, M(25, 16, [12]), false);
        var back = SelectTools.Shrink(g, s1, false);
        Assert.Equal([12], Indices(back.Points));
        Assert.Equal(0, Count(back.Faces));
        var g5 = GridLod(5, 5);
        var f = SelectTools.Grow(g5, M(36, 25, [], [12]), true);
        Assert.Equal([6, 7, 8, 11, 12, 13, 16, 17, 18], Indices(f.Faces));
        var fb = SelectTools.Shrink(g5, f, true);
        Assert.Equal([12], Indices(fb.Faces));
        Assert.Equal(4, Count(fb.Points));
        var corner = SelectTools.Grow(g, M(25, 16, [], [5]), true);
        Assert.Equal([0, 1, 4, 5], Indices(SelectTools.Shrink(g, corner, true).Faces));
    }

    [Fact]
    public void Grow_never_selects_blocked_points()
    {
        var g = GridLod(4, 4);
        var s = SelectTools.Grow(g, M(25, 16, [12]), false, MaskOf(25, 6));
        Assert.Equal(0, s.Points[6]);
        Assert.Equal(3, Count(s.Faces));
    }

    [Fact]
    public void Connected_selects_whole_parts_and_stops_at_blocked_points()
    {
        var lod = Primitives.CreateBox(Lod.Empty(0), new BoxParams());
        lod = Primitives.CreateBox(lod, new BoxParams(1, 1, 1, 2, 1, 1) { At = new Vec3(5, 0, 0) });
        var s = SelectTools.Connected(lod, M(lod.PointCount, lod.FaceCount, [9]));
        Assert.Equal(Enumerable.Range(8, lod.PointCount - 8).ToArray(), Indices(s.Points));
        Assert.Equal(lod.FaceCount - 6, Count(s.Faces));
        var viaFace = SelectTools.Connected(lod, M(lod.PointCount, lod.FaceCount, [], [0]));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], Indices(viaFace.Points));
        var blocked = new byte[lod.PointCount];
        for (int i = 8; i < blocked.Length; i++) blocked[i] = 1;
        blocked[9] = 0;
        Assert.Equal([9], Indices(SelectTools.Connected(lod, M(lod.PointCount, lod.FaceCount, [9]), blocked).Points));
    }

    [Fact]
    public void Faces_from_points_points_from_faces_and_deselect()
    {
        var g = GridLod(2, 1);
        var sel = M(6, 2, [0, 1, 3, 4, 5], [1]);
        Assert.Equal([0], Indices(SelectTools.FacesFromPoints(g, sel).Faces));
        Assert.Equal([0, 1, 3, 4, 5], Indices(SelectTools.FacesFromPoints(g, sel).Points));
        Assert.Equal([1, 2, 4, 5], Indices(SelectTools.PointsFromFaces(g, sel).Points));
        Assert.Equal([1], Indices(SelectTools.PointsFromFaces(g, sel).Faces));
        var s2 = M(4, 2, [0, 1], [1]);
        Assert.Equal([0, 1], Indices(SelectTools.DeselectFaces(s2).Points));
        Assert.Equal(0, Count(SelectTools.DeselectFaces(s2).Faces));
        Assert.Equal(0, Count(SelectTools.DeselectPoints(s2).Points));
        Assert.Equal([1], Indices(SelectTools.DeselectPoints(s2).Faces));
    }

    [Fact]
    public void Unselect_triangles_keeps_points_of_remaining_quads()
    {
        var t = FaceOps.Triangulate(GridLod(2, 1), MaskOf(2, 1)).Lod; // quad0, tri(1,2,5), tri(1,5,4)
        var r = SelectTools.UnselectTriangles(t, M(6, 3, [0, 1, 2, 3, 4, 5], [0, 1, 2]));
        Assert.Equal([0], Indices(r.Faces));
        Assert.Equal([0, 1, 3, 4], Indices(r.Points));
    }

    [Fact]
    public void Stored_selection_operators()
    {
        var g = GridLod(2, 1);
        var cur = M(6, 2, [0, 1, 2], [0]);
        var stored = M(6, 2, [2, 3], [1]);
        Assert.Equal([0, 1, 2, 3], Indices(SelectTools.CombineWithStored(g, cur, stored, StoredSelectionOp.Union).Points));
        Assert.Equal([0, 1], Indices(SelectTools.CombineWithStored(g, cur, stored, StoredSelectionOp.Complement).Points));
        Assert.Equal([2], Indices(SelectTools.CombineWithStored(g, cur, stored, StoredSelectionOp.Intersect).Points));
        Assert.Equal([1], Indices(SelectTools.CombineWithStored(g, cur, stored, StoredSelectionOp.Recall).Faces));
        var r = SelectTools.CombineWithStored(g, cur, new SelectionMask(MaskOf(3, 2), []), StoredSelectionOp.Union);
        Assert.Equal(6, r.Points.Length);
        Assert.Equal(2, r.Faces.Length);
        var b = SelectTools.CombineWithStored(g, cur, stored, StoredSelectionOp.Union, MaskOf(6, 1));
        Assert.Equal([0, 2, 3], Indices(b.Points));
        Assert.Equal(0, Count(b.Faces));
    }

    [Fact]
    public void Hide_and_unhide_keep_flags_tags_and_selection_in_sync()
    {
        var g = Selection.WithSelected(GridLod(2, 1), M(6, 2, [0, 1, 3, 4], [0]));
        var h = SelectTools.Hide(g, MaskOf(6, 0, 1));
        CheckLod(h.Lod);
        Assert.Equal(2, h.Count);
        Assert.Equal([0, 1], Indices(Selection.HiddenPoints(h.Lod)));
        Assert.NotEqual(0u, h.Lod.PointFlags[0] & PointFlags.Hidden);
        Assert.Equal([3, 4], Indices(Selection.GetSelected(h.Lod).Points));
        Assert.Equal(0, Count(Selection.GetSelected(h.Lod).Faces));
        Assert.Equal(0, SelectTools.Hide(h.Lod, MaskOf(6, 0)).Count);
        var u = SelectTools.UnhideAll(h.Lod);
        Assert.Equal(2, u.Count);
        Assert.Equal(0, Count(Selection.HiddenPoints(u.Lod)));
        Assert.Equal(0, SelectTools.UnhideAll(u.Lod).Count);
        var tagged = Selection.WithStateTag(g, StateKind.Hidden, SelectionMask.Empty(g));
        var th = SelectTools.Hide(tagged, MaskOf(6, 2));
        Assert.Equal([0, 0, 1, 0, 0, 0], th.Lod.State(StateKind.Hidden)!.Points);
        Assert.Equal([0, 1], th.Lod.State(StateKind.Hidden)!.Faces);
        CheckLod(th.Lod);
    }

    [Fact]
    public void Lock_and_unlock()
    {
        var g = Selection.WithSelected(GridLod(1, 1), M(4, 1, [0, 1, 2, 3], [0]));
        var l = SelectTools.Lock(g, MaskOf(4, 2));
        Assert.Equal([2], Indices(Selection.LockedPoints(l.Lod)));
        Assert.Equal([2], Indices(SelectTools.BlockedPoints(l.Lod)));
        Assert.Equal([0, 1, 3], Indices(Selection.GetSelected(l.Lod).Points));
        Assert.Equal(0, Count(Selection.GetSelected(l.Lod).Faces));
        var u = SelectTools.UnlockAll(l.Lod);
        Assert.Equal(1, u.Count);
        Assert.Equal(0, Count(Selection.LockedPoints(u.Lod)));
        CheckLod(u.Lod);
    }
}
