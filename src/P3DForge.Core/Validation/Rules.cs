using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Topology;

namespace P3DForge.Core.Validation;

/// <summary>Helpers shared by the rules.</summary>
internal static class R
{
    public static bool IsGeometryCategory(ValidationContext c, int lod) => c.TypeOf(lod).Category == LodCategory.Geometry;

    /// <summary>Geometry and Geometry PhysX: collision LODs whose defects break physics (errors, not warnings).</summary>
    public static bool IsCollision(ValidationContext c, int lod) => c.TypeOf(lod).Id is "geometry" or "geometryPhysX";

    public static bool IsVisual(ValidationContext c, int lod) => c.TypeOf(lod).Category is LodCategory.Visual or LodCategory.View;

    public static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    public static int[] Concat(IEnumerable<int[]> parts)
    {
        var set = new SortedSet<int>();
        foreach (var p in parts) set.UnionWith(p);
        return [.. set];
    }

    public static string Names(IReadOnlyList<string> names, int max = 6) =>
        names.Count <= max ? string.Join(", ", names) : string.Join(", ", names.Take(max)) + $" … (+{names.Count - max})";
}

/// <summary>Objects with visual or other geometry LODs but no Geometry LOD have no collision and no mass.</summary>
public sealed class MissingGeometryRule : IValidationRule
{
    public string Id => "geometry.missing";
    public string Title => "Geometry LOD";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        var lods = c.Model.Lods;
        bool hasGeometry = false, needsIt = false;
        for (int i = 0; i < lods.Length; i++)
        {
            var t = c.TypeOf(i);
            if (t.Id == "geometry") hasGeometry = true;
            else if (t.Category == LodCategory.Geometry || t.Id is "roadway" || t.Category == LodCategory.Visual && lods[i].FaceCount > 0) needsIt = true;
        }
        if (!hasGeometry && needsIt)
            yield return new ValidationIssue(Severity.Warning, Id, "No Geometry LOD: the object has no collision and no mass",
                Hint: "LOD › New LOD… › Geometry, then build closed convex components and Structure › Set Mass… (not needed for pure proxy/decal models)");
    }
}

/// <summary>Two LODs with the same resolution (the engine keeps only one).</summary>
public sealed class DuplicateResolutionRule : IValidationRule
{
    public string Id => "lod.duplicateResolution";
    public string Title => "LOD resolutions";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        var seen = new Dictionary<float, int>();
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            float r = c.Model.Lods[i].Resolution;
            if (seen.TryGetValue(r, out int first))
                yield return new ValidationIssue(Severity.Error, Id, $"Duplicate LOD resolution {c.LodName(i)} (same as LOD #{first + 1})", i,
                    Hint: "LOD › LOD Properties… to give it its own resolution, or delete it");
            else seen[r] = i;
        }
    }
}

/// <summary>Geometry LOD without mass; closed components without mass.</summary>
public sealed class GeometryMassRule : IValidationRule
{
    public string Id => "geometry.mass";
    public string Title => "Mass";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (c.TypeOf(i).Id != "geometry") continue;
            var lod = c.Model.Lods[i];
            if (lod.FaceCount == 0) continue;
            double total = Mass.Total(lod);
            if (total <= 0)
            {
                yield return new ValidationIssue(Severity.Warning, Id, "Geometry LOD has no mass", i,
                    Hint: "Structure › Set Mass… (Window › Mass shows the total and the centre of mass)");
                continue;
            }
            var m = Mass.Values(lod)!;
            var massless = new List<int[]>();
            var names = new List<string>();
            foreach (var r in c.Components(i))
            {
                if (!r.IsClosed) continue;
                double sum = 0;
                foreach (int p in r.Component.Points) sum += m[p];
                if (sum <= 0) { massless.Add(r.Component.Points); names.Add($"#{r.Index + 1}"); }
            }
            if (massless.Count > 0)
                yield return new ValidationIssue(Severity.Info, "geometry.componentMass", $"{R.Plural(massless.Count, "component")} without mass ({R.Names(names)})", i,
                    Points: R.Concat(massless), Hint: "Select the component and Structure › Set Mass…; massless parts do not take part in physics");
        }
    }
}

/// <summary>Components of geometry LODs that are not closed (open or non-manifold edges).</summary>
public sealed class ComponentClosedRule : IValidationRule
{
    public string Id => "components.notClosed";
    public string Title => "Closed components";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsGeometryCategory(c, i)) continue;
            var open = c.Components(i).Where(r => !r.IsClosed).ToList();
            if (open.Count == 0) continue;
            int edges = open.Sum(r => r.Closed.OpenEdgeCount + r.Closed.NonManifoldEdgeCount);
            var pts = R.Concat(open.Select(r => r.Closed.OpenEdges.Concat(r.Closed.NonManifoldEdges).ToArray()));
            yield return new ValidationIssue(R.IsCollision(c, i) ? Severity.Error : Severity.Warning, Id,
                $"{R.Plural(open.Count, "component")} not closed ({R.Plural(edges, "open/non-manifold edge")})", i,
                Points: pts, Faces: R.Concat(open.Select(r => r.Closed.ProblemFaces)),
                Hint: "Structure › Topology › Find Non-Closed; close the holes or use Convexity › Component Convex Hull");
            var flipped = c.Components(i).Where(r => r.Closed.InconsistentEdgeCount > 0 || r.InsideOut).ToList();
            if (flipped.Count > 0)
                yield return new ValidationIssue(Severity.Warning, "components.winding", $"{R.Plural(flipped.Count, "component")} with inverted or inconsistent face winding", i,
                    Faces: R.Concat(flipped.Select(r => r.InsideOut ? r.Component.Faces : r.Closed.InconsistentFaces)),
                    Hint: "Faces › Reverse the selected faces so every normal points outwards");
        }
    }
}

/// <summary>Components of geometry LODs that are not convex (the engine treats each component as its convex hull).</summary>
public sealed class ComponentConvexRule : IValidationRule
{
    public string Id => "components.notConvex";
    public string Title => "Convex components";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsGeometryCategory(c, i)) continue;
            var bad = c.Components(i).Where(r => r.IsClosed && !r.IsConvex).ToList();
            if (bad.Count == 0) continue;
            double worst = bad.Max(r => r.Convexity!.MaxViolation);
            yield return new ValidationIssue(Severity.Warning, Id,
                $"{R.Plural(bad.Count, "component")} not convex" + (worst > 0 ? $" (up to {worst * 1000:0.#} mm inside the hull)" : ""), i,
                Faces: R.Concat(bad.Select(r => r.Convexity!.NonConvexFaces)),
                Hint: "Split them into convex pieces or Structure › Convexity › Component Convex Hull (collisions use the hull anyway)");
        }
    }
}

/// <summary>Geometry LODs need ComponentXX selections matching their closed components.</summary>
public sealed class ComponentSelectionsRule : IValidationRule
{
    public string Id => "components.selections";
    public string Title => "ComponentXX selections";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsGeometryCategory(c, i)) continue;
            var lod = c.Model.Lods[i];
            var closed = c.Components(i).Where(r => r.IsClosed).ToList();
            if (closed.Count == 0) continue;
            var sels = lod.Tags.OfType<SelectionTag>().Where(s => ComponentAnalysis.IsComponentName(s.Name)).ToList();
            if (sels.Count == 0)
            {
                yield return new ValidationIssue(Severity.Error, Id, $"No ComponentXX selections ({R.Plural(closed.Count, "closed component")})", i,
                    Hint: "Structure › Topology › Find Components");
                continue;
            }
            var have = new HashSet<string>(sels.Select(s => Key(s.Points, lod.PointCount)));
            var want = new HashSet<string>(closed.Select(r => Key(r.Component.Points)));
            var missing = closed.Where(r => !have.Contains(Key(r.Component.Points))).ToList();
            var extra = sels.Where(s => !want.Contains(Key(s.Points, lod.PointCount))).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                var parts = new List<string>();
                if (missing.Count > 0) parts.Add($"{R.Plural(missing.Count, "closed component")} without a selection");
                if (extra.Count > 0) parts.Add($"{R.Plural(extra.Count, "selection")} matching no closed component ({R.Names(extra.Select(s => s.Name).ToList(), 4)})");
                var pts = R.Concat(missing.Select(r => r.Component.Points).Concat(extra.Select(s => NonZero(s.Points, lod.PointCount))));
                yield return new ValidationIssue(Severity.Warning, Id, $"ComponentXX selections are out of date: {string.Join("; ", parts)}", i,
                    Points: pts, Hint: "Structure › Topology › Find Components recreates Component01..NN");
            }
        }
    }

    static int[] NonZero(byte[] mask, int count)
    {
        var o = new List<int>();
        for (int p = 0; p < count && p < mask.Length; p++) if (mask[p] != 0) o.Add(p);
        return [.. o];
    }

    static string Key(byte[] mask, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int p = 0; p < count && p < mask.Length; p++)
            if (mask[p] != 0) sb.Append(p).Append(',');
        return sb.ToString();
    }

    static string Key(int[] points)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int p in points) sb.Append(p).Append(',');
        return sb.ToString();
    }
}

/// <summary>Degenerate faces (repeated points, no area) and invalid point indices.</summary>
public sealed class DegenerateFacesRule : IValidationRule
{
    public string Id => "faces.degenerate";
    public string Title => "Degenerate faces";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (c.Model.Lods[i].FaceCount == 0) continue;
            var r = c.FaceChecks(i);
            if (r.Invalid.Length > 0)
                yield return new ValidationIssue(Severity.Error, "faces.invalid", $"{R.Plural(r.Invalid.Length, "face")} with invalid point indices", i,
                    Faces: r.Invalid, Hint: "Delete them (Edit › Delete)");
            if (r.Degenerate.Length > 0)
                yield return new ValidationIssue(R.IsCollision(c, i) ? Severity.Error : Severity.Warning, Id, $"{R.Plural(r.Degenerate.Length, "degenerate face")} (repeated points or no area)", i,
                    Faces: r.Degenerate, Hint: "Structure › Degenerated Faces: Repair");
            if (r.Duplicate.Length > 0)
                yield return new ValidationIssue(Severity.Warning, "faces.duplicate", $"{R.Plural(r.Duplicate.Length, "duplicate face")}", i,
                    Faces: r.Duplicate, Hint: "Structure › Remove Duplicate Faces");
        }
    }
}

/// <summary>Quads whose 4th point is off the plane of the others (shading artifacts; ambiguous collision planes).</summary>
public sealed class NonPlanarQuadsRule : IValidationRule
{
    public string Id => "faces.nonPlanar";
    public string Title => "Non-planar quads";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (c.Model.Lods[i].FaceCount == 0) continue;
            var r = c.FaceChecks(i);
            if (r.NonPlanar.Length > 0)
                yield return new ValidationIssue(Severity.Warning, Id,
                    $"{R.Plural(r.NonPlanar.Length, "non-planar quad")} (up to {r.MaxNonPlanarity * 1000:0.##} mm)", i,
                    Faces: r.NonPlanar, Hint: "Faces › Triangulate or Squarize the selected quads");
        }
    }
}

/// <summary>
/// Texture, material and proxy paths: absolute paths (drive letters), files missing under the project root and
/// .tga/.png textures in visual LODs.
/// </summary>
public sealed class PathRule : IValidationRule
{
    public string Id => "paths";
    public string Title => "Paths";

    static readonly string[] SourceImage = [".tga", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public static bool IsAbsolute(string p) =>
        p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' || p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        var missingCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Missing(string path)
        {
            if (c.Exists is null || c.Resolve is null) return false;
            if (missingCache.TryGetValue(path, out bool m)) return m;
            var abs = c.Resolve(path);
            m = abs is not null && !c.Exists(abs);
            missingCache[path] = m;
            return m;
        }

        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            c.Cancel.ThrowIfCancellationRequested();
            var lod = c.Model.Lods[i];
            // Faces per string index, textures and materials separately.
            var byTexture = new Dictionary<uint, List<int>>();
            var byMaterial = new Dictionary<uint, List<int>>();
            for (int f = 0; f < lod.FaceCount; f++)
            {
                uint t = lod.FaceTexture[f], m = lod.FaceMaterial[f];
                if (t != 0) (byTexture.TryGetValue(t, out var lt) ? lt : byTexture[t] = []).Add(f);
                if (m != 0) (byMaterial.TryGetValue(m, out var lm) ? lm : byMaterial[m] = []).Add(f);
            }
            bool visual = R.IsVisual(c, i);
            foreach (var (map, kind) in new[] { (byTexture, "texture"), (byMaterial, "material") })
            {
                foreach (var (si, faces) in map.OrderBy(kv => kv.Key))
                {
                    if (si >= lod.Strings.Length) continue;
                    string path = lod.Strings[(int)si].Trim();
                    if (path.Length == 0 || path.StartsWith('#')) continue; // procedural texture
                    var fa = faces.ToArray();
                    if (IsAbsolute(path))
                        yield return new ValidationIssue(Severity.Error, "paths.absolute", $"Absolute {kind} path {path} ({R.Plural(fa.Length, "face")})", i, Faces: fa,
                            Hint: @"Use a path relative to the project drive root (e.g. dz\structures\...); Tools › Mass Texture & Material Renaming…");
                    else if (Missing(path))
                        yield return new ValidationIssue(Severity.Warning, "paths.missing", $"Missing {kind} {path} ({R.Plural(fa.Length, "face")})", i, Faces: fa,
                            Hint: "The file is not under the project root (Settings: project root); fix the path or add the file");
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (kind == "texture" && visual && SourceImage.Contains(ext))
                        yield return new ValidationIssue(Severity.Warning, "textures.format", $"Texture {path} is {ext}, not .paa ({R.Plural(fa.Length, "face")})", i, Faces: fa,
                            Hint: "Convert it to .paa (ImageToPAA / TexView) and reference the .paa");
                    else if (kind == "material" && ext != ".rvmat" && ext.Length > 0)
                        yield return new ValidationIssue(Severity.Warning, "paths.materialType", $"Material {path} is not an .rvmat ({R.Plural(fa.Length, "face")})", i, Faces: fa);
                }
            }
            // Proxies.
            foreach (var t in lod.Tags)
            {
                if (t is not SelectionTag s || !Proxies.IsProxyName(s.Name)) continue;
                string p3d;
                try { p3d = Proxies.Parse(s.Name).Path; }
                catch (Exception) { continue; }
                if (p3d.Length == 0) continue;
                var pts = NonZero(s.Points, lod.PointCount);
                var fcs = NonZero(s.Faces, lod.FaceCount);
                string file = p3d.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase) ? p3d : p3d + ".p3d";
                if (IsAbsolute(p3d))
                    yield return new ValidationIssue(Severity.Error, "paths.absolute", $"Absolute proxy path {p3d}", i, pts, fcs,
                        Hint: "Proxy paths are relative to the project drive root");
                else if (Missing(file))
                    yield return new ValidationIssue(Severity.Warning, "paths.missing", $"Missing proxy model {file}", i, pts, fcs,
                        Hint: "The proxied .p3d is not under the project root");
            }
        }
    }

    static int[] NonZero(byte[] mask, int count)
    {
        var o = new List<int>();
        for (int i = 0; i < count && i < mask.Length; i++) if (mask[i] != 0) o.Add(i);
        return [.. o];
    }
}

/// <summary>Named selections without points or faces (leftovers; animations bound to them do nothing).</summary>
public sealed class EmptySelectionsRule : IValidationRule
{
    public string Id => "selections.empty";
    public string Title => "Empty named selections";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            var empty = new List<string>();
            foreach (var t in c.Model.Lods[i].Tags)
                if (t is SelectionTag s && !s.Points.AsSpan().ContainsAnyExcept((byte)0) && !s.Faces.AsSpan().ContainsAnyExcept((byte)0))
                    empty.Add(s.Name);
            if (empty.Count > 0)
                yield return new ValidationIssue(Severity.Info, Id, $"{R.Plural(empty.Count, "empty named selection")}: {R.Names(empty)}", i,
                    Hint: "Delete them in the Named Selections window unless model.cfg needs them in this LOD");
        }
    }
}

/// <summary>Faces with neither a texture nor a material in visual LODs (rendered with the default/white texture).</summary>
public sealed class UntexturedFacesRule : IValidationRule
{
    static bool Blank(Lod lod, uint s) => s == 0 || s >= lod.Strings.Length || lod.Strings[(int)s].Trim().Length == 0;

    public string Id => "faces.untextured";
    public string Title => "Untextured faces";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (!R.IsVisual(c, i)) continue;
            var lod = c.Model.Lods[i];
            var proxy = ComponentAnalysis.ProxyFaces(lod);
            var faces = new List<int>();
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (proxy is not null && proxy[f] != 0) continue;
                if (Blank(lod, lod.FaceTexture[f]) && Blank(lod, lod.FaceMaterial[f])) faces.Add(f);
            }
            if (faces.Count > 0)
                yield return new ValidationIssue(Severity.Info, Id, $"{R.Plural(faces.Count, "face")} without texture or material", i, Faces: [.. faces],
                    Hint: "Faces › Face Properties (E) to assign a texture/material, or ignore for invisible helper faces");
        }
    }
}

/// <summary>Geometry LOD without an "autocenter" property (the engine re-centres the model on its bounding box).</summary>
public sealed class AutocenterRule : IValidationRule
{
    public string Id => "geometry.autocenter";
    public string Title => "autocenter";

    public IEnumerable<ValidationIssue> Check(ValidationContext c)
    {
        for (int i = 0; i < c.Model.Lods.Length; i++)
        {
            if (c.TypeOf(i).Id != "geometry") continue;
            bool has = c.Model.Lods[i].Tags.OfType<PropertyTag>().Any(p => p.Key.Equals("autocenter", StringComparison.OrdinalIgnoreCase));
            if (!has)
                yield return new ValidationIssue(Severity.Info, Id, "No \"autocenter\" named property in the Geometry LOD", i,
                    Hint: "Add autocenter = 0 (Named Properties) for buildings, static objects and proxies so the model keeps its origin");
        }
    }
}
