using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Scene;

namespace P3DForge.Core.Tests;

/// <summary>
/// OBJ / FBX import, OBJ export and model merge. testdata/import was made with Blender 5.1 (default exports): a 1 m
/// cube "TestCube" at Blender (1, −2, 3) rotated 0.5 rad about Z with a textured material, and a 2 m plane "Floor".
/// </summary>
public class ImportTests
{
    static string File_(string name) => TestData.Need(TestData.Path_("testdata", "import", name));

    static Lod Import(string name)
    {
        var path = File_(name);
        var options = new ImportOptions { ZUp = name.Contains("zup"), Texture = Path.GetFileName };
        var r = name.EndsWith(".fbx") ? Fbx.Read(File.ReadAllBytes(path), options) : Obj.Read(File.ReadAllText(path), Path.GetDirectoryName(path), options);
        Assert.Empty(r.Warnings);
        return MeshMath.AppendSafe(Lod.Empty(0), r.Fragment, selectNew: false);
    }

    static (double X, double Y, double Z) Centre(Lod lod, string selection)
    {
        var s = lod.Selection(selection)!;
        double x = 0, y = 0, z = 0;
        int n = 0;
        for (int i = 0; i < lod.PointCount; i++)
            if (s.Points[i] != 0)
            {
                x += lod.Positions[i * 3];
                y += lod.Positions[i * 3 + 1];
                z += lod.Positions[i * 3 + 2];
                n++;
            }
        return (x / n, y / n, z / n);
    }

    static double Volume(Lod lod, byte[] faces)
    {
        double v = 0;
        var p = lod.Positions;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces[f] == 0) continue;
            for (int t = 1; t + 1 < lod.FaceVertCount[f]; t++)
            {
                int a = (int)lod.FacePoints[f * 4] * 3, b = (int)lod.FacePoints[f * 4 + t] * 3, c = (int)lod.FacePoints[f * 4 + t + 1] * 3;
                v += p[a] * (p[b + 1] * p[c + 2] - p[b + 2] * p[c + 1]) - p[a + 1] * (p[b] * p[c + 2] - p[b + 2] * p[c]) + p[a + 2] * (p[b] * p[c + 1] - p[b + 1] * p[c]);
            }
        }
        return v / 6;
    }

    [Theory]
    [InlineData("cube.obj")]
    [InlineData("cube_zup.obj")]
    [InlineData("cube.fbx")]
    public void Blender_cube_lands_like_the_arma_toolbox_puts_it(string file)
    {
        var lod = Import(file);
        Assert.Equal(7, lod.FaceCount);
        var (x, y, z) = Centre(lod, "TestCube");
        // Blender (1, −2, 3) → P3D (x, z, y) = (1, 3, −2)
        Assert.Equal(1, x, 3);
        Assert.Equal(3, y, 3);
        Assert.Equal(-2, z, 3);
        // closed and facing out the way MLOD faces do (negative signed volume of 1 m³)
        Assert.Equal(-1, Volume(lod, lod.Selection("TestCube")!.Faces), 3);
        Assert.Contains("tex_co.png", lod.Strings);
    }

    [Fact]
    public void Obj_and_fbx_give_the_same_points()
    {
        static List<string> Points(Lod l) => Enumerable.Range(0, l.PointCount)
            .Select(i => $"{N(l.Positions[i * 3])} {N(l.Positions[i * 3 + 1])} {N(l.Positions[i * 3 + 2])}").Order().ToList();
        static string N(float v) => (Math.Round(v, 3) + 0.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture); // no "-0.000"
        Assert.Equal(Points(Import("cube.obj")), Points(Import("cube.fbx")));
    }

    [Fact]
    public void Obj_export_round_trips()
    {
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams(2, 1, 3) { At = new Vec3(1, 2, 3) });
        box = Ops.Attributes.SetFaceTextureMaterial(box, MeshMath.Ones(box.FaceCount), "mod\\data\\box_co.paa", null);
        var (obj, mtl) = Obj.Write(box, null, "box", "box.mtl", t => t);
        Assert.Contains("map_Kd mod\\data\\box_co.paa", mtl);
        var back = MeshMath.AppendSafe(Lod.Empty(0), Obj.Read(obj, null, new ImportOptions()).Fragment, selectNew: false);
        Assert.Equal(box.FaceCount, back.FaceCount);
        for (int f = 0; f < box.FaceCount; f++)
            for (int j = 0; j < box.FaceVertCount[f]; j++)
            {
                for (int k = 0; k < 3; k++)
                    Assert.Equal(box.Positions[box.FacePoints[f * 4 + j] * 3 + k], back.Positions[back.FacePoints[f * 4 + j] * 3 + k], 4);
                Assert.Equal(box.FaceUV[f * 8 + j * 2], back.FaceUV[f * 8 + j * 2], 4);
                Assert.Equal(box.FaceUV[f * 8 + j * 2 + 1], back.FaceUV[f * 8 + j * 2 + 1], 4);
            }
    }

    [Fact]
    public void Merge_appends_by_resolution_adds_new_lods_and_renumbers_proxies()
    {
        var a = Proxies.Create(Primitives.CreateBox(Lod.Empty(1), new BoxParams(1, 1, 1)), "\\dz\\x\\mag", 1).Lod;
        var target = new P3DModel([a, Lod.Empty(1e13f)]);
        var b1 = Proxies.Create(Primitives.CreateBox(Lod.Empty(1), new BoxParams(1, 1, 1)), "\\dz\\x\\mag", 1).Lod;
        var b2 = Primitives.CreateBox(Lod.Empty(2), new BoxParams(1, 1, 1));
        var source = new P3DModel([b1, b2]);

        var r = Merge.Models(target, source, "scope");
        Assert.Equal(1, r.Appended);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.RenamedProxies);
        Assert.Equal([1f, 2f, 1e13f], r.Model.Lods.Select(l => l.Resolution));
        var lod1 = r.Model.Lods[0];
        Assert.Equal(a.FaceCount + b1.FaceCount, lod1.FaceCount);
        Assert.Equal(2, ProxyPlacement.Find(lod1).Count); // both proxies keep their own triangle
        Assert.Equal(b1.FaceCount, MeshMath.CountSet(lod1.Selection("scope")!.Faces));
        Assert.NotNull(r.Model.Lods[1].Selection("scope"));
    }
}
