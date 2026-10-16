using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class ExtrudeTests
{
    [Fact]
    public void Extruding_the_top_of_a_box_keeps_it_closed_and_selects_the_top()
    {
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams());
        int top = Enumerable.Range(0, box.FaceCount).First(f => FacePts(box, f).All(p => box.Positions[p * 3 + 1] > 0.4f));
        var mask = MaskOf(box.FaceCount, top);
        Assert.Equal(new Vec3(0, 1, 0), Extrude.AverageOutwardNormal(box, mask));
        var r = Extrude.Faces(box, mask, 0.5);
        CheckLod(r.Lod);
        Assert.Equal(1, r.FaceCount);
        Assert.Equal(4, r.SideFaces);
        Assert.Equal(4, r.NewPoints);
        Assert.Equal(12, r.Lod.PointCount);
        Assert.Equal(10, r.Lod.FaceCount);
        Assert.True(IsClosedConsistent(r.Lod));
        Assert.Equal(-1.5, SignedVolume(r.Lod), 5);
        var sel = Selection.GetSelected(r.Lod);
        Assert.Equal([top], Indices(sel.Faces));
        Assert.Equal([8, 9, 10, 11], Indices(sel.Points));
        foreach (uint p in FacePts(r.Lod, top)) Assert.Equal(1, r.Lod.Positions[p * 3 + 1], 6);
    }

    [Fact]
    public void Extruding_a_grid_region_moves_interior_points_and_duplicates_boundary_points()
    {
        var g = GridLod(3, 3);
        var r = Extrude.Faces(g, MaskOf(9, 0, 1, 3, 4), 1, segments: 2);
        CheckLod(r.Lod);
        Assert.Equal(16, r.NewPoints);
        Assert.Equal(16, r.SideFaces);
        Assert.Equal(1f, r.Lod.Positions[5 * 3 + 1]);
        var seen = new HashSet<(uint, uint)>();
        for (int f = 0; f < r.Lod.FaceCount; f++)
        {
            var p = FacePts(r.Lod, f);
            for (int k = 0; k < p.Length; k++) Assert.True(seen.Add((p[k], p[(k + 1) % p.Length])));
        }
        Assert.Equal(new Vec3(0, 1, 0), r.Direction);
        int side = r.Lod.FaceCount - 1;
        Assert.Equal(@"grid\a_co.paa", r.Lod.Texture(side));
        Assert.Equal(r.Lod.FaceCount * 8, r.Lod.UVSet(0)!.UV.Length);
        Assert.Equal(FaceUVs(r.Lod, side), FaceUVs(r.Lod, side, r.Lod.UVSet(0)!.UV));
        var row0 = r.Lod.Selection("row0")!;
        Assert.Equal(2 * 3, Enumerable.Range(16, r.Lod.PointCount - 16).Count(i => row0.Points[i] != 0));
    }

    [Fact]
    public void Extrude_keeps_mass_uv_animation_consistent_on_a_rich_LOD()
    {
        var r = Extrude.Faces(RichCube(), MaskOf(6, 4), 0.25);
        CheckLod(r.Lod);
        Assert.Equal(r.Lod.PointCount, r.Lod.MassTag!.Mass.Length);
        Assert.Equal([0f, 0, 0, 0], r.Lod.MassTag!.Mass.AsSpan(8).ToArray());
        Assert.Equal(r.Lod.FaceCount * 8, r.Lod.UVSet(1)!.UV.Length);
        Assert.Equal(r.Lod.PointCount * 3, r.Lod.Animations.First().Positions.Length);
        Assert.True(IsClosedConsistent(r.Lod));
        Assert.Equal(-1.25, SignedVolume(r.Lod), 5);
    }

    [Fact]
    public void Zero_distance_degenerate_and_explicit_directions()
    {
        var p = Primitives.CreatePlane(Lod.Empty(0), new PlaneParams());
        var r = Extrude.Faces(p, MaskOf(1, 0), 0);
        CheckLod(r.Lod);
        Assert.Equal(5, r.Lod.FaceCount);
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams());
        var all = Extrude.Faces(box, All(6), 1);
        Assert.Null(all.Direction);
        CheckLod(all.Lod);
        Assert.Same(box, Extrude.Faces(box, new byte[6], 1).Lod);
        var dir = Extrude.Faces(p, MaskOf(1, 0), 2, direction: new Vec3(0, 0, 1));
        foreach (uint q in FacePts(dir.Lod, 0)) Assert.True(Math.Abs(dir.Lod.Positions[q * 3 + 2]) > 1.4);
    }
}

public class ClipboardTests
{
    static byte[] Copy(Lod lod, byte[] points, byte[] faces) => MeshMath.FragmentToMlod(Structure.Extract(lod, points, faces), lod.Resolution);

    static Lod Paste(Lod target, byte[] bytes) => MeshMath.AppendSafe(target, MeshMath.MlodToFragment(bytes).Fragment, selectNew: true);

    [Fact]
    public void Round_trips_a_selection_into_an_empty_LOD()
    {
        var cube = RichCube();
        var bytes = Copy(cube, MaskOf(8, 2, 3, 6, 7), MaskOf(6, 4));
        var (fragment, resolution) = MeshMath.MlodToFragment(bytes);
        Assert.Equal(cube.Resolution, resolution);
        Assert.Equal(4, fragment.PointCount);
        Assert.Contains("4 points, 1 faces, 2 named selections (Component01, top)", MeshMath.FragmentSummary(fragment));
        var lod = Paste(Lod.Empty((float)1e13), bytes);
        CheckLod(lod);
        Assert.Equal(4, lod.PointCount);
        Assert.Equal(1, lod.FaceCount);
        Assert.Equal(cube.Texture(4), lod.Texture(0));
        Assert.Equal(cube.Material(4), lod.Material(0));
        Assert.Equal(FaceUVs(cube, 4), FaceUVs(lod, 0));
        Assert.Equal(FaceUVs(cube, 4, cube.UVSet(1)!.UV), FaceUVs(lod, 0, lod.UVSet(1)!.UV));
        Assert.Equal(FaceUVs(cube, 4), FaceUVs(lod, 0, lod.UVSet(0)!.UV));
        Assert.Equal([3f, 4, 7, 8], lod.MassTag!.Mass);
        Assert.Equal([1, 1, 128, 255], lod.Selection("top")!.Points);
        Assert.Equal([1, 1, 1, 1], Selection.GetSelected(lod).Points);
        Assert.Equal([1], Selection.GetSelected(lod).Faces);
    }

    [Fact]
    public void Pastes_into_a_populated_LOD_merging_selections_and_keeping_UV_set_0()
    {
        var cube = RichCube();
        var bytes = Copy(cube, All(8), All(6));
        var grid = GridLod(2, 2);
        var lod = Paste(grid, bytes);
        CheckLod(lod);
        Assert.Equal(9 + 8, lod.PointCount);
        Assert.Equal(4 + 6, lod.FaceCount);
        for (int f = 0; f < 4; f++) Assert.Equal(FaceUVs(grid, f), FaceUVs(lod, f, lod.UVSet(0)!.UV));
        for (int f = 4; f < 10; f++) Assert.Equal(FaceUVs(cube, f - 4), FaceUVs(lod, f, lod.UVSet(0)!.UV));
        Assert.Equal(17, lod.Selection("row0")!.Points.Length);
        Assert.NotNull(lod.Selection("component01"));
        Assert.Equal([1f, 2, 3, 4, 5, 6, 7, 8], lod.MassTag!.Mass.AsSpan(9).ToArray());
        Assert.Equal(0f, lod.MassTag!.Mass[0]);
        var twice = Paste(lod, bytes);
        CheckLod(twice);
        Assert.Single(twice.Selections, s => s.Name.Equals("top", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Does_not_clobber_face_UVs_of_a_LOD_without_a_stage0_tag()
    {
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams());
        Assert.Null(box.UVSet(0));
        var lod = Paste(box, Copy(RichCube(), All(8), All(6)));
        CheckLod(lod);
        Assert.Null(lod.UVSet(0));
        for (int f = 0; f < 6; f++) Assert.Equal(FaceUVs(box, f), FaceUVs(lod, f));
        var cube = RichCube();
        for (int f = 6; f < 12; f++) Assert.Equal(FaceUVs(cube, f - 6), FaceUVs(lod, f));
        Assert.Equal(12 * 8, lod.UVSet(1)!.UV.Length);
    }

    [Fact]
    public void Copies_isolated_points_and_rejects_garbage()
    {
        var lod = Paste(GridLod(1, 1), Copy(RichCube(), MaskOf(8, 0, 5), new byte[6]));
        CheckLod(lod);
        Assert.Equal(6, lod.PointCount);
        Assert.Equal(1, lod.FaceCount);
        Assert.ThrowsAny<Exception>(() => MeshMath.MlodToFragment([1, 2, 3]));
    }
}
