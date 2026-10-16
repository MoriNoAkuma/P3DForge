using System.Collections.Immutable;
using P3DForge.Core.Materials;
using P3DForge.Core.Model;

namespace P3DForge.Core.Anim;

/// <summary>One damage level of DamageSystem ▸ GlobalHealth ▸ Health ▸ healthLevels[]: health and its textures / rvmats.</summary>
public sealed record HealthLevel(double Health, IReadOnlyList<string> Paths);

/// <summary>
/// A config.cpp class that shows this model, with the values that change its look (resolved through inheritance):
/// hiddenSelections[] with hiddenSelectionsTextures[] / hiddenSelectionsMaterials[] (camo variants) and healthLevels[].
/// </summary>
/// <param name="Class">The config class.</param>
/// <param name="Selections">hiddenSelections[].</param>
/// <param name="Textures">hiddenSelectionsTextures[].</param>
/// <param name="Materials">hiddenSelectionsMaterials[].</param>
/// <param name="Health">healthLevels[] of DamageSystem ▸ GlobalHealth ▸ Health.</param>
/// <param name="Scope">scope (2 = spawned in game).</param>
/// <param name="Clothing">A clothing item (ClothingTypes): its ground, male and female models each have only some of the hidden selections.</param>
public sealed record ModelVariant(ClassRef Class, IReadOnlyList<string> Selections, IReadOnlyList<string> Textures,
    IReadOnlyList<string> Materials, IReadOnlyList<HealthLevel> Health, int Scope, bool Clothing = false)
{
    public string Name => Class.Name;
    public string Section => Class.Section;
}

/// <summary>A problem with the hidden selections / damage materials of a model and its config classes.</summary>
/// <param name="Message">What is wrong, for the list.</param>
/// <param name="Error">Breaks the look in game (else a warning).</param>
/// <param name="Lod">LOD index the problem is in (-1 = the config).</param>
/// <param name="Selection">The hidden selection concerned, if any.</param>
public sealed record VariantIssue(string Message, bool Error, int Lod = -1, string? Selection = null);

/// <summary>
/// Camo and damage variants of a model from config.cpp, and the render-only LOD copy that shows one: faces of a hidden
/// selection get its texture / material; for a damage level, faces whose ORIGINAL texture or rvmat is the pristine
/// entry i of healthLevels get entry i of that level (the engine's swap — e.g. ghillie_green_co → ghillie_tan_damage_co).
/// </summary>
public static class ModelVariants
{
    public static readonly string[] Sections = ["CfgVehicles", "CfgWeapons", "CfgMagazines", "CfgNonAIVehicles", "CfgAmmo"];

    /// <summary>
    /// Classes that show <paramref name="p3dGamePath"/>: through model= (own or inherited) or, for clothing, the worn
    /// models of ClothingTypes (male= / female=). Exact paths win; else the same file name counts, where a DeODOL
    /// copy "name_mlod.p3d" stands for "name.p3d". The model's own classes come first.
    /// </summary>
    public static List<ModelVariant> Find(ConfigLibrary lib, string p3dGamePath)
    {
        var want = ConfigLibrary.NormalizeModel(p3dGamePath);
        var file = Path.GetFileName(want);
        var alt = file.EndsWith("_mlod.p3d", StringComparison.Ordinal) ? file[..^"_mlod.p3d".Length] + ".p3d" : null;
        var exact = new List<ModelVariant>();
        var byName = new List<ModelVariant>();
        foreach (var section in Sections)
            foreach (var c in lib.All(section))
            {
                // the nearest class of the chain that names models decides what the class shows
                var chain = lib.Chain(section, c.Name);
                var models = chain.Select(ModelsOf).FirstOrDefault(m => m.Count > 0);
                if (models is null) continue;
                if (models.Contains(want)) exact.Add(Resolve(lib, c, chain));
                else if (models.Any(m => Path.GetFileName(m) is var f && (f == file || f == alt))) byName.Add(Resolve(lib, c, chain));
            }
        var list = exact.Count > 0 ? exact : byName;
        return [.. list.OrderBy(v => v.Scope == 2 ? 1 : 0).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The models a class names itself (model=, ClothingTypes male= / female=), normalized.</summary>
    static List<string> ModelsOf(ClassRef c)
    {
        var list = new List<string>(3);
        if (c.Class.GetString("model") is { Length: > 0 } m) list.Add(ConfigLibrary.NormalizeModel(m));
        if (c.Class.Class("ClothingTypes") is { } ct)
            foreach (var key in new[] { "male", "female" })
                if (ct.GetString(key) is { Length: > 0 } w) list.Add(ConfigLibrary.NormalizeModel(w));
        return list;
    }

    static ModelVariant Resolve(ConfigLibrary lib, ClassRef c, List<ClassRef> chain)
    {
        IReadOnlyList<string> Strings(string key) => lib.Get(c.Section, c.Name, key, out _) is { IsArray: true } v ? [.. v.Items.Select(i => i.AsString())] : [];
        ConfigValue? health = null;
        foreach (var x in chain)
            if (x.Class.Class("DamageSystem")?.Class("GlobalHealth")?.Class("Health")?.Get("healthLevels") is { } h)
            {
                health = h;
                break;
            }
        int scope = lib.Get(c.Section, c.Name, "scope", out _) is { IsNumber: true } s ? s.AsInt() : 0;
        bool clothing = chain.Any(x => x.Class.Class("ClothingTypes") is not null);
        return new ModelVariant(c, Strings("hiddenSelections"), Strings("hiddenSelectionsTextures"), Strings("hiddenSelectionsMaterials"), ParseHealth(health), scope, clothing);
    }

    /// <summary>healthLevels[] = {{1.0, {"a.rvmat", …}}, {0.7, {…}}, …} → levels (empty lists kept).</summary>
    public static List<HealthLevel> ParseHealth(ConfigValue? v)
    {
        var list = new List<HealthLevel>();
        if (v is not { IsArray: true }) return list;
        foreach (var level in v.Items)
        {
            if (!level.IsArray || level.Items.Count == 0) continue;
            double health = level.Items[0].IsNumber ? level.Items[0].AsDouble() : double.NaN;
            var paths = level.Items.Count > 1 && level.Items[1].IsArray ? level.Items[1].Items.Select(i => i.AsString()).ToList() : [];
            list.Add(new HealthLevel(health, paths));
        }
        return list;
    }

    /// <summary>Game path as stored in faces: no leading backslash, backslashes, lower case for comparing.</summary>
    public static string Key(string path) => path.Trim().Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    static string Stored(string path) => path.Trim().Replace('/', '\\').TrimStart('\\');

    static bool IsMaterial(string path) => path.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Visual LODs: resolutions and the view (pilot / gunner / cargo / commander) LODs.</summary>
    public static bool IsVisual(Lod lod) => LodNames.TypeOf(lod.Resolution).Type.Category is LodCategory.Visual or LodCategory.View;

    /// <summary>
    /// The LOD as the game shows <paramref name="v"/> at damage level <paramref name="level"/> (0 = pristine);
    /// <paramref name="lod"/> itself when nothing changes. Render-only: the document is not edited.
    /// </summary>
    public static Lod Apply(Lod lod, ModelVariant? v, int level)
    {
        if (v is null || lod.FaceCount == 0) return lod;
        var strings = lod.Strings.ToBuilder();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < strings.Count; i++) index.TryAdd(strings[i], i);
        uint Intern(string s)
        {
            if (!index.TryGetValue(s, out int i))
            {
                i = strings.Count;
                strings.Add(s);
                index[s] = i;
            }
            return (uint)i;
        }
        uint[]? tex = null, mat = null;
        for (int i = 0; i < v.Selections.Count; i++)
        {
            if (lod.Selection(v.Selections[i]) is not { } sel) continue;
            string? t = i < v.Textures.Count && v.Textures[i].Trim().Length > 0 ? Stored(v.Textures[i]) : null;
            string? m = i < v.Materials.Count && v.Materials[i].Trim().Length > 0 ? Stored(v.Materials[i]) : null;
            if (t is null && m is null) continue;
            uint ti = t is null ? 0 : Intern(t), mi = m is null ? 0 : Intern(m);
            for (int f = 0; f < lod.FaceCount && f < sel.Faces.Length; f++)
            {
                if (sel.Faces[f] == 0) continue;
                if (t is not null) (tex ??= (uint[])lod.FaceTexture.Clone())[f] = ti;
                if (m is not null) (mat ??= (uint[])lod.FaceMaterial.Clone())[f] = mi;
            }
        }
        if (level > 0 && level < v.Health.Count)
        {
            var pristine = v.Health[0].Paths;
            var damaged = v.Health[level].Paths;
            for (int i = 0; i < pristine.Count && i < damaged.Count; i++)
            {
                if (pristine[i].Trim().Length == 0 || damaged[i].Trim().Length == 0) continue;
                string from = Key(pristine[i]);
                bool material = IsMaterial(from);
                uint to = Intern(Stored(damaged[i]));
                var original = material ? lod.FaceMaterial : lod.FaceTexture;
                for (int f = 0; f < lod.FaceCount; f++)
                    if (Key(lod.Strings[(int)original[f]]) == from)
                        (material ? mat ??= (uint[])lod.FaceMaterial.Clone() : tex ??= (uint[])lod.FaceTexture.Clone())[f] = to;
            }
        }
        if (tex is null && mat is null) return lod;
        return lod with { FaceTexture = tex ?? lod.FaceTexture, FaceMaterial = mat ?? lod.FaceMaterial, Strings = strings.ToImmutable() };
    }

    /// <summary>
    /// Checks hidden selections and damage levels against the model: every hidden selection in every visual LOD
    /// (else the camo does not change at that distance), as many textures / rvmats as selections, files that exist
    /// (<paramref name="fileProblem"/>: null when the game path resolves, else (error?, why)), and pristine damage
    /// entries that a face actually uses.
    /// </summary>
    public static List<VariantIssue> Check(P3DModel model, IReadOnlyList<ModelVariant> variants, Func<string, (bool Error, string Why)?> fileProblem)
    {
        var issues = new List<VariantIssue>();
        var all = variants.SelectMany(v => v.Selections).Where(s => s.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // only the hidden selections this model has somewhere matter per LOD: a clothing item's ground, male and female
        // models carry one each (camoGround / camoMale / camoFemale)
        var visual = model.Lods.Where(l => IsVisual(l) && l.FaceCount > 0).ToList();
        var selections = all.Where(s => visual.Any(l => l.Selection(s) is not null)).ToList();
        bool clothing = variants.Any(v => v.Clothing);
        if (!clothing)
            foreach (var s in all.Except(selections, StringComparer.OrdinalIgnoreCase))
                issues.Add(new VariantIssue($"hidden selection \"{s}\" is in no LOD of the model — the texture / rvmat config.cpp sets for it goes nowhere", false, -1, s));
        for (int li = 0; li < model.Lods.Length; li++)
        {
            var lod = model.Lods[li];
            if (!IsVisual(lod) || lod.FaceCount == 0) continue;
            foreach (var s in selections)
            {
                var sel = lod.Selection(s);
                if (sel is null)
                    issues.Add(new VariantIssue($"hidden selection \"{s}\" is missing in {LodNames.Name(lod.Resolution)} — the camo / damage texture does not change at that distance", true, li, s));
                else if (!sel.Faces.Any(b => b != 0))
                    issues.Add(new VariantIssue($"hidden selection \"{s}\" has no faces in {LodNames.Name(lod.Resolution)}", false, li, s));
            }
        }
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void CheckFile(string path, string what, ModelVariant v)
        {
            if (path.Trim().Length == 0 || path.TrimStart().StartsWith('#') || !checkedPaths.Add(Key(path))) return;
            if (fileProblem(Stored(path)) is { } p) issues.Add(new VariantIssue($"{v.Name}: {what} {path} — {p.Why}", p.Error));
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lod in model.Lods.Where(IsVisual))
            for (int f = 0; f < lod.FaceCount; f++)
            {
                used.Add(Key(lod.Texture(f)));
                used.Add(Key(lod.Material(f)));
            }
        foreach (var v in variants)
        {
            if (v.Textures.Count > v.Selections.Count)
                issues.Add(new VariantIssue($"{v.Name}: {v.Textures.Count} hiddenSelectionsTextures for {v.Selections.Count} hiddenSelections — the extra ones are ignored", false));
            if (v.Materials.Count > v.Selections.Count)
                issues.Add(new VariantIssue($"{v.Name}: {v.Materials.Count} hiddenSelectionsMaterials for {v.Selections.Count} hiddenSelections — the extra ones are ignored", false));
            foreach (var t in v.Textures) CheckFile(t, "texture", v);
            foreach (var m in v.Materials) CheckFile(m, "material", v);
            if (v.Health.Count == 0) continue;
            for (int k = 1; k < v.Health.Count; k++)
                if (v.Health[k].Paths.Count != v.Health[0].Paths.Count && v.Health[k].Paths.Count > 0)
                    issues.Add(new VariantIssue($"{v.Name}: healthLevels[{k}] has {v.Health[k].Paths.Count} entries, the pristine level {v.Health[0].Paths.Count}", false));
            foreach (var p in v.Health[0].Paths)
                if (p.Trim().Length > 0 && !used.Contains(Key(p)))
                    issues.Add(new VariantIssue($"{v.Name}: healthLevels pristine entry {p} is not used by any face — its damage textures never show (the swap matches the model's own texture / rvmat)", false));
            foreach (var level in v.Health)
                foreach (var p in level.Paths)
                    CheckFile(p, IsMaterial(p) ? "damage material" : "damage texture", v);
        }
        // the same message from several variants once
        return [.. issues.GroupBy(i => (i.Message, i.Lod)).Select(g => g.First())];
    }
}
