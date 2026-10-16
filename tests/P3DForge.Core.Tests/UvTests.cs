using System.Numerics;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class UvTests
{
    static Lod Box() => Primitives.CreateBox(Lod.Empty(0), new BoxParams(2, 1, 4));

    [Fact]
    public void Sets_add_put_delete_and_survive_a_save()
    {
        var lod = Box();
        Assert.Equal([0], Uv.Sets(lod));
        lod = Uv.AddSet(lod, 0, out int added);
        Assert.Equal(1, added);
        Assert.Equal([0, 1], Uv.Sets(lod));
        Assert.Equal(lod.FaceUV, Uv.Get(lod, 1));

        var moved = Uv.Transform(Uv.Get(lod, 1), Uv.SlotsOf(lod, null), Matrix3x2.CreateTranslation(0.5f, 0));
        lod = Uv.Put(lod, 1, moved);
        Assert.Equal(moved, Uv.Get(lod, 1));
        Assert.NotEqual(moved, lod.FaceUV); // set 0 untouched

        var saved = OpsTestHelpers.RoundTrip(lod);
        Assert.Equal(moved, Uv.Get(saved, 1));

        lod = Uv.DeleteSet(lod, 1);
        Assert.Equal([0], Uv.Sets(lod));
        Assert.Throws<ArgumentException>(() => Uv.DeleteSet(lod, 0));
    }

    [Fact]
    public void Put_on_set_0_keeps_the_stage_0_tag_in_sync()
    {
        var lod = Uv.AddSet(Box(), 0, out _); // also writes set 0 as a tag, like Object Builder
        Assert.NotNull(lod.UVSet(0));
        var flipped = Uv.Transform(lod.FaceUV, Uv.SlotsOf(lod, null), Uv.Scale(-1, 1, new Vector2(0.5f, 0.5f)));
        lod = Uv.Put(lod, 0, flipped);
        Assert.Equal(flipped, lod.FaceUV);
        Assert.Equal(flipped, lod.UVSet(0)!.UV);
        Assert.Same(lod, Uv.Put(lod, 0, (float[])flipped.Clone())); // no change → same LOD
    }

    [Fact]
    public void Fit_fills_the_unit_square()
    {
        var lod = Box();
        var slots = Uv.SlotsOf(lod, null);
        var uv = Uv.Transform(lod.FaceUV, slots, Matrix3x2.CreateScale(3, 0.5f) * Matrix3x2.CreateTranslation(-4, 7));
        var fit = Uv.Fit(uv, slots, keepAspect: false);
        var b = Uv.Bounds(fit, slots)!.Value;
        Assert.Equal(0, b.MinU, 4);
        Assert.Equal(0, b.MinV, 4);
        Assert.Equal(1, b.MaxU, 4);
        Assert.Equal(1, b.MaxV, 4);
    }

    [Fact]
    public void Planar_along_z_follows_x_and_y()
    {
        var lod = Box(); // 2 wide (X) × 1 high (Y)
        var faces = MeshMath.Ones(lod.FaceCount);
        var uv = Uv.PlanarAxis(lod, lod.FaceUV, faces, 2);
        var slots = Uv.SlotsOf(lod, faces);
        var b = Uv.Bounds(uv, slots)!.Value;
        Assert.Equal(1, b.MaxU - b.MinU, 4); // the longer side fills 0–1
        Assert.Equal(0.5, b.MaxV - b.MinV, 4); // aspect kept
        // u grows with x; v grows downward (higher points get smaller v)
        int hi = slots.MaxBy(s => lod.Positions[lod.FacePoints[s] * 3 + 1]);
        int lo = slots.MinBy(s => lod.Positions[lod.FacePoints[s] * 3 + 1]);
        Assert.True(uv[hi * 2 + 1] < uv[lo * 2 + 1]);
    }

    [Fact]
    public void Welded_and_island_follow_shared_uvs()
    {
        var lod = Box();
        var uv = Uv.PlanarAxis(lod, lod.FaceUV, MeshMath.Ones(lod.FaceCount), 1); // top view: top and bottom overlap
        var slots = Uv.SlotsOf(lod, null);
        var welded = Uv.Welded(lod, uv, [slots[0]], slots);
        Assert.Contains(slots[0], welded);
        Assert.All(welded, s => Assert.Equal(lod.FacePoints[slots[0]], lod.FacePoints[s]));
        // A planar projection gives every point one UV, so the closed box is one island…
        Assert.Equal(lod.FaceCount, Uv.Island(lod, uv, 0, null).Count);
        // …and moving one face's UVs away cuts it off.
        var cut = Uv.Transform(uv, Enumerable.Range(0, lod.FaceVertCount[0]), Matrix3x2.CreateTranslation(5, 0));
        Assert.Equal([0], Uv.Island(lod, cut, 0, null));
    }
}
