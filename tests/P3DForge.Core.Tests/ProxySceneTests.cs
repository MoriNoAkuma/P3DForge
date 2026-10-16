using System.Numerics;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Scene;
using Xunit;

namespace P3DForge.Core.Tests;

public class ProxySceneTests
{
    static void Near(Matrix4x4 expected, Matrix4x4 actual, float eps = 1e-5f)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= eps, $"[{r},{c}] expected {expected[r, c]} got {actual[r, c]}\n{expected}\n{actual}");
    }

    static void Near(Vector3 expected, Vector3 actual, float eps = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= eps, $"expected {expected} got {actual}");

    static Matrix4x4 Tri(Vec3 a, Vec3 b, Vec3 c) => ProxyPlacement.FromTriangle(a, b, c) ?? throw new InvalidOperationException("degenerate");

    /// <summary>A LOD with one proxy whose triangle points are replaced by a, b, c.</summary>
    static Lod LodWithProxy(string path, Vec3 a, Vec3 b, Vec3 c, int index = 1)
    {
        var (lod, _) = Proxies.Create(Lod.Empty(0f), path, index);
        float[] p = [(float)a.X, (float)a.Y, (float)a.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)c.X, (float)c.Y, (float)c.Z];
        return lod with { Positions = p };
    }

    [Fact]
    public void CanonicalTriangleIsIdentity()
    {
        var (lod, name) = Proxies.Create(Lod.Empty(0f), @"\dz\x\box", null);
        var placed = Assert.Single(ProxyPlacement.Find(lod));
        Assert.Equal(name, placed.Name);
        Assert.Equal(@"\dz\x\box", placed.Path);
        Assert.Equal(1, placed.Index);
        Assert.Equal(0, placed.Face);
        Near(Matrix4x4.Identity, placed.Transform);
    }

    [Fact]
    public void TranslatedTriangleTranslates()
    {
        var (lod, _) = Proxies.Create(Lod.Empty(0f), @"\a\b", 7, new Vec3(1, 2, 3));
        var m = Assert.Single(ProxyPlacement.Find(lod)).Transform;
        Near(Matrix4x4.CreateTranslation(1, 2, 3), m);
        Near(new Vector3(1.5f, 2, 3), Vector3.Transform(new Vector3(0.5f, 0, 0), m));
    }

    [Fact]
    public void ScaleOfTheTriangleIsIgnored()
    {
        // 5x bigger triangle, same orientation: the model is placed at unit scale (binarize normalizes the axes)
        var m = Tri(new Vec3(0, 0, 0), new Vec3(0, 10, 0), new Vec3(0, 0, 5));
        Near(Matrix4x4.Identity, m);
        // vertex order does not matter (angles decide the roles)
        Near(m, Tri(new Vec3(0, 0, 5), new Vec3(0, 0, 0), new Vec3(0, 10, 0)));
        Near(m, Tri(new Vec3(0, 10, 0), new Vec3(0, 0, 5), new Vec3(0, 0, 0)));
    }

    [Fact]
    public void YawedTriangleRotatesAboutUp()
    {
        // forward (short leg) along +X, up along +Y
        var m = Tri(new Vec3(0, 0, 0), new Vec3(0, 2, 0), new Vec3(1, 0, 0));
        Near(new Vector3(1, 0, 0), Vector3.TransformNormal(Vector3.UnitZ, m)); // model forward → host +X
        Near(new Vector3(0, 1, 0), Vector3.TransformNormal(Vector3.UnitY, m)); // up stays up
        Near(new Vector3(0, 0, -1), Vector3.TransformNormal(Vector3.UnitX, m)); // model right → host -Z
        Assert.Equal(1f, m.GetDeterminant(), 4); // a rotation, never a mirror
    }

    [Fact]
    public void TiltedAndTranslatedTriangle()
    {
        // up tilted 45° towards +Z, forward perpendicular to it, origin at (5, 1, -2)
        var o = new Vec3(5, 1, -2);
        double s = Math.Sqrt(0.5);
        var up = new Vec3(0, s, s) * 3;
        var fwd = new Vec3(0, -s, s) * 1.5;
        var m = Tri(o, o + up, o + fwd);
        Near(new Vector3(5, 1, -2), m.Translation);
        Near(new Vector3(0, (float)s, (float)s), Vector3.TransformNormal(Vector3.UnitY, m));
        Near(new Vector3(0, -(float)s, (float)s), Vector3.TransformNormal(Vector3.UnitZ, m));
        Near(new Vector3(1, 0, 0), Vector3.TransformNormal(Vector3.UnitX, m));
        Assert.Equal(1f, m.GetDeterminant(), 4);
    }

    [Fact]
    public void NonRightTriangleIsOrthonormalized()
    {
        // forward leg not perpendicular to up (origin angle 107°): Z is re-derived from X × Y (a3ob / Buldozer convention)
        var m = Tri(new Vec3(0, 0, 0), new Vec3(0, 2, 0), new Vec3(0, -0.3, 1));
        Near(new Vector3(0, 1, 0), Vector3.TransformNormal(Vector3.UnitY, m));
        Near(new Vector3(0, 0, 1), Vector3.TransformNormal(Vector3.UnitZ, m));
        Assert.Equal(1f, m.GetDeterminant(), 4);
    }

    [Fact]
    public void DegenerateProxyIsSkipped()
    {
        var lod = LodWithProxy(@"\a\b", new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 2, 0));
        Assert.Empty(ProxyPlacement.Find(lod));
        Assert.True(ProxyPlacement.HasProxies(lod));
    }

    [Fact]
    public void PositionsOverrideIsUsed()
    {
        var (lod, _) = Proxies.Create(Lod.Empty(0f), @"\a\b", 1);
        var moved = (float[])lod.Positions.Clone();
        for (int i = 0; i < 3; i++) moved[i * 3] += 4;
        Near(new Vector3(4, 0, 0), Assert.Single(ProxyPlacement.Find(lod, moved)).Transform.Translation);
    }

    // ---- nesting ----------------------------------------------------------------------------------------

    static PlacedProxy Placed(string name, Matrix4x4 m) =>
        new($"proxy:{name}.001", name, 1, 0, m);

    [Fact]
    public void NestedTransformsCompose()
    {
        // parent: yawed so its forward is host +X, at (10, 0, 0); child: 1 m in front of the parent's origin
        var parent = Placed(@"\p", Tri(new Vec3(10, 0, 0), new Vec3(10, 2, 0), new Vec3(11, 0, 0)));
        var child = Placed(@"\c", Matrix4x4.CreateTranslation(0, 0, 1));
        (string, IReadOnlyList<PlacedProxy>?) Look(string path) => path == @"\p" ? ("p", new[] { child }) : ("c", null);
        var flat = ProxyFlatten.Flatten([parent], Look);
        Assert.Equal(2, flat.Count);
        Assert.Equal(0, flat[0].Depth);
        Assert.Equal(1, flat[1].Depth);
        Assert.Equal(0, flat[1].Parent);
        Assert.Equal(0, flat[1].Root);
        Assert.Equal(-1, flat[1].Face);
        Assert.Equal("proxy:\\p.001 > proxy:\\c.001", flat[1].Name);
        Near(new Vector3(11, 0, 0), Vector3.Transform(Vector3.Zero, flat[1].Transform));
        // the child's own forward is also host +X
        Near(new Vector3(1, 0, 0), Vector3.TransformNormal(Vector3.UnitZ, flat[1].Transform));
    }

    [Fact]
    public void NestingIsCycleSafeAndDepthLimited()
    {
        // a → b → a … : every level is listed once, the cycle is not expanded again
        var a = Placed(@"\a", Matrix4x4.CreateTranslation(1, 0, 0));
        var b = Placed(@"\b", Matrix4x4.CreateTranslation(0, 1, 0));
        (string, IReadOnlyList<PlacedProxy>?) Look(string p) => p == @"\a" ? ("a", new[] { b }) : ("b", new[] { a });
        var flat = ProxyFlatten.Flatten([a], Look);
        Assert.Equal(3, flat.Count); // a, a>b, a>b>a (not expanded: a is already on the chain)
        Assert.Equal(new[] { 0, 1, 2 }, flat.Select(f => f.Depth).ToArray());

        // self-reference of the edited file
        var self = Placed(@"\self", Matrix4x4.Identity);
        Assert.Single(ProxyFlatten.Flatten([self], _ => ("self", new[] { self }), rootKey: "SELF"));

        // a long chain stops at the depth limit
        var chain = Enumerable.Range(0, 8).Select(i => Placed($@"\n{i}", Matrix4x4.CreateTranslation(1, 0, 0))).ToArray();
        var deep = ProxyFlatten.Flatten([chain[0]], p =>
        {
            int i = int.Parse(p[2..]);
            return ($"n{i}", i + 1 < chain.Length ? new[] { chain[i + 1] } : null);
        });
        Assert.Equal(ProxyFlatten.DefaultMaxDepth + 1, deep.Count);
        Near(new Vector3(4, 0, 0), deep[^1].Transform.Translation);

        // instance cap
        var many = Enumerable.Range(0, 50).Select(i => Placed($@"\m{i}", Matrix4x4.Identity)).ToArray();
        Assert.Equal(10, ProxyFlatten.Flatten(many, p => (p, null), maxInstances: 10).Count);
    }

    // ---- files ------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveProxyPaths()
    {
        Assert.Equal(@"P:\dz\x\box.p3d", ProxyFiles.Resolve(@"\dz\x\box", @"P:\"));
        Assert.Equal(@"P:\dz\x\box.p3d", ProxyFiles.Resolve(@"dz\x\box", @"P:\"));
        Assert.Equal(@"P:\dz\x\box.p3d", ProxyFiles.Resolve("dz/x/box", "P:"));
        Assert.Equal(@"P:\dz\x\box.P3D", ProxyFiles.Resolve(@"\dz\x\box.P3D", @"P:\"));
        Assert.Equal(@"D:\work\dz\x\box.p3d", ProxyFiles.Resolve(@"\\dz\x\\box", @"D:\work\"));
        Assert.Equal(@"C:\abs\m.p3d", ProxyFiles.Resolve(@"C:\abs\m", @"P:\"));
        Assert.Equal(ProxyFiles.Key(@"P:\DZ\X\Box.p3d"), ProxyFiles.Key(@"p:\dz\x\box.P3D"));
    }

    [Fact]
    public void LoadReportsMissingOdolAndBrokenFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "p3dforge-proxytests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = ProxyFiles.Load(Path.Combine(dir, "nope.p3d"));
            Assert.True(missing.Missing);
            Assert.Null(missing.Model);
            Assert.NotNull(missing.Error);

            var odol = Path.Combine(dir, "odol.p3d");
            File.WriteAllBytes(odol, [.. "ODOL"u8, 7, 0, 0, 0, 1, 2, 3]);
            var r = ProxyFiles.Load(odol);
            Assert.Equal(ProxyFiles.OdolError, r.Error);
            Assert.False(r.Missing);

            var broken = Path.Combine(dir, "broken.p3d");
            File.WriteAllBytes(broken, [.. "MLOD"u8, 1, 1, 0, 0, 5, 0, 0, 0]);
            r = ProxyFiles.Load(broken);
            Assert.Null(r.Model);
            Assert.StartsWith("cannot read", r.Error);

            // valid MLOD: memory LOD first, the visual LOD is chosen for display
            var good = Path.Combine(dir, "good.p3d");
            var visual = OpsTestHelpers.RichCube() with { Resolution = 1f };
            var memory = Lod.Empty(LodNames.ResolutionFor("memory"));
            Mlod.WriteFile(good, new P3DModel([memory, visual]));
            r = ProxyFiles.Load(good);
            Assert.Null(r.Error);
            Assert.NotNull(r.Model);
            Assert.Equal(1, r.LodIndex);
            Assert.True(r.Length > 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DisplayLodIsMostDetailedResolution()
    {
        var m = new P3DModel([Lod.Empty(LodNames.ResolutionFor("geometry")), Lod.Empty(2f), Lod.Empty(1f), Lod.Empty(3f)]);
        Assert.Equal(2, ProxyPlacement.DisplayLodIndex(m));
        Assert.Equal(0, ProxyPlacement.DisplayLodIndex(new P3DModel([Lod.Empty(LodNames.ResolutionFor("geometry"))])));
    }

    // ---- picking ----------------------------------------------------------------------------------------

    [Fact]
    public void RayHitsPlacedModel()
    {
        var cube = OpsTestHelpers.RichCube();
        var bounds = ProxyPlacement.Bounds(cube)!.Value;
        var size = bounds.Max - bounds.Min;
        Assert.True(size.X > 0 && size.Y > 0 && size.Z > 0);
        // placed 10 m along +X; ray from far -X along +X at the cube's centre height
        var m = Matrix4x4.CreateTranslation(10, 0, 0);
        var center = (bounds.Min + bounds.Max) / 2;
        var o = new Vector3(-50, center.Y, center.Z);
        var t = ProxyRay.RayPlaced(cube, bounds, m, o, Vector3.UnitX);
        Assert.NotNull(t);
        Assert.Equal(60 + bounds.Min.X, t!.Value, 3);
        // a ray passing above misses (box reject)
        Assert.Null(ProxyRay.RayPlaced(cube, bounds, m, new Vector3(-50, bounds.Max.Y + 1, center.Z), Vector3.UnitX));
        // rotated placement: forward along +X, the hit distance is the same rigid distance
        var rot = Tri(new Vec3(10, 0, 0), new Vec3(10, 2, 0), new Vec3(11, 0, 0));
        var hit = ProxyRay.RayPlaced(cube, bounds, rot, new Vector3(10.5f, 50, -0.5f), -Vector3.UnitY);
        Assert.NotNull(hit);
        Assert.Equal(50 - bounds.Max.Y, hit!.Value, 3);
    }

    [Fact]
    public void SampleBuildingProxies()
    {
        var model = Mlod.ReadFile(TestData.Sample("Test_Building_sample_building.p3d"));
        var lod = model.Lods[0];
        var list = ProxyPlacement.Find(lod);
        Assert.Equal(11, list.Count);
        // the sample stores every proxy triangle hidden (point flag) — the Objects panel marks them "hidden"
        var hidden = Selection.HiddenPoints(lod);
        Assert.All(list, p => Assert.True(hidden[p.A] != 0 && hidden[p.B] != 0 && hidden[p.C] != 0));
        // the window proxy is placed with the canonical triangle orientation (no rotation)
        var window = list.Single(p => p.Path.Contains("Ind_Workshop_Window", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1f, window.Transform.M11, 3);
        Assert.Equal(1f, window.Transform.M22, 3);
        Assert.Equal(1f, window.Transform.M33, 3);
        // every placement is a rigid rotation (orthonormal, right-handed: det = +1)
        Assert.All(list, p => Assert.Equal(1f, p.Transform.GetDeterminant(), 3));
    }

    [Fact]
    public void AlternativesLookNearTheEditedModel()
    {
        var alts = ProxyFiles.Alternatives(@"\mycar\proxy\door", @"P:\MyCar_test\car.p3d");
        Assert.Equal(@"P:\MyCar_test\mycar\proxy\door.p3d", alts[0]);
        Assert.Equal(@"P:\MyCar_test\proxy\door.p3d", alts[1]);
        Assert.Equal(@"P:\MyCar_test\door.p3d", alts[2]);
        Assert.DoesNotContain(@"P:\door.p3d", alts); // bare file names only in the model's own folder
        Assert.Empty(ProxyFiles.Alternatives(@"C:\x\door.p3d", @"P:\a\b.p3d"));

        var dir = Path.Combine(Path.GetTempPath(), "p3dforge_alt_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "mod", "proxy"));
            var host = Path.Combine(dir, "mod", "host.p3d");
            var odolRoot = Path.Combine(dir, "root");
            Directory.CreateDirectory(Path.Combine(odolRoot, "pfx", "proxy"));
            File.WriteAllBytes(Path.Combine(odolRoot, "pfx", "proxy", "door.p3d"), "ODOL\u0007\0\0\0"u8.ToArray());
            var primary = ProxyFiles.Resolve(@"\pfx\proxy\door", odolRoot);
            var r = ProxyFiles.LoadPreferSource(primary, @"\pfx\proxy\door", host, out bool alt);
            Assert.False(alt);
            Assert.Equal(ProxyFiles.OdolError, r.Error);

            Mlod.WriteFile(Path.Combine(dir, "mod", "proxy", "door.p3d"), new P3DModel([OpsTestHelpers.RichCube()]));
            r = ProxyFiles.LoadPreferSource(primary, @"\pfx\proxy\door", host, out alt);
            Assert.True(alt);
            Assert.NotNull(r.Model);
            Assert.Equal(Path.Combine(dir, "mod", "proxy", "door.p3d"), r.File, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
