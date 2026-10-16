using P3DForge.Core.Model;
using P3DForge.Core.Validation;

namespace P3DForge.Core.Tests;

public class DayZRulesTests
{
    static Lod Cube(float resolution) => OpsTestHelpers.RichCube() with { Resolution = resolution };

    static Lod Memory(params string[] points)
    {
        var lod = Lod.Empty(1e15f) with
        {
            Positions = new float[points.Length * 3],
            PointFlags = new uint[points.Length],
        };
        var tags = points.Select((p, i) => (Tag)new SelectionTag(1, p, OpsTestHelpers.MaskOf(points.Length, i), [])).ToList();
        return lod with { Tags = [.. tags] };
    }

    static List<ValidationIssue> Run(IValidationRule rule, P3DModel model, ModelKind kind = ModelKind.Unknown,
        string[]? skeleton = null, string[]? animated = null, string[]? hidden = null) =>
        [.. rule.Check(new ValidationContext
        {
            Model = model,
            Kind = kind,
            SkeletonBones = skeleton ?? [],
            AnimatedBones = animated ?? [],
            HiddenSelections = hidden ?? [],
        })];

    [Fact]
    public void WeaponNeedsMuzzleAndInventoryPoints()
    {
        var model = new P3DModel([Cube(0), Memory("usti hlavne", "eye", "boundingbox_min", "boundingbox_max", "ce_center", "ce_radius", "invview")]);
        var issues = Run(new DayZMemoryPointsRule(), model, ModelKind.Weapon);
        var error = Assert.Single(issues, i => i.Severity == Severity.Error);
        Assert.Contains("konec hlavne", error.Message);
        Assert.Equal(1, error.LodIndex);
        Assert.Contains(issues, i => i.Severity == Severity.Info && i.Message.Contains("nabojnicestart"));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Warning);
        // an item needs only the inventory points; unknown kinds are not checked
        Assert.Empty(Run(new DayZMemoryPointsRule(), model, ModelKind.Item));
        Assert.Empty(Run(new DayZMemoryPointsRule(), new P3DModel([Cube(0)])));
        Assert.Equal(Severity.Error, Assert.Single(Run(new DayZMemoryPointsRule(), new P3DModel([Cube(0)]), ModelKind.Weapon)).Severity);
    }

    [Fact]
    public void ShadowVolumeMustBeClosedAndSharp()
    {
        var closed = Cube(10000);
        var issues = Run(new ShadowVolumeRule(), new P3DModel([Cube(0), closed]));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
        Assert.Contains(issues, i => i.Message.Contains("smooth edge"));        // the cube has 3 sharp edges of 12
        Assert.Contains(issues, i => i.Message.Contains("texture or material")); // it is textured

        var open = OpsTestHelpers.RoundTrip(Ops.FaceOps.Remove(closed, OpsTestHelpers.MaskOf(6, 0)));
        Assert.Contains(Run(new ShadowVolumeRule(), new P3DModel([Cube(0), open])), i => i.Severity == Severity.Error && i.Message.Contains("not closed"));
    }

    [Fact]
    public void LodsMustGetSimpler()
    {
        var small = OpsTestHelpers.RoundTrip(Ops.FaceOps.Remove(Cube(1), OpsTestHelpers.MaskOf(6, 0, 1)));
        var issues = Run(new LodOrderRule(), new P3DModel([small with { Resolution = 0 }, Cube(1)]));
        var w = Assert.Single(issues);
        Assert.Equal(1, w.LodIndex);
        Assert.Empty(Run(new LodOrderRule(), new P3DModel([Cube(0), small])));
    }

    [Fact]
    public void BoneWeightsBelowFullAreReported()
    {
        // "top": points 2, 3 at 100 %, 6 at 50 %, 7 at ~0 %
        var issues = Run(new BoneWeightsRule(), new P3DModel([Cube(0)]), skeleton: ["top", "Component01"]);
        Assert.Empty(issues); // Component01 holds every point at 100 %: the sums are ≥ 100 %
        var w = Assert.Single(Run(new BoneWeightsRule(), new P3DModel([Cube(0)]), skeleton: ["top"]));
        Assert.Equal([6, 7], w.Points!);
    }

    [Fact]
    public void AnimatedPartsAndHiddenSelectionsInEveryLod()
    {
        var model = new P3DModel([Cube(0), Cube(1), Cube(1e13f)]);
        var bones = Run(new BonesInLodsRule(), model, ModelKind.Building, animated: ["top", "door1"]);
        Assert.Equal([0, 1, 2], bones.Select(i => i.LodIndex).ToArray());
        Assert.All(bones, i => Assert.Contains("door1", i.Message));
        Assert.Equal(Severity.Info, bones[1].Severity);   // a far LOD may drop a part
        Assert.Equal(Severity.Warning, bones[2].Severity); // the geometry of a building door must move

        // "camo" is in no LOD: another model of the item uses it (clothing), not this one's problem here
        Assert.Empty(Run(new HiddenSelectionsRule(), model, hidden: ["top", "camo"]));
        var partial = new P3DModel([Cube(0), Cube(1) with { Tags = [.. Cube(1).Tags.Where(t => t is not SelectionTag { Name: "top" })] }]);
        var hidden = Run(new HiddenSelectionsRule(), partial, hidden: ["top", "camo"]);
        Assert.Equal([1], hidden.Select(i => i.LodIndex).ToArray());
        Assert.All(hidden, i => Assert.Equal(Severity.Error, i.Severity));
    }

    [Fact]
    public void CollapsedUvsOnTexturedFaces()
    {
        var lod = Cube(0);
        var uv = (float[])lod.FaceUV.Clone();
        for (int k = 0; k < 8; k++) uv[8 + k] = 0.5f; // face 1 (textured): every vertex on one UV point
        var issues = Run(new UvMappingRule(), new P3DModel([lod with { FaceUV = uv }]));
        Assert.Equal([1], Assert.Single(issues).Faces!);
        Assert.Empty(Run(new UvMappingRule(), new P3DModel([lod])));
    }
}

public class ValidationFixesTests
{
    [Fact]
    public void FixesRemoveTheirIssues()
    {
        var model = new P3DModel([OpsTestHelpers.RichCube() with { Resolution = 0 }, OpsTestHelpers.RichCube() with { Resolution = 10000 }]);
        var ctx = new ValidationContext { Model = model, SkeletonBones = ["top"] };
        var fixable = Validator.Run(ctx, [new ShadowVolumeRule(), new BoneWeightsRule()]).Where(i => i.CanFix).ToList();
        Assert.Equal(4, fixable.Count); // shadow: smooth edges + textured; weights of both LODs (both have "top")
        foreach (var issue in Validator.Run(ctx, [new ShadowVolumeRule(), new BoneWeightsRule()]).Where(i => i.CanFix))
            model = ValidationFixes.Apply(model, issue);
        var after = Validator.Run(new ValidationContext { Model = model, SkeletonBones = ["top"] }, [new ShadowVolumeRule(), new BoneWeightsRule()]);
        Assert.Empty(after);
        Assert.Equal(1f, Weights.Decode(model.Lods[0].Selection("top")!.Points[6]));
        Assert.Equal("", model.Lods[1].Texture(0));
    }
}
