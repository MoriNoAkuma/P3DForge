using P3DForge.Core.Anim;
using P3DForge.Core.Validation;

namespace P3DForge.App.Services;

/// <summary>
/// What the DayZ validation rules need to know beyond the model: its kind (weapon, magazine, item, clothing, vehicle,
/// building — from the config.cpp class that uses it and that class's bases), the hiddenSelections of its classes,
/// and the model.cfg bones.
/// </summary>
public static class DayZContext
{
    public sealed record Info(ModelKind Kind, string? Source, IReadOnlyList<string> HiddenSelections);

    /// <summary>Kind and hidden selections from the config.cpp files around <paramref name="modelFile"/> (any thread).</summary>
    public static Info Find(string? modelFile, string projectRoot)
    {
        if (modelFile is null) return new Info(ModelKind.Unknown, null, []);
        var (lib, _) = GameConfigs.Load(modelFile, projectRoot);
        var game = AppServices.NormalizeGamePath(modelFile);
        var variants = ModelVariants.Find(lib, game);
        var hidden = variants.SelectMany(v => v.Selections).Where(s => s.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // the class that shows the model (model= or ClothingTypes), base classes first: their chain says the kind
        var owner = variants.OrderBy(v => v.Scope == 2 ? 1 : 0).FirstOrDefault();
        if (owner is null) return new Info(ModelKind.Unknown, null, hidden);
        var chain = lib.Chain(owner.Section, owner.Name).Select(c => c.Name).ToList();
        var kind = Classify(owner.Section, chain);
        // the chain as far as it is known ("AKM: Rifle_Base: …")
        return new Info(kind, $"{owner.Section} ▸ {string.Join(": ", chain.Take(4))}{(chain.Count > 4 ? ": …" : "")}", hidden);
    }

    /// <summary>The kind from a class's section and base classes (nearest first).</summary>
    public static ModelKind Classify(string section, IReadOnlyList<string> chain)
    {
        bool Has(params string[] names) => chain.Any(c => names.Any(n => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)));
        if (section.Equals("CfgWeapons", StringComparison.OrdinalIgnoreCase)) return ModelKind.Weapon;
        if (section.Equals("CfgMagazines", StringComparison.OrdinalIgnoreCase)) return ModelKind.Magazine;
        if (!section.Equals("CfgVehicles", StringComparison.OrdinalIgnoreCase)) return ModelKind.Unknown;
        if (Has("CarScript", "Car", "BoatScript", "Boat", "Transport")) return ModelKind.Vehicle;
        if (Has("Clothing", "Clothing_Base")) return ModelKind.Clothing;
        if (Has("HouseNoDestruct", "House", "BuildingSuper", "Building", "BuildingBase", "BaseBuildingBase", "Fence", "Static")) return ModelKind.Building;
        if (Has("Inventory_Base", "ItemBase", "InventoryItem", "InventoryItemSuper", "Edible_Base", "Container_Base")) return ModelKind.Item;
        // the base is in a config that was not read: a bare chain without bases of the game is most likely an item
        return ModelKind.Unknown;
    }

    /// <summary>model.cfg bones of the open model (UI thread): (all skeleton bones, bones that animations move).</summary>
    public static (List<string> Skeleton, List<string> Animated) Bones()
    {
        Anim.AnimSession.Refresh();
        var skeleton = Anim.AnimSession.AllBones.Select(b => b.Name).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var animated = Anim.AnimSession.All.Select(a => a.Selection).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (skeleton, animated);
    }
}
