using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <param name="Model">The merged model.</param>
/// <param name="Appended">Source LODs appended to a target LOD of the same resolution.</param>
/// <param name="Added">Source LODs added as new LODs (sorted in by resolution).</param>
/// <param name="RenamedProxies">Proxy selections renumbered because the target LOD already used the name.</param>
public sealed record ModelMergeResult(P3DModel Model, int Appended, int Added, int RenamedProxies);

/// <summary>
/// File ▸ Merge (Object Builder): every LOD of another model goes into the LOD with the same resolution (appended,
/// becomes the selection) or is added as a new LOD. Named selections with the same name join; proxy selections that
/// would collide get the next free index so each proxy keeps its own triangle. Optionally all merged geometry also
/// gets one named selection (Merge as Selection). Pure.
/// </summary>
public static class Merge
{
    public static ModelMergeResult Models(P3DModel target, P3DModel source, string? selectionName = null)
    {
        var lods = target.Lods.ToList();
        int appended = 0, added = 0, renamed = 0;
        foreach (var src in source.Lods)
        {
            if (src.PointCount == 0 && src.FaceCount == 0 && !src.Tags.Any()) continue;
            int at = LodList.Find(new P3DModel([.. lods]), src.Resolution);
            if (at < 0)
            {
                lods.Insert(LodList.SortedInsertIndex(lods, src.Resolution), WithWholeSelection(src, selectionName));
                added++;
                continue;
            }
            var frag = Structure.Extract(src, MeshMath.Ones(src.PointCount), MeshMath.Ones(src.FaceCount));
            frag = RenumberProxies(frag, lods[at], ref renamed);
            if (!string.IsNullOrWhiteSpace(selectionName))
                frag = frag with
                {
                    Selections = frag.Selections.RemoveAll(s => string.Equals(s.Name, selectionName, StringComparison.OrdinalIgnoreCase))
                        .Add(new NamedWeights(selectionName, MeshMath.Ones(frag.PointCount), MeshMath.Ones(frag.FaceCount))),
                };
            lods[at] = MeshMath.AppendSafe(lods[at], frag, selectNew: true);
            appended++;
        }
        return new ModelMergeResult(target with { Lods = [.. lods] }, appended, added, renamed);
    }

    /// <summary>A copy of a whole LOD with one more selection holding all of it.</summary>
    static Lod WithWholeSelection(Lod lod, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || lod.Selection(name) is not null) return lod;
        return lod with { Tags = lod.Tags.Add(new SelectionTag(1, name, MeshMath.Ones(lod.PointCount), MeshMath.Ones(lod.FaceCount))) };
    }

    static Fragment RenumberProxies(Fragment frag, Lod into, ref int renamed)
    {
        var taken = new HashSet<string>(into.Selections.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        // new names must not clash with the target or with the fragment's own proxies
        var used = new HashSet<string>(taken.Concat(frag.Selections.Select(s => s.Name)), StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = frag.Selections.ToBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (!Proxies.IsProxyName(s.Name) || !taken.Contains(s.Name)) continue;
            var (path, _) = Proxies.Parse(s.Name);
            if (!next.TryGetValue(path, out int n)) n = Proxies.NextIndex(into, path);
            string name;
            do name = Proxies.SelectionName(path, n++);
            while (used.Contains(name));
            next[path] = n;
            used.Add(name);
            list[i] = s with { Name = name };
            renamed++;
        }
        return frag with { Selections = list.ToImmutable() };
    }
}
