using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Tests;

public class BinarizeLogTests
{
    /// <summary>The LOD layout of the rifle in testdata/binarize_sample.log (a real DayZ Tools binarize run, paths renamed).</summary>
    static P3DModel Rifle() => new([.. new float[] { 0, 1, 2, 3, 4, 5, 1100, 1e13f, 1e15f, 6e15f, 7e15f, 1.0001f }.Select(Lod.Empty)]);

    [Fact]
    public void ParsesARealLog()
    {
        var log = File.ReadAllText(TestData.Need(TestData.Path_("testdata", "binarize_sample.log")));
        var m = BinarizeLog.Parse(log, Rifle());
        Assert.DoesNotContain(m, x => x.Text.Contains("No entry") || x.Text.Contains("Terrain grid") || x.Text.Contains("jenkins"));
        // one entry per LOD, every vertex listed (binarize writes a line per vertex and face)
        var weights = m.Where(x => x.Text.Contains("sum of weights")).ToList();
        Assert.Equal([1, 2], weights.Select(x => x.Lod).Order().ToArray());
        Assert.Contains(251, weights.Single(x => x.Lod == 1).Points!);
        Assert.Contains(107, weights.Single(x => x.Lod == 2).Points!);
        Assert.Equal(2, m.Count(x => x.Error && x.Text.StartsWith("Material not loaded")));
        var uv2 = Assert.Single(m, x => x.Text.Contains("2nd UV set"));
        Assert.Equal(6, uv2.Lod); // "Warnings in …:VIEW_PILOT"
        Assert.Contains(m, x => x.Text.StartsWith("UV mapping too varied") && x.Count > 5);
        Assert.Equal(-1, Assert.Single(m, x => x.Text.Contains("LODs not ordered by face count")).Lod);
        Assert.All(m.Where(x => x.Text.StartsWith("UV mapping too varied")), x => Assert.Equal(-1, x.Lod));
        // paths of the temporary copy are shortened to the file name
        Assert.DoesNotContain(m, x => x.Text.Contains(@"binz\src"));
    }
}
