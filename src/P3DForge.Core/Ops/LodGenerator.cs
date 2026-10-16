using System.Collections.Immutable;
using P3DForge.Core.Model;
using P3DForge.Core.Topology;

namespace P3DForge.Core.Ops;

/// <summary>What <see cref="LodGenerator.Generate"/> builds.</summary>
/// <param name="Source">The visual LOD everything is made from.</param>
/// <param name="Ratios">Resolution LODs to make, each as a share of the source's triangles (0.5, 0.25, …).</param>
/// <param name="Shadow">Make a Shadow Volume (10000).</param>
/// <param name="Geometry">Make a Geometry LOD with mass.</param>
/// <param name="Fire">Make a Fire Geometry.</param>
/// <param name="View">Make a View Geometry.</param>
/// <param name="Mass">Total mass of the Geometry LOD (kg), distributed by the volume of its parts.</param>
/// <param name="MinPart">Parts smaller than this share of the model's size are left out of the hull LODs.</param>
/// <param name="Replace">Replace existing LODs of the same type / resolution (else new resolutions are picked).</param>
/// <param name="MaxHulls">At most this many hulls: the other parts are grouped by where they are.</param>
/// <param name="Separate">Named selections that get hulls of their own (model.cfg bones: doors, bolts, magazines).</param>
public sealed record LodGenOptions(int Source, double[] Ratios, bool Shadow, bool Geometry, bool Fire, bool View,
    double Mass = 10, double MinPart = 0.03, bool Replace = false, int MaxHulls = 10, IReadOnlyList<string>? Separate = null);

/// <summary>The new model and what was made (for the log).</summary>
public sealed record LodGenResult(P3DModel Model, List<string> Report);

/// <summary>
/// Builds the usual LOD set from one visual LOD: simplified resolution LODs (<see cref="Decimate"/>, each from the
/// previous one) and, from convex hulls of its parts (proxies left out), a closed sharp-edged Shadow Volume and
/// Geometry (ComponentXX selections, mass by volume) / Fire / View Geometry. Named selections (bones) stay on the
/// hull points so animated parts keep their collision.
/// </summary>
public static class LodGenerator
{
    public const float ShadowResolution = 10000, GeometryResolution = 1e13f, ViewGeometryResolution = 6e15f, FireGeometryResolution = 7e15f;

    public static LodGenResult Generate(P3DModel model, LodGenOptions o, CancellationToken ct = default)
    {
        var report = new List<string>();
        var source = model.Lods[o.Source];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- resolution LODs
        var prev = source;
        double prevRatio = 1;
        float res = source.Resolution;
        foreach (var ratio in o.Ratios.Where(r => r > 0 && r < 1).OrderByDescending(r => r))
        {
            ct.ThrowIfCancellationRequested();
            var low = Decimate.Run(prev, ratio / prevRatio, out var st, ct);
            res = NextResolution(model, res, o.Replace);
            model = Put(model, low with { Resolution = res }, o.Replace);
            report.Add($"{LodNames.Name(res)}: {st.TrianglesAfter:N0} triangles ({100.0 * st.TrianglesAfter / Math.Max(1, Triangles(source)):0} % of {LodNames.Name(source.Resolution)})" +
                       (st.TrianglesAfter > Triangles(source) * ratio * 1.25 ? " — seams / texture borders limit the reduction" : ""));
            prev = low;
            prevRatio = ratio;
        }

        // ---- hull LODs
        if (o.Shadow || o.Geometry || o.Fire || o.View)
        {
            ct.ThrowIfCancellationRequested();
            var (hulls, parts, dropped) = Hulls(source, o.MinPart, Math.Max(1, o.MaxHulls), o.Separate ?? [], ct);
            if (hulls.FaceCount == 0) report.Add("No closed hulls could be built (no parts with volume) — no Shadow / Geometry made");
            else
            {
                string partsText = $"{parts} group{(parts == 1 ? "" : "s")} of parts" + (dropped > 0 ? $", {dropped} small part{(dropped == 1 ? "" : "s")} left out" : "");
                if (o.Shadow)
                {
                    var shadow = FaceOps.SetSharpEdges(Strip(hulls, keepSelections: false), MeshMath.Ones(hulls.PointCount), sharp: true).Lod;
                    model = Put(model, Structure.RecalcNormals(shadow) with { Resolution = ShadowResolution }, o.Replace);
                    report.Add($"ShadowVolume: {shadow.FaceCount:N0} faces, convex hulls of {partsText}");
                }
                if (o.Geometry)
                {
                    var geo = Strip(hulls, keepSelections: true);
                    geo = Mass.Distribute(geo, MeshMath.Ones(geo.PointCount), o.Mass, MassDistribution.Volume, out _);
                    if (geo.Property("autocenter") is null) geo = NamedProperties.Set(geo, "autocenter", "0");
                    model = Put(model, geo with { Resolution = GeometryResolution }, o.Replace);
                    report.Add($"Geometry: {parts} component{(parts == 1 ? "" : "s")}, {o.Mass:0.##} kg by volume, autocenter = {geo.Property("autocenter")}");
                }
                if (o.Fire)
                {
                    model = Put(model, Strip(hulls, keepSelections: true) with { Resolution = FireGeometryResolution }, o.Replace);
                    report.Add("Fire Geometry: the same hulls (set penetration rvmats on the faces if bullets should pass some parts)");
                }
                if (o.View)
                {
                    model = Put(model, Strip(hulls, keepSelections: true) with { Resolution = ViewGeometryResolution }, o.Replace);
                    report.Add("View Geometry: the same hulls");
                }
                if (parts > 64) report.Add($"⚠ {parts} parts is a lot for collision LODs — raise \"leave out parts smaller than\" or merge parts");
            }
        }
        report.Add($"done in {sw.ElapsedMilliseconds} ms");
        return new LodGenResult(model, report);
    }

    static int Triangles(Lod lod)
    {
        int n = 0;
        for (int f = 0; f < lod.FaceCount; f++) n += lod.FaceVertCount[f] - 2;
        return n;
    }

    /// <summary>The next resolution after <paramref name="after"/> (whole numbers, like 1, 2, 3); with replace an existing one is reused.</summary>
    static float NextResolution(P3DModel model, float after, bool replace)
    {
        float r = MathF.Floor(after) + 1;
        if (replace) return r;
        while (LodList.Find(model, r) >= 0) r++;
        return r;
    }

    /// <summary>Adds the LOD (in sorted position) or, with replace, swaps it for the LOD of the same resolution.</summary>
    static P3DModel Put(P3DModel model, Lod lod, bool replace)
    {
        int at = LodList.Find(model, lod.Resolution);
        if (at >= 0)
        {
            if (replace) return model.WithLod(at, lod);
            throw new InvalidOperationException($"The model already has a {LodNames.Name(lod.Resolution)} LOD (tick \"replace existing\" to rebuild it)");
        }
        return LodList.Insert(model, lod).Model;
    }

    /// <summary>
    /// Convex hulls of the parts of <paramref name="source"/> (simplified first, proxies and parts below the size limit
    /// left out): parts of each <paramref name="separate"/> selection share one hull, the rest is grouped by position
    /// (k-means on the part centres) into at most <paramref name="maxHulls"/> hulls.
    /// </summary>
    static (Lod Hulls, int Groups, int Dropped) Hulls(Lod source, double minPart, int maxHulls, IReadOnlyList<string> separate, CancellationToken ct)
    {
        var src = Triangles(source) > 200 ? Decimate.Run(source, 0.3, out _, ct) : source;
        var faces = ComponentAnalysis.ExcludeProxies(src, null) ?? MeshMath.Ones(src.FaceCount);
        var comps = MeshTopology.FindComponents(src, faces).Components.Where(c => c.Faces.Length > 0).ToList();
        double size = Diagonal(src.Positions, Enumerable.Range(0, src.PointCount));
        int dropped = 0;
        var bySelection = new Dictionary<string, List<Component>>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<Component>();
        foreach (var c in comps)
        {
            if (Diagonal(src.Positions, c.Points) < size * minPart)
            {
                dropped++;
                continue;
            }
            // a part mostly inside an animated selection follows it
            string? owner = separate.FirstOrDefault(name => src.Selection(name) is { } t && c.Points.Count(p => t.Points[p] != 0) * 2 > c.Points.Length);
            if (owner is null) rest.Add(c);
            else if (bySelection.TryGetValue(owner, out var list)) list.Add(c);
            else bySelection[owner] = [c];
        }
        var groups = bySelection.Values.ToList();
        groups.AddRange(Cluster(src, rest, Math.Max(1, maxHulls - groups.Count)));
        // mark every group with a temporary selection, keep only their faces, then replace each by its hull
        var keepFaces = new byte[src.FaceCount];
        var keepPoints = new byte[src.PointCount];
        var tags = src.Tags;
        for (int g = 0; g < groups.Count; g++)
        {
            var gp = new byte[src.PointCount];
            var gf = new byte[src.FaceCount];
            foreach (var c in groups[g])
            {
                foreach (int p in c.Points) gp[p] = keepPoints[p] = 1;
                foreach (int f in c.Faces) gf[f] = keepFaces[f] = 1;
            }
            tags = tags.Add(new SelectionTag(1, TempName(g), gp, gf));
        }
        var lod = Structure.Compact(src with { Tags = tags }, keepPoints, keepFaces);
        int built = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            if (lod.Selection(TempName(g)) is not { } t) continue;
            var mask = new SelectionMask(t.Points, t.Faces);
            var next = HullOps.ReplaceSelectionWithHull(lod, mask, Thin(lod, t.Points), out var hull, out _);
            if (!hull.Ok) continue; // flat part: left as it is
            lod = next;
            built++;
        }
        for (int g = 0; g < groups.Count; g++)
            if (lod.Selection(TempName(g)) is not null) lod = NamedSelections.Delete(lod, TempName(g));
        lod = Selection.WithSelected(lod, new SelectionMask(new byte[lod.PointCount], new byte[lod.FaceCount]));
        lod = HullOps.CreateComponentSelections(lod, null, out _);
        return (lod, built, dropped);
    }

    static string TempName(int g) => "__forge_hull_" + g;

    /// <summary>
    /// The outermost point of each cell of a coarse grid (6 cells across the group): a hull on these has tens of faces
    /// instead of hundreds and still wraps the part closely.
    /// </summary>
    static int[] Thin(Lod lod, byte[] pointMask, int cells = 6)
    {
        var pts = NamedSelections.Indices(pointMask);
        if (pts.Length <= 64) return pts;
        double cx = 0, cy = 0, cz = 0;
        foreach (int p in pts)
        {
            cx += lod.Positions[p * 3];
            cy += lod.Positions[p * 3 + 1];
            cz += lod.Positions[p * 3 + 2];
        }
        cx /= pts.Length;
        cy /= pts.Length;
        cz /= pts.Length;
        double cell = Math.Max(1e-6, Diagonal(lod.Positions, pts) / cells);
        var best = new Dictionary<(long, long, long), (int P, double D)>();
        foreach (int p in pts)
        {
            double x = lod.Positions[p * 3], y = lod.Positions[p * 3 + 1], z = lod.Positions[p * 3 + 2];
            var key = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell));
            double d = (x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz);
            if (!best.TryGetValue(key, out var b) || d > b.D) best[key] = (p, d);
        }
        return best.Count < 4 ? pts : [.. best.Values.Select(v => v.P).Order()];
    }

    /// <summary>Groups parts into <paramref name="k"/> clusters by their centres (k-means, seeded along the longest axis).</summary>
    static List<List<Component>> Cluster(Lod lod, List<Component> parts, int k)
    {
        if (parts.Count <= k) return parts.Select(p => new List<Component> { p }).ToList();
        var centres = new (double X, double Y, double Z, double W)[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            double x = 0, y = 0, z = 0;
            foreach (int p in parts[i].Points)
            {
                x += lod.Positions[p * 3];
                y += lod.Positions[p * 3 + 1];
                z += lod.Positions[p * 3 + 2];
            }
            int n = parts[i].Points.Length;
            centres[i] = (x / n, y / n, z / n, Math.Max(1e-6, Diagonal(lod.Positions, parts[i].Points)));
        }
        // seeds: evenly along the axis the parts spread most on
        double sx = centres.Max(c => c.X) - centres.Min(c => c.X), sy = centres.Max(c => c.Y) - centres.Min(c => c.Y), sz = centres.Max(c => c.Z) - centres.Min(c => c.Z);
        int axis = sx >= sy && sx >= sz ? 0 : sy >= sz ? 1 : 2;
        double Along((double X, double Y, double Z, double W) c) => axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;
        var order = Enumerable.Range(0, centres.Length).OrderBy(i => Along(centres[i])).ToArray();
        var means = new (double X, double Y, double Z)[k];
        for (int m = 0; m < k; m++)
        {
            var c = centres[order[(int)((m + 0.5) * order.Length / k)]];
            means[m] = (c.X, c.Y, c.Z);
        }
        var assign = new int[centres.Length];
        for (int iter = 0; iter < 12; iter++)
        {
            for (int i = 0; i < centres.Length; i++)
            {
                double best = double.MaxValue;
                for (int m = 0; m < k; m++)
                {
                    double dx = centres[i].X - means[m].X, dy = centres[i].Y - means[m].Y, dz = centres[i].Z - means[m].Z;
                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < best)
                    {
                        best = d;
                        assign[i] = m;
                    }
                }
            }
            for (int m = 0; m < k; m++)
            {
                double x = 0, y = 0, z = 0, w = 0;
                for (int i = 0; i < centres.Length; i++)
                {
                    if (assign[i] != m) continue;
                    x += centres[i].X * centres[i].W;
                    y += centres[i].Y * centres[i].W;
                    z += centres[i].Z * centres[i].W;
                    w += centres[i].W;
                }
                if (w > 0) means[m] = (x / w, y / w, z / w);
            }
        }
        var groups = new List<List<Component>>();
        for (int m = 0; m < k; m++)
        {
            var g = new List<Component>();
            for (int i = 0; i < parts.Count; i++)
                if (assign[i] == m) g.Add(parts[i]);
            if (g.Count > 0) groups.Add(g);
        }
        return groups;
    }

    static double Diagonal(float[] pos, IEnumerable<int> points)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (int p in points)
        {
            x0 = Math.Min(x0, pos[p * 3]); x1 = Math.Max(x1, pos[p * 3]);
            y0 = Math.Min(y0, pos[p * 3 + 1]); y1 = Math.Max(y1, pos[p * 3 + 1]);
            z0 = Math.Min(z0, pos[p * 3 + 2]); z1 = Math.Max(z1, pos[p * 3 + 2]);
        }
        return x1 < x0 ? 0 : Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
    }

    /// <summary>No textures, materials, UV sets, mass, animation phases or properties; selections only when asked.</summary>
    static Lod Strip(Lod lod, bool keepSelections)
    {
        lod = Attributes.SetFaceTextureMaterial(lod, MeshMath.Ones(lod.FaceCount), "", "");
        var tags = lod.Tags.Where(t => t switch
        {
            UVSetTag or MassTag or AnimationTag or PropertyTag => false,
            SelectionTag s => keepSelections && !Proxies.IsProxyName(s.Name) && !NamedSelections.IsEmpty(s),
            _ => true,
        }).ToImmutableArray();
        var uv = new float[lod.FaceCount * 8];
        return lod with { Tags = tags, FaceUV = uv };
    }
}
