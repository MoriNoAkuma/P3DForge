using System.Diagnostics;
using System.Numerics;
using System.Text;
using P3DForge.Core.Materials;

namespace P3DForge.Core.Tests;

public class MaterialsTests(ITestOutputHelper output)
{
    const string Super = """
        ambient[]={1,1,1,1};
        diffuse[]={1,1,1,1};
        forcedDiffuse[]={0,0,0,0};
        emmisive[]={0,0,0,1};
        specular[]={0.2,0.2,0.2,1};
        specularPower=50;
        PixelShaderID="Super";
        VertexShaderID="Super";
        renderFlags[]={"NoZWrite","AddBlend"};
        surfaceInfo="dz\data\data\penetration\metal.bisurf";
        class Stage1
        {
        	texture="dz\vehicles\x\data\body_nohq.paa";
        	uvSource="tex";
        	class uvTransform
        	{
        		aside[]={1,0,0};
        		up[]={0,1,0};
        		dir[]={0,0,0};
        		pos[]={0,0,0};
        	};
        };
        class Stage2
        {
        	texture="#(argb,8,8,3)color(0.5,0.5,0.5,1,DT)";
        	uvSource="tex";
        	class uvTransform
        	{
        		aside[]={4,0,0};
        		up[]={0,4,0};
        		dir[]={0,0,0};
        		pos[]={0,0,0};
        	};
        };
        class Stage3
        {
        	texture="#(argb,8,8,3)color(0,0,0,0,MC)";
        	uvSource="tex";
        	class uvTransform
        	{
        		aside[]={1,0,0};
        		up[]={0,1,0};
        		dir[]={0,0,0};
        		pos[]={0,0,0};
        	};
        };
        class Stage4
        {
        	texture="dz\vehicles\x\data\body_as.paa";
        	uvSource="tex1";
        	class uvTransform
        	{
        		aside[]={1,0,0};
        		up[]={0,1,0};
        		dir[]={0,0,0};
        		pos[]={0,0,0};
        	};
        };
        class Stage5
        {
        	texture="dz\vehicles\x\data\body_smdi.paa";
        	uvSource="tex";
        	Filter="Anizotropic";
        	class uvTransform
        	{
        		aside[]={1,0,0};
        		up[]={0,1,0};
        		dir[]={0,0,0};
        		pos[]={0,0,0};
        	};
        };
        class Stage6
        {
        	texture="#(ai,64,64,1)fresnel(1.36,0.001)";
        	uvSource="none";
        };
        class Stage7
        {
        	texture="dz\data\data\env_land_co.paa";
        	useWorldEnvMap="true";
        	uvSource="none";
        };
        class Fresnel
        {
        	mystery=0.200;
        	list[]={1,"two",{3,4.50}};
        };
        """;

    const string Multi = """
        ambient[]={1,1,1,1};
        diffuse[]={0.44999999,0.41999999,0.25,1};
        PixelShaderID="Multi";
        VertexShaderID="Multi";
        class TexGen0
        {
        	uvSource="tex";
        	class uvTransform
        	{
        		aside[]={1,0,0};
        		up[]={0,1,0};
        		dir[]={0,0,1};
        		pos[]={0,0,0};
        	};
        };
        class TexGen1
        {
        	uvSource="tex1";
        	class uvTransform
        	{
        		aside[]={2.7,0,0};
        		up[]={0,2.7,0};
        		dir[]={0,0,1};
        		pos[]={0.25,0.5,0};
        	};
        };
        class Stage0
        {
        	texture="dz\structures\x\data\matress_co.paa";
        	texGen="0";
        };
        class Stage1
        {
        	texture="dz\structures\x\data\table_co.paa";
        	texGen="1";
        };
        class Stage5
        {
        	texture="#(argb,8,8,3)color(0.15,0.01,1,1,DTSMDI)";
        	texGen=1;
        };
        class Stage11
        {
        	texture="#(argb,8,8,3)color(0.5,0.5,1,1,NOHQ)";
        	texGen="0";
        };
        """;

    [Fact]
    public void ParsesSuperShaderMaterial()
    {
        var m = Rvmat.Parse(Super);
        Assert.Empty(m.Warnings);
        Assert.Equal("Super", m.PixelShaderID);
        Assert.Equal("Super", m.VertexShaderID);
        Assert.Equal(new Rgba(0.2f, 0.2f, 0.2f, 1), m.Specular);
        Assert.Equal(new Rgba(0, 0, 0, 1), m.Emmisive);
        Assert.Equal(Rgba.Zero, m.ForcedDiffuse);
        Assert.Equal(50, m.SpecularPower);
        Assert.Equal(new[] { "NoZWrite", "AddBlend" }, m.RenderFlags);
        Assert.True(m.HasRenderFlag("nozwrite"));
        Assert.Equal(@"dz\data\data\penetration\metal.bisurf", m.SurfaceInfo);
        Assert.Equal(7, m.Stages.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, m.Stages.Select(s => s.Index));

        Assert.Equal(1, m.FindStageBySuffix("_nohq")!.Index);
        Assert.Equal(5, m.FindStageBySuffix("smdi")!.Index);
        Assert.Equal(2, m.FindStageBySuffix("_dt")!.Index);
        Assert.Equal(3, m.FindStageBySuffix("_mc")!.Index);
        Assert.Equal(4, m.FindStageBySuffix("_as")!.Index);
        Assert.Equal(6, m.FindStageBySuffix("fresnel")!.Index);
        Assert.Null(m.FindStageBySuffix("_ca"));
        Assert.Equal(7, m.FindStage(StageRole.Environment)!.Index);
        Assert.Equal(StageRole.Normal, m.Stage(1)!.Role);

        var dt = m.Stage(2)!;
        Assert.Equal(new UvTransform(new(4, 0, 0), new(0, 4, 0), Vector3.Zero, Vector3.Zero), dt.EffectiveUvTransform);
        Assert.Equal(new Vector2(2, 1), dt.EffectiveUvTransform.Apply(new(0.5f, 0.25f)));
        Assert.Equal(1, m.Stage(4)!.EffectiveUvSet);
        Assert.Equal(-1, m.Stage(6)!.EffectiveUvSet);
        Assert.Null(m.Stage(6)!.UvTransform);
        Assert.True(m.Stage(6)!.EffectiveUvTransform.IsIdentity);
        Assert.Equal("Anizotropic", m.Stage(5)!.Filter);

        Assert.True(ProceduralTexture.TryParseColor(dt.Texture, out var c, out var tag));
        Assert.Equal(new Rgba(0.5f, 0.5f, 0.5f, 1), c);
        Assert.Equal("DT", tag);
        Assert.True(ProceduralTexture.TryParseFresnel(m.Stage(6)!.Texture, out var n, out var k));
        Assert.Equal(1.36f, n);
        Assert.Equal(0.001f, k);

        var unknown = m.Root.Class("Fresnel")!;
        Assert.Equal("0.200", unknown.Get("mystery")!.Text);
        Assert.Equal(3, unknown.Get("list")!.Items.Count);
        Assert.Equal("true", m.Stage(7)!.Class.GetString("useWorldEnvMap"));
    }

    [Fact]
    public void TexGenDrivesEffectiveUvTransform()
    {
        var m = Rvmat.Parse(Multi);
        Assert.Empty(m.Warnings);
        Assert.Equal(2, m.TexGens.Count);
        Assert.Equal(4, m.Stages.Count);
        var s1 = m.Stage(1)!;
        Assert.Equal(1, s1.TexGen);
        Assert.Equal("tex1", s1.EffectiveUvSource);
        Assert.Equal(1, s1.EffectiveUvSet);
        var t = s1.EffectiveUvTransform;
        Assert.Equal(2.7f, t.Aside.X);
        Assert.Equal(new Vector2(2.7f * 0.5f + 0.25f, 0.5f), t.Apply(new(0.5f, 0)));
        Assert.Equal(1, m.Stage(5)!.TexGen); // numeric form
        Assert.Equal("_dtsmdi", m.Stage(5)!.Suffix);
        Assert.Equal(11, m.FindStageBySuffix("_nohq")!.Index);
        Assert.Equal(StageRole.Diffuse, m.Stage(0)!.Role);
        Assert.Equal(StageRole.Specular, m.Stage(5)!.Role);
        Assert.True(m.Stage(0)!.EffectiveUvTransform.IsIdentity);

        // texGen pointing at a missing TexGen falls back to the stage (identity)
        m.Stage(11)!.TexGen = 9;
        Assert.True(m.Stage(11)!.EffectiveUvTransform.IsIdentity);
        Assert.Equal("9", m.Stage(11)!.Class.Get("texGen")!.Text);
        Assert.Equal(ConfigValueKind.String, m.Stage(11)!.Class.Get("texGen")!.Kind);
        m.Stage(5)!.TexGen = 0;
        Assert.Equal(ConfigValueKind.Int, m.Stage(5)!.Class.Get("texGen")!.Kind);
    }

    [Fact]
    public void TextRoundTripIsLossless()
    {
        foreach (var text in new[] { Super, Multi })
        {
            var a = Rvmat.Parse(text);
            var written = a.ToText();
            var b = Rvmat.Parse(written);
            Assert.Empty(b.Warnings);
            Assert.True(a.Root.DeepEquals(b.Root), "parse → write → parse differs");
            Assert.Equal(written, b.ToText());
        }
        // the canonical writer reproduces BI-formatted files byte for byte (in the file's own newline)
        var nl = Super.Contains("\r\n") ? "\r\n" : "\n";
        Assert.Equal(Super + nl, Rvmat.Parse(Super).ToText());
        Assert.Equal(Super.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", ConfigText.Write(Rvmat.Parse(Super).Root));
    }

    [Fact]
    public void SavingKeepsTheFilesOwnLayout()
    {
        // "name = value" files (written by other tools) keep their spacing and LF newlines, so a diff shows only real edits
        var spaced = "ambient[] = {1,1,1,1};\ndiffuse[] = {1,1,1,1};\nspecularPower = 100;\nPixelShaderID = \"Super\";\n"
            + "class Stage1\n{\n\ttexture = \"a_nohq.paa\";\n\tuvSource = \"tex\";\n\tclass uvTransform\n\t{\n\t\taside[] = {1,0,0};\n"
            + "\t\tup[] = {0,1,0};\n\t\tdir[] = {0,0,1};\n\t\tpos[] = {0,0,0};\n\t};\n};\n";
        var m = Rvmat.Parse(spaced);
        Assert.Equal(new ConfigTextStyle("\n", true), m.TextStyle);
        Assert.Equal(spaced, m.ToText());
        var c = m.Clone();
        c.SpecularPower = 40;
        var before = spaced.Split('\n');
        var after = c.ToText().Split('\n');
        Assert.Equal(before.Length, after.Length);
        Assert.Contains("specularPower = 40;", after);
        Assert.Equal(1, after.Where((l, i) => l != before[i]).Count());
        // BI style and CRLF are detected too; += keeps its spacing
        Assert.Equal(ConfigTextStyle.Bi, ConfigTextStyle.Detect("a=1;\r\nb[]={1};\r\n"));
        Assert.Equal("x[] += {1};\n", ConfigText.Write(ConfigText.Parse("x[] += {1};\n"), ConfigTextStyle.Detect("x[] += {1};\n")));
    }

    [Fact]
    public void ParserIsTolerant()
    {
        const string text = """"
            // header comment
            #include "basicdefines.hpp"
            #define FOO 1
            ambient[]={1,1,1,1}
            diffuse[] = { 0.5 , .5, 5e-1, 1. };   /* inline */
            specularPower = 50 // missing semicolon
            PixelShaderID=Super;
            name = some words here;
            weird[] = 3;
            @@@ garbage;
            arr[] += {"a", "b""quoted"""};
            arr2[] -= {1};
            class Base;
            delete Old;
            class Child: Base { x = 0x10; };
            class Stage1
            {
            	texture="dz\a\b_nohq.paa";
            	class uvTransform { aside[]={2,0,0}; up[]={0,3,0}; dir[]={0,0,0}; pos[]={0.5,0.25,0}; };
            """";
        var m = Rvmat.Parse(text);
        foreach (var w in m.Warnings) output.WriteLine(w);
        Assert.Contains(m.Warnings, w => w.Contains("#include"));
        Assert.Contains(m.Warnings, w => w.Contains("missing '}'"));
        Assert.Contains(m.Warnings, w => w.Contains("comment"));
        Assert.Equal(new Rgba(0.5f, 0.5f, 0.5f, 1), m.Diffuse);
        Assert.Equal(50, m.SpecularPower);
        Assert.Equal("Super", m.PixelShaderID);
        Assert.Equal(ConfigValueKind.Word, m.Root.Get("PixelShaderID")!.Kind);
        Assert.Equal("some words here", m.Root.GetString("name"));
        Assert.True(m.Root.Get("weird")!.IsArray);
        var arr = m.Root.Property("arr")!;
        Assert.Equal(1, arr.ArrayOp);
        Assert.Equal("b\"quoted\"", arr.Value.Items[1].Text);
        Assert.Equal(2, m.Root.Property("arr2")!.ArrayOp);
        Assert.True(m.Root.Class("Base")!.IsExtern);
        Assert.IsType<ConfigDelete>(m.Root.Find("Old"));
        Assert.Equal("Base", m.Root.Class("Child")!.Base);
        Assert.Equal(16, m.Root.Class("Child")!.GetNumber("x"));
        var st = m.Stage(1)!;
        Assert.Equal(new Vector2(2 * 0.1f + 0.5f, 3 * 0.1f + 0.25f), st.EffectiveUvTransform.Apply(new(0.1f, 0.1f)));
        // and it still round-trips
        var again = Rvmat.Parse(m.ToText());
        Assert.Empty(again.Warnings);
        Assert.True(m.Root.DeepEquals(again.Root));

        // garbage never throws
        foreach (var junk in new[] { "", "}", "class", "class X {", "a[]={{{", "x=\"unterminated", "/* open", "{}{};;;", "a=;b[]={,,};" })
            _ = Rvmat.Parse(junk);
    }

    [Fact]
    public void EditsKeepUnknownEntriesAndOrder()
    {
        var m = Rvmat.Parse(Super);
        var before = m.Root.Entries.Select(e => e.Name).ToArray();
        m.PixelShaderID = "Multi";
        m.Diffuse = new Rgba(0.25f, 0.5f, 0.75f, 1);
        m.SpecularPower = 12.5f;
        m.SetRenderFlag("AddBlend", false);
        m.SetRenderFlag("AlphaTest128", true);
        m.Stage(2)!.UvTransform = UvTransform.FromComponents(2, 3, 0, 0.5f, 0);
        m.Stage(1)!.Texture = @"dz\other\x_nohq.paa";
        Assert.Equal(before, m.Root.Entries.Select(e => e.Name).ToArray());
        var text = m.ToText();
        Assert.Contains("PixelShaderID=\"Multi\";", text);
        Assert.Contains("diffuse[]={0.25,0.5,0.75,1};", text);
        Assert.Contains("specularPower=12.5;", text);
        Assert.Contains("renderFlags[]={\"NoZWrite\",\"AlphaTest128\"};", text);
        Assert.Contains("aside[]={2,0,0};", text);
        Assert.Contains("up[]={0,3,0};", text);
        Assert.Contains("pos[]={0.5,0,0};", text);
        Assert.Contains("mystery=0.200;", text);

        // new stage goes after the last stage, before the unknown class
        var s8 = m.AddStage(8, @"dz\x\y_mc.paa");
        var names = m.Root.Entries.Select(e => e.Name).ToList();
        Assert.Equal(names.IndexOf("Stage7") + 1, names.IndexOf("Stage8"));
        Assert.Equal("tex", s8.UvSource);
        Assert.True(s8.EffectiveUvTransform.IsIdentity);
        Assert.Equal(9, m.NextStageIndex);
        Assert.True(m.RemoveStage(8));
        Assert.Null(m.Stage(8));

        // clone is deep
        var c = m.Clone();
        c.Stage(1)!.Texture = "changed";
        Assert.Equal(@"dz\other\x_nohq.paa", m.Stage(1)!.Texture);

        // properties are inserted above classes
        var empty = new Rvmat();
        empty.AddStage(1);
        empty.PixelShaderID = "Super";
        empty.Ambient = Rgba.White;
        Assert.Equal(new[] { "PixelShaderID", "ambient", "Stage1" }, empty.Root.Entries.Select(e => e.Name));
        var tg = empty.AddTexGen(0);
        Assert.Equal(new[] { "PixelShaderID", "ambient", "TexGen0", "Stage1" }, empty.Root.Entries.Select(e => e.Name));
        Assert.Equal("tex", tg.UvSource);
        m.RenderFlags = [];
        Assert.Null(m.Root.Find("renderFlags"));
    }

    [Fact]
    public void UvTransformComponentsRoundTrip()
    {
        foreach (var (su, sv, rot, ou, ov) in new[] { (1f, 1f, 0f, 0f, 0f), (2f, 3f, 0f, 0.5f, -0.25f), (4f, 4f, 90f, 0f, 0f), (1.5f, -2f, 30f, 1f, 2f) })
        {
            var t = UvTransform.FromComponents(su, sv, rot, ou, ov);
            t.Decompose(out var su2, out var sv2, out var rot2, out var ou2, out var ov2);
            Assert.Equal(su, su2, 4);
            Assert.Equal(sv, sv2, 4);
            Assert.Equal(rot, rot2, 3);
            Assert.Equal(ou, ou2, 5);
            Assert.Equal(ov, ov2, 5);
            var uv = new Vector2(0.3f, 0.7f);
            var a = t.Apply(uv);
            var b = Vector2.Transform(uv, t.ToMatrix3x2());
            Assert.Equal(a.X, b.X, 5);
            Assert.Equal(a.Y, b.Y, 5);
        }
        var r90 = UvTransform.FromComponents(1, 1, 90, 0, 0);
        Assert.Equal(new Vector3(0, 1, 0), r90.Aside);
        Assert.Equal(new Vector3(-1, 0, 0), r90.Up);
        Assert.True(UvTransform.Identity.IsIdentity);
    }

    [Fact]
    public void RapRoundTrip()
    {
        foreach (var text in new[] { Super, Multi })
        {
            var a = Rvmat.Parse(text);
            var bin = a.ToRap();
            Assert.True(Rap.IsRap(bin));
            var b = Rvmat.FromBytes(bin);
            Assert.True(b.WasBinarized);
            Assert.True(a.Root.DeepEquals(b.Root, semantic: true), "text → raP → tree differs");
            Assert.Equal(a.Stages.Count, b.Stages.Count);
            Assert.Equal(a.Specular, b.Specular);
            // raP → text → raP is stable
            var again = Rvmat.Parse(b.ToText()).ToRap();
            Assert.Equal(bin, again);
        }
        // many entries (compressed counts >= 128) and += arrays
        var big = new ConfigClass("");
        for (int i = 0; i < 300; i++) big.Entries.Add(new ConfigProperty($"p{i}", ConfigValue.Int(i)));
        big.Entries.Add(new ConfigProperty("arr", ConfigValue.Array(Enumerable.Range(0, 200).Select(i => ConfigValue.Float(i * 0.5))), 1));
        big.Entries.Add(new ConfigProperty("big", ConfigValue.Int(5_000_000_000)));
        big.Entries.Add(new ConfigClass("ext") { IsExtern = true });
        big.Entries.Add(new ConfigDelete { Name = "gone" });
        var back = Rap.Read(Rap.Write(big));
        Assert.True(big.DeepEquals(back, semantic: true));
    }

    [Fact]
    public void RapReaderHandlesHandEncodedFile()
    {
        // Encoded by hand from the format description (independent of Rap.Write):
        // PixelShaderID="Super"; specularPower=50 (int); specular[]={0.5,1}; class Stage1 { texture="a.paa"; };
        var b = new List<byte> { 0, (byte)'r', (byte)'a', (byte)'P', 0, 0, 0, 0, 8, 0, 0, 0 };
        var enumPatch = b.Count;
        b.AddRange([0, 0, 0, 0]);
        void Z(string s)
        {
            b.AddRange(Encoding.ASCII.GetBytes(s));
            b.Add(0);
        }
        Z(""); // root base
        b.Add(4); // entries
        b.Add(1); b.Add(0); Z("PixelShaderID"); Z("Super");
        b.Add(1); b.Add(2); Z("specularPower"); b.AddRange(BitConverter.GetBytes(50));
        b.Add(2); Z("specular"); b.Add(2); b.Add(1); b.AddRange(BitConverter.GetBytes(0.5f)); b.Add(2); b.AddRange(BitConverter.GetBytes(1));
        b.Add(0); Z("Stage1");
        var classPatch = b.Count;
        b.AddRange([0, 0, 0, 0]);
        var body = b.Count;
        Z(""); b.Add(1); b.Add(1); b.Add(0); Z("texture"); Z("a.paa");
        var enums = b.Count;
        b.AddRange([0, 0, 0, 0]);
        var data = b.ToArray();
        BitConverter.GetBytes(body).CopyTo(data, classPatch);
        BitConverter.GetBytes(enums).CopyTo(data, enumPatch);

        var m = Rvmat.FromBytes(data);
        Assert.Equal("Super", m.PixelShaderID);
        Assert.Equal(50, m.SpecularPower);
        Assert.Equal(new Rgba(0.5f, 0.5f, 0.5f, 1), m.Specular);
        Assert.Equal("a.paa", m.Stage(1)!.Texture);
        Assert.Equal("PixelShaderID=\"Super\";\r\nspecularPower=50;\r\nspecular[]={0.5,1};\r\nclass Stage1\r\n{\r\n\ttexture=\"a.paa\";\r\n};\r\n", m.ToText());

        // corrupt data throws InvalidDataException, never anything else
        for (int cut = 4; cut < data.Length; cut += 3)
        {
            try
            {
                Rap.Read(data[..cut]);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void TextureSuffixes()
    {
        Assert.Equal("_nohq", TextureNames.Suffix(@"dz\x\body_NOHQ.paa"));
        Assert.Equal("_co", TextureNames.Suffix("dz/x/body_co.tga"));
        Assert.Equal("_smdi", TextureNames.Suffix("#(argb,8,8,3)color(1,1,1,1,SMDI)"));
        Assert.Equal("fresnel", TextureNames.Suffix("#(ai,64,64,1)fresnel(1.5,0.2)"));
        Assert.Equal("", TextureNames.Suffix("plain.paa"));
        Assert.Equal(StageRole.Environment, TextureNames.RoleOf(@"dz\data\data\env_land_co.paa"));
        Assert.Equal(StageRole.AmbientShadow, TextureNames.RoleOf("#(argb,8,8,3)color(0.7,0.7,0.7,1,ADS)"));
        Assert.Equal(0, TextureNames.UvSetIndex("tex"));
        Assert.Equal(2, TextureNames.UvSetIndex("tex2"));
        Assert.Equal(-1, TextureNames.UvSetIndex("none"));
        Assert.True(ProceduralTexture.TryParseColor("#(rgb,1,1,1)color(1,0,0)", out var c, out var tag));
        Assert.Equal(new Rgba(1, 0, 0, 1), c);
        Assert.Null(tag);
    }

    [Fact]
    public void StoreResolvesCachesOverridesAndReloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "p3dforge_mat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "dz", "x"));
        try
        {
            var file = Path.Combine(root, "dz", "x", "body.rvmat");
            File.WriteAllText(file, Super);
            var store = new MaterialStore(root);
            var changes = new List<string>();
            store.Changed += changes.Add;

            Assert.Null(store.Resolve(""));
            Assert.Null(store.Resolve("#(argb,8,8,3)color(1,1,1,1)"));
            Assert.Equal(file, store.Resolve(@"\dz\x\body.rvmat"), ignoreCase: true);
            Assert.Equal(file, store.Resolve("dz/x/body.rvmat"), ignoreCase: true);
            var key = store.Key(@"DZ\X\BODY.rvmat")!;
            Assert.Equal(key, store.Key(file));

            var m = store.Get(@"dz\x\body.rvmat")!;
            Assert.NotNull(m);
            Assert.Same(m, store.Get(file));
            Assert.Equal("Super", m.PixelShaderID);
            Assert.Null(store.Get(@"dz\x\missing.rvmat"));
            Assert.Equal("file not found", store.GetError(@"dz\x\missing.rvmat"));
            Assert.False(store.FileExists(@"dz\x\missing.rvmat"));

            var edit = m.Clone();
            edit.PixelShaderID = "Multi";
            var v0 = store.Version(file);
            store.SetOverride(@"\dz\x\body.rvmat", edit);
            Assert.Same(edit, store.Get(file));
            Assert.True(store.HasOverride(file));
            Assert.Equal(new[] { key }, store.OverrideKeys);
            Assert.Equal(new[] { key }, changes);
            Assert.True(store.Version(file) > v0);
            Assert.Same(m, store.LoadFromDisk(file));
            Assert.True(store.ClearOverride(file));
            Assert.False(store.ClearOverride(file));
            Assert.Same(m, store.Get(file));
            Assert.Equal(2, changes.Count);

            Assert.Equal(0, store.CheckForChanges());
            var changed = Rvmat.Parse(Super);
            changed.SpecularPower = 7;
            changed.Save(file, keepBackup: true);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            Assert.True(File.Exists(file + ".bak"));
            Assert.Equal(1, store.CheckForChanges());
            Assert.Equal(7, store.Get(file)!.SpecularPower);
            Assert.Equal(3, changes.Count);

            // binarized file through the store
            var bin = Path.Combine(root, "dz", "x", "bin.rvmat");
            File.WriteAllBytes(bin, Rvmat.Parse(Multi).ToRap());
            var bm = store.Get(@"dz\x\bin.rvmat")!;
            Assert.True(bm.WasBinarized);
            Assert.Equal(4, bm.Stages.Count);

            store.ProjectRootProvider = () => @"Q:\elsewhere";
            Assert.Equal(@"Q:\elsewhere\dz\a.rvmat", store.Resolve(@"dz\a.rvmat"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealDayZMaterialsSmoke()
    {
        const string dz = @"P:\DZ";
        if (!Directory.Exists(dz))
        {
            output.WriteLine("P:\\DZ not present — skipped");
            return;
        }
        var sw = Stopwatch.StartNew();
        int files = 0, binarized = 0, stages = 0, warned = 0;
        foreach (var sub in new[] { "animals", "characters", "structures", "weapons", "vehicles", "gear" })
        {
            var dir = Path.Combine(dz, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.rvmat", SearchOption.AllDirectories).Take(60))
            {
                var bytes = File.ReadAllBytes(f); // read-only
                var m = Rvmat.FromBytes(bytes, f);
                files++;
                if (m.WasBinarized) binarized++;
                if (m.Warnings.Count > 0)
                {
                    warned++;
                    output.WriteLine($"{f}: {string.Join("; ", m.Warnings.Take(3))}");
                }
                stages += m.Stages.Count;
                foreach (var s in m.Stages) _ = s.EffectiveUvTransform;
                var again = Rvmat.Parse(m.ToText());
                Assert.True(m.Root.DeepEquals(again.Root, semantic: m.WasBinarized), $"round trip differs: {f}");
                Assert.True(m.Root.DeepEquals(Rap.Read(m.ToRap()), semantic: true), $"raP round trip differs: {f}");
            }
        }
        output.WriteLine($"{files} rvmats ({binarized} binarized, {warned} with warnings), {stages} stages in {sw.ElapsedMilliseconds} ms");
        Assert.True(files > 0);
    }

    [Fact]
    public void IndexScansCachesAndSearches()
    {
        var root = Path.Combine(Path.GetTempPath(), "p3dforge_idx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        try
        {
            File.WriteAllText(Path.Combine(root, "a", "metal_body.rvmat"), Super);
            File.WriteAllText(Path.Combine(root, "a", "b", "bed.rvmat"), Multi);
            File.WriteAllBytes(Path.Combine(root, "a", "b", "bin.rvmat"), Rvmat.Parse(Super).ToRap());
            File.WriteAllText(Path.Combine(root, "a", "b", "broken.rvmat"), "class {{{ ;;");
            File.WriteAllText(Path.Combine(root, "a", "other.txt"), "x");
            var cache = Path.Combine(root, "idx.json");
            var idx = new MaterialIndex(cache);
            var reports = new List<MaterialScanProgress>();
            Assert.Equal(4, idx.Scan(root, new SyncProgress<MaterialScanProgress>(reports.Add)));
            Assert.True(reports[^1].Done);
            Assert.Equal(4, reports[^1].Parsed);
            var all = idx.Entries(root);
            var body = all.Single(e => e.Name == "metal_body");
            Assert.Equal("Super", body.Shader);
            Assert.Equal(@"dz\vehicles\x\data\body_nohq.paa", body.Normal);
            Assert.Equal(@"dz\vehicles\x\data\body_smdi.paa", body.Specular);
            Assert.Equal(@"dz\structures\x\data\matress_co.paa", all.Single(e => e.Name == "bed").Diffuse);
            Assert.True(all.Single(e => e.Name == "bin").Binarized);
            idx.Save();

            // a fresh index loads the cache and re-parses only the changed file
            var idx2 = new MaterialIndex(cache);
            idx2.Load();
            Assert.Equal(4, idx2.Count);
            File.WriteAllText(Path.Combine(root, "a", "b", "bed.rvmat"), Super);
            File.SetLastWriteTimeUtc(Path.Combine(root, "a", "b", "bed.rvmat"), DateTime.UtcNow.AddMinutes(1));
            File.Delete(Path.Combine(root, "a", "b", "broken.rvmat"));
            reports.Clear();
            Assert.Equal(3, idx2.Scan(root, new SyncProgress<MaterialScanProgress>(reports.Add)));
            Assert.Equal(1, reports[^1].Parsed);
            Assert.Equal("Super", idx2.Entries().Single(e => e.Name == "bed").Shader);

            var hits = MaterialIndex.Search(idx2.Entries(), "b BOD");
            Assert.Equal("metal_body", Assert.Single(hits).Name);
            Assert.Equal(3, MaterialIndex.Search(idx2.Entries(), "  ").Count);
            Assert.Equal(2, MaterialIndex.Search(idx2.Entries(), @"a\b\").Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void SpherePreviewRendersLitSphere()
    {
        var input = SphereInputs.FromMaterial(Rvmat.Parse(Super));
        input.Diffuse = new PreviewImage(2, 2, [200, 30, 30, 255, 200, 30, 30, 255, 200, 30, 30, 255, 200, 30, 30, 255]);
        input.Normal = PreviewImage.Solid(new Rgba(0.5f, 0.5f, 1, 1));
        input.Smdi = PreviewImage.Solid(new Rgba(1, 0.5f, 0.5f, 1));
        var sw = Stopwatch.StartNew();
        var px = SpherePreview.RenderBgra(128, input);
        output.WriteLine($"128 px sphere in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(128 * 128 * 4, px.Length);
        Assert.Equal(0, px[3]); // corner transparent
        var c = (64 * 128 + 64) * 4;
        Assert.Equal(255, px[c + 3]);
        Assert.True(px[c + 2] > px[c + 1] + 40, "centre should be red"); // BGRA
        // lit side (upper left) brighter than the shadow side (lower right)
        int Lum(int x, int y)
        {
            var o = (y * 128 + x) * 4;
            return px[o] + px[o + 1] + px[o + 2];
        }
        Assert.True(Lum(40, 40) > Lum(100, 100));
        // no textures at all still works
        var plain = SpherePreview.RenderBgra(96, new SphereInputs());
        Assert.Equal(255, plain[(48 * 96 + 48) * 4 + 3]);
    }

    [Fact]
    public void TemplateIsAValidSuperMaterial()
    {
        var m = RvmatTemplates.Super(@"dz\x\a_nohq.paa");
        Assert.Equal("Super", m.PixelShaderID);
        Assert.Equal(7, m.Stages.Count);
        Assert.Equal(@"dz\x\a_nohq.paa", m.FindStage(StageRole.Normal)!.Texture);
        Assert.Equal("none", m.Stage(6)!.UvSource);
        var again = Rvmat.Parse(m.ToText());
        Assert.Empty(again.Warnings);
        Assert.True(m.Root.DeepEquals(again.Root));
        Assert.Equal(@"dz\x\body", RvmatTemplates.BaseName(@"dz\x\body_co.paa"));
        Assert.Null(RvmatTemplates.BaseName("#(argb,8,8,3)color(1,1,1,1)"));
    }

    [Fact]
    public void MaterialPreviewPicksAndGuessesTextures()
    {
        // Super takes the colour map from the face texture: the preview guesses it from the _nohq sibling
        var m = RvmatTemplates.Super(@"dz\x\wall_nohq.paa");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"dz\x\wall_co.paa" };
        var (d, n, _) = MaterialPreview.Textures(m, files.Contains);
        Assert.Equal(@"dz\x\wall_co.paa", d);
        Assert.Equal(@"dz\x\wall_nohq.paa", n);
        Assert.Null(MaterialPreview.Textures(m, _ => false).Diffuse);
        Assert.Equal(@"a\b_ca.paa", MaterialPreview.GuessColorMap(@"a\b_smdi.paa", f => f.EndsWith("_ca.paa")));

        // procedural textures load as 1×1 colours; missing files load as null; render never throws
        var loaded = new List<string>();
        PreviewImage? Load(string t)
        {
            loaded.Add(t);
            return MaterialPreview.LoadTexture(t, x => Path.Combine(Path.GetTempPath(), "p3dforge-missing", x));
        }
        var px = MaterialPreview.Render(m, 64, Load, files.Contains);
        Assert.Equal(64 * 64 * 4, px.Length);
        Assert.Contains(@"dz\x\wall_co.paa", loaded);
        Assert.Equal(255, px[(32 * 64 + 32) * 4 + 3]);
        var solid = MaterialPreview.LoadTexture("#(argb,8,8,3)color(1,0,0,1,CO)", _ => null);
        Assert.NotNull(solid);
        Assert.Equal(1, solid.Width);
        Assert.Equal(255, solid.Rgba[0]);
    }
}
