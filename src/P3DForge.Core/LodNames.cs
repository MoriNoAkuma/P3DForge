using System.Globalization;

namespace P3DForge.Core;

public enum LodCategory
{
    Visual,
    View,
    Shadow,
    Geometry,
    Special,
    Edit,
}

/// <summary>A LOD type: either a single special resolution or an indexed family (resolution = base + n).</summary>
public sealed record LodType(string Id, string Name, double Base, LodCategory Category, double? RangeEnd = null, bool Renderable = false)
{
    public bool IsFamily => RangeEnd.HasValue;
}

/// <summary>LOD resolution values ↔ Object Builder names (see docs/FORMAT.md).</summary>
public static class LodNames
{
    public static readonly IReadOnlyList<LodType> Types =
    [
        new("visual", "Resolution", 0, LodCategory.Visual, 1000, true),
        new("viewGunner", "View - Gunner", 1000, LodCategory.View, null, true),
        new("viewPilot", "View - Pilot", 1100, LodCategory.View, null, true),
        new("viewCargo", "View - Cargo", 1200, LodCategory.View, 1300, true),
        new("shadowVolume", "ShadowVolume", 10000, LodCategory.Shadow, 11000),
        new("shadowBuffer", "ShadowBuffer", 11000, LodCategory.Shadow, 12000),
        new("edit", "Edit", 20000, LodCategory.Edit, 30000),
        new("geometry", "Geometry", 1e13, LodCategory.Geometry),
        new("geometryBuoyancy", "Geometry Buoyancy", 2e13, LodCategory.Geometry),
        new("geometryPhysX", "Geometry PhysX", 4e13, LodCategory.Geometry),
        new("memory", "Memory", 1e15, LodCategory.Special),
        new("landContact", "LandContact", 2e15, LodCategory.Special),
        new("roadway", "Roadway", 3e15, LodCategory.Special),
        new("paths", "Paths", 4e15, LodCategory.Special),
        new("hitpoints", "Hit-points", 5e15, LodCategory.Special),
        new("viewGeometry", "View Geometry", 6e15, LodCategory.Geometry),
        new("fireGeometry", "Fire Geometry", 7e15, LodCategory.Geometry),
        new("viewCargoGeometry", "View Cargo Geometry", 8e15, LodCategory.Geometry),
        new("viewCargoFireGeometry", "View Cargo Fire Geometry", 9e15, LodCategory.Geometry),
        new("viewCommander", "View - Commander", 1e16, LodCategory.View, null, true),
        new("viewCommanderGeometry", "View Commander Geometry", 1.1e16, LodCategory.Geometry),
        new("viewCommanderFireGeometry", "View Commander Fire Geometry", 1.2e16, LodCategory.Geometry),
        new("viewPilotGeometry", "View Pilot Geometry", 1.3e16, LodCategory.Geometry),
        new("viewPilotFireGeometry", "View Pilot Fire Geometry", 1.4e16, LodCategory.Geometry),
        new("viewGunnerGeometry", "View Gunner Geometry", 1.5e16, LodCategory.Geometry),
        new("viewGunnerFireGeometry", "View Gunner Fire Geometry", 1.6e16, LodCategory.Geometry),
        new("subParts", "Sub Parts", 1.7e16, LodCategory.Special),
        new("shadowVolumeViewCargo", "ShadowVolume - View Cargo", 1.8e16, LodCategory.Shadow),
        new("shadowVolumeViewPilot", "ShadowVolume - View Pilot", 1.9e16, LodCategory.Shadow),
        new("shadowVolumeViewGunner", "ShadowVolume - View Gunner", 2e16, LodCategory.Shadow),
        new("wreck", "Wreck", 2.1e16, LodCategory.Special),
    ];

    public static readonly LodType Unknown = new("unknown", "Unknown", 0, LodCategory.Special);

    static bool Same(double a, double b) => (float)a == (float)b || Math.Abs(a - b) <= Math.Abs(b) * 1e-6;

    /// <summary>Classifies a resolution; <c>index</c> is n for indexed families.</summary>
    public static (LodType Type, double Index) TypeOf(float resolution)
    {
        double r = resolution;
        foreach (var t in Types)
            if (t.Base >= 1e13 && !t.IsFamily && Same(r, t.Base)) return (t, 0);
        foreach (var t in Types)
        {
            if (t.Base >= 1e13) continue;
            if (t.IsFamily)
            {
                if (r >= t.Base && r < t.RangeEnd!.Value) return (t, r - t.Base);
            }
            else if (Same(r, t.Base))
            {
                return (t, 0);
            }
        }
        return (Unknown, 0);
    }

    static string Fmt(double n) => n.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Display name: "1.000", "Geometry", "ShadowVolume 0.000", "View - Cargo 1.000", ...</summary>
    public static string Name(float resolution)
    {
        var (type, index) = TypeOf(resolution);
        return type.Id switch
        {
            "visual" => Fmt(resolution),
            "viewCargo" or "shadowVolume" or "shadowBuffer" or "edit" => $"{type.Name} {Fmt(index)}",
            "unknown" => $"Unknown ({((double)resolution).ToString("0.###e+0", CultureInfo.InvariantCulture)})",
            _ => type.Name,
        };
    }

    /// <summary>Resolution for a type id and optional family index.</summary>
    public static float ResolutionFor(string typeId, double index = 0)
    {
        var t = Types.FirstOrDefault(x => x.Id == typeId) ?? throw new ArgumentException($"Unknown LOD type {typeId}");
        return (float)(t.IsFamily ? t.Base + index : t.Base);
    }

    public static bool IsGeometry(float resolution) => TypeOf(resolution).Type.Id == "geometry";
    public static bool IsMemory(float resolution) => TypeOf(resolution).Type.Id == "memory";
}
