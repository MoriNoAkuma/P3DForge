using P3DForge.Core.Ops;
using P3DForge.Core.Model;

namespace P3DForge.Core.Topology;

/// <summary>Closedness / convexity / volume of one component.</summary>
public sealed class ComponentReport
{
    public required int Index { get; init; }
    public required Component Component { get; init; }
    public required ClosedResult Closed { get; init; }
    /// <summary>Null when convexity was not requested.</summary>
    public ConvexityResult? Convexity { get; init; }
    /// <summary>Right-handed signed volume (negative for a correctly wound closed component).</summary>
    public double SignedVolume { get; init; }
    public bool IsClosed => Closed.Closed;
    public bool IsConvex => Convexity is null || Convexity.Convex;
    /// <summary>Closed and consistently wound, but with positive signed volume (faces point inwards).</summary>
    public bool InsideOut => Closed.Closed && Closed.InconsistentEdgeCount == 0 && SignedVolume > 0;
    /// <summary>Hull failed because the component has no volume (flat, colinear, coincident).</summary>
    public bool Flat => Convexity is { HullFailure: HullFailure.Coplanar or HullFailure.Colinear or HullFailure.Degenerate or HullFailure.TooFewPoints };
    public double Volume => Math.Abs(SignedVolume);
}

/// <summary>Per-component analysis of a LOD (Geometry / Fire / View Geometry checks).</summary>
public static class ComponentAnalysis
{
    /// <summary>Components of the (masked) faces with their closedness, convexity (optional) and volume.</summary>
    /// <remarks>Proxy triangles (faces of "proxy:…" selections) are left out unless <paramref name="includeProxies"/>.</remarks>
    public static ComponentReport[] Analyze(Lod lod, byte[]? faceMask = null, bool convexity = true, double tolerance = 0, bool includeProxies = false)
    {
        var comps = MeshTopology.FindComponents(lod, includeProxies ? faceMask : ExcludeProxies(lod, faceMask));
        var o = new ComponentReport[comps.Count];
        for (int c = 0; c < comps.Count; c++)
        {
            var comp = comps.Components[c];
            o[c] = new ComponentReport
            {
                Index = c,
                Component = comp,
                Closed = MeshTopology.CheckClosed(lod, comp.Faces),
                Convexity = convexity ? Convexity.Check(lod, comp.Faces, tolerance) : null,
                SignedVolume = Measure.SignedVolume(lod, comp.Faces),
            };
        }
        return o;
    }

    /// <summary>Faces that belong to a proxy selection ("proxy:…"), or null when the LOD has no proxy.</summary>
    public static byte[]? ProxyFaces(Lod lod)
    {
        byte[]? m = null;
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s || !Proxies.IsProxyName(s.Name)) continue;
            m ??= new byte[lod.FaceCount];
            var f = s.Faces;
            for (int i = 0; i < f.Length && i < m.Length; i++) if (f[i] != 0) m[i] = 1;
        }
        return m;
    }

    /// <summary>
    /// <paramref name="faceMask"/> (null = every face) without proxy faces; returns <paramref name="faceMask"/> itself
    /// when the LOD has no proxy.
    /// </summary>
    public static byte[]? ExcludeProxies(Lod lod, byte[]? faceMask)
    {
        var proxy = ProxyFaces(lod);
        if (proxy is null) return faceMask;
        var m = new byte[lod.FaceCount];
        for (int f = 0; f < m.Length; f++)
            m[f] = (byte)(proxy[f] == 0 && (faceMask is null || faceMask[f] != 0) ? 1 : 0);
        return m;
    }

    /// <summary>Two-digit component name like Object Builder: Component01 … Component99, Component100 ….</summary>
    public static string ComponentName(int index) => "Component" + (index + 1).ToString("00");

    /// <summary>True for names of the form ComponentNN (case-insensitive).</summary>
    public static bool IsComponentName(string name)
    {
        if (name.Length <= 9 || !name.StartsWith("Component", StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 9; i < name.Length; i++)
            if (name[i] < '0' || name[i] > '9') return false;
        return true;
    }
}
