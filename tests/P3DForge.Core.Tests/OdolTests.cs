using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Scene;

namespace P3DForge.Core.Tests;

/// <summary>Binarized (ODOL) game models from P:\DZ — read only, nothing is copied into the repository.</summary>
public class OdolTests(ITestOutputHelper output)
{
    const string Root = @"P:\DZ";

    static List<string> Files(string sub, int max)
    {
        var dir = Path.Combine(Root, sub);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.p3d", SearchOption.AllDirectories).Take(max).ToList();
    }

    [Fact]
    public void Layout_matches_the_lod_address_table()
    {
        var files = Files("structures\\furniture", 120).Concat(Files("weapons", 120)).ToList();
        Assert.SkipWhen(files.Count == 0, "P:\\DZ not available");
        var failures = new List<string>();
        int odol = 0;
        foreach (var f in files)
        {
            var data = File.ReadAllBytes(f);
            if (!Odol.IsOdol(data)) continue;
            odol++;
            if (Odol.Verify(data) is { Count: > 0 } p) failures.Add($"{f}: {p[0]}");
        }
        output.WriteLine($"{odol} ODOL files verified, {failures.Count} failures");
        Assert.True(odol > 0);
        Assert.Empty(failures);
    }

    /// <summary>DayZ Tools write version 53 (older) and 55 (current) — mods on the work drive have both.</summary>
    [Theory]
    [InlineData("odol53", 53)]
    [InlineData("odol55", 55)]
    public void Other_dayz_versions_read_exactly(string key, int version)
    {
        var data = File.ReadAllBytes(TestData.Local(key));
        Assert.Equal(version, BitConverter.ToInt32(data, 4));
        Assert.Empty(Odol.Verify(data));
        Assert.True(Odol.Read(data).Lods.Any(l => l.FaceCount > 0));
    }

    /// <summary>The current DayZ Tools binarize writes version 56 (testdata: the sandbox building binarized by it).</summary>
    [Fact]
    public void Version_56_of_current_dayz_tools_reads_exactly()
    {
        var data = File.ReadAllBytes(TestData.Need(TestData.Path_("testdata", "odol56_building.p3d")));
        Assert.Equal(56, BitConverter.ToInt32(data, 4));
        var trace = new List<string>();
        Odol.Trace = (step, pos) => trace.Add($"{step}@{pos}");
        try
        {
            var problems = Odol.Verify(data);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems) + Environment.NewLine + string.Join(" ", trace.Take(40)));
        }
        finally
        {
            Odol.Trace = null;
        }
        var model = Odol.Read(data);
        var source = Mlod.Read(File.ReadAllBytes(TestData.Sample("Test_Building_sample_building.p3d")), null);
        Assert.Equal(source.Lods.Length, model.Lods.Length);
        // binarize splits some faces (non-planar quads, sections)
        Assert.InRange(model.Lods[0].FaceCount, source.Lods[0].FaceCount, source.Lods[0].FaceCount * 1.1);
    }

    /// <summary>Same conventions as MLOD: closed meshes have a negative signed volume and normals follow the faces.</summary>
    [Fact]
    public void Converted_lod_uses_mlod_winding()
    {
        var file = Path.Combine(Root, @"structures\furniture\decoration\box_c\box_c.p3d");
        Assert.SkipWhen(!File.Exists(file), "box_c.p3d not available");
        var model = Odol.Read(File.ReadAllBytes(file));
        var lod = model.Lods[ProxyPlacement.DisplayLodIndex(model)];
        Assert.Equal(6, lod.FaceCount);
        Assert.True(SignedVolume(lod) < 0);
        Assert.All(Enumerable.Range(0, lod.FaceCount), f => Assert.True(NormalAgrees(lod, f)));
        Assert.Contains(lod.Strings, s => s.EndsWith(".paa", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lod.Strings, s => s.StartsWith('\\'));
    }

    [Fact]
    public void Proxies_become_proxy_selections()
    {
        var withProxy = Files("structures", 400)
            .Select(f => (File: f, Data: File.ReadAllBytes(f)))
            .Where(x => Odol.IsOdol(x.Data))
            .Select(x => Odol.Read(x.Data))
            .SelectMany(m => m.Lods)
            .FirstOrDefault(ProxyPlacement.HasProxies);
        Assert.SkipWhen(withProxy is null, "no ODOL model with proxies found");
        var placed = ProxyPlacement.Find(withProxy!);
        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.True(Proxies.IsProxyName(p.Name) && p.Path.StartsWith('\\')));
    }

    [Fact]
    public void Proxy_loader_draws_binarized_models()
    {
        var file = Path.Combine(Root, @"structures\furniture\decoration\box_c\box_c.p3d");
        Assert.SkipWhen(!File.Exists(file), "box_c.p3d not available");
        var r = ProxyFiles.Load(file);
        Assert.Null(r.Error);
        Assert.True(r.Binarized);
        Assert.True(r.Model!.Lods[r.LodIndex].FaceCount > 0);
    }

    static double SignedVolume(Lod l)
    {
        double v = 0;
        var p = l.Positions;
        for (int f = 0; f < l.FaceCount; f++)
            for (int t = 1; t + 1 < l.FaceVertCount[f]; t++)
            {
                int a = (int)l.FacePoints[f * 4] * 3, b = (int)l.FacePoints[f * 4 + t] * 3, c = (int)l.FacePoints[f * 4 + t + 1] * 3;
                v += p[a] * (p[b + 1] * p[c + 2] - p[b + 2] * p[c + 1]) - p[a + 1] * (p[b] * p[c + 2] - p[b + 2] * p[c])
                     + p[a + 2] * (p[b] * p[c + 1] - p[b + 1] * p[c]);
            }
        return v / 6;
    }

    static bool NormalAgrees(Lod l, int f)
    {
        var p = l.Positions;
        int a = (int)l.FacePoints[f * 4] * 3, b = (int)l.FacePoints[f * 4 + 1] * 3, c = (int)l.FacePoints[f * 4 + 2] * 3;
        double ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
        double vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
        double gx = uy * vz - uz * vy, gy = uz * vx - ux * vz, gz = ux * vy - uy * vx;
        int n = (int)l.FaceNormals[f * 4] * 3;
        return gx * l.Normals[n] + gy * l.Normals[n + 1] + gz * l.Normals[n + 2] > 0;
    }
}
