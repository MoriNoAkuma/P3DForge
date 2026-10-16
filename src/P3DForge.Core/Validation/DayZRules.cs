using P3DForge.Core.Model;

namespace P3DForge.Core.Validation;

/// <summary>What the model is in game (from its config.cpp class, else a guess from its path); decides the DayZ rules.</summary>
public enum ModelKind
{
    Unknown,
    Weapon,
    Magazine,
    Item,
    Clothing,
    Vehicle,
    Building,
}

/// <summary>Memory points every vanilla model of a kind has (taken from the game's own models).</summary>
public static class DayZRequirements
{
    /// <summary>Inventory preview, the central economy and "pick up" use these (apple, magazine, jacket, every weapon has them).</summary>
    public static readonly string[] Inventory = ["boundingbox_min", "boundingbox_max", "ce_center", "ce_radius", "invview"];

    /// <summary>(point, severity, why) for weapons.</summary>
    public static readonly (string Point, Severity Severity, string Why)[] Weapon =
    [
        ("usti hlavne", Severity.Error, "muzzle end: bullets, muzzle flash and the aim direction start here"),
        ("konec hlavne", Severity.Error, "barrel start: with \"usti hlavne\" it gives the firing direction"),
        ("eye", Severity.Warning, "iron sights: the camera position when aiming down the sights"),
        ("nabojnicestart", Severity.Info, "shell ejection start (no spent cases without it)"),
        ("nabojniceend", Severity.Info, "shell ejection direction"),
    ];

    public static readonly (string Point, Severity Severity, string Why)[] Vehicle =
    [
        ("pos_driver", Severity.Warning, "where the driver gets in"),
        ("pos_driver_dir", Severity.Warning, "direction the driver faces when getting in"),
    ];

    public static bool IsInventory(ModelKind k) => k is ModelKind.Weapon or ModelKind.Magazine or ModelKind.Item or ModelKind.Clothing;
}

/// <summary>Required memory points by <see cref="ValidationContext.Kind"/>.</summary>
public sealed class DayZMemoryPointsRule : IValidationRule
{
    public string Id => "dayz.memoryPoints";
    public string Title => "DayZ memory points";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        if (c.Kind == ModelKind.Unknown) yield break;
        int mem = Array.FindIndex(c.Model.Lods.ToArray(), l => LodNames.IsMemory(l.Resolution));
        var what = c.KindSource is { } s ? $" ({s})" : "";
        if (mem < 0)
        {
            if (c.Kind is ModelKind.Weapon or ModelKind.Vehicle || DayZRequirements.IsInventory(c.Kind))
                yield return new ValidationIssue(c.Kind == ModelKind.Weapon ? Severity.Error : Severity.Warning, Id,
                    $"No Memory LOD — a {c.Kind.ToString().ToLowerInvariant()}{what} needs memory points", Hint: "LOD › New LOD › Memory, then Create › Point for each point and name it (Named Selections)");
            yield break;
        }
        var lod = c.Model.Lods[mem];
        bool Has(string name) => lod.Selection(name) is { } t && t.Points.Any(b => b != 0);
        var list = new List<(string, Severity, string)>();
        if (DayZRequirements.IsInventory(c.Kind))
            list.AddRange(DayZRequirements.Inventory.Select(p => (p, Severity.Warning, "inventory view / central economy bounds")));
        if (c.Kind == ModelKind.Weapon) list.AddRange(DayZRequirements.Weapon);
        if (c.Kind == ModelKind.Vehicle) list.AddRange(DayZRequirements.Vehicle);
        var missing = list.Where(x => !Has(x.Item1)).ToList();
        foreach (var g in missing.GroupBy(x => x.Item2).OrderByDescending(g => g.Key))
            yield return new ValidationIssue(g.Key, Id,
                $"Memory LOD lacks {string.Join(", ", g.Select(x => $"\"{x.Item1}\""))} — usual for a {c.Kind.ToString().ToLowerInvariant()}{what}", mem,
                Hint: string.Join("\n", g.Select(x => $"{x.Item1}: {x.Item3}")) + "\nCreate › Point in the Memory LOD, select it and name it in Named Selections (exact spelling, spaces included)");
    }
}

/// <summary>Shadow volume LODs: closed (else the shadow has holes / artefacts), simpler than LOD 0, all edges sharp, untextured.</summary>
public sealed class ShadowVolumeRule : IValidationRule
{
    public string Id => "dayz.shadowVolume";
    public string Title => "Shadow volume";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        int lod0 = Array.FindIndex(c.Model.Lods.ToArray(), l => LodNames.TypeOf(l.Resolution).Type.Category == LodCategory.Visual);
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (c.TypeOf(i).Category != LodCategory.Shadow) continue;
            var lod = c.Model.Lods[i];
            if (lod.FaceCount == 0) continue;
            var open = c.Components(i).Where(r => !r.IsClosed).ToList();
            if (open.Count > 0)
                yield return new ValidationIssue(Severity.Error, Id, $"{R.Plural(open.Count, "part")} of the shadow volume not closed — the shadow gets holes or streaks", i,
                    Points: R.Concat(open.Select(r => r.Closed.OpenEdges.Concat(r.Closed.NonManifoldEdges).ToArray())), Faces: R.Concat(open.Select(r => r.Closed.ProblemFaces)),
                    Hint: "Close the holes (Structure › Topology › Find Non-Closed) or rebuild the shadow from convex hulls (LOD › Generate LODs…)");
            if (lod0 >= 0 && lod.FaceCount > c.Model.Lods[lod0].FaceCount / 2 && lod.FaceCount > 200)
                yield return new ValidationIssue(Severity.Warning, Id, $"Shadow volume has {lod.FaceCount:N0} faces — LOD {LodNames.Name(c.Model.Lods[lod0].Resolution)} has {c.Model.Lods[lod0].FaceCount:N0}; a shadow should be much simpler", i,
                    Hint: "LOD › Generate LODs… makes a simplified closed shadow volume");
            int edges = UniqueEdges(lod), sharp = lod.Tags.OfType<SharpEdgesTag>().Sum(t => t.Edges.Length / 2);
            if (sharp < edges)
                yield return new ValidationIssue(Severity.Warning, Id, $"{R.Plural(edges - Math.Min(edges, sharp), "smooth edge")} in the shadow volume — every edge should be sharp", i,
                    Hint: "Select all faces, Faces › Sharp Edges", Fix: ValidationFixes.SharpAll);
            int textured = 0;
            for (int f = 0; f < lod.FaceCount; f++)
                if (lod.Texture(f).Length > 0 || lod.Material(f).Length > 0) textured++;
            if (textured > 0)
                yield return new ValidationIssue(Severity.Info, Id, $"{R.Plural(textured, "face")} of the shadow volume have a texture or material (not used)", i,
                    Hint: "Select all, Surfaces › clear the texture and material", Fix: ValidationFixes.ClearSurfaces);
        }
    }

    static int UniqueEdges(Lod lod)
    {
        var set = new HashSet<ulong>();
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int n = lod.FaceVertCount[f];
            for (int k = 0; k < n; k++)
            {
                uint a = lod.FacePoints[f * 4 + k], b = lod.FacePoints[f * 4 + (k + 1) % n];
                set.Add(a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a);
            }
        }
        return set.Count;
    }
}

/// <summary>Resolution LODs must get simpler with distance (binarize: "LODs not ordered by face count").</summary>
public sealed class LodOrderRule : IValidationRule
{
    public string Id => "dayz.lodOrder";
    public string Title => "LOD order";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        var visual = Enumerable.Range(0, c.Model.Lods.Length).Where(i => c.TypeOf(i).Category == LodCategory.Visual)
            .OrderBy(i => c.Model.Lods[i].Resolution).ToList();
        for (int k = 1; k < visual.Count; k++)
        {
            var a = c.Model.Lods[visual[k - 1]];
            var b = c.Model.Lods[visual[k]];
            if (b.FaceCount > a.FaceCount)
                yield return new ValidationIssue(Severity.Warning, Id,
                    $"{LodNames.Name(b.Resolution)} has more faces ({b.FaceCount:N0}) than the closer {LodNames.Name(a.Resolution)} ({a.FaceCount:N0})", visual[k],
                    Hint: "Each resolution LOD should have fewer faces than the previous one (binarize warns, the engine may pick the wrong LOD)");
        }
    }
}

/// <summary>Fire Geometry (bullets) and View Geometry (sight blocking) for objects that need them.</summary>
public sealed class CollisionLodsRule : IValidationRule
{
    public string Id => "dayz.collisionLods";
    public string Title => "Fire / View Geometry";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        if (c.Kind is not (ModelKind.Building or ModelKind.Vehicle or ModelKind.Weapon)) yield break;
        bool Has(string id) => c.Model.Lods.Any(l => LodNames.TypeOf(l.Resolution).Type.Id == id && l.FaceCount > 0);
        if (!Has("fireGeometry"))
            yield return new ValidationIssue(c.Kind == ModelKind.Weapon ? Severity.Info : Severity.Warning, Id,
                $"No Fire Geometry — bullets {(c.Kind == ModelKind.Weapon ? "and melee hits " : "")}pass through the {c.Kind.ToString().ToLowerInvariant()}",
                Hint: "LOD › Generate LODs… can build it from convex hulls of the parts");
        if (c.Kind is ModelKind.Building or ModelKind.Vehicle && !Has("viewGeometry"))
            yield return new ValidationIssue(Severity.Info, Id, $"No View Geometry — the {c.Kind.ToString().ToLowerInvariant()} does not block sight for AI / the zombie senses",
                Hint: "Usually a copy of the Geometry LOD");
    }
}

/// <summary>Animated parts (model.cfg bones) present in every visual LOD; collision parts of doors etc.</summary>
public sealed class BonesInLodsRule : IValidationRule
{
    public string Id => "dayz.bones";
    public string Title => "Animated parts in every LOD";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        if (c.AnimatedBones.Count == 0) yield break;
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            var lod = c.Model.Lods[i];
            if (lod.FaceCount == 0) continue;
            var type = c.TypeOf(i);
            // resolution LODs (view LODs are interiors: they need not show every outside part)
            bool visual = type.Category == LodCategory.Visual;
            bool collision = type.Id is "geometry" or "fireGeometry" or "viewGeometry";
            if (!visual && !(collision && c.Kind is ModelKind.Building or ModelKind.Vehicle)) continue;
            var missing = c.AnimatedBones.Where(b => lod.Selection(b) is null).ToList();
            if (missing.Count == 0) continue;
            // a far LOD may drop small parts on purpose: only the first resolution LOD and collision LODs warn
            bool first = visual && c.Model.Lods.Take(i).All(l => LodNames.TypeOf(l.Resolution).Type.Category != LodCategory.Visual);
            yield return new ValidationIssue(first || collision ? Severity.Warning : Severity.Info, Id,
                $"{(collision ? "Collision" : "Visual")} LOD lacks the animated part{(missing.Count == 1 ? "" : "s")} {R.Names(missing)} — {(collision ? "its collision does not move with the animation" : "it does not animate at this distance")}", i,
                Hint: "The named selection must exist with the bone's name (model.cfg skeletonBones) in every LOD that shows the part");
        }
    }
}

/// <summary>hiddenSelections (config.cpp) in every visual LOD, else the camo / damage texture does not change there.</summary>
public sealed class HiddenSelectionsRule : IValidationRule
{
    public string Id => "dayz.hiddenSelections";
    public string Title => "Hidden selections";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        if (c.HiddenSelections.Count == 0) yield break;
        // only those the model has in some visual LOD (clothing: the ground / male / female models carry one each)
        var used = c.HiddenSelections.Where(s => Enumerable.Range(0, c.Model.Lods.Length).Any(i => R.IsVisual(c, i) && c.Model.Lods[i].Selection(s) is not null)).ToList();
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsVisual(c, i) || c.Model.Lods[i].FaceCount == 0) continue;
            var missing = used.Where(s => c.Model.Lods[i].Selection(s) is null).ToList();
            if (missing.Count > 0)
                yield return new ValidationIssue(Severity.Error, Id, $"Lacks the hidden selection{(missing.Count == 1 ? "" : "s")} {R.Names(missing.Select(s => $"\"{s}\"").ToList())} of config.cpp — camo / damage textures do not change at this distance", i,
                    Hint: "Select the faces that use the camo texture and create the named selection (Tools › Camo & Damage shows every variant)");
        }
    }
}

/// <summary>Vertex weights over the skeleton bones must add up to 100 % (binarize: "sum of weights is 75, should be 100").</summary>
public sealed class BoneWeightsRule : IValidationRule
{
    public string Id => "dayz.weights";
    public string Title => "Bone weights";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        if (c.SkeletonBones.Count == 0) yield break;
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            var lod = c.Model.Lods[i];
            var tags = c.SkeletonBones.Select(b => lod.Selection(b)).OfType<SelectionTag>().ToList();
            if (tags.Count == 0) continue;
            var sum = new float[lod.PointCount];
            var any = new bool[lod.PointCount];
            foreach (var t in tags)
                for (int p = 0; p < lod.PointCount && p < t.Points.Length; p++)
                {
                    if (t.Points[p] == 0) continue;
                    sum[p] += Weights.Decode(t.Points[p]);
                    any[p] = true;
                }
            // above 100 % is normal for parent + child bones (a wheel in its damper and itself); only missing weight is a fault
            var under = new List<int>();
            for (int p = 0; p < sum.Length; p++)
                if (any[p] && sum[p] < 0.99f) under.Add(p);
            if (under.Count > 0)
                yield return new ValidationIssue(Severity.Warning, Id, $"{R.Plural(under.Count, "point")} with bone weights below 100 % (e.g. point {under[0]}: {sum[under[0]] * 100:0} %)", i, Points: [.. under],
                    Hint: "The rest of the weight goes nowhere: the point lags behind the part. Fix scales each point's bone weights to 100 % (Points › Weights… by hand)",
                    Fix: ValidationFixes.NormalizeWeights + string.Join("|", c.SkeletonBones));
        }
    }
}

/// <summary>Textured faces whose UVs have no area (binarize: "UV mapping too varied … texel size inf"), and bad UV values.</summary>
public sealed class UvMappingRule : IValidationRule
{
    public string Id => "dayz.uv";
    public string Title => "UV mapping";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsVisual(c, i)) continue;
            var lod = c.Model.Lods[i];
            var flat = new List<int>();
            var bad = new List<int>();
            for (int f = 0; f < lod.FaceCount; f++)
            {
                var tex = lod.Texture(f);
                if (tex.Length == 0 || tex.StartsWith('#')) continue;
                int n = lod.FaceVertCount[f];
                double area = 0;
                bool finite = true;
                for (int k = 0; k < n; k++)
                {
                    float u0 = lod.FaceUV[f * 8 + k * 2], v0 = lod.FaceUV[f * 8 + k * 2 + 1];
                    int k1 = (k + 1) % n;
                    float u1 = lod.FaceUV[f * 8 + k1 * 2], v1 = lod.FaceUV[f * 8 + k1 * 2 + 1];
                    if (!float.IsFinite(u0) || !float.IsFinite(v0)) finite = false;
                    area += (double)u0 * v1 - (double)u1 * v0;
                }
                if (!finite) bad.Add(f);
                else if (Math.Abs(area) < 1e-12) flat.Add(f);
            }
            if (bad.Count > 0)
                yield return new ValidationIssue(Severity.Error, Id, $"{R.Plural(bad.Count, "face")} with invalid UV coordinates (NaN / infinite)", i, Faces: [.. bad],
                    Hint: "Re-map these faces (Surfaces › UV editor)");
            if (flat.Count > 0)
                yield return new ValidationIssue(Severity.Warning, Id, $"{R.Plural(flat.Count, "textured face")} with collapsed UVs (all vertices on one UV point or line)", i, Faces: [.. flat],
                    Hint: "Binarize reports \"UV mapping too varied\" and the texture is one smeared colour; map them in the UV editor");
        }
    }
}

/// <summary>Draw calls of the first LOD (each texture + material pair is a section) and a 2nd UV set in special LODs.</summary>
public sealed class SectionsRule : IValidationRule
{
    public string Id => "dayz.sections";
    public string Title => "Sections";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            var lod = c.Model.Lods[i];
            var type = c.TypeOf(i);
            if (type.Category == LodCategory.Visual)
            {
                var pairs = new HashSet<(uint, uint)>();
                for (int f = 0; f < lod.FaceCount; f++) pairs.Add((lod.FaceTexture[f], lod.FaceMaterial[f]));
                if (pairs.Count > 16)
                    yield return new ValidationIssue(Severity.Info, Id, $"{pairs.Count} sections (texture + material pairs) — each one is a draw call", i,
                        Hint: "Merge textures into an atlas / share materials where possible");
            }
            else if (lod.FaceCount > 0 && lod.Tags.OfType<UVSetTag>().Any(t => t.Stage == 1))
                yield return new ValidationIssue(Severity.Info, Id, "Special LOD has a 2nd UV set (binarize warns; it is not used here)", i,
                    Hint: "UV editor › UV set 1 › Delete", Fix: ValidationFixes.DeleteUvSet1);
        }
    }
}
