using System.Collections.Immutable;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Topology;
using P3DForge.Core.Validation;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class ValidationTests
{
    static Lod Box(double s, Vec3 at = default) => Primitives.CreateBox(Lod.Empty(0), new BoxParams(s, s, s) { At = at });

    static Lod Merge(Lod a, Lod b) => Structure.Append(a, Structure.Extract(b, All(b.PointCount), All(b.FaceCount)), selectNew: false);

    static Lod WithTexture(Lod lod, string texture, string material = "") => Structure.SetFaceTextures(lod, All(lod.FaceCount), texture, material);

    static float Geo => LodNames.ResolutionFor("geometry");

    /// <summary>A clean model: textured visual box + Geometry box with Component01, mass and autocenter.</summary>
    static P3DModel Clean()
    {
        var visual = WithTexture(Box(1), @"dz\test\data\box_co.paa", @"dz\test\data\box.rvmat");
        var geo = Box(1) with { Resolution = Geo };
        geo = HullOps.CreateComponentSelections(geo, null, out _);
        geo = Mass.Distribute(geo, All(geo.PointCount), 50, MassDistribution.Equal, out _);
        geo = NamedProperties.Set(geo, "autocenter", "0");
        return new P3DModel([visual, geo]);
    }

    static List<ValidationIssue> Run(P3DModel model, Func<string, bool>? exists = null) =>
        Validator.Run(new ValidationContext { Model = model, Resolve = p => @"P:\" + p.TrimStart('\\'), Exists = exists ?? (_ => true) });

    [Fact]
    public void Clean_model_has_no_issues()
    {
        var issues = Run(Clean());
        Assert.True(issues.Count == 0, string.Join("\n", issues.Select(i => $"{i.Rule}: {i.Message}")));
        Assert.Equal("no issues", Validator.Summary(issues));
    }

    [Fact]
    public void Broken_model_reports_every_rule()
    {
        var m = Clean();
        // Visual LOD: absolute .tga texture on one box, a missing texture on another, one untextured face, an empty selection.
        var a = WithTexture(Box(1), @"C:\work\box_co.tga");
        var b = WithTexture(Box(1, new Vec3(3, 0, 0)), @"dz\test\data\missing_co.paa", @"dz\test\data\box.rvmat");
        var visual = Merge(a, b);
        visual = Structure.SetFaceTextures(visual, MaskOf(visual.FaceCount, 0), "", "");
        visual = visual with { Tags = visual.Tags.Add(new SelectionTag(1, "nothing", new byte[visual.PointCount], new byte[visual.FaceCount])) };
        // Geometry: an L-prism (not convex) + an open box, no ComponentXX, no mass, no autocenter.
        var l = TopologyTests.LPrism();
        var open = Box(1, new Vec3(5, 0, 0));
        var keep = All(open.FaceCount);
        keep[0] = 0;
        open = Structure.Compact(open, All(open.PointCount), keep);
        var geo = Merge(l, open) with { Resolution = Geo };
        // A degenerate quad (repeated point) in the geometry.
        var fp = (uint[])geo.FacePoints.Clone();
        fp[14 * 4 + 3] = fp[14 * 4 + 2]; // first face of the open box
        geo = geo with { FacePoints = fp };
        var dup = Box(1) with { Resolution = 0 }; // same resolution as the visual LOD
        var model = new P3DModel([visual, geo, dup]);

        var issues = Run(model, p => !p.Contains("missing", StringComparison.OrdinalIgnoreCase));
        string Dump() => string.Join("\n", issues.Select(i => $"{i.LodIndex} {i.Severity} {i.Rule}: {i.Message}"));
        ValidationIssue One(string rule, int lod) => issues.FirstOrDefault(i => i.Rule == rule && i.LodIndex == lod) ?? throw new Xunit.Sdk.XunitException($"no {rule} in LOD {lod}\n{Dump()}");

        Assert.Equal(Severity.Error, One("lod.duplicateResolution", 2).Severity);
        var abs = One("paths.absolute", 0);
        Assert.Equal(Severity.Error, abs.Severity);
        Assert.Equal(5, abs.Faces!.Length); // 6 faces of box a minus the untextured one
        Assert.Equal(Severity.Warning, One("textures.format", 0).Severity);
        var missing = One("paths.missing", 0);
        Assert.Contains("missing_co.paa", missing.Message);
        Assert.Equal(6, missing.Faces!.Length);
        Assert.Equal([0], One("faces.untextured", 0).Faces!);
        Assert.Contains("nothing", One("selections.empty", 0).Message);

        Assert.Equal(Severity.Warning, One("geometry.mass", 1).Severity);
        Assert.Equal(Severity.Error, One("components.notClosed", 1).Severity);
        Assert.Equal(Severity.Warning, One("components.notConvex", 1).Severity);
        Assert.Equal(Severity.Error, One("components.selections", 1).Severity);
        Assert.Equal(Severity.Error, One("faces.degenerate", 1).Severity);
        Assert.Equal(Severity.Info, One("geometry.autocenter", 1).Severity);
        Assert.True(One("components.notClosed", 1).CanSelect);

        // Sorted by LOD, errors first inside a LOD.
        for (int i = 1; i < issues.Count; i++)
        {
            Assert.True(issues[i - 1].LodIndex <= issues[i].LodIndex);
            if (issues[i - 1].LodIndex == issues[i].LodIndex) Assert.True(issues[i - 1].Severity >= issues[i].Severity, Dump());
        }
    }

    [Fact]
    public void Missing_geometry_out_of_date_components_and_massless_components()
    {
        var visualOnly = new P3DModel([WithTexture(Box(1), @"dz\a_co.paa", @"dz\a.rvmat")]);
        Assert.Contains(Run(visualOnly), i => i.Rule == "geometry.missing");

        var m = Clean();
        var geo = m.Lods[1];
        // Add a second box: Component01 no longer covers every component, and the new one has no mass.
        geo = Merge(geo, Box(1, new Vec3(4, 0, 0)));
        var issues = Run(m with { Lods = m.Lods.SetItem(1, geo) });
        var sel = issues.Single(i => i.Rule == "components.selections");
        Assert.Equal(Severity.Warning, sel.Severity);
        Assert.Equal(8, sel.Points!.Length);
        var cm = issues.Single(i => i.Rule == "geometry.componentMass");
        Assert.Equal(Severity.Info, cm.Severity);
    }

    sealed class Throwing : IValidationRule
    {
        public string Id => "test.throw";
        public string Title => "Throwing rule";
        public IEnumerable<ValidationIssue> Check(ValidationContext ctx) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void A_failing_rule_becomes_an_issue()
    {
        var issues = Validator.Run(new ValidationContext { Model = Clean() }, [new Throwing(), new AutocenterRule()]);
        var e = Assert.Single(issues);
        Assert.Equal(Severity.Error, e.Severity);
        Assert.Contains("boom", e.Message);
    }

    [Fact]
    public void Sample_building_validates_quickly()
    {
        string path = TestData.Sample("Test_Building_sample_building.p3d");
        var model = Mlod.ReadFile(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = Run(model);
        sw.Stop();
        TestContext.Current.SendDiagnosticMessage($"sample building: {Validator.Summary(issues)} in {sw.ElapsedMilliseconds} ms\n" +
            string.Join("\n", issues.Select(i => $"  [{i.LodIndex}] {i.Severity} {i.Rule}: {i.Message}")));
        int geo = model.Lods.ToList().FindIndex(l => LodNames.IsGeometry(l.Resolution));
        // Object Builder's Component01..15 match the closed components: no selection issue in Geometry.
        Assert.DoesNotContain(issues, i => i.LodIndex == geo && i.Rule == "components.selections");
        Assert.DoesNotContain(issues, i => i.Rule == "lod.duplicateResolution");
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void Btr_validates_quickly()
    {
        string path = TestData.Local("btr"); // a big vehicle as benchmark (54 MB, 12 LODs)
        var model = Mlod.ReadFile(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = Validator.Run(new ValidationContext { Model = model, Resolve = p => p.Length > 1 && p[1] == ':' ? p : @"P:\" + p.TrimStart('\\'), Exists = File.Exists });
        sw.Stop();
        TestContext.Current.SendDiagnosticMessage($"BTR: {Validator.Summary(issues)} in {sw.ElapsedMilliseconds} ms\n" +
            string.Join("\n", issues.Select(i => $"  [{i.LodIndex}] {i.Severity} {i.Rule}: {i.Message}")));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }
}
