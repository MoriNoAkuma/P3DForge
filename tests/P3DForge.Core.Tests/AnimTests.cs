using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class AnimTests
{
    const string Cfg = """
        // hand-written: comments and layout must survive edits
        #define ROT 1
        class CfgSkeletons
        {
        	class Default
        	{
        		isDiscrete = 1;
        		skeletonInherit = "";
        		skeletonBones[] = {};
        	};
        	class gun_skeleton: Default
        	{
        		skeletonBones[] =
        		{
        			"boltbody", "",
        			"bolthandle", "boltbody" // the handle rides on the bolt
        		};
        	};
        };
        class CfgModels
        {
        	class Default
        	{
        		sections[] = {};
        		skeletonName = "";
        	};
        	class gun: Default
        	{
        		skeletonName = "gun_skeleton";
        		class Animations
        		{
        			class bolt_back
        			{
        				type = "translation";
        				source = "bolt";
        				selection = "boltbody";
        				axis = "bolt_axis";
        				minValue = "0";
        				maxValue = "1";
        				offset0 = 0;
        				offset1 = "0,5";
        			};
        			class handle_up
        			{
        				type = "rotation";
        				source = "bolt";
        				selection = "bolthandle";
        				axis = "handle_axis";
        				angle0 = 0;
        				angle1 = "rad 90";
        			};
        		};
        	};
        	class gun_scoped: gun
        	{
        		class Animations: Animations
        		{
        			class scope_hide
        			{
        				type = "hide";
        				source = "user";
        				selection = "scope";
        				hideValue = 0.5;
        			};
        		};
        	};
        };
        """;

    [Fact]
    public void Spans_find_nested_classes_and_properties()
    {
        var anims = ConfigSpans.Find(Cfg, "CfgModels", "gun", "Animations")!.Value;
        Assert.Equal(["bolt_back", "handle_up"], ConfigSpans.Classes(Cfg, anims).Select(c => c.Name));
        var skel = ConfigSpans.Find(Cfg, "cfgskeletons", "GUN_SKELETON")!.Value; // case-insensitive
        Assert.Equal("Default", skel.Base);
        var bones = ConfigSpans.Property(Cfg, skel, "skeletonBones")!.Value;
        Assert.Contains("\"bolthandle\", \"boltbody\"", Cfg[bones.ValueStart..bones.ValueEnd]);
        Assert.Null(ConfigSpans.Find(Cfg, "CfgModels", "nope"));
    }

    [Fact]
    public void Values_read_like_binarize()
    {
        Assert.True(ModelCfg.TryEval("\"1,7453293\"", out var a) && Math.Abs(a - 1.7453293) < 1e-9);
        Assert.True(ModelCfg.TryEval("rad 90", out var b) && Math.Abs(b - Math.PI / 2) < 1e-9);
        Assert.True(ModelCfg.TryEval("-rad 45", out var c) && Math.Abs(c + Math.PI / 4) < 1e-9);
        Assert.True(ModelCfg.TryEval("(rad 30)*2", out var d) && Math.Abs(d - Math.PI / 3) < 1e-9);
        Assert.True(ModelCfg.TryEval("3.14159/2", out var e) && Math.Abs(e - 1.570795) < 1e-6);
        Assert.False(ModelCfg.TryEval("rad", out _));
    }

    [Fact]
    public void Model_cfg_resolves_skeleton_and_inherited_animations()
    {
        var cfg = ModelCfg.Parse(Cfg);
        Assert.Equal("gun_skeleton", cfg.SkeletonName("gun"));
        Assert.Equal("gun_skeleton", cfg.SkeletonName("gun_scoped")); // inherited
        Assert.Equal([new Bone("boltbody", ""), new Bone("bolthandle", "boltbody")], cfg.Bones("gun_skeleton"));
        var gun = cfg.Animations("gun");
        Assert.Equal(["bolt_back", "handle_up"], gun.Select(a => a.Name));
        Assert.Equal(0.5, gun[0].Offset1, 9);
        Assert.Equal(Math.PI / 2, gun[1].Angle1, 9);
        var scoped = cfg.Animations("gun_scoped");
        Assert.Equal(["scope_hide", "bolt_back", "handle_up"], scoped.Select(a => a.Name));
        Assert.Equal("gun_scoped", scoped[0].Owner);
        Assert.Equal("gun", scoped[1].Owner);
    }

    [Fact]
    public void Editing_one_animation_changes_only_its_class()
    {
        var cfg = ModelCfg.Parse(Cfg);
        var own = cfg.OwnAnimations("gun").ToList();
        own[1] = own[1] with { Angle1 = Math.PI }; // handle_up to 180°
        own.Add(new AnimDef { Name = "trigger_pull", Type = "rotationX", Source = "trigger", Selection = "trigger", Angle1 = 0.3 });
        var text = cfg.WithOwnAnimations("gun", own);
        // the untouched class and everything around keep their exact text
        var boltOld = ConfigSpans.Find(Cfg, "CfgModels", "gun", "Animations", "bolt_back")!.Value;
        var boltNew = ConfigSpans.Find(text, "CfgModels", "gun", "Animations", "bolt_back")!.Value;
        Assert.Equal(Cfg[boltOld.Start..boltOld.End], text[boltNew.Start..boltNew.End]);
        Assert.StartsWith("// hand-written: comments and layout must survive edits", text);
        Assert.Contains("// the handle rides on the bolt", text);
        Assert.Contains("#define ROT 1", text);
        var back = ModelCfg.Parse(text);
        var anims = back.Animations("gun");
        Assert.Equal(["bolt_back", "handle_up", "trigger_pull"], anims.Select(a => a.Name));
        Assert.Equal(Math.PI, anims[1].Angle1, 6);
        Assert.Equal(0.3, anims[2].Angle1, 6);
        // removing
        var removed = ModelCfg.Parse(text).WithOwnAnimations("gun", anims.Where(a => a.Name != "bolt_back").Select(a => a with { Owner = "gun" }).ToList());
        Assert.Equal(["handle_up", "trigger_pull"], ModelCfg.Parse(removed).Animations("gun").Select(a => a.Name));
    }

    [Fact]
    public void A_new_model_cfg_is_created_from_nothing()
    {
        var cfg = ModelCfg.Parse("");
        var text = cfg.WithBones("door", [new Bone("door", "")]);
        text = ModelCfg.Parse(text).WithOwnAnimations("door", [new AnimDef { Name = "door_open", Type = "rotation", Source = "user", Selection = "door", Axis = "door_axis", Angle1 = 1.5 }]);
        var back = ModelCfg.Parse(text);
        Assert.Equal("door_skeleton", back.SkeletonName("door"));
        Assert.Equal([new Bone("door", "")], back.Bones("door_skeleton"));
        var a = Assert.Single(back.Animations("door"));
        Assert.Equal(("door_axis", 1.5), (a.Axis, a.Angle1));
    }

    /// <summary>A 1 m box "lid" (bone) above a box "base", a memory LOD with the hinge axis along X at the lid's back edge.</summary>
    static (Lod Lod, Lod Memory) Rig()
    {
        var lid = Primitives.CreateBox(Lod.Empty(0), new BoxParams(1, 0.1, 1) { At = new Vec3(0, 1, 0) });
        var lidSel = new SelectionTag(1, "lid", MeshMath.Ones(lid.PointCount), MeshMath.Ones(lid.FaceCount));
        lid = lid with { Tags = lid.Tags.Add(lidSel) };
        var memory = Proxies.Create(Lod.Empty(1e15f), "\\x", 1).Lod; // three points to build on
        float[] p = [-0.5f, 1, -0.5f, 0.5f, 1, -0.5f, 0, 0, 0];
        memory = memory with
        {
            Positions = p,
            Tags = [new SelectionTag(1, "lid_axis", [1, 1, 0], new byte[memory.FaceCount])],
        };
        return (lid, memory);
    }

    [Fact]
    public void Rig_rotates_about_the_memory_axis()
    {
        var (lod, memory) = Rig();
        var anim = new AnimDef { Name = "open", Type = "rotation", Source = "user", Selection = "lid", Axis = "lid_axis", MinValue = 0, MaxValue = 1, Angle1 = Math.PI / 2 };
        var rig = new AnimRig(lod, memory, [new Bone("lid", "")], [anim]);
        Assert.Empty(rig.Problems);
        var at0 = rig.Evaluate(new Dictionary<string, double> { ["user"] = 0 });
        Assert.Equal(lod.Positions, at0);
        var open = rig.Evaluate(new Dictionary<string, double> { ["user"] = 1 });
        // the hinge (back edge, z = -0.5, y = 1) stays; the front edge (z = +0.5) swings down to y = 0: a positive angle
        // about +X turns +Z towards -Y, the direction the game uses (checked in game on vehicle doors)
        for (int i = 0; i < lod.PointCount; i++)
        {
            float z = lod.Positions[i * 3 + 2], y = lod.Positions[i * 3 + 1];
            if (Math.Abs(z + 0.5f) < 1e-4 && Math.Abs(y - 1) < 1e-4) Assert.Equal(1, open[i * 3 + 1], 4);
            if (Math.Abs(z - 0.5f) < 1e-4 && Math.Abs(y - 1) < 1e-4) Assert.Equal(0, open[i * 3 + 1], 3);
        }
        var half = rig.Evaluate(new Dictionary<string, double> { ["user"] = 0.5 });
        Assert.NotEqual(open, half);
        // the bone's transform (the viewport turns the part's box with it) moves a point exactly as Evaluate does
        var m = rig.BoneTransform("LID", new Dictionary<string, double> { ["user"] = 1 })!.Value;
        var moved = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(lod.Positions[0], lod.Positions[1], lod.Positions[2]), m);
        Assert.Equal(open[1], moved.Y, 4);
        Assert.Equal(open[2], moved.Z, 4);
        Assert.Null(rig.BoneTransform("nope", new Dictionary<string, double>()));
    }

    [Fact]
    public void Rig_translates_hides_and_follows_parent_bones()
    {
        var (lod, memory) = Rig();
        var slide = new AnimDef { Name = "slide", Type = "translation", Source = "s", Selection = "lid", Axis = "lid_axis", Offset1 = 2 }; // 2 axis lengths (1 m) along +X
        var rig = new AnimRig(lod, memory, [new Bone("lid", "")], [slide]);
        var p = rig.Evaluate(new Dictionary<string, double> { ["s"] = 1 });
        Assert.Equal(lod.Positions[0] + 2, p[0], 4);
        var hide = new AnimDef { Name = "hide", Type = "hide", Source = "h", Selection = "lid", HideValue = 0.5 };
        var hidden = new AnimRig(lod, memory, [new Bone("lid", "")], [hide]).Evaluate(new Dictionary<string, double> { ["h"] = 1 });
        Assert.True(hidden.Chunk(3).Select(c => (c[0], c[1], c[2])).Distinct().Count() == 1); // collapsed to a point
        // a child bone ("lid" under "frame") moves with its parent's translation
        var frame = new AnimDef { Name = "f", Type = "translationY", Source = "f", Selection = "frame", Offset1 = 1 };
        var childRig = new AnimRig(lod, memory, [new Bone("frame", ""), new Bone("lid", "frame")], [frame]);
        var child = childRig.Evaluate(new Dictionary<string, double> { ["f"] = 1 });
        Assert.Equal(lod.Positions[1] + 1, child[1], 4);
        // the child's axes are carried by the parent (the viewport draws the child's axis there)
        var up = childRig.ParentTransform("lid", new Dictionary<string, double> { ["f"] = 1 });
        Assert.Equal(1, up.M42, 4);
        Assert.True(childRig.ParentTransform("frame", new Dictionary<string, double> { ["f"] = 1 }).IsIdentity);
    }

    [Fact]
    public void Rig_reports_missing_bones_and_far_axes()
    {
        var (lod, memory) = Rig();
        var a = new AnimDef { Name = "x", Type = "rotation", Source = "user", Selection = "lid", Axis = "nope" };
        var b = new AnimDef { Name = "y", Type = "rotation", Source = "user", Selection = "ghost", Axis = "lid_axis" };
        var rig = new AnimRig(lod, memory, [new Bone("lid", "")], [a, b]);
        Assert.Contains(rig.Problems, p => p.Contains("axis \"nope\" not found"));
        Assert.Contains(rig.Problems, p => p.Contains("\"ghost\" is not a bone"));
        Assert.Equal(0, rig.ActiveAnimations - 1); // only y can move (although nothing is selected by "ghost")
    }

    [Fact]
    public void Phase_rest_and_source_address()
    {
        var a = new AnimDef { MinValue = -1, MaxValue = 1, SourceAddress = "clamp" };
        Assert.Equal(0, AnimRig.Rest(a));
        Assert.Equal(0.5, AnimRig.Phase(a, 0), 9);
        Assert.Equal(1, AnimRig.Phase(a, 5), 9);
        Assert.Equal(0.25, AnimRig.Phase(a with { MinValue = 0, MaxValue = 1, SourceAddress = "AnimLoop" }, 3.25), 9);
        Assert.Equal(0.75, AnimRig.Phase(a with { MinValue = 0, MaxValue = 1, SourceAddress = "mirror" }, 1.25), 9);
    }

    /// <summary>
    /// A small .anm as Workbench writes it: "Weapon_Bolt" slides 0 → 0.1 m along X and turns 90° about Y over frames
    /// 0..10; "Weapon_Trigger" has no keys.
    /// </summary>
    static byte[] SyntheticAnm(int version)
    {
        var head = new MemoryStream();
        var data = new MemoryStream();
        void U16(Stream s, int v) => s.Write(BitConverter.GetBytes((ushort)v));
        void F32(Stream s, float v) => s.Write(BitConverter.GetBytes(v));
        void Name(string n, int pad)
        {
            var b = Encoding.ASCII.GetBytes(n);
            head.Write(b);
            head.Write(new byte[pad - b.Length]);
        }
        foreach (var (name, keyed) in new[] { ("Weapon_Bolt", true), ("Weapon_Trigger", false) })
        {
            int k = keyed ? 2 : 0, scaleKeys = version == 6 ? 1 : 0;
            if (version == 5)
            {
                Name(name, 32);
                foreach (var f in new[] { 0f, 0.1f, 0f, 1f }) F32(head, f); // translation, rotation: min, range
                foreach (var v in new[] { 11, k, k, 0 }) U16(head, v);
            }
            else
            {
                foreach (var f in new[] { 0f, 0.1f, 0f, 1f, 1f, 0f }) F32(head, f); // + scale min, range
                foreach (var v in new[] { 11, k, k, scaleKeys }) U16(head, v);
                head.WriteByte(0);
                head.WriteByte((byte)name.Length);
                Name(name, name.Length);
            }
            // DATA: translation, (scale), rotation — the key frames, then the values
            foreach (var v in keyed ? new[] { 0, 10, 0, 0, 0, 65535, 0, 0 } : []) U16(data, v);
            for (int i = 0; i < scaleKeys; i++) foreach (var v in new[] { 0, 0, 0, 0 }) U16(data, v);
            foreach (var v in keyed ? new[] { 0, 10, 0, 0, 0, 65535, 0, 46341, 0, 46341 } : []) U16(data, v);
        }
        static byte[] Chunk(string tag, byte[] body) =>
            [.. Encoding.ASCII.GetBytes(tag), .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(body.Length)), .. body];
        byte[] set = [.. Chunk("FPS\0", BitConverter.GetBytes(30)), .. Chunk("HEAD", head.ToArray()), .. Chunk("DATA", data.ToArray())];
        var form = Chunk($"SET{version}", set);
        return Chunk("FORM", [.. "ANIM"u8.ToArray(), .. form]);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Anm_reads_bones_keys_and_samples_between_frames(int version)
    {
        var bytes = SyntheticAnm(version);
        var clip = Anm.Read(bytes)!;
        Assert.Equal((30, 11), (clip.Fps, clip.Frames));
        Assert.Equal(["Weapon_Bolt", "Weapon_Trigger"], Anm.Bones(bytes));
        var bolt = clip.Bones[0];
        Assert.Equal(0.05f, bolt.Translation(5)!.Value.X, 4);
        Assert.Equal(0.1f, bolt.Translation(30)!.Value.X, 4); // past the last key: holds it
        var q = bolt.Rotation(10)!.Value;
        Assert.Equal(1f, Vector3.Transform(Vector3.UnitZ, q).X, 3); // 90° about +Y
        Assert.Equal(MathF.Sin(MathF.PI / 4), Vector3.Transform(Vector3.UnitZ, bolt.Rotation(5)!.Value).X, 3); // halfway: 45°
        Assert.Null(clip.Bones[1].Translation(0));
        // damaged or other files are refused, not misread
        Assert.Null(Anm.Read(bytes[..^2]));
        Assert.Null(Anm.Read("not an anm"u8.ToArray()));
        Assert.Empty(Anm.Bones("not an anm"u8.ToArray()));

        var real = @"P:\DZ\anims\anm\player\reloads\cz527\w_cz527_states.anm";
        if (File.Exists(real)) Assert.Contains("Weapon_Bolt", Anm.Bones(File.ReadAllBytes(real)));
    }

    [Fact]
    public void Anm_pose_moves_the_remapped_selection_in_model_axes()
    {
        var clip = Anm.Read(SyntheticAnm(6))!;
        var lod = Proxies.Create(Lod.Empty(0), "\\x", 1).Lod; // three points
        lod = lod with
        {
            Positions = [1, 0, 0, 0, 0, 0, 5, 5, 5],
            Tags = [new SelectionTag(1, "bolt", [1, 1, 0], new byte[lod.FaceCount])],
        };
        var pos = Anm.Pose(clip, 10, lod, [("bolt", "Weapon_Bolt")]);
        // .anm: 90° about its Y (= the model's -Z) and 0.1 along X: model (1, 0, 0) → (0.1, -1, 0)
        Assert.Equal([0.1f, -1f, 0f], pos[..3].Select(v => MathF.Round(v, 3)));
        Assert.Equal([0.1f, 0f, 0f], pos[3..6].Select(v => MathF.Round(v, 3)));
        Assert.Equal([5f, 5f, 5f], pos[6..]);
        Assert.Equal(lod.Positions, Anm.Pose(clip, 10, lod, [("bolt", "Weapon_Nothing")]));
        // a reused array gets the same pose, whatever it held before
        var reused = Enumerable.Repeat(9f, 9).ToArray();
        Assert.Same(reused, Anm.Pose(clip, 10, lod, [("bolt", "Weapon_Bolt")], reused));
        Assert.Equal(pos, reused);

        // the game's bolt-action: opening turns the bolt about the bore (model X at about 0.13 m up), so a point on
        // the bore only slides back along it
        var real = @"P:\DZ\anims\anm\player\reloads\cz527\w_cz527_states.anm";
        if (!File.Exists(real)) return;
        var bolt = Anm.Read(File.ReadAllBytes(real))!.Bones.Single(b => b.Name == "Weapon_Bolt");
        var onBore = Vector3.Transform(new Vector3(0, 0.1294f, 0), bolt.ModelTransform(1));
        Assert.Equal(0.1294f, onBore.Y, 3);
        Assert.Equal(0f, onBore.Z, 3);
        Assert.True(onBore.X > 0.05f, $"the bolt slides back (+X): {onBore.X}");
        var handle = Vector3.Transform(new Vector3(0, 0.1294f, 0.05f), bolt.ModelTransform(1)); // the handle sticks out right (+Z)…
        Assert.True(handle.Y > 0.17f, $"…and turns up: {handle}");
    }

    [Fact]
    public void Asi_lists_its_animations_without_guids()
    {
        const string asi = "$animsetinstance {\r\n #parent \"{7324B2E80BB624A1}DZ/anims/workspaces/player/player_main/player_main_rifle.asi\"\r\n $animations {\r\n" +
                           "   \"WeaponOperations.ErcRas.Fire\" \"{589AD8ADE3FEF13E}DZ/anims/anm/player/reloads/cz527/p_erc_fire.anm\"\r\n" +
                           "   \"Combat.Erc.Block\" \"dz/anims/anm/block.anm\"\r\n }\r\n}";
        Assert.Equal([("WeaponOperations.ErcRas.Fire", "DZ/anims/anm/player/reloads/cz527/p_erc_fire.anm"), ("Combat.Erc.Block", "dz/anims/anm/block.anm")],
            WeaponLinks.AsiAnimations(asi));
    }

    [Fact]
    public void Script_folder_goes_into_CfgMods()
    {
        // no CfgMods: a whole one is appended (and reads back)
        const string cfg = "class CfgPatches\r\n{\r\n\tclass guns { units[] = {}; };\r\n};\r\n";
        var added = WeaponLinks.WithWorldScripts(cfg, "guns_Scripts", "guns/scripts/4_World")!;
        Assert.StartsWith(cfg, added);
        Assert.NotNull(ConfigSpans.Find(added, "CfgMods", "guns_Scripts", "defs", "worldScriptModule"));
        Assert.Contains("files[] = {\"guns/scripts/4_World\"};", added);
        // compiled already (the folder or one above it): the same text
        Assert.Same(added, WeaponLinks.WithWorldScripts(added, "x", "guns/scripts/4_World"));
        Assert.Same(added, WeaponLinks.WithWorldScripts(added, "x", @"Guns\Scripts\4_World\Weapons"));
        // another folder joins the files[] there
        Assert.Contains("{\"guns/scripts/4_World\", \"more/scripts/4_World\"}", WeaponLinks.WithWorldScripts(added, "x", "more/scripts/4_World"));
        // CfgMods without a world script module: left to the author
        Assert.Null(WeaponLinks.WithWorldScripts("class CfgMods { class m { type = \"mod\"; }; };", "x", "a/b"));
    }

    [Fact]
    public void Game_folder_drops_the_pbo_prefix()
    {
        Assert.Equal("guns/scripts/4_World", WeaponLinks.GameFolder(@"LIS\guns\scripts\4_World", @"LIS\guns\rifle\x.p3d", @"\guns\rifle\x.p3d"));
        Assert.Equal("MyMod/scripts/4_World", WeaponLinks.GameFolder(@"MyMod\scripts\4_World", @"MyMod\x.p3d", @"MyMod\x.p3d"));
        Assert.Equal("MyMod/scripts/4_World", WeaponLinks.GameFolder(@"MyMod\scripts\4_World", @"MyMod\x.p3d", @"\other\y.p3d")); // the model is elsewhere: as it is
    }

    [Fact]
    public void Anm_reads_every_vanilla_animation()
    {
        const string root = @"P:\DZ\anims\anm";
        Assert.SkipWhen(!Directory.Exists(root), $"{root} not available");
        int read = 0;
        var failed = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*.anm", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            if (bytes.Length < 16 || bytes[15] is not ((byte)'5' or (byte)'6')) continue; // SET4: a few old files, not read
            var clip = Anm.Read(bytes);
            if (clip is null || clip.Fps <= 0 || clip.Bones.Any(b => b.TFrames.Concat(b.QFrames).Any(k => k >= clip.Frames))) failed.Add(f);
            else read++;
        }
        Assert.Empty(failed);
        Assert.True(read > 1000, $"only {read} animations");
    }

    const string Config = """
        class CfgPatches { class mod { requiredAddons[] = {"DZ_Weapons_Firearms"}; }; };
        class cfgWeapons
        {
        	class BoltActionRifle_Base;
        	class MyRifle_Base: BoltActionRifle_Base
        	{
        		scope = 0;
        		model = "\mod\guns\myrifle.p3d";
        		displayName = "My rifle";
        	};
        	class MyRifle: MyRifle_Base { scope = 2; };
        };
        """;

    const string Game = """
        class CfgWeapons
        {
        	class Rifle_Base { boneRemap[] = {"bolt", "Weapon_Bolt", "trigger", "Weapon_Trigger"}; };
        	class BoltActionRifle_Base: Rifle_Base
        	{
        		weaponStateAnim = "dz\anims\anm\player\reloads\cz527\w_cz527_states.anm";
        		boneRemap[] = {"bolthandle", "Weapon_Bolt", "trigger", "Weapon_Trigger", "magazine", "Weapon_Magazine"};
        	};
        };
        """;

    [Fact]
    public void Config_library_inherits_across_files_and_finds_the_model()
    {
        var lib = new ConfigLibrary();
        lib.Add("mod", Config);
        lib.Add("game", Game);
        var hit = Assert.Single(lib.ByModel("CfgWeapons", "mod\\guns\\myrifle.p3d"));
        Assert.Equal(("MyRifle_Base", true), (hit.Class.Name, hit.ExactPath));
        Assert.False(Assert.Single(lib.ByModel("CfgWeapons", "OTHER\\mod\\guns\\myrifle.p3d")).ExactPath); // same file name only
        Assert.Equal(["MyRifle", "MyRifle_Base", "BoltActionRifle_Base", "Rifle_Base"], lib.Chain("CfgWeapons", "MyRifle").Select(c => c.Name));
        var remap = WeaponLinks.BoneRemap(lib.Get("CfgWeapons", "MyRifle", "boneRemap", out var from));
        Assert.Equal("BoltActionRifle_Base", from!.Name);
        Assert.Contains(("bolthandle", "Weapon_Bolt"), remap);

        var check = WeaponLinks.Check(["Weapon_Bolt", "Weapon_Trigger", "Weapon_Magazine", "Weapon_Bone_06"], remap,
            skeleton: ["bolthandle", "trigger"], selections: ["bolthandle", "trigger"]);
        Assert.True(check.Single(c => c.AnmBone == "Weapon_Bolt").Ok);
        Assert.Contains("named selection", check.Single(c => c.AnmBone == "Weapon_Magazine").Note);
        Assert.Equal("", check.Single(c => c.AnmBone == "Weapon_Bone_06").ModelBone);
    }

    [Fact]
    public void Config_string_is_replaced_or_inserted_after_model()
    {
        var text = WeaponLinks.SetConfigString(Config, "cfgWeapons", "MyRifle_Base", "weaponStateAnim", @"dz\anims\anm\player\reloads\svd\w_svd_states.anm")!;
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        int model = lines.FindIndex(l => l.StartsWith("model"));
        Assert.StartsWith("weaponStateAnim = \"dz\\anims\\anm\\player\\reloads\\svd\\w_svd_states.anm\";", lines[model + 1]);
        var again = WeaponLinks.SetConfigString(text, "cfgWeapons", "MyRifle_Base", "weaponStateAnim", "x.anm")!;
        Assert.Single(again.Split('\n'), l => l.Contains("weaponStateAnim"));
        Assert.Contains("\"x.anm\"", again);
        Assert.Null(WeaponLinks.SetConfigString(Config, "cfgWeapons", "Nope", "weaponStateAnim", "x"));
    }

    const string Script = """
        modded class ModItemRegisterCallbacks
        {
            override void RegisterFireArms( DayZPlayerType pType, DayzPlayerItemBehaviorCfg pBehavior )
            {
                super.RegisterFireArms( pType, pBehavior );
                pType.AddItemInHandsProfileIK("Old_Base","dz/anims/workspaces/player/player_main/weapons/player_main_cz527.asi", pBehavior, "dz/anims/anm/player/ik/weapons/cz527.anm", "dz/anims/anm/player/reloads/cz527/w_CZ527_states.anm");
            }
        }
        """;

    [Fact]
    public void Script_calls_are_found_replaced_and_added()
    {
        var link = Assert.Single(WeaponLinks.FindLinks("s.c", Script));
        Assert.Equal(("Old_Base", "pBehavior"), (link.ClassName, link.Behavior));
        Assert.EndsWith("w_CZ527_states.anm", link.States);
        var replaced = WeaponLinks.SetLink(Script, link, "Old_Base", "a.asi", "ik.anm", "st.anm")!;
        Assert.Contains("pType.AddItemInHandsProfileIK(\"Old_Base\", \"a.asi\", pBehavior, \"ik.anm\", \"st.anm\");", replaced);
        Assert.Single(WeaponLinks.FindLinks("s.c", replaced));
        var added = WeaponLinks.SetLink(Script, null, "MyRifle_Base", "b.asi", "ik2.anm", "")!;
        var links = WeaponLinks.FindLinks("s.c", added);
        Assert.Equal(["Old_Base", "MyRifle_Base"], links.OrderBy(l => l.Start).Select(l => l.ClassName));
        Assert.True(added.IndexOf("MyRifle_Base", StringComparison.Ordinal) > added.IndexOf("super.RegisterFireArms", StringComparison.Ordinal));
        Assert.Null(WeaponLinks.SetLink("class X {}", null, "A", "a", "b", "c"));
        Assert.Contains("RegisterFireArms", WeaponLinks.NewScript("A", "a.asi", "b.anm", "c.anm"));
    }
}
