using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class FaceOpsTests
{
    static byte[] Bytes(Lod lod) => Mlod.Write(new P3DModel([lod]));

    static float[] Reorder(float[] uv, int[] order) => order.SelectMany(k => new[] { uv[k * 2], uv[k * 2 + 1] }).ToArray();

    [Fact]
    public void Reverse_remaps_every_per_slot_array_and_twice_is_identity()
    {
        var cube = Selection.WithSelected(RichCube(), new SelectionMask(All(8), MaskOf(6, 0, 4)));
        var r = FaceOps.Reverse(cube, MaskOf(6, 0, 4));
        CheckLod(r);
        Assert.Equal([0u, 3u, 2u, 1u], FacePts(r, 0));
        Assert.Equal(FacePts(cube, 1), FacePts(r, 1));
        int[] rev = [0, 3, 2, 1];
        Assert.Equal(Reorder(FaceUVs(cube, 0), rev), FaceUVs(r, 0));
        Assert.Equal(Reorder(FaceUVs(cube, 0), rev), FaceUVs(r, 0, r.UVSet(0)!.UV));
        Assert.Equal(Reorder(FaceUVs(cube, 0, cube.UVSet(1)!.UV), rev), FaceUVs(r, 0, r.UVSet(1)!.UV));
        Assert.Equal(rev.Select(k => cube.FaceNormals[k]).ToArray(), r.FaceNormals.AsSpan(0, 4).ToArray());
        Assert.Equal(Bytes(cube), Bytes(FaceOps.Reverse(r, MaskOf(6, 0, 4))));
        Assert.Equal(1, SignedVolume(FaceOps.Reverse(cube, All(6))), 5);
    }

    [Fact]
    public void Reverse_keeps_junk_bytes_of_triangle_slot_3()
    {
        var lod = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(1, 6, 3));
        var fp = (uint[])lod.FacePoints.Clone();
        fp[3] = 2;
        lod = lod with { FacePoints = fp };
        var r = FaceOps.Reverse(lod, MaskOf(lod.FaceCount, 0));
        Assert.Equal(2u, r.FacePoints[3]);
        Assert.Equal([fp[0], fp[2], fp[1]], FacePts(r, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Triangulate_splits_quads_copying_per_face_data(bool diag13)
    {
        var cube = Selection.WithSelected(RichCube(), new SelectionMask(All(8), MaskOf(6, 4)));
        var (lod, split) = FaceOps.Triangulate(cube, All(6), diag13);
        CheckLod(lod);
        Assert.Equal(6, split);
        Assert.Equal(12, lod.FaceCount);
        Assert.True(IsClosedConsistent(lod));
        Assert.Equal(-1, SignedVolume(lod), 5);
        var q = FacePts(cube, 4);
        int[] a = diag13 ? [0, 1, 3] : [0, 1, 2], b = diag13 ? [1, 2, 3] : [0, 2, 3];
        Assert.Equal(a.Select(k => q[k]).ToArray(), FacePts(lod, 8));
        Assert.Equal(b.Select(k => q[k]).ToArray(), FacePts(lod, 9));
        Assert.Equal(Reorder(FaceUVs(cube, 4, cube.UVSet(1)!.UV), b), FaceUVs(lod, 9, lod.UVSet(1)!.UV));
        Assert.Equal(cube.Texture(4), lod.Texture(8));
        Assert.Equal(cube.Texture(4), lod.Texture(9));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0], lod.Selection("top")!.Faces);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0], Selection.GetSelected(lod).Faces);
    }

    [Fact]
    public void Triangulate_only_touches_selected_quads()
    {
        var cube = RichCube();
        var (lod, split) = FaceOps.Triangulate(cube, MaskOf(6, 2));
        Assert.Equal(1, split);
        Assert.Equal(7, lod.FaceCount);
        Assert.Equal(FacePts(cube, 0), FacePts(lod, 0));
        Assert.Equal(FacePts(cube, 3), FacePts(lod, 4));
        var again = FaceOps.Triangulate(lod, MaskOf(7, 2, 3));
        Assert.Equal(0, again.Split);
        Assert.Same(lod, again.Lod);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Squarize_restores_the_quads_of_a_triangulated_box(bool diag13)
    {
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams(1, 2, 3, 2, 2, 2));
        var tri = FaceOps.Triangulate(box, All(box.FaceCount), diag13).Lod;
        var (lod, merged) = FaceOps.Squarize(tri, null);
        CheckLod(lod);
        Assert.Equal(box.FaceCount, merged);
        Assert.Equal((box.FaceCount, 0), FaceOps.CountKinds(lod, All(lod.FaceCount)));
        Assert.True(IsClosedConsistent(lod));
        Assert.Equal(-6, SignedVolume(lod), 4);
    }

    [Fact]
    public void Squarize_keeps_corner_UVs_and_merges_selection_bytes()
    {
        var cube = RichCube();
        var tri = FaceOps.Triangulate(cube, All(6)).Lod;
        var (lod, merged) = FaceOps.Squarize(tri, null);
        Assert.Equal(6, merged);
        CheckLod(lod);
        for (int f = 0; f < 6; f++)
        {
            var pts = FacePts(lod, f);
            int o = Enumerable.Range(0, 6).First(g => FacePts(cube, g).Order().SequenceEqual(pts.Order()));
            var op = FacePts(cube, o);
            var (uv, uv1) = (FaceUVs(lod, f), FaceUVs(lod, f, lod.UVSet(1)!.UV));
            var (ouv, ouv1) = (FaceUVs(cube, o), FaceUVs(cube, o, cube.UVSet(1)!.UV));
            for (int k = 0; k < pts.Length; k++)
            {
                int j = Array.IndexOf(op, pts[k]);
                Assert.Equal((ouv[j * 2], ouv[j * 2 + 1]), (uv[k * 2], uv[k * 2 + 1]));
                Assert.Equal((ouv1[j * 2], ouv1[j * 2 + 1]), (uv1[k * 2], uv1[k * 2 + 1]));
            }
            Assert.Equal(cube.Texture(o), lod.Texture(f));
        }
        Assert.Equal(cube.Selection("top")!.Faces, lod.Selection("top")!.Faces);
    }

    [Fact]
    public void Squarize_respects_textures_seams_folds_and_the_mask()
    {
        var g = GridLod(2, 1);
        var tri = FaceOps.Triangulate(g, All(2)).Lod;
        Assert.Equal(1, FaceOps.Squarize(Structure.SetFaceTextures(tri, MaskOf(4, 0), "other_co.paa", null), null).Merged);
        var uv = (float[])tri.FaceUV.Clone();
        uv[(3 * 4 + 0) * 2] += 0.5f;
        Assert.Equal(1, FaceOps.Squarize(tri with { FaceUV = uv }, null).Merged);
        var pos = (float[])tri.Positions.Clone();
        pos[1] = 0.5f;
        Assert.Equal(1, FaceOps.Squarize(tri with { Positions = pos }, null).Merged);
        var box = FaceOps.Triangulate(Primitives.CreateBox(Lod.Empty(0), new BoxParams()), All(6)).Lod;
        Assert.Equal(6, FaceOps.Squarize(box, null).Merged);
        Assert.Equal(1, FaceOps.Squarize(tri, MaskOf(4, 0, 1)).Merged);
        Assert.Equal(0, FaceOps.Squarize(tri, MaskOf(4, 1, 2)).Merged);
    }

    [Fact]
    public void Uncross_fixes_bow_tie_quads()
    {
        var g = GridLod(2, 1);
        var fp = (uint[])g.FacePoints.Clone();
        fp[2] = 3;
        fp[3] = 4;
        var crossed = g with { FacePoints = fp };
        var (lod, changed) = FaceOps.Uncross(crossed, All(2));
        CheckLod(lod);
        Assert.Equal(1, changed);
        Assert.Equal([0u, 1u, 4u, 3u], FacePts(lod, 0));
        Assert.Equal(FacePts(g, 1), FacePts(lod, 1));
        Assert.Equal(Reorder(FaceUVs(crossed, 0), [0, 1, 3, 2]), FaceUVs(lod, 0));
        Assert.Equal(0, FaceOps.Uncross(g, All(2)).Changed);
    }

    [Fact]
    public void Move_faces_to_top_and_bottom()
    {
        var cube = Selection.WithSelected(RichCube(), new SelectionMask(All(8), MaskOf(6, 4)));
        var top = FaceOps.Move(cube, MaskOf(6, 3, 4), FaceOrder.Top);
        CheckLod(top);
        Assert.Equal(FacePts(cube, 3), FacePts(top, 0));
        Assert.Equal(FacePts(cube, 4), FacePts(top, 1));
        Assert.Equal(FacePts(cube, 0), FacePts(top, 2));
        Assert.Equal([0, 1, 0, 0, 0, 0], top.Selection("top")!.Faces);
        Assert.Equal([0, 1, 0, 0, 0, 0], Selection.GetSelected(top).Faces);
        Assert.Equal(FaceUVs(cube, 4, cube.UVSet(1)!.UV), FaceUVs(top, 1, top.UVSet(1)!.UV));
        Assert.Equal(cube.Texture(4), top.Texture(1));
        var bottom = FaceOps.Move(cube, MaskOf(6, 0), FaceOrder.Bottom);
        Assert.Equal(FacePts(cube, 0), FacePts(bottom, 5));
        Assert.Equal(FacePts(cube, 1), FacePts(bottom, 0));
        Assert.Equal(-1, SignedVolume(bottom), 5);
    }

    [Fact]
    public void Sharp_and_smooth_edges()
    {
        var g = GridLod(2, 2);
        Assert.Null(g.SharpEdges);
        var pts = MaskOf(g.PointCount, 0, 1, 3, 4);
        var a = FaceOps.SetSharpEdges(g, pts, true);
        Assert.Equal(4, a.Changed);
        CheckLod(a.Lod);
        Assert.Equal(8, a.Lod.SharpEdges!.Edges.Length);
        int iSharp = a.Lod.Tags.IndexOf(a.Lod.SharpEdges!), iSel = a.Lod.Tags.IndexOf(a.Lod.Selection("row0")!);
        Assert.True(iSharp < iSel);
        Assert.Equal(0, FaceOps.SetSharpEdges(a.Lod, pts, true).Changed);
        var b = FaceOps.SetSharpEdges(a.Lod, MaskOf(g.PointCount, 0, 1), false);
        Assert.Equal(1, b.Changed);
        Assert.Equal(6, b.Lod.SharpEdges!.Edges.Length);
        var c = FaceOps.SetSharpEdges(b.Lod, pts, false);
        Assert.Equal(3, c.Changed);
        Assert.Null(c.Lod.SharpEdges);
        CheckLod(c.Lod);
        Assert.Equal(3, FaceOps.SetSharpEdges(RichCube(), All(8), false).Changed);
    }

    [Fact]
    public void Remove_faces_keeps_points()
    {
        var cube = RichCube();
        var r = FaceOps.Remove(cube, MaskOf(6, 1, 4));
        CheckLod(r);
        Assert.Equal(8, r.PointCount);
        Assert.Equal(4, r.FaceCount);
        Assert.Equal([0, 0, 0, 0], r.Selection("top")!.Faces);
        Assert.Equal(32, r.UVSet(1)!.UV.Length);
    }
}
