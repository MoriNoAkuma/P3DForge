using P3DForge.Core.Model;
using P3DForge.Core.IO;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class DecimateTests
{
    static (float[] Min, float[] Max) Bounds(Lod lod)
    {
        var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
        for (int p = 0; p < lod.PointCount; p++)
            for (int a = 0; a < 3; a++)
            {
                min[a] = Math.Min(min[a], lod.Positions[p * 3 + a]);
                max[a] = Math.Max(max[a], lod.Positions[p * 3 + a]);
            }
        return (min, max);
    }

    [Fact]
    public void FlatGridLosesInteriorPointsButKeepsItsBorderAndUvs()
    {
        var grid = OpsTestHelpers.GridLod(16, 16); // 256 quads = 512 triangles
        var low = Decimate.Run(grid, 0.1, out var stats);
        OpsTestHelpers.CheckLod(low);
        Assert.Equal(512, stats.TrianglesBefore);
        Assert.True(low.FaceCount <= 80, $"{low.FaceCount} faces");
        Assert.Equal(Bounds(grid), Bounds(low), (a, b) => a.Min.SequenceEqual(b.Min) && a.Max.SequenceEqual(b.Max));
        // a planar UV mapping stays exact: u = x / 16, v = z / 16 at every corner
        for (int f = 0; f < low.FaceCount; f++)
            for (int k = 0; k < low.FaceVertCount[f]; k++)
            {
                int p = (int)low.FacePoints[f * 4 + k];
                Assert.Equal(low.Positions[p * 3] / 16, low.FaceUV[f * 8 + k * 2], 4);
                Assert.Equal(low.Positions[p * 3 + 2] / 16, low.FaceUV[f * 8 + k * 2 + 1], 4);
            }
        // UV set 0 tag kept in step with the faces, the selection still names the first row's corners
        Assert.Equal(low.FaceUV, low.UVSet(0)!.UV);
        Assert.NotNull(low.Selection("row0"));
        Assert.True(low.Faces.Texture.All(t => low.Strings[(int)t] == @"grid\a_co.paa"));
    }

    [Fact]
    public void ClosedSphereStaysClosedWithItsVolume()
    {
        var sphere = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(1, 32, 16));
        var low = Decimate.Run(sphere, 0.3, out var stats);
        OpsTestHelpers.CheckLod(low);
        Assert.True(stats.TrianglesAfter < stats.TrianglesBefore * 0.4, $"{stats.TrianglesBefore} → {stats.TrianglesAfter}");
        Assert.True(OpsTestHelpers.IsClosedConsistent(low));
        double v0 = OpsTestHelpers.SignedVolume(sphere), v1 = OpsTestHelpers.SignedVolume(low);
        Assert.InRange(v1 / v0, 0.85, 1.01);
    }

    [Fact]
    public void TextureBordersDoNotMove()
    {
        var grid = OpsTestHelpers.GridLod(16, 16);
        // the left half another texture: the border is the line x = 8
        var left = new byte[grid.FaceCount];
        for (int f = 0; f < grid.FaceCount; f++) left[f] = f % 16 < 8 ? (byte)1 : (byte)0;
        grid = Structure.SetFaceTextures(grid, left, @"grid\b_co.paa", null);
        var low = Decimate.Run(grid, 0.1, out _);
        OpsTestHelpers.CheckLod(low);
        for (int f = 0; f < low.FaceCount; f++)
        {
            bool b = low.Texture(f) == @"grid\b_co.paa";
            for (int k = 0; k < 3; k++)
            {
                float x = low.Positions[low.FacePoints[f * 4 + k] * 3];
                Assert.True(b ? x <= 8 + 1e-4 : x >= 8 - 1e-4, $"face {f} ({low.Texture(f)}) reaches x = {x}");
            }
        }
    }
}

public class LodGeneratorTests(Xunit.ITestOutputHelper output)
{
    static P3DModel Sample()
    {
        var lod = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(1, 24, 12));
        lod = Primitives.CreateBox(lod, new BoxParams { At = new Vec3(3, 0, 0), Selection = "door" });
        lod = Primitives.CreateBox(lod, new BoxParams { At = new Vec3(-3, 0, 0) });
        lod = Structure.SetFaceTextures(lod, MeshMath.Ones(lod.FaceCount), @"x\a_co.paa", @"x\a.rvmat");
        return new P3DModel([lod]);
    }

    [Fact]
    public void BuildsTheUsualLodSet()
    {
        var r = LodGenerator.Generate(Sample(), new LodGenOptions(0, [0.5, 0.25], Shadow: true, Geometry: true, Fire: true, View: true, Mass: 20, Separate: ["door"]));
        foreach (var line in r.Report) output.WriteLine(line);
        var m = r.Model;
        Assert.Equal([0f, 1f, 2f, 10000f, 1e13f, 6e15f, 7e15f], m.Lods.Select(l => l.Resolution).ToArray());
        Assert.True(m.Lods[1].FaceCount < m.Lods[0].FaceCount && m.Lods[2].FaceCount < m.Lods[1].FaceCount);
        var ctx = new Validation.ValidationContext { Model = m };
        var issues = Validation.Validator.Run(ctx, [new Validation.ShadowVolumeRule(), new Validation.ComponentClosedRule(), new Validation.ComponentConvexRule(), new Validation.ComponentSelectionsRule(), new Validation.GeometryMassRule()]);
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
        var geo = m.Lods[4];
        Assert.Equal(20, Mass.Total(geo), 3);
        Assert.NotNull(geo.Selection("Component03"));
        Assert.NotNull(geo.Selection("door"));      // the animated part keeps its collision
        Assert.Equal("", m.Lods[3].Texture(0));     // shadow: no textures
        // asking again without "replace" fails instead of doubling LODs; with replace the set is rebuilt
        Assert.Throws<InvalidOperationException>(() => LodGenerator.Generate(m, new LodGenOptions(0, [], true, false, false, false)));
        Assert.Equal(7, LodGenerator.Generate(m, new LodGenOptions(0, [0.5, 0.25], true, true, true, true, Replace: true)).Model.Lods.Length);
    }

    [Fact]
    public void RealWeapon()
    {
        var file = TestData.Local("weapon");
        var model = Mlod.Read(File.ReadAllBytes(file), null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LodGenerator.Generate(new P3DModel([model.Lods[0]]), new LodGenOptions(0, [0.5, 0.25, 0.12], true, true, true, false));
        foreach (var line in r.Report) output.WriteLine(line);
        output.WriteLine($"total {sw.ElapsedMilliseconds} ms");
        Assert.All(r.Model.Lods, OpsTestHelpers.CheckLod);
    }
}
