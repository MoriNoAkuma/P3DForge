namespace P3DForge.Core.Topology;

/// <summary>
/// Undirected edge key (min &lt;&lt; 32 | max) with a well-mixed hash. The default <see cref="long"/> hash (low ^ high)
/// collides massively for mesh edges (a, a+1), turning dictionaries quadratic.
/// </summary>
internal sealed class EdgeKey : IEqualityComparer<long>
{
    public static readonly EdgeKey Comparer = new();

    public static long Of(uint a, uint b) => a < b ? ((long)a << 32) | b : ((long)b << 32) | a;

    public bool Equals(long x, long y) => x == y;

    public int GetHashCode(long x)
    {
        ulong z = (ulong)x + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (int)(z ^ (z >> 31));
    }
}
