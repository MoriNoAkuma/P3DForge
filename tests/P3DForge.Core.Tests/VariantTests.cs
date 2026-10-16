using System.Collections.Immutable;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;

namespace P3DForge.Core.Tests;

public class VariantTests
{
    const string Config = """
        class CfgVehicles
        {
        	class Inventory_Base;
        	class Wrap_ColorBase: Inventory_Base
        	{
        		model = "\mymod\wrap\wrap.p3d";
        		hiddenSelections[] = {"camo"};
        		hiddenSelectionsMaterials[] = {"mymod\wrap\data\wrap.rvmat"};
        	};
        	class Wrap_Tan: Wrap_ColorBase
        	{
        		scope = 2;
        		hiddenSelectionsTextures[] = {"\mymod\wrap\data\wrap_tan_co.paa"};
        		class DamageSystem
        		{
        			class GlobalHealth
        			{
        				class Health
        				{
        					hitpoints = 100;
        					healthLevels[] = {
        						{1.0, {"dz\a_co.paa", "dz\a.rvmat"}},
        						{0.7, {"mymod\wrap\data\wrap_tan_co.paa", "dz\a.rvmat"}},
        						{0.5, {"mymod\wrap\data\wrap_tan_damage_co.paa", "dz\a_damage.rvmat"}},
        						{0.3, {"mymod\wrap\data\wrap_tan_damage_co.paa", "dz\a_damage.rvmat"}},
        						{0.0, {"mymod\wrap\data\wrap_tan_destruct_co.paa", "dz\a_destruct.rvmat"}}
        					};
        				};
        			};
        		};
        	};
        	class Wrap_Other: Wrap_Tan
        	{
        		model = "\mymod\other\other.p3d";
        	};
        	class Unrelated: Inventory_Base
        	{
        		model = "\mymod\other\other.p3d";
        	};
        };
        """;

    static ConfigLibrary Library()
    {
        var lib = new ConfigLibrary();
        lib.Add(@"P:\mymod\wrap\config.cpp", Config);
        return lib;
    }

    /// <summary>Visual LOD cube: faces 0 and 1 in "camo", faces 0, 1, 4 textured dz\a_co.paa + dz\a.rvmat.</summary>
    static Lod Cube(float resolution = 0, bool camo = true)
    {
        var lod = OpsTestHelpers.RichCube() with { Resolution = resolution };
        if (!camo) return lod;
        return lod with { Tags = lod.Tags.Add(new SelectionTag(1, "Camo", OpsTestHelpers.All(8), [1, 1, 0, 0, 0, 0])) };
    }

    [Fact]
    public void FindsTheModelsClassesWithInheritedValues()
    {
        var found = ModelVariants.Find(Library(), @"mymod\wrap\wrap.p3d");
        Assert.Equal(["Wrap_ColorBase", "Wrap_Tan"], found.Select(v => v.Name).ToArray());
        var tan = found.Single(v => v.Name == "Wrap_Tan");
        Assert.Equal(2, tan.Scope);
        Assert.Equal(["camo"], tan.Selections);
        Assert.Equal([@"\mymod\wrap\data\wrap_tan_co.paa"], tan.Textures);
        Assert.Equal([@"mymod\wrap\data\wrap.rvmat"], tan.Materials);
        Assert.Equal(5, tan.Health.Count);
        Assert.Equal(0.5, tan.Health[2].Health);
        Assert.Equal(2, tan.Health[4].Paths.Count);
        // Wrap_Other inherits from Wrap_Tan but shows another model
        Assert.DoesNotContain(ModelVariants.Find(Library(), @"mymod\wrap\wrap.p3d"), v => v.Name == "Wrap_Other");
        Assert.Equal(["Unrelated", "Wrap_Other"], ModelVariants.Find(Library(), @"\mymod\other\other.p3d").Select(v => v.Name).ToArray());
    }

    [Fact]
    public void ApplySwapsHiddenSelectionsAndDamageEntries()
    {
        var tan = ModelVariants.Find(Library(), @"mymod\wrap\wrap.p3d").Single(v => v.Name == "Wrap_Tan");
        var lod = Cube();
        var pristine = ModelVariants.Apply(lod, tan, 0);
        Assert.Equal(@"mymod\wrap\data\wrap_tan_co.paa", pristine.Texture(0));
        Assert.Equal(@"mymod\wrap\data\wrap.rvmat", pristine.Material(1));
        Assert.Equal(@"dz\a_co.paa", pristine.Texture(4));   // not in the hidden selection
        Assert.Equal("", pristine.Texture(2));
        // the document LOD is untouched
        Assert.Equal(@"dz\a_co.paa", lod.Texture(0));

        var damaged = ModelVariants.Apply(lod, tan, 2);
        // the swap matches the ORIGINAL texture / rvmat of every face (also outside the hidden selection)
        Assert.Equal(@"mymod\wrap\data\wrap_tan_damage_co.paa", damaged.Texture(0));
        Assert.Equal(@"mymod\wrap\data\wrap_tan_damage_co.paa", damaged.Texture(4));
        Assert.Equal(@"dz\a_damage.rvmat", damaged.Material(4));
        Assert.Equal("", damaged.Texture(2));
        Assert.Same(lod, ModelVariants.Apply(lod, null, 3));
    }

    [Fact]
    public void CheckReportsMissingSelectionsFilesAndUnusedDamageEntries()
    {
        var variants = ModelVariants.Find(Library(), @"mymod\wrap\wrap.p3d");
        var model = new P3DModel([Cube(0), Cube(1, camo: false), Cube(1e13f, camo: false)]);
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"mymod\wrap\data\wrap_tan_co.paa", @"mymod\wrap\data\wrap.rvmat", @"dz\a_co.paa", @"dz\a.rvmat" };
        var issues = ModelVariants.Check(model, variants, f => exists.Contains(f) ? null : (true, "not found"));
        // LOD 1 is visual and lacks "camo"; the geometry LOD does not matter
        var missing = Assert.Single(issues, i => i.Selection == "camo");
        Assert.True(missing.Error);
        Assert.Equal(1, missing.Lod);
        Assert.Contains(issues, i => i.Error && i.Message.Contains("wrap_tan_damage_co.paa"));
        Assert.Contains(issues, i => i.Error && i.Message.Contains(@"dz\a_destruct.rvmat"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("not used by any face"));

        // a pristine entry no face uses never swaps
        var unused = new ModelVariant(variants[1].Class, ["camo"], [], [],
            [new HealthLevel(1, [@"dz\b.rvmat"]), new HealthLevel(0, [@"dz\b_destruct.rvmat"])], 2);
        Assert.Contains(ModelVariants.Check(new P3DModel([Cube()]), [unused], _ => null), i => i.Message.Contains("not used by any face"));
    }

    [Fact]
    public void WornClothingIsFoundThroughClothingTypesAndMlodNames()
    {
        var lib = new ConfigLibrary();
        lib.Add("config.cpp", """
            class CfgVehicles
            {
            	class Clothing;
            	class Pants: Clothing
            	{
            		scope = 2;
            		model = "\mymod\pants_g.p3d";
            		hiddenSelections[] = {"camoGround", "camoMale", "camoFemale"};
            		hiddenSelectionsTextures[] = {"a_co.paa", "a_co.paa", "a_co.paa"};
            		class ClothingTypes { male = "\mymod\pants_m.p3d"; female = "\mymod\pants_f.p3d"; };
            	};
            	class Pants_Red: Pants { hiddenSelectionsTextures[] = {"r_co.paa", "r_co.paa", "r_co.paa"}; };
            };
            """);
        Assert.Equal(["Pants", "Pants_Red"], ModelVariants.Find(lib, @"mymod\pants_f.p3d").Select(v => v.Name).ToArray());
        // a DeODOL copy saved anywhere: "pants_f_mlod.p3d" stands for pants_f.p3d
        var found = ModelVariants.Find(lib, @"D:\work\pants_f_mlod.p3d");
        Assert.Equal(2, found.Count);
        Assert.True(found[0].Clothing);
        // the female model has only camoFemale: no complaints about camoGround / camoMale
        var lod = OpsTestHelpers.RichCube() with { Resolution = 0 };
        lod = lod with { Tags = lod.Tags.Add(new SelectionTag(1, "camoFemale", OpsTestHelpers.All(8), OpsTestHelpers.All(6))) };
        Assert.DoesNotContain(ModelVariants.Check(new P3DModel([lod]), found, _ => null), i => i.Selection is not null);
    }

    [Fact]
    public void ParsesHealthLevelsWithEmptyLists()
    {
        var root = Materials.ConfigText.Parse("healthLevels[]={ {1,{}}, {0.6,{}}, {0.5,{}}, {0.3,{}}, {0,{}}};");
        var levels = ModelVariants.ParseHealth(root.Get("healthLevels"));
        Assert.Equal(5, levels.Count);
        Assert.All(levels, l => Assert.Empty(l.Paths));
        Assert.Equal(0.6, levels[1].Health);
        Assert.Empty(ModelVariants.ParseHealth(null));
    }
}
