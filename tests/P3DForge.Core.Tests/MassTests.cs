using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Topology;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class MassTests
{
    static Lod Box(double s, Vec3 at) => Primitives.CreateBox(Lod.Empty(0), new BoxParams(s, s, s) { At = at });

    /// <summary>Unit cube at the origin (points 0..7) + 2 m cube at x = 10 (points 8..15); no mass.</summary>
    static Lod TwoBoxes()
    {
        var a = Box(1, default);
        var b = Box(2, new Vec3(10, 0, 0));
        return Structure.Append(a, Structure.Extract(b, All(b.PointCount), All(b.FaceCount)), selectNew: false);
    }

    [Fact]
    public void Totals_centre_and_inertia_of_point_masses()
    {
        var lod = Mass.WithValues(TwoBoxes(), new float[16]);
        Assert.Equal(0, Mass.Total(lod));
        Assert.False(Mass.Properties(lod).HasMass);

        // 2 kg at each corner (x ±0.5) of the unit cube's -Y face... use two opposite corners.
        var m = new float[16];
        int p0 = 0, p1 = 0;
        for (int i = 0; i < 8; i++)
        {
            var q = Pos(lod, i);
            if (q[0] < 0 && q[1] < 0 && q[2] < 0) p0 = i;
            if (q[0] > 0 && q[1] > 0 && q[2] > 0) p1 = i;
        }
        m[p0] = 2; m[p1] = 6;
        lod = Mass.WithValues(lod, m);
        Assert.Equal(8, Mass.Total(lod));
        Assert.Equal(2, Mass.Total(lod, MaskOf(16, p0)));
        Assert.Equal(2, Mass.PointsWithMass(lod));
        var pr = Mass.Properties(lod);
        Assert.Equal(8, pr.Total);
        Assert.Equal(2, pr.Points);
        // Centre: weighted between (−.5,−.5,−.5) and (.5,.5,.5) → 0.25 along the diagonal.
        Assert.Equal(0.25, pr.Center.X, 6);
        Assert.Equal(0.25, pr.Center.Y, 6);
        Assert.Equal(0.25, pr.Center.Z, 6);
        // Distances from the centre: 0.75·(1,1,1) and 0.25·(1,1,1). Ixx = Σ m (y² + z²) = 2·2·0.5625 + 6·2·0.0625 = 3.
        Assert.Equal(3, pr.Ixx, 6);
        Assert.Equal(3, pr.Iyy, 6);
        Assert.Equal(-1.5, pr.Ixy, 6);
    }

    [Fact]
    public void SetEach_Distribute_and_Clear()
    {
        var lod = TwoBoxes();
        Assert.Null(lod.MassTag);
        var first = MaskOf(16, Enumerable.Range(0, 8).ToArray());

        var each = Mass.SetEach(lod, first, 1.5);
        CheckLod(each);
        Assert.Null(lod.MassTag); // input untouched
        Assert.Equal(12, Mass.Total(each), 5);
        Assert.Equal(0, Mass.Total(each, MaskOf(16, 8)));

        var eq = Mass.Distribute(each, All(16), 160, MassDistribution.Equal, out bool fb);
        Assert.False(fb);
        Assert.Equal(160, Mass.Total(eq), 3);
        Assert.Equal(10, Mass.Values(eq)![3], 4);

        // By volume: 1 m³ and 8 m³ → 1:8.
        var vol = Mass.Distribute(lod, All(16), 900, MassDistribution.Volume, out fb);
        Assert.False(fb);
        Assert.Equal(100, Mass.Total(vol, first), 3);
        Assert.Equal(800, Mass.Total(vol, MaskOf(16, Enumerable.Range(8, 8).ToArray())), 3);

        // By area: 6 m² and 24 m² → 1:4.
        var area = Mass.Distribute(lod, All(16), 500, MassDistribution.Area, out fb);
        Assert.Equal(100, Mass.Total(area, first), 3);

        // Volume over an open piece (one face of a box) falls back to equal.
        var faceOnly = Selection.PointsFromFaces(lod, MaskOf(12, 0));
        var open = Mass.Distribute(lod, faceOnly, 40, MassDistribution.Volume, out fb);
        Assert.True(fb);
        Assert.Equal(40, Mass.Total(open), 4);

        var dens = Mass.FromDensity(lod, All(16), 1000, out double total, out int comps);
        Assert.Equal(2, comps);
        Assert.Equal(9000, total, 3);
        Assert.Equal(9000, Mass.Total(dens), 1);

        var cleared = Mass.Clear(vol, first);
        Assert.Equal(0, Mass.Total(cleared, first));
        Assert.Equal(800, Mass.Total(cleared), 3);
        Assert.Null(Mass.Clear(vol, null).MassTag);
        Assert.Null(Mass.Clear(cleared, All(16)).MassTag); // all zero → tag removed
        Assert.Same(lod, Mass.Clear(lod, null));
        Assert.Throws<ArgumentException>(() => Mass.SetEach(lod, first, -1));
    }

    [Fact]
    public void Mass_survives_topology_edits_and_round_trips()
    {
        var lod = Mass.Distribute(TwoBoxes(), All(16), 90, MassDistribution.Volume, out _);
        var hulled = HullOps.ComponentHulls(lod, null, out _);
        CheckLod(hulled);
        Assert.Equal(90, Mass.Total(hulled), 3);
        var rt = RoundTrip(lod);
        Assert.Equal(Mass.Values(lod), Mass.Values(rt));
        var per = Mass.PerSelection(HullOps.CreateComponentSelections(lod, null, out _));
        Assert.Equal(10, per.Single(s => s.Name == "Component01").Mass, 3);
        Assert.Equal(80, per.Single(s => s.Name == "Component02").Mass, 3);
    }
}
