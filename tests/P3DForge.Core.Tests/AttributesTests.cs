using System.Collections.Immutable;
using P3DForge.Core;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class AttributesTests
{
    /// <summary>TwoQuads with textures: face 0 = "dz\a\x_co.paa" + "dz\a\x.rvmat", face 1 = "DZ\A\Y_co.paa" + "".</summary>
    static Lod Textured()
    {
        var lod = SelectionsOpsTests.TwoQuads();
        return lod with
        {
            Strings = ["", @"dz\a\x_co.paa", @"dz\a\x.rvmat", @"DZ\A\Y_co.paa"],
            FaceTexture = [1, 3],
            FaceMaterial = [2, 0],
        };
    }

    static P3DModel Model(params Lod[] lods) => new(lods.ToImmutableArray());

    [Fact]
    public void SetFaceFlags_ReplacesOnlyTheField_OnMaskedFaces()
    {
        var lod = Textured() with { FaceFlags = [FaceFlags.NoShadow | 0x200, 0x300] };
        var next = Attributes.SetFaceFlags(lod, [1, 0], FaceFlags.ZBiasMask, 0x100);
        Assert.Equal(new uint[] { FaceFlags.NoShadow | 0x100, 0x300 }, next.FaceFlags);
        Assert.Equal(new uint[] { FaceFlags.NoShadow | 0x200, 0x300 }, lod.FaceFlags); // input untouched
        Assert.Same(lod.FacePoints, next.FacePoints);
        Assert.Same(lod, Attributes.SetFaceFlags(lod, [0, 1], FaceFlags.ZBiasMask, 0x300)); // no change → same LOD
        Mlod.AssertIntegrity(next);
    }

    [Fact]
    public void SetFaceFlags_UserValue()
    {
        var lod = Textured();
        var next = Attributes.SetFaceFlags(lod, [1, 1], FaceFlags.UserMask, 5u << FaceFlags.UserShift);
        Assert.All(next.FaceFlags, f => Assert.Equal(5u, FaceFlags.UserValue(f)));
    }

    [Fact]
    public void SetPointFlags_KeepsOtherBits()
    {
        var lod = Textured();
        lod = lod with { PointFlags = [PointFlags.Hidden, 0, 0, 0, 0, PointFlags.NoFog] };
        var next = Attributes.SetPointFlags(lod, [1, 0, 0, 0, 0, 1], PointFlags.LandMask, PointFlags.OnLand);
        Assert.Equal(PointFlags.Hidden | PointFlags.OnLand, next.PointFlags[0]);
        Assert.Equal(PointFlags.NoFog | PointFlags.OnLand, next.PointFlags[5]);
        Assert.Equal(0u, next.PointFlags[1]);
        Assert.Equal(PointFlags.Hidden, lod.PointFlags[0]);
        Assert.Throws<ArgumentException>(() => Attributes.SetPointFlags(lod, [1], PointFlags.LandMask, 1));
    }

    [Fact]
    public void FlagSummary_CommonOrMixedPerField()
    {
        var s = FlagSummary.Empty.Add(FaceFlags.NoShadow | 0x100).Add(FaceFlags.NoShadow | 0x200);
        Assert.Equal(2, s.Count);
        Assert.True(s.Bit(FaceFlags.NoShadow));
        Assert.Null(s.Common(FaceFlags.ZBiasMask));
        Assert.True(s.IsMixed(FaceFlags.ZBiasMask));
        Assert.Equal(0u, s.Common(FaceFlags.LightMask));
        Assert.Null(FlagSummary.Empty.Common(FaceFlags.LightMask));
    }

    [Fact]
    public void SummarizeFaces_TextureMixed_MaterialCommon()
    {
        var lod = Textured();
        var both = Attributes.SummarizeFaces(lod, [1, 1]);
        Assert.Equal(2, both.Count);
        Assert.True(both.TextureMixed);
        Assert.Null(both.Texture);
        Assert.True(both.MaterialMixed);
        var one = Attributes.SummarizeFaces(lod, [0, 1]);
        Assert.Equal(1, one.First);
        Assert.Equal(@"DZ\A\Y_co.paa", one.Texture);
        Assert.Equal("", one.Material);
        Assert.False(one.TextureMixed);
        var none = Attributes.SummarizeFaces(lod, [0, 0]);
        Assert.Equal(0, none.Count);
        Assert.False(none.TextureMixed);
    }

    [Fact]
    public void SummarizePoints_CentroidBoundsHidden()
    {
        var lod = Textured() with { PointFlags = [PointFlags.Hidden, PointFlags.Locked, 0, 0, 0, 0] };
        var s = Attributes.SummarizePoints(lod, [1, 1, 1, 1, 0, 0]);
        Assert.Equal(4, s.Count);
        Assert.Equal(new Vec3(0.5, 0.5, 0), s.Centroid);
        Assert.Equal(new Vec3(0, 0, 0), s.Min);
        Assert.Equal(new Vec3(1, 1, 0), s.Max);
        Assert.Equal(1, s.Hidden);
        Assert.Equal(1, s.Locked);
        Assert.True(s.Flags.IsMixed(PointFlags.SpecialMask));
        Assert.Equal(0u, s.Flags.Common(PointFlags.LandMask));
    }

    [Fact]
    public void SetFaceTextureMaterial_NullMeansUnchanged()
    {
        var lod = Textured();
        var next = Attributes.SetFaceTextureMaterial(lod, [0, 1], @"dz\b\z_co.paa", null);
        Assert.Equal(@"dz\a\x_co.paa", next.Texture(0));
        Assert.Equal(@"dz\b\z_co.paa", next.Texture(1));
        Assert.Equal("", next.Material(1));
        Assert.Equal(@"DZ\A\Y_co.paa", lod.Texture(1));
        Assert.Same(lod, Attributes.SetFaceTextureMaterial(lod, [1, 0], @"dz\a\x_co.paa", @"dz\a\x.rvmat"));
        Assert.Same(lod, Attributes.SetFaceTextureMaterial(lod, [1, 1], null, null));
        var mat = Attributes.SetFaceTextureMaterial(lod, [1, 1], null, @"dz\m.rvmat");
        Assert.Equal(@"dz\m.rvmat", mat.Material(0));
        Assert.Equal(@"dz\m.rvmat", mat.Material(1));
        Mlod.AssertIntegrity(mat);
    }

    [Fact]
    public void FacesWithTexture_CaseAndSlashInsensitive()
    {
        var lod = Textured();
        Assert.Equal(new byte[] { 0, 1 }, Attributes.FacesWithTexture(lod, @"\dz/a/y_CO.paa"));
        Assert.Equal(new byte[] { 1, 0 }, Attributes.FacesWithMaterial(lod, @"DZ\a\X.RVMAT"));
        Assert.Equal(new byte[] { 0, 1 }, Attributes.FacesWithMaterial(lod, ""));
        Assert.Equal(new byte[] { 0, 0 }, Attributes.FacesWithTexture(lod, "nothing.paa"));
    }

    [Fact]
    public void Usage_CountsPerLod_MergesSpellings()
    {
        var a = Textured();
        var b = Textured() with { Strings = ["", @"dz\A\X_CO.paa"], FaceTexture = [1, 1], FaceMaterial = [0, 0] };
        var use = Attributes.Usage(Model(a, b));
        var x = use.Single(u => u.Kind == ResourceKind.Texture && Attributes.PathKey(u.Path) == @"dz\a\x_co.paa");
        Assert.Equal(new[] { 1, 2 }, x.FacesPerLod);
        Assert.Equal(3, x.TotalFaces);
        Assert.Equal(2, x.LodCount);
        Assert.Equal(@"dz\a\x_co.paa", x.Path); // first spelling wins
        Assert.Equal(3, use.Count); // x_co, y_co, x.rvmat ("" excluded)
        Assert.Equal(ResourceKind.Material, use[^1].Kind);
    }

    [Theory]
    [InlineData(PathMatch.Substring, @"\a\", @"\b\", @"dz\b\x_co.paa")]
    [InlineData(PathMatch.Prefix, @"dz/a", @"mymod\data", @"mymod\data\x_co.paa")]
    [InlineData(PathMatch.Exact, @"DZ\A\X_CO.PAA", @"new.paa", @"new.paa")]
    [InlineData(PathMatch.Wildcard, @"dz\a\*_co.paa", @"mod\*_ca.paa", @"mod\x_ca.paa")]
    [InlineData(PathMatch.Wildcard, @"dz\?\*", @"mod\?\tex\*", @"mod\a\tex\x_co.paa")]
    [InlineData(PathMatch.Wildcard, @"*x_co.paa", @"plain.paa", @"plain.paa")]
    public void ReplacePath_Modes(PathMatch match, string find, string replace, string expected)
    {
        Assert.Equal(expected, Attributes.ReplacePath(@"dz\a\x_co.paa", find, replace, new ReplaceOptions { Match = match }));
    }

    [Fact]
    public void ReplacePath_CaseSensitiveAndNoMatch()
    {
        var cs = new ReplaceOptions { CaseInsensitive = false };
        Assert.Null(Attributes.ReplacePath(@"dz\a\x_co.paa", @"\A\", @"\b\", cs));
        Assert.Null(Attributes.ReplacePath(@"dz\a\x_co.paa", @"zz", @"b", new ReplaceOptions()));
        Assert.Null(Attributes.ReplacePath(@"dz\a\x_co.paa", "", "b", new ReplaceOptions()));
        Assert.Null(Attributes.ReplacePath(@"dz\a\x_co.paa", "x", "x", new ReplaceOptions())); // no change
        Assert.Null(Attributes.ReplacePath(@"dz\a\x_co.paa", @"dz\*.rvmat", "m", new ReplaceOptions { Match = PathMatch.Wildcard }));
    }

    [Fact]
    public void ReplacePaths_AllLods_TexturesOnly_CountsFaces()
    {
        var a = Textured();
        var b = Textured() with { FaceTexture = [1, 1] };
        var model = Model(a, b);
        var (next, count) = Attributes.ReplacePaths(model, @"dz\a\", @"mod\", new ReplaceOptions { Materials = false });
        Assert.Equal(4, count); // all four faces (case-insensitive also hits "DZ\A\Y_co.paa")
        Assert.Equal(@"mod\x_co.paa", next.Lods[0].Texture(0));
        Assert.Equal(@"mod\Y_co.paa", next.Lods[0].Texture(1));
        Assert.Equal(@"mod\x_co.paa", next.Lods[1].Texture(1));
        Assert.Equal(@"dz\a\x.rvmat", next.Lods[0].Material(0)); // materials untouched
        Assert.Equal(@"dz\a\x_co.paa", model.Lods[0].Texture(0)); // input untouched
        foreach (var l in next.Lods) Mlod.AssertIntegrity(l);

        var (same, zero) = Attributes.ReplacePaths(model, "nothing", "x", new ReplaceOptions());
        Assert.Same(model, same);
        Assert.Equal(0, zero);
    }

    [Fact]
    public void ReplacePaths_MergesIntoExistingPoolEntry()
    {
        var lod = Textured();
        var (next, count) = Attributes.ReplacePaths(Model(lod), @"DZ\A\Y_co.paa", @"dz\a\x_co.paa", new ReplaceOptions { Match = PathMatch.Exact });
        Assert.Equal(1, count);
        Assert.Equal(next.Lods[0].FaceTexture[0], next.Lods[0].FaceTexture[1]);
        Assert.Equal(lod.Strings.Length, next.Lods[0].Strings.Length);
    }

    [Fact]
    public void PreviewReplace_AggregatesOverLods()
    {
        var a = Textured();
        var b = Textured() with { FaceTexture = [1, 1] };
        var p = Attributes.PreviewReplace(Model(a, b), @"x", @"q", new ReplaceOptions());
        var tex = p.Single(c => c.Kind == ResourceKind.Texture);
        Assert.Equal(@"dz\a\x_co.paa", tex.From);
        Assert.Equal(@"dz\a\q_co.paa", tex.To);
        Assert.Equal(3, tex.Faces);
        Assert.Equal(2, tex.Lods);
        var mat = p.Single(c => c.Kind == ResourceKind.Material);
        Assert.Equal(@"dz\a\q.rvmat", mat.To);
        Assert.Equal(2, mat.Faces);
    }

    [Fact]
    public void EffectiveFaces_FallsBackToFullySelectedFaces()
    {
        var lod = Textured();
        Assert.Equal(new byte[] { 0, 1 }, Attributes.EffectiveFaces(lod, [1, 1, 1, 1, 1, 1], [0, 1]));
        Assert.Equal(new byte[] { 1, 0 }, Attributes.EffectiveFaces(lod, [1, 1, 1, 1, 0, 0], [0, 0]));
        Assert.Equal(new byte[] { 0, 0 }, Attributes.EffectiveFaces(lod, [0, 1, 0, 0, 0, 0], [0, 0]));
    }

    [Fact]
    public void SetPointPosition_And_OffsetPoints()
    {
        var lod = Textured();
        var moved = Attributes.SetPointPosition(lod, 2, new Vec3(5, 6, 7));
        Assert.Equal(new Vec3(5, 6, 7), Attributes.Point(moved, 2));
        Assert.Equal(new Vec3(1, 1, 0), Attributes.Point(lod, 2));
        Assert.Same(moved, Attributes.SetPointPosition(moved, 2, new Vec3(5, 6, 7)));
        Assert.Throws<ArgumentException>(() => Attributes.SetPointPosition(lod, 0, new Vec3(double.NaN, 0, 0)));

        var off = Attributes.OffsetPoints(lod, [1, 0, 0, 0, 0, 1], new Vec3(0, 0, 2));
        Assert.Equal(new Vec3(0, 0, 2), Attributes.Point(off, 0));
        Assert.Equal(new Vec3(2, 1, 2), Attributes.Point(off, 5));
        Assert.Equal(new Vec3(1, 0, 0), Attributes.Point(off, 1));
        Assert.Same(lod, Attributes.OffsetPoints(lod, [1, 0, 0, 0, 0, 0], Vec3.Zero));
    }

    [Fact]
    public void Measure_DistanceAnglesEdgesArea()
    {
        var lod = Textured();
        var two = Attributes.Measure(lod, [1, 0, 0, 0, 1, 0], new byte[2]);
        Assert.Equal(2.0, two.Distance!.Value, 6);
        Assert.Equal(new Vec3(2, 0, 0), two.Delta);
        Assert.Equal(0, two.Edges); // 0-4 is not a face edge

        var three = Attributes.Measure(lod, [1, 1, 1, 0, 0, 0], new byte[2]);
        Assert.Null(three.Distance);
        Assert.Equal(new[] { 0, 1, 2 }, three.FirstPoints);
        Assert.Equal(45.0, three.Angles![0], 6);
        Assert.Equal(90.0, three.Angles[1], 6);
        Assert.Equal(45.0, three.Angles[2], 6);
        Assert.Equal(2, three.Edges); // 0-1 and 1-2 (0-2 is a diagonal)
        Assert.Equal(2.0, three.EdgeLength, 6);

        var all = Attributes.Measure(lod, [1, 1, 1, 1, 1, 1], [1, 1]);
        Assert.Equal(7, all.Edges); // shared edge 1-2 counted once
        Assert.Equal(7.0, all.EdgeLength, 6);
        Assert.Equal(2.0, all.Area, 6);
        Assert.Equal(new Vec3(2, 1, 0), all.Size);
        Assert.Equal(1.0, Attributes.FaceArea(lod, 0), 6);
    }

    [Fact]
    public void SetFaceUV_UpdatesFaceAndStage0_LeavesOtherSets()
    {
        var baseLod = Textured();
        var stage1 = new float[baseLod.FaceCount * 8];
        Array.Fill(stage1, 0.5f);
        var lod = baseLod with
        {
            Tags = baseLod.Tags.Add(new UVSetTag(1, 0, (float[])baseLod.FaceUV.Clone())).Add(new UVSetTag(1, 1, stage1)),
        };
        float[] uv = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f];
        var next = Attributes.SetFaceUV(lod, 1, uv);
        Assert.Equal(uv, next.FaceUV.AsSpan(8, 8).ToArray());
        Assert.Equal(lod.FaceUV.AsSpan(0, 8).ToArray(), next.FaceUV.AsSpan(0, 8).ToArray()); // face 0 untouched
        var sets = next.UVSets.ToList();
        Assert.Equal(uv, sets.First(s => s.Stage == 0).UV.AsSpan(8, 8).ToArray());
        Assert.Same(stage1, sets.First(s => s.Stage == 1).UV);
        Assert.NotEqual(uv, lod.FaceUV.AsSpan(8, 8).ToArray()); // input untouched
        Assert.Same(next, Attributes.SetFaceUV(next, 1, uv)); // no change → same LOD
        Assert.Throws<ArgumentException>(() => Attributes.SetFaceUV(lod, 1, [0f, 0f]));
        Assert.Throws<ArgumentException>(() => Attributes.SetFaceUV(lod, 1, [0, 0, 0, 0, 0, 0, 0, float.NaN]));
        Mlod.AssertIntegrity(next);
    }

    /// <summary>Regression: edge keys hashed with ulong's default (lo ^ hi) collided for neighbouring indices (≈ 1 s on the BTR).</summary>
    [Fact]
    public void Measure_LargeGrid_CountsEdgesFast()
    {
        var lod = Primitives.CreatePlane(Lod.Empty(1), new PlaneParams(10, 20, 200, 200));
        Assert.Equal(201 * 201, lod.PointCount);
        var points = new byte[lod.PointCount];
        Array.Fill(points, (byte)1);
        var faces = new byte[lod.FaceCount];
        Array.Fill(faces, (byte)1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = Attributes.Measure(lod, points, faces);
        sw.Stop();
        Assert.Equal(2 * 200 * 201, m.Edges);
        Assert.Equal(200.0, m.Area, 6);
        Assert.Equal(201 * 10.0 + 201 * 20.0, m.EdgeLength, 3); // 201 lines of 10 m along X + 201 of 20 m along Z
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Measure took {sw.ElapsedMilliseconds} ms");
    }
}
