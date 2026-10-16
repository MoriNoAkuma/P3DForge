namespace P3DForge.Core.Topology;

/// <summary>Disjoint-set forest over <c>n</c> elements (union by rank, path halving). Every element starts in its own set.</summary>
public sealed class DisjointSets
{
    private readonly int[] _parent;
    private readonly byte[] _rank;

    public DisjointSets(int n)
    {
        _parent = new int[n];
        for (int i = 0; i < n; i++) _parent[i] = i;
        _rank = new byte[n];
    }

    public int Count => _parent.Length;

    /// <summary>Representative of the set containing <paramref name="x"/>.</summary>
    public int Find(int x)
    {
        var parent = _parent;
        while (parent[x] != x)
        {
            int grand = parent[parent[x]];
            parent[x] = grand;
            x = grand;
        }
        return x;
    }

    /// <summary>Merges the sets containing <paramref name="a"/> and <paramref name="b"/>; returns the new representative.</summary>
    public int Union(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra == rb) return ra;
        if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
        _parent[rb] = ra;
        if (_rank[ra] == _rank[rb]) _rank[ra]++;
        return ra;
    }

    public bool Same(int a, int b) => Find(a) == Find(b);
}
