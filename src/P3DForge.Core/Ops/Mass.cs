using P3DForge.Core.Model;
using P3DForge.Core.Topology;

namespace P3DForge.Core.Ops;

/// <summary>How <see cref="Mass.Distribute"/> splits a total mass over points.</summary>
public enum MassDistribution
{
    /// <summary>Same mass on every point (Object Builder "Set Mass" on a selection).</summary>
    Equal,
    /// <summary>Each closed component gets a share proportional to its volume, split equally among its points.</summary>
    Volume,
    /// <summary>Proportional to the face area around each point (1/n of every incident n-gon).</summary>
    Area,
}

/// <summary>Mass, centre of mass and inertia tensor (about the centre, RAW axes) of point masses.</summary>
public sealed record MassProperties(double Total, int Points, Vec3 Center, double Ixx, double Iyy, double Izz, double Ixy, double Ixz, double Iyz)
{
    public static readonly MassProperties Empty = new(0, 0, default, 0, 0, 0, 0, 0, 0);
    public bool HasMass => Total > 0;
}

/// <summary>
/// Per-point masses (#Mass# tag, used by the engine from the Geometry LOD). Pure: every method returns a new Lod and
/// never writes into the arrays of its input.
/// </summary>
public static class Mass
{
    /// <summary>The per-point masses, or null when the LOD has no (valid) #Mass# tag.</summary>
    public static float[]? Values(Lod lod) => lod.MassTag?.Mass is { } m && m.Length == lod.PointCount ? m : null;

    /// <summary>Sum of the masses of all points, or of the points with a non-zero <paramref name="pointMask"/> byte.</summary>
    public static double Total(Lod lod, byte[]? pointMask = null)
    {
        var m = Values(lod);
        if (m is null) return 0;
        double t = 0;
        for (int i = 0; i < m.Length; i++)
            if (pointMask is null || pointMask[i] != 0) t += m[i];
        return t;
    }

    /// <summary>Number of points with a non-zero mass.</summary>
    public static int PointsWithMass(Lod lod)
    {
        var m = Values(lod);
        if (m is null) return 0;
        int n = 0;
        for (int i = 0; i < m.Length; i++) if (m[i] != 0) n++;
        return n;
    }

    /// <summary>Total mass, centre of mass and inertia tensor about the centre of the (masked) point masses.</summary>
    public static MassProperties Properties(Lod lod, byte[]? pointMask = null)
    {
        var m = Values(lod);
        if (m is null) return MassProperties.Empty;
        var P = lod.Positions;
        double t = 0, cx = 0, cy = 0, cz = 0;
        int n = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (pointMask is not null && pointMask[i] == 0 || m[i] == 0) continue;
            double w = m[i];
            t += w;
            cx += w * P[i * 3];
            cy += w * P[i * 3 + 1];
            cz += w * P[i * 3 + 2];
            n++;
        }
        if (n == 0 || t == 0) return MassProperties.Empty with { Points = n };
        cx /= t; cy /= t; cz /= t;
        double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (pointMask is not null && pointMask[i] == 0 || m[i] == 0) continue;
            double w = m[i], x = P[i * 3] - cx, y = P[i * 3 + 1] - cy, z = P[i * 3 + 2] - cz;
            xx += w * x * x; yy += w * y * y; zz += w * z * z;
            xy += w * x * y; xz += w * x * z; yz += w * y * z;
        }
        return new MassProperties(t, n, new Vec3(cx, cy, cz), yy + zz, xx + zz, xx + yy, -xy, -xz, -yz);
    }

    /// <summary>Replaces the #Mass# values (creates the tag when missing; inserted first like Object Builder files).</summary>
    public static Lod WithValues(Lod lod, float[] mass)
    {
        if (mass.Length != lod.PointCount) throw new ArgumentException("mass array size differs from the point count");
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is MassTag t) return lod with { Tags = lod.Tags.SetItem(i, t with { Mass = mass }) };
        return lod with { Tags = lod.Tags.Insert(0, new MassTag(1, mass)) };
    }

    /// <summary>A writable copy of the masses (zeros when there is no tag).</summary>
    static float[] Copy(Lod lod) => Values(lod) is { } m ? (float[])m.Clone() : new float[lod.PointCount];

    /// <summary>Sets the mass of every masked point to <paramref name="value"/>.</summary>
    public static Lod SetEach(Lod lod, byte[] pointMask, double value)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentException("mass must be a non-negative number");
        var m = Copy(lod);
        int n = 0;
        for (int i = 0; i < m.Length; i++)
            if (pointMask[i] != 0) { m[i] = (float)value; n++; }
        return n == 0 ? lod : WithValues(lod, m);
    }

    /// <summary>
    /// Distributes <paramref name="total"/> over the masked points by <paramref name="mode"/>. When the mode yields no
    /// weight (no closed component for <see cref="MassDistribution.Volume"/>, no face for Area) the mass is split
    /// equally and <paramref name="fallback"/> is true. Unmasked points keep their mass.
    /// </summary>
    public static Lod Distribute(Lod lod, byte[] pointMask, double total, MassDistribution mode, out bool fallback)
    {
        if (!double.IsFinite(total) || total < 0) throw new ArgumentException("mass must be a non-negative number");
        var w = Weights(lod, pointMask, mode);
        double sum = 0;
        for (int i = 0; i < w.Length; i++) sum += w[i];
        fallback = false;
        if (sum <= 0 && mode != MassDistribution.Equal)
        {
            fallback = true;
            w = Weights(lod, pointMask, MassDistribution.Equal);
            sum = 0;
            for (int i = 0; i < w.Length; i++) sum += w[i];
        }
        if (sum <= 0) return lod;
        var m = Copy(lod);
        for (int i = 0; i < m.Length; i++)
            if (pointMask[i] != 0) m[i] = (float)(total * w[i] / sum);
        return WithValues(lod, m);
    }

    /// <summary>
    /// Mass from a density (kg/m³): every closed component whose points are all masked gets density × volume, split
    /// equally among its points. Returns the LOD unchanged (and 0) when no closed component qualifies.
    /// </summary>
    public static Lod FromDensity(Lod lod, byte[] pointMask, double density, out double total, out int components)
    {
        if (!double.IsFinite(density) || density < 0) throw new ArgumentException("density must be a non-negative number");
        var m = Copy(lod);
        total = 0;
        components = 0;
        foreach (var (pts, volume) in ClosedComponents(lod, pointMask))
        {
            double each = density * volume / pts.Length;
            foreach (int p in pts) m[p] = (float)each;
            total += density * volume;
            components++;
        }
        return components == 0 ? lod : WithValues(lod, m);
    }

    /// <summary>Distribution weights of the masked points (0 elsewhere).</summary>
    public static double[] Weights(Lod lod, byte[] pointMask, MassDistribution mode)
    {
        int pc = lod.PointCount;
        var w = new double[pc];
        switch (mode)
        {
            case MassDistribution.Equal:
                for (int i = 0; i < pc; i++) if (pointMask[i] != 0) w[i] = 1;
                break;
            case MassDistribution.Area:
            {
                var fp = lod.FacePoints;
                var nv = lod.FaceVertCount;
                for (int f = 0; f < lod.FaceCount; f++)
                {
                    int n = nv[f];
                    double share = Measure.FaceArea(lod, f) / n;
                    for (int k = 0; k < n; k++)
                    {
                        uint p = fp[f * 4 + k];
                        if (p < pc && pointMask[p] != 0) w[p] += share;
                    }
                }
                break;
            }
            case MassDistribution.Volume:
                foreach (var (pts, volume) in ClosedComponents(lod, pointMask))
                    foreach (int p in pts) w[p] = volume / pts.Length;
                break;
        }
        return w;
    }

    /// <summary>Closed components (proxies excluded) whose points are all masked, with their absolute volume.</summary>
    static IEnumerable<(int[] Points, double Volume)> ClosedComponents(Lod lod, byte[] pointMask)
    {
        var faces = Selection.FacesFromPoints(lod, pointMask);
        foreach (var r in ComponentAnalysis.Analyze(lod, faces, convexity: false))
        {
            if (!r.IsClosed || r.Volume <= 0) continue;
            bool all = true;
            foreach (int p in r.Component.Points) if (pointMask[p] == 0) { all = false; break; }
            if (all) yield return (r.Component.Points, r.Volume);
        }
    }

    /// <summary>Zeroes the masked points (null = all: the #Mass# tag is removed). A tag left all-zero is removed.</summary>
    public static Lod Clear(Lod lod, byte[]? pointMask)
    {
        if (lod.MassTag is null) return lod;
        if (pointMask is not null && Values(lod) is { } cur)
        {
            var m = (float[])cur.Clone();
            bool any = false, changed = false;
            for (int i = 0; i < m.Length; i++)
            {
                if (pointMask[i] != 0 && m[i] != 0) { m[i] = 0; changed = true; }
                if (m[i] != 0) any = true;
            }
            if (!changed) return lod;
            if (any) return WithValues(lod, m);
        }
        return lod with { Tags = lod.Tags.RemoveAll(t => t is MassTag) };
    }

    /// <summary>Mass of every named selection (its points with a non-zero weight), in tag order. Proxies included.</summary>
    public static (string Name, double Mass, int Points)[] PerSelection(Lod lod)
    {
        var m = Values(lod);
        var list = new List<(string, double, int)>();
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s) continue;
            double sum = 0;
            int n = 0;
            var pts = s.Points;
            int len = Math.Min(pts.Length, lod.PointCount);
            for (int i = 0; i < len; i++)
            {
                if (pts[i] == 0) continue;
                n++;
                if (m is not null) sum += m[i];
            }
            list.Add((s.Name, sum, n));
        }
        return [.. list];
    }

    /// <summary>Index of the LOD whose mass the engine uses: the first Geometry LOD, else -1.</summary>
    public static int GeometryLodIndex(P3DModel model)
    {
        for (int i = 0; i < model.Lods.Length; i++)
            if (LodNames.IsGeometry(model.Lods[i].Resolution)) return i;
        return -1;
    }
}
