using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

/// <summary>A real weapon of your own (testdata/local.json "weapon", skipped when absent): reduction never opens new edges (holes).</summary>
public class DecimateRealModelTests(Xunit.ITestOutputHelper output)
{
    static (int Open, int NonManifold) Edges(Lod lod)
    {
        var count = new Dictionary<long, int>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int n = lod.FaceVertCount[f];
            for (int k = 0; k < n; k++)
            {
                long a = lod.FacePoints[f * 4 + k], b = lod.FacePoints[f * 4 + (k + 1) % n];
                long key = a < b ? (a << 32) | b : (b << 32) | a;
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }
        return (count.Values.Count(c => c == 1), count.Values.Count(c => c > 2));
    }

    [Fact]
    public void ReductionOpensNoHoles()
    {
        var file = TestData.Local("weapon");
        var lod = Mlod.Read(File.ReadAllBytes(file), null).Lods[0];
        output.WriteLine($"source: {lod.FaceCount} faces, edges {Edges(lod)}");
        var tri = FaceOps.Triangulate(lod, MeshMath.Ones(lod.FaceCount)).Lod;
        output.WriteLine($"triangulated: {tri.FaceCount} faces, edges {Edges(tri)}");
        foreach (var r in new[] { 0.5, 0.25, 0.12 })
        {
            var low = Decimate.Run(lod, r, out var st);
            var (open, nonManifold) = Edges(low);
            output.WriteLine($"{r}: {low.FaceCount} faces, edges ({open}, {nonManifold}), locked {st.LockedPoints}");
            Assert.True(open <= Edges(lod).Open, $"{r}: {open} open edges, the source has {Edges(lod).Open}");
            Assert.Equal(0, nonManifold);
        }
    }
}
