using System.Buffers.Binary;
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
        // the hinge (back edge, z = -0.5, y = 1) stays; the front edge (z = +0.5) swings to y = 1 ± 1
        for (int i = 0; i < lod.PointCount; i++)
        {
            float z = lod.Positions[i * 3 + 2], y = lod.Positions[i * 3 + 1];
            if (Math.Abs(z + 0.5f) < 1e-4 && Math.Abs(y - 1) < 1e-4) Assert.Equal(1, open[i * 3 + 1], 4);
            if (Math.Abs(z - 0.5f) < 1e-4 && Math.Abs(y - 1) < 1e-4) Assert.Equal(1, Math.Abs(open[i * 3 + 1] - 1), 3);
        }
        var half = rig.Evaluate(new Dictionary<string, double> { ["user"] = 0.5 });
        Assert.NotEqual(open, half);
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
        var child = new AnimRig(lod, memory, [new Bone("frame", ""), new Bone("lid", "frame")], [frame]).Evaluate(new Dictionary<string, double> { ["f"] = 1 });
        Assert.Equal(lod.Positions[1] + 1, child[1], 4);
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

    [Fact]
    public void Anm_bone_names_come_from_the_head_chunk()
    {
        // FORM <size> ANIM SET5 <size> FPS\0 <4> 30 HEAD <2 × 56> …
        var ms = new MemoryStream();
        void Bytes(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void BE(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            ms.Write(b);
        }
        Bytes("FORM");
        BE(0);
        Bytes("ANIMSET5");
        BE(0);
        Bytes("FPS\0");
        BE(4);
        BE(30);
        Bytes("HEAD");
        BE(2 * 56);
        foreach (var name in new[] { "Weapon_Bolt", "Weapon_Trigger" })
        {
            var rec = new byte[56];
            Encoding.ASCII.GetBytes(name).CopyTo(rec, 0);
            ms.Write(rec);
        }
        Assert.Equal(["Weapon_Bolt", "Weapon_Trigger"], Anm.Bones(ms.ToArray()));
        Assert.Empty(Anm.Bones("not an anm"u8.ToArray()));

        var real = @"P:\DZ\anims\anm\player\reloads\cz527\w_cz527_states.anm";
        if (File.Exists(real)) Assert.Contains("Weapon_Bolt", Anm.Bones(File.ReadAllBytes(real)));
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
