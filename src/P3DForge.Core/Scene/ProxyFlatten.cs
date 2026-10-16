using System.Numerics;

namespace P3DForge.Core.Scene;

/// <summary>One proxy instance after flattening nested proxies.</summary>
/// <param name="Name">Top level: the named selection; nested: "parent &gt; child".</param>
/// <param name="Path">Proxy model path (as stored in the selection name, without the index).</param>
/// <param name="Face">Proxy triangle in the host LOD (top level), -1 for nested instances.</param>
/// <param name="Depth">0 = placed in the host LOD; n = nested n levels deep.</param>
/// <param name="Transform">Proxy model RAW → host RAW (composed through the parents).</param>
/// <param name="Parent">Index of the parent instance in the flattened list (-1 for top level).</param>
/// <param name="Root">Index of the top-level ancestor (itself for top level).</param>
public readonly record struct FlatProxy(string Name, string Path, int Face, int Depth, Matrix4x4 Transform, int Parent, int Root);

/// <summary>Nested proxies (proxies inside proxy models) flattened with composed transforms (pure).</summary>
public static class ProxyFlatten
{
    public const int DefaultMaxDepth = 3;
    public const int DefaultMaxInstances = 20000;

    /// <summary>
    /// Depth-first flattening: every top-level proxy followed by its nested proxies. <paramref name="lookup"/> maps a
    /// proxy path to (cycle key of its resolved file, the proxies placed in its display LOD — null while not loaded or
    /// when it cannot be loaded). Nesting stops at <paramref name="maxDepth"/>; a proxy whose file is already on the
    /// current chain (<paramref name="rootKey"/> = the edited file itself) is listed but not expanded (cycle-safe).
    /// Child transform composition (row vectors): childToHost = childToParent * parentToHost.
    /// </summary>
    public static List<FlatProxy> Flatten(
        IReadOnlyList<PlacedProxy> top,
        Func<string, (string Key, IReadOnlyList<PlacedProxy>? Children)> lookup,
        string? rootKey = null,
        int maxDepth = DefaultMaxDepth,
        int maxInstances = DefaultMaxInstances)
    {
        var result = new List<FlatProxy>(top.Count);
        var chain = new List<string>(maxDepth + 2);
        if (rootKey is not null) chain.Add(rootKey);
        for (int i = 0; i < top.Count && result.Count < maxInstances; i++)
        {
            var p = top[i];
            int index = result.Count;
            result.Add(new FlatProxy(p.Name, p.Path, p.Face, 0, p.Transform, -1, index));
            Expand(result, index, index, p.Path, p.Transform, 0, lookup, chain, maxDepth, maxInstances);
        }
        return result;
    }

    static void Expand(
        List<FlatProxy> result, int parent, int root, string path, Matrix4x4 toHost, int depth,
        Func<string, (string Key, IReadOnlyList<PlacedProxy>? Children)> lookup, List<string> chain, int maxDepth, int maxInstances)
    {
        if (depth >= maxDepth) return;
        var (key, children) = lookup(path);
        if (children is null || children.Count == 0) return;
        foreach (var k in chain)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return; // cycle
        chain.Add(key);
        var parentName = result[parent].Name;
        for (int i = 0; i < children.Count && result.Count < maxInstances; i++)
        {
            var c = children[i];
            var m = c.Transform * toHost;
            int index = result.Count;
            result.Add(new FlatProxy(parentName + " > " + c.Name, c.Path, -1, depth + 1, m, parent, root));
            Expand(result, index, root, c.Path, m, depth + 1, lookup, chain, maxDepth, maxInstances);
        }
        chain.RemoveAt(chain.Count - 1);
    }
}
