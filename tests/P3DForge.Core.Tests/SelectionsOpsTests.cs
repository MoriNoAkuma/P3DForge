using System.Collections.Immutable;
using P3DForge.Core;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class SelectionsOpsTests
{
    /// <summary>Two quads sharing an edge: points 0..5, faces 0 (0,1,2,3) and 1 (1,4,5,2).</summary>
    internal static Lod TwoQuads(float resolution = 1f)
    {
        return new Lod
        {
            Resolution = resolution,
            Positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 2, 0, 0, 2, 1, 0],
            PointFlags = new uint[6],
            Normals = [0, 0, -1],
            FaceVertCount = [4, 4],
            FacePoints = [0, 1, 2, 3, 1, 4, 5, 2],
            FaceNormals = new uint[8],
            FaceUV = new float[16],
            FaceFlags = new uint[2],
            FaceTexture = new uint[2],
            FaceMaterial = new uint[2],
            Tags =
            [
                new PropertyTag(1, "autocenter", "0", null),
                new SelectionTag(1, "left", [1, 1, 1, 1, 0, 0], [1, 0]),
                new SelectionTag(1, "Right", [0, 1, 0, 0, 1, 1], [0, 1]),
                new StateTag(1, StateKind.Selected, new byte[6], new byte[2]),
            ],
        };
    }

    static SelectionMask Mask(byte[] points, byte[] faces) => new(points, faces);

    static string[] Names(Lod lod) => lod.Selections.Select(s => s.Name).ToArray();

    [Fact]
    public void Validate_RejectsBadNames()
    {
        Assert.NotNull(NamedSelections.Validate(""));
        Assert.NotNull(NamedSelections.Validate("   "));
        Assert.NotNull(NamedSelections.Validate("#Mass#"));
        Assert.NotNull(NamedSelections.Validate("a\0b"));
        Assert.NotNull(NamedSelections.Validate("колесо"));
        Assert.Null(NamedSelections.Validate("wheel_1_1_damper"));
    }

    [Fact]
    public void Validate_Latin1Boundary()
    {
        Assert.Null(NamedSelections.Validate("café"));
        Assert.NotNull(NamedSelections.Validate("ň")); // ň is outside latin1
    }

    [Fact]
    public void Create_InsertsAfterLastSelection_CopiesMask_DoesNotMutateInput()
    {
        var lod = TwoQuads();
        var tagsBefore = lod.Tags;
        var pts = new byte[] { 0, 0, 0, 0, 1, 1 };
        var next = NamedSelections.Create(lod, "new", Mask(pts, [0, 0]));
        Assert.Equal(["left", "Right", "new"], Names(next));
        Assert.IsType<SelectionTag>(next.Tags[3]);
        Assert.IsType<StateTag>(next.Tags[4]);
        pts[4] = 0; // caller's mask must not be referenced
        Assert.Equal(1, next.Selection("new")!.Points[4]);
        Assert.Equal(tagsBefore, lod.Tags);
        Mlod.AssertIntegrity(next);
    }

    [Fact]
    public void Create_WithoutSelections_AppendsAtEnd()
    {
        var lod = TwoQuads() with { Tags = [new PropertyTag(1, "a", "b", null)] };
        var next = NamedSelections.Create(lod, "x", SelectionMask.Empty(lod));
        Assert.IsType<SelectionTag>(next.Tags[1]);
    }

    [Fact]
    public void Create_DuplicateOrInvalid_Throws()
    {
        var lod = TwoQuads();
        Assert.Throws<ArgumentException>(() => NamedSelections.Create(lod, "LEFT", SelectionMask.Empty(lod)));
        Assert.Throws<ArgumentException>(() => NamedSelections.Create(lod, "#x", SelectionMask.Empty(lod)));
        Assert.Throws<ArgumentException>(() => NamedSelections.Create(lod, "x", Mask(new byte[3], new byte[2])));
    }

    [Fact]
    public void Delete_ByName_RemovesAllCaseInsensitiveMatches()
    {
        var lod = TwoQuads();
        lod = lod with { Tags = lod.Tags.Insert(3, new SelectionTag(1, "LEFT", new byte[6], new byte[2])) };
        var next = NamedSelections.Delete(lod, "left");
        Assert.Equal(["Right"], Names(next));
        Assert.Same(lod, NamedSelections.Delete(lod, "missing"));
    }

    [Fact]
    public void DeleteAt_RemovesOnlyThatTag()
    {
        var lod = TwoQuads();
        lod = lod with { Tags = lod.Tags.Insert(3, new SelectionTag(1, "left", new byte[6], new byte[2])) };
        var next = NamedSelections.DeleteAt(lod, 3);
        Assert.Equal(["left", "Right"], Names(next));
        Assert.Same(lod, NamedSelections.DeleteAt(lod, 0)); // a property, not a selection
    }

    [Fact]
    public void Rename_KeepsPositionAndContents()
    {
        var lod = TwoQuads();
        var next = NamedSelections.Rename(lod, "left", "door");
        Assert.Equal(["door", "Right"], Names(next));
        Assert.Same(lod.Selection("left")!.Points, next.Selection("door")!.Points);
        Assert.Equal(["LEFT", "Right"], Names(NamedSelections.Rename(lod, "left", "LEFT"))); // case-only rename
        Assert.Same(lod, NamedSelections.Rename(lod, "left", "left"));
        Assert.Throws<ArgumentException>(() => NamedSelections.Rename(lod, "left", "right"));
        Assert.Throws<ArgumentException>(() => NamedSelections.Rename(lod, "missing", "x"));
        Assert.Throws<ArgumentException>(() => NamedSelections.Rename(lod, "left", ""));
    }

    [Fact]
    public void Redefine_ReplacesContents()
    {
        var lod = TwoQuads();
        var next = NamedSelections.Redefine(lod, "left", Mask([0, 0, 0, 0, 1, 0], [0, 0]));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0 }, next.Selection("left")!.Points);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 0, 0 }, lod.Selection("left")!.Points);
        var same = NamedSelections.Redefine(lod, "left", Mask([1, 1, 1, 1, 0, 0], [1, 0]));
        Assert.Same(lod, same);
    }

    [Fact]
    public void Add_KeepsExistingWeights_Remove_Clears()
    {
        var lod = TwoQuads();
        lod = NamedSelections.SetWeight(lod, "left", [1, 0, 0, 0, 0, 0], 0.5f);
        var added = NamedSelections.Add(lod, "left", Mask([1, 0, 0, 0, 1, 0], [0, 1]));
        var s = added.Selection("left")!;
        Assert.Equal(Weights.Encode(0.5f), s.Points[0]);
        Assert.Equal(1, s.Points[4]);
        Assert.Equal(1, s.Faces[1]);
        Assert.Same(lod, NamedSelections.Add(lod, "left", Mask([0, 1, 0, 0, 0, 0], [1, 0])));

        var removed = NamedSelections.Remove(added, "left", Mask([1, 1, 0, 0, 0, 0], [1, 0]));
        var r = removed.Selection("left")!;
        Assert.Equal(new byte[] { 0, 0, 1, 1, 1, 0 }, r.Points);
        Assert.Equal(new byte[] { 0, 1 }, r.Faces);
        Assert.Same(lod, NamedSelections.Remove(lod, "left", Mask([0, 0, 0, 0, 0, 1], [0, 1])));
    }

    [Fact]
    public void SetWeight_EncodesAndAddsPoints_ZeroRemovesFaces()
    {
        var lod = TwoQuads();
        var w = NamedSelections.SetWeight(lod, "left", [0, 0, 0, 0, 1, 0], 0.25f);
        Assert.Equal(Weights.Encode(0.25f), w.Selection("left")!.Points[4]);
        Assert.InRange(Weights.Decode(w.Selection("left")!.Points[4]), 0.24f, 0.26f);

        var z = NamedSelections.SetWeight(lod, "left", [1, 0, 0, 0, 0, 0], 0f);
        Assert.Equal(0, z.Selection("left")!.Points[0]);
        Assert.Equal(0, z.Selection("left")!.Faces[0]); // face 0 used point 0
        Assert.Equal(1, lod.Selection("left")!.Faces[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => NamedSelections.SetWeight(lod, "left", new byte[6], 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => NamedSelections.SetWeight(lod, "left", new byte[6], float.NaN));
        Assert.Same(lod, NamedSelections.SetWeight(lod, "left", [1, 0, 0, 0, 0, 0], 1f));
    }

    [Fact]
    public void DeleteEmpty_RemovesOnlyEmpty()
    {
        var lod = TwoQuads();
        lod = NamedSelections.Create(lod, "empty", SelectionMask.Empty(lod));
        var next = NamedSelections.DeleteEmpty(lod);
        Assert.Equal(["left", "Right"], Names(next));
        Assert.Same(next, NamedSelections.DeleteEmpty(next));
    }

    [Fact]
    public void Counts_ContainsAll_Indices()
    {
        var lod = TwoQuads();
        var s = lod.Selection("left")!;
        Assert.Equal((4, 1), NamedSelections.Counts(s));
        Assert.True(NamedSelections.ContainsAll(s.Points, [0, 3]));
        Assert.False(NamedSelections.ContainsAll(s.Points, [0, 4]));
        Assert.False(NamedSelections.ContainsAll(s.Points, []));
        Assert.Equal([1, 4, 5], NamedSelections.Indices(lod.Selection("right")!.Points));
    }

    [Fact]
    public void UniqueName_AppendsNumber()
    {
        var lod = TwoQuads();
        Assert.Equal("x", NamedSelections.UniqueName(lod, "x"));
        Assert.Equal("left1", NamedSelections.UniqueName(lod, "left"));
        lod = NamedSelections.Create(lod, "left1", SelectionMask.Empty(lod));
        Assert.Equal("left2", NamedSelections.UniqueName(lod, "left"));
    }

    [Fact]
    public void ModelWide_DeleteAndRename()
    {
        var a = TwoQuads(1f);
        var b = TwoQuads(2f) with { Tags = [new SelectionTag(1, "other", new byte[6], new byte[2])] };
        var c = TwoQuads(3f);
        var model = new P3DModel([a, b, c]);

        Assert.Equal([0, 2], NamedSelections.LodsWith(model, "LEFT"));
        var del = NamedSelections.DeleteAll(model, "left");
        Assert.Null(del.Lods[0].Selection("left"));
        Assert.Null(del.Lods[2].Selection("left"));
        Assert.Same(model.Lods[1], del.Lods[1]);
        Assert.Same(model, NamedSelections.DeleteAll(model, "missing"));

        var ren = NamedSelections.RenameAll(model, "left", "door");
        Assert.NotNull(ren.Lods[0].Selection("door"));
        Assert.NotNull(ren.Lods[2].Selection("door"));
        Assert.Same(model.Lods[1], ren.Lods[1]);

        // Atomic: "other" exists in LOD 1 only, renaming "left" → "other" conflicts nowhere; "Right" conflicts in LODs 0 and 2.
        Assert.Throws<ArgumentException>(() => NamedSelections.RenameAll(model, "left", "right"));
        Assert.Throws<ArgumentException>(() => NamedSelections.RenameAll(model, "left", "#bad"));
    }

    [Fact]
    public void CreatedSelection_RoundTripsThroughMlod()
    {
        var lod = TwoQuads();
        lod = NamedSelections.Create(lod, "weighted", Mask([Weights.Encode(0.3f), 1, 0, 0, 0, 0], [0, 0]));
        var model = new P3DModel([lod]);
        var back = Mlod.Read(Mlod.Write(model));
        var s = back.Lods[0].Selection("weighted")!;
        Assert.Equal(Weights.Encode(0.3f), s.Points[0]);
        Assert.Equal(["left", "Right", "weighted"], Names(back.Lods[0]));
    }

    // ---- LOD list ----------------------------------------------------------------------------------

    static P3DModel Lods(params float[] res) => new(res.Select(r => Lod.Empty(r)).ToImmutableArray());

    [Fact]
    public void LodList_InsertSorted_AndUnsorted()
    {
        var sorted = Lods(0, 1, 1e13f);
        var (m, i) = LodList.Insert(sorted, Lod.Empty(2));
        Assert.Equal(2, i);
        Assert.Equal([0f, 1f, 2f, 1e13f], m.Lods.Select(l => l.Resolution));

        var unsorted = Lods(1, 0, 1e13f);
        (m, i) = LodList.Insert(unsorted, Lod.Empty(5), afterIndex: 0);
        Assert.Equal(1, i);
        Assert.Equal([1f, 5f, 0f, 1e13f], m.Lods.Select(l => l.Resolution));
    }

    [Fact]
    public void LodList_Remove_KeepsOne()
    {
        Assert.Single(LodList.Remove(Lods(0, 1), 0).Lods);
        Assert.Throws<InvalidOperationException>(() => LodList.Remove(Lods(0), 0));
    }

    [Fact]
    public void LodList_SetResolution_MovesInSortedModel_RejectsDuplicates()
    {
        var m = Lods(0, 1, 2, 1e13f);
        var (n, i) = LodList.SetResolution(m, 0, 5);
        Assert.Equal(2, i);
        Assert.Equal([1f, 2f, 5f, 1e13f], n.Lods.Select(l => l.Resolution));
        Assert.Throws<ArgumentException>(() => LodList.SetResolution(m, 0, 1e13f));
        Assert.Same(m, LodList.SetResolution(m, 1, 1).Model);
    }

    [Fact]
    public void LodList_Sort_MapsActive()
    {
        var m = Lods(1e13f, 1, 0);
        var (s, active) = LodList.Sort(m, 0);
        Assert.Equal([0f, 1f, 1e13f], s.Lods.Select(l => l.Resolution));
        Assert.Equal(2, active);
        Assert.Same(s, LodList.Sort(s, 1).Model);
    }

    [Fact]
    public void LodList_FreeAndDefaultResolutions()
    {
        var m = Lods(0, 1, 1e13f, 10000);
        Assert.Equal(2f, LodList.DefaultNewResolution(m));
        Assert.Equal(2f, LodList.FreeResolutionLike(m, 1));
        Assert.Equal(10001f, LodList.FreeResolutionLike(m, 10000));
        Assert.Equal(20000f, LodList.FreeResolutionLike(m, 1e13f)); // single type -> first free Edit LOD
    }

    [Fact]
    public void LodList_Choices()
    {
        Assert.Equal((1e13f, (string?)null), LodList.ResolutionFromChoice("geometry", 7));
        Assert.Equal((10003f, (string?)null), LodList.ResolutionFromChoice("shadowVolume", 3));
        Assert.Equal((1.5f, (string?)null), LodList.ResolutionFromChoice("visual", 1.5));
        Assert.NotNull(LodList.ResolutionFromChoice("visual", 1000).Error);
        Assert.NotNull(LodList.ResolutionFromChoice("shadowVolume", -1).Error);
        Assert.Equal(("shadowVolume", 3.0), LodList.ChoiceFromResolution(10003));
        Assert.Equal(("geometry", 0.0), LodList.ChoiceFromResolution(1e13f));
        Assert.Equal("custom", LodList.ChoiceFromResolution(5e19f).TypeId);
    }

    [Fact]
    public void LodList_CopySelectionTo_AppendsAndSelects()
    {
        var src = TwoQuads(1f);
        var dst = TwoQuads(2f);
        var model = new P3DModel([src, dst]);
        var mask = Mask([1, 1, 1, 1, 0, 0], [1, 0]);
        var next = LodList.CopySelectionTo(model, 0, [1, 1, 0], mask);
        Assert.Same(model.Lods[0], next.Lods[0]);
        Assert.Equal(10, next.Lods[1].PointCount);
        Assert.Equal(3, next.Lods[1].FaceCount);
        var sel = Selection.GetSelected(next.Lods[1]);
        Assert.Equal(4, sel.PointCount);
        Assert.Equal(1, sel.FaceCount);
        Mlod.AssertIntegrity(next.Lods[1]);
        Assert.Same(model, LodList.CopySelectionTo(model, 0, [1], SelectionMask.Empty(src)));
    }
}
