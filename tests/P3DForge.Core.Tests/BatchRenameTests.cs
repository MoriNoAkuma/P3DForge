using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class BatchRenameTests
{
    static readonly ReplaceOptions Contains = new() { Match = PathMatch.Substring };

    [Fact]
    public void ModelsGetNewTexturesMaterialsAndProxyPaths()
    {
        var lod = OpsTestHelpers.RichCube() with { Resolution = 0 };
        lod = lod with { Tags = lod.Tags.Add(new SelectionTag(1, @"proxy:\dz\a\gun.001", new byte[8], new byte[6])) };
        var bytes = Mlod.Write(new P3DModel([lod]));
        var (next, count) = BatchRename.Model(bytes, Attributes.Replacer(@"dz\", @"mymod\", Contains), Contains);
        Assert.NotNull(next);
        var m = Mlod.Read(next, null);
        Assert.Equal(@"mymod\a_co.paa", m.Lods[0].Texture(0));
        Assert.Equal(@"mymod\a.rvmat", m.Lods[0].Material(0));
        Assert.NotNull(m.Lods[0].Selection(@"proxy:\mymod\a\gun.001"));
        Assert.True(count >= 7); // 3 textured faces × (texture + material) + the proxy
        Assert.Null(BatchRename.Model(bytes, Attributes.Replacer("nothing", "x", Contains), Contains).Bytes);
    }

    [Fact]
    public void TextsChangeOnlyInsideQuotes()
    {
        const string cfg = """class dz_thing { model="\dz\gear\thing.p3d"; hiddenSelectionsTextures[]={"dz\gear\data\thing_co.paa","#(argb,8,8,3)color(1,0,0,1)"}; };""";
        var (text, n) = BatchRename.Text(cfg, Attributes.Replacer(@"dz\gear", @"mymod\gear", Contains));
        Assert.Equal(2, n);
        Assert.Contains("""model="\mymod\gear\thing.p3d" """.TrimEnd(), text);
        Assert.Contains(""" "mymod\gear\data\thing_co.paa" """.Trim(), text);
        Assert.StartsWith("class dz_thing", text); // the class name is not a path
        // a prefix match ignores the leading backslash and keeps it
        var (prefixed, _) = BatchRename.Text(cfg, Attributes.Replacer(@"dz\gear", "x", new ReplaceOptions { Match = PathMatch.Prefix }));
        Assert.Contains("""model="\x\thing.p3d" """.TrimEnd(), prefixed);
    }
}
