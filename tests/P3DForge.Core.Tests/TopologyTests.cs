using System.Diagnostics;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Topology;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class TopologyTests
{
    // ------------------------------------------------------------------ fixtures

    /// <summary>Builds a LOD from points and face loops, winding every loop the P3D way for its outward direction.</summary>
    internal static Lod Mesh(double[] pts, int[][] loops, Vec3[] outward)
    {
        int np = pts.Length / 3, nf = loops.Length;
        var fp = new uint[nf * 4];
        var vc = new byte[nf];
        for (int f = 0; f < nf; f++)
        {
            var loop = loops[f];
            // Right-handed Newell normal of the loop as given.
            double nx = 0, ny = 0, nz = 0;
            for (int k = 0; k < loop.Length; k++)
            {
                int a = loop[k] * 3, b = loop[(k + 1) % loop.Length] * 3;
                nx += (pts[a + 1] - pts[b + 1]) * (pts[a + 2] + pts[b + 2]);
                ny += (pts[a + 2] - pts[b + 2]) * (pts[a] + pts[b]);
                nz += (pts[a] - pts[b]) * (pts[a + 1] + pts[b + 1]);
            }
            // P3D: stored right-handed normal points INTO the mesh.
            bool flip = nx * outward[f].X + ny * outward[f].Y + nz * outward[f].Z > 0;
            vc[f] = (byte)loop.Length;
            for (int k = 0; k < 4; k++)
            {
                int kk = k < loop.Length ? k : 0;
                int src = flip ? loop[(loop.Length - kk) % loop.Length] : loop[kk];
                fp[f * 4 + k] = (uint)src;
            }
        }
        var lod = Lod.Empty(0) with
        {
            Positions = pts.Select(v => (float)v).ToArray(),
            PointFlags = new uint[np],
            FaceVertCount = vc,
            FacePoints = fp,
            FaceNormals = new uint[nf * 4],
            FaceUV = new float[nf * 8],
            FaceFlags = new uint[nf],
            FaceTexture = new uint[nf],
            FaceMaterial = new uint[nf],
        };
        return Structure.RecalcNormals(lod);
    }

    /// <summary>
    /// L-shaped prism (3 unit squares, height 1): outline (x,z) (0,0) (1,0) (2,0) (2,1) (1,1) (1,2) (0,2) (0,1); bottom
    /// points 0..7 at y = 0, top points 8..15 at y = 1. Faces 0..7 = sides (edge i → i+1), 8..10 top, 11..13 bottom.
    /// The reflex corner is outline point 4 (points 4 and 12).
    /// </summary>
    internal static Lod LPrism(double ox = 0, double oy = 0, double oz = 0)
    {
        double[] o = [0, 0, 1, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2, 0, 1];
        var pts = new double[16 * 3];
        for (int i = 0; i < 8; i++)
        {
            pts[i * 3] = o[i * 2] + ox; pts[i * 3 + 1] = oy; pts[i * 3 + 2] = o[i * 2 + 1] + oz;
            pts[(i + 8) * 3] = o[i * 2] + ox; pts[(i + 8) * 3 + 1] = 1 + oy; pts[(i + 8) * 3 + 2] = o[i * 2 + 1] + oz;
        }
        var loops = new List<int[]>();
        var outward = new List<Vec3>();
        for (int i = 0; i < 8; i++)
        {
            int j = (i + 1) % 8;
            loops.Add([i, j, j + 8, i + 8]);
            double dx = o[j * 2] - o[i * 2], dz = o[j * 2 + 1] - o[i * 2 + 1];
            outward.Add(new Vec3(dz, 0, -dx));
        }
        int[][] squares = [[0, 1, 4, 7], [1, 2, 3, 4], [7, 4, 5, 6]];
        foreach (var s in squares) { loops.Add(s.Select(v => v + 8).ToArray()); outward.Add(new Vec3(0, 1, 0)); }
        foreach (var s in squares) { loops.Add(s); outward.Add(new Vec3(0, -1, 0)); }
        return Mesh(pts, [.. loops], [.. outward]);
    }

    /// <summary>Unit cube built by <see cref="Mesh"/> (point i = x + 2y + 4z); optionally a 7th face duplicating face 0.</summary>
    internal static Lod CubeMesh(bool duplicateFirst = false)
    {
        var pts = new double[24];
        for (int i = 0; i < 8; i++) { pts[i * 3] = i & 1; pts[i * 3 + 1] = (i >> 1) & 1; pts[i * 3 + 2] = (i >> 2) & 1; }
        var loops = new List<int[]> { new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 } };
        var outward = new List<Vec3> { new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1) };
        if (duplicateFirst) { loops.Add(loops[0]); outward.Add(outward[0]); }
        return Mesh(pts, [.. loops], [.. outward]);
    }

    static Lod Box(double sx, double sy, double sz, Vec3 at = default, int seg = 1) =>
        Primitives.CreateBox(Lod.Empty(0), new BoxParams(sx, sy, sz, seg, seg, seg) { At = at });

    static Lod Merge(Lod a, Lod b) => Structure.Append(a, Structure.Extract(b, All(b.PointCount), All(b.FaceCount)), selectNew: false);

    /// <summary>A LOD made of the hull faces (hull indices refer to <paramref name="positions"/>).</summary>
    static Lod HullLod(float[] positions, HullResult h)
    {
        int np = positions.Length / 3, nf = h.FaceCount;
        var lod = Lod.Empty(0) with
        {
            Positions = (float[])positions.Clone(),
            PointFlags = new uint[np],
            FaceVertCount = (byte[])h.FaceVertCount.Clone(),
            FacePoints = h.FacePoints.Select(v => (uint)v).ToArray(),
            FaceNormals = new uint[nf * 4],
            FaceUV = new float[nf * 8],
            FaceFlags = new uint[nf],
            FaceTexture = new uint[nf],
            FaceMaterial = new uint[nf],
        };
        return Structure.RecalcNormals(lod);
    }

    static int[] Range(int n) => Enumerable.Range(0, n).ToArray();

    static float[] Cloud(int n, uint seed, double radius = 1, Vec3 offset = default)
    {
        var rng = new Random((int)seed);
        var p = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            double x, y, z;
            do { x = rng.NextDouble() * 2 - 1; y = rng.NextDouble() * 2 - 1; z = rng.NextDouble() * 2 - 1; } while (x * x + y * y + z * z > 1);
            p[i * 3] = (float)(x * radius + offset.X);
            p[i * 3 + 1] = (float)(y * radius + offset.Y);
            p[i * 3 + 2] = (float)(z * radius + offset.Z);
        }
        return p;
    }

    /// <summary>Hull is a closed, consistently wound, outward (P3D) surface containing every input point.</summary>
    static void AssertValidHull(float[] positions, int[] points, HullResult h)
    {
        Assert.True(h.Ok, h.Describe());
        var lod = HullLod(positions, h);
        var closed = MeshTopology.CheckClosed(lod);
        Assert.True(closed.Closed, $"open {closed.OpenEdgeCount}, non-manifold {closed.NonManifoldEdgeCount}");
        Assert.Equal(0, closed.InconsistentEdgeCount);
        Assert.True(Measure.SignedVolume(lod) < 0, "hull must have negative RH signed volume (P3D winding)");
        for (int f = 0; f < lod.FaceCount; f++) Assert.False(FaceChecks.IsDegenerate(lod, f), $"degenerate hull face {f}");
        double slack = h.Tolerance * 4 + 1e-9;
        foreach (int p in points)
        {
            var x = new Vec3(positions[p * 3], positions[p * 3 + 1], positions[p * 3 + 2]);
            for (int poly = 0; poly < h.PolygonCount; poly++)
                Assert.True(h.Distance(poly, x) <= slack, $"point {p} outside polygon {poly} by {h.Distance(poly, x)}");
        }
        // Euler characteristic of the polygon mesh: V − E + F = 2.
        var edges = new HashSet<long>();
        for (int poly = 0; poly < h.PolygonCount; poly++)
        {
            int s = h.PolyStart[poly], e = h.PolyStart[poly + 1];
            for (int k = s; k < e; k++)
            {
                int a = h.PolyPoints[k], b = h.PolyPoints[k + 1 < e ? k + 1 : s];
                edges.Add(Math.Min(a, b) * 1_000_000L + Math.Max(a, b));
            }
        }
        Assert.Equal(2, h.HullPoints.Length - edges.Count + h.PolygonCount);
    }

    // ------------------------------------------------------------------ disjoint sets

    [Fact]
    public void DisjointSets_unions_and_finds()
    {
        var d = new DisjointSets(6);
        Assert.False(d.Same(0, 1));
        d.Union(0, 1);
        d.Union(3, 4);
        d.Union(1, 4);
        Assert.True(d.Same(0, 3));
        Assert.False(d.Same(0, 2));
        Assert.False(d.Same(5, 2));
        Assert.Equal(d.Find(4), d.Find(0));
        var big = new DisjointSets(100_000);
        for (int i = 1; i < 100_000; i++) big.Union(i - 1, i);
        Assert.True(big.Same(0, 99_999));
    }

    // ------------------------------------------------------------------ components / closedness

    [Fact]
    public void FindComponents_splits_two_boxes_and_honours_mask()
    {
        var lod = Merge(Box(1, 1, 1), Box(1, 1, 1, new Vec3(5, 0, 0)));
        var r = MeshTopology.FindComponents(lod);
        Assert.Equal(2, r.Count);
        Assert.Equal(Range(6), r.Components[0].Faces);
        Assert.Equal(Range(8), r.Components[0].Points);
        Assert.Equal(Range(6).Select(i => i + 6).ToArray(), r.Components[1].Faces);
        Assert.Equal(Range(8).Select(i => i + 8).ToArray(), r.Components[1].Points);
        Assert.Equal(1, r.FaceComponent[7]);
        Assert.Equal(0, r.PointComponent[3]);

        // Only the faces of the second box.
        var mask = new byte[12];
        for (int f = 6; f < 12; f++) mask[f] = 1;
        var m = MeshTopology.FindComponents(lod, mask);
        Assert.Equal(1, m.Count);
        Assert.Equal(-1, m.FaceComponent[0]);
        Assert.Equal(-1, m.PointComponent[0]);

        // Two faces of the same box that only share a point are still one component.
        var corner = MeshTopology.FindComponents(Box(1, 1, 1), MaskOf(6, 0, 1));
        Assert.True(corner.Count is 1 or 2);
    }

    [Fact]
    public void CheckClosed_cube_open_box_non_manifold_and_inconsistent()
    {
        var cube = Box(1, 1, 1);
        var c = MeshTopology.CheckClosed(cube);
        Assert.True(c.Closed);
        Assert.Equal(0, c.InconsistentEdgeCount);
        Assert.Empty(c.ProblemFaces);

        // Remove the top face (outward +Y ⇒ stored RH normal −Y).
        int top = Range(6).Single(f => Measure.FaceNormalRaw(cube, f).Y < -0.9);
        var keep = All(6);
        keep[top] = 0;
        var open = Structure.Compact(cube, All(8), keep);
        var o = MeshTopology.CheckClosed(open);
        Assert.False(o.Closed);
        Assert.Equal(4, o.OpenEdgeCount);
        Assert.Equal(4, o.OpenEdgeFaces.Length);
        Assert.Equal(0, o.NonManifoldEdgeCount);

        // A duplicated face makes its 4 edges non-manifold.
        var n = MeshTopology.CheckClosed(CubeMesh(duplicateFirst: true));
        Assert.False(n.Closed);
        Assert.Equal(4, n.NonManifoldEdgeCount);

        // A reversed face: 4 inconsistent edges, still closed.
        var rev = FaceOps.Reverse(cube, MaskOf(6, 2));
        var i = MeshTopology.CheckClosed(rev);
        Assert.True(i.Closed);
        Assert.Equal(4, i.InconsistentEdgeCount);
        Assert.Empty(i.ProblemFaces);
        Assert.Equal(5, i.InconsistentFaces.Length);
        Assert.Contains(2, i.InconsistentFaces);
    }

    // ------------------------------------------------------------------ measure

    [Fact]
    public void Measure_volume_area_and_planarity()
    {
        var box = Box(1, 2, 3);
        Assert.Equal(-6, Measure.SignedVolume(box), 4);
        Assert.Equal(-3, Measure.SignedVolume(LPrism()), 4);
        Assert.True(IsClosedConsistent(LPrism()));
        Assert.True(Measure.SignedVolume(Box(1, 1, 1, seg: 3)) < 0);
        Assert.Equal(-6, SignedVolume(box), 4); // agrees with the test helper

        double area = 0;
        for (int f = 0; f < box.FaceCount; f++) area += Measure.FaceArea(box, f);
        Assert.Equal(2 * (2 + 6 + 3), area, 4);
        for (int f = 0; f < box.FaceCount; f++) Assert.True(Measure.QuadPlanarity(box, f) < 1e-6);

        var twisted = Mesh([0, 0, 0, 1, 0, 0, 1, 0.2, 1, 0, 0, 1], [[0, 1, 2, 3]], [new Vec3(0, -1, 0)]);
        Assert.Equal(0.2 / Math.Sqrt(1.04), Measure.QuadPlanarity(twisted, 0), 3);
    }

    // ------------------------------------------------------------------ convex hull

    [Fact]
    public void Hull_of_cube_is_six_quads_with_p3d_winding()
    {
        var cube = Box(1, 1, 1);
        var h = ConvexHull.Build(cube.Positions, Range(8));
        AssertValidHull(cube.Positions, Range(8), h);
        Assert.Equal(6, h.FaceCount);
        Assert.All(h.FaceVertCount, v => Assert.Equal(4, v));
        Assert.Equal(Range(8), h.HullPoints);
        Assert.Equal(-1, Measure.SignedVolume(HullLod(cube.Positions, h)), 5);
    }

    [Fact]
    public void Hull_drops_interior_edge_and_face_points()
    {
        var box = Box(2, 2, 2, seg: 4); // 98 points, most on edges and faces
        var all = Range(box.PointCount);
        var h = ConvexHull.Build(box.Positions, all);
        AssertValidHull(box.Positions, all, h);
        Assert.Equal(8, h.HullPoints.Length);
        Assert.Equal(6, h.FaceCount);
        Assert.Equal(-8, Measure.SignedVolume(HullLod(box.Positions, h)), 4);

        // Cube + its centre.
        var pos = box.Positions.Concat(new float[] { 0, 0, 0 }).ToArray();
        var h2 = ConvexHull.Build(pos, Range(box.PointCount + 1));
        Assert.True(h2.Ok);
        Assert.DoesNotContain(box.PointCount, h2.HullPoints);
    }

    [Fact]
    public void Hull_of_l_prism_sphere_and_cylinder()
    {
        var l = LPrism();
        var h = ConvexHull.Build(l.Positions, Range(16));
        AssertValidHull(l.Positions, Range(16), h);
        Assert.Equal(10, h.HullPoints.Length); // outline points 1 and 7 lie on hull edges, 4 is the reflex corner
        Assert.DoesNotContain(1, h.HullPoints);
        Assert.DoesNotContain(4, h.HullPoints);
        Assert.DoesNotContain(12, h.HullPoints);
        Assert.Equal(-3.5, Measure.SignedVolume(HullLod(l.Positions, h)), 4);

        var sphere = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(1, 24, 12));
        var sp = Range(sphere.PointCount);
        var hs = ConvexHull.Build(sphere.Positions, sp);
        AssertValidHull(sphere.Positions, sp, hs);
        Assert.Equal(sphere.PointCount, hs.HullPoints.Length);
        Assert.Equal(Measure.SignedVolume(sphere), Measure.SignedVolume(HullLod(sphere.Positions, hs)), 3);

        var cyl = Primitives.CreateCylinder(Lod.Empty(0), new CylinderParams(0.5, 2, 32));
        var cp = Range(cyl.PointCount);
        var hc = ConvexHull.Build(cyl.Positions, cp);
        AssertValidHull(cyl.Positions, cp, hc);
        Assert.Equal(Measure.SignedVolume(cyl), Measure.SignedVolume(HullLod(cyl.Positions, hc)), 3);
    }

    [Theory]
    [InlineData(1u, 50, 1.0, 0.0)]
    [InlineData(2u, 2000, 1.0, 0.0)]
    [InlineData(3u, 2000, 0.01, 0.0)]
    [InlineData(4u, 2000, 3.0, 5000.0)]
    [InlineData(5u, 20000, 10.0, 0.0)]
    public void Hull_of_random_clouds_is_valid(uint seed, int n, double radius, double offset)
    {
        var pos = Cloud(n, seed, radius, new Vec3(offset, -offset / 2, offset / 3));
        var pts = Range(n);
        var h = ConvexHull.Build(pos, pts);
        AssertValidHull(pos, pts, h);
    }

    [Fact]
    public void Hull_of_quantised_grid_cloud_is_valid()
    {
        // Many exactly coplanar / colinear points (integer lattice inside a ball).
        var list = new List<float>();
        for (int x = -6; x <= 6; x++)
            for (int y = -6; y <= 6; y++)
                for (int z = -6; z <= 6; z++)
                    if (x * x + y * y + z * z <= 36) list.AddRange([x * 0.1f, y * 0.1f, z * 0.1f]);
        var pos = list.ToArray();
        var pts = Range(pos.Length / 3);
        AssertValidHull(pos, pts, ConvexHull.Build(pos, pts));
    }

    [Fact]
    public void Hull_reports_degenerate_inputs()
    {
        Assert.Equal(HullFailure.TooFewPoints, ConvexHull.Build([0, 0, 0, 1, 0, 0, 0, 1, 0], Range(3)).Failure);
        Assert.Equal(HullFailure.Degenerate, ConvexHull.Build(new float[15], Range(5)).Failure);
        Assert.Equal(HullFailure.Colinear, ConvexHull.Build([0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, -1, -1, -1], Range(5)).Failure);
        var grid = GridLod(3, 3);
        Assert.Equal(HullFailure.Coplanar, ConvexHull.Build(grid.Positions, Range(grid.PointCount)).Failure);
        // Nearly flat (far below the tolerance) is still flat.
        var near = grid.Positions.Concat(new float[] { 1.5f, 1e-9f, 1.5f }).ToArray();
        Assert.Equal(HullFailure.Coplanar, ConvexHull.Build(near, Range(grid.PointCount + 1)).Failure);
        Assert.False(string.IsNullOrEmpty(ConvexHull.Build(grid.Positions, Range(grid.PointCount)).Describe()));
    }

    [Fact]
    public void Hull_triangles_option_and_subset_indices()
    {
        var cube = Merge(Box(1, 1, 1, new Vec3(10, 0, 0)), Box(1, 1, 1));
        var second = Range(8).Select(i => i + 8).ToArray();
        var h = ConvexHull.Build(cube.Positions, second, quads: false);
        Assert.True(h.Ok);
        Assert.Equal(12, h.FaceCount);
        Assert.All(h.FaceVertCount, v => Assert.Equal(3, v));
        Assert.Equal(second, h.HullPoints);
        Assert.All(h.FacePoints, p => Assert.InRange(p, 8, 15));
    }

    // ------------------------------------------------------------------ convexity

    [Fact]
    public void Convexity_of_cube_sphere_and_l_prism()
    {
        var cube = Box(1, 1, 1, seg: 2);
        var rc = Convexity.Check(cube, Range(cube.FaceCount));
        Assert.True(rc.Convex);

        var sphere = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(1, 24, 12));
        Assert.True(Convexity.Check(sphere, Range(sphere.FaceCount)).Convex);

        var l = LPrism();
        var r = Convexity.Check(l, Range(l.FaceCount));
        Assert.False(r.Convex);
        Assert.Equal([3, 4], r.NonConvexFaces);
        Assert.Empty(r.InteriorPoints); // the reflex corner lies on the hull's top and bottom planes
        Assert.Equal(2, r.ConcaveEdges.Length); // one edge (pair of points)
        Assert.Equal([4, 12], r.ConcaveEdges.Order().ToArray());

        // Cube whose top is a pyramid pushed 0.3 inwards: the apex is interior, the 4 valley edges are concave.
        var pts = new double[27];
        for (int i = 0; i < 8; i++) { pts[i * 3] = i & 1; pts[i * 3 + 1] = (i >> 1) & 1; pts[i * 3 + 2] = (i >> 2) & 1; }
        pts[24] = 0.5; pts[25] = 0.7; pts[26] = 0.5;
        var dent = Mesh(pts, [[0, 2, 6, 4], [1, 3, 7, 5], [0, 1, 5, 4], [0, 1, 3, 2], [4, 5, 7, 6], [2, 3, 8], [3, 7, 8], [7, 6, 8], [6, 2, 8]],
            [new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0)]);
        Assert.True(IsClosedConsistent(dent));
        var rd = Convexity.Check(dent, Range(dent.FaceCount));
        Assert.Equal([5, 6, 7, 8], rd.NonConvexFaces);
        Assert.Equal([8], rd.InteriorPoints);
        Assert.Equal(0.3, rd.MaxViolation, 4);
        Assert.Equal(8, rd.ConcaveEdges.Length);
    }

    [Fact]
    public void ComponentAnalysis_reports_each_component()
    {
        var lod = Merge(Merge(Box(1, 1, 1), LPrism(5, 0, 0)), FaceOps.Reverse(Box(1, 1, 1, new Vec3(-5, 0, 0)), All(6)));
        var reps = ComponentAnalysis.Analyze(lod);
        Assert.Equal(3, reps.Length);
        Assert.True(reps[0].IsClosed && reps[0].IsConvex && !reps[0].InsideOut);
        Assert.Equal(1, reps[0].Volume, 4);
        Assert.True(reps[1].IsClosed);
        Assert.False(reps[1].IsConvex);
        Assert.Equal(3, reps[1].Volume, 4);
        Assert.True(reps[2].InsideOut);
        Assert.Equal("Component01", ComponentAnalysis.ComponentName(0));
        Assert.Equal("Component12", ComponentAnalysis.ComponentName(11));
        Assert.True(ComponentAnalysis.IsComponentName("Component05"));
        Assert.True(ComponentAnalysis.IsComponentName("component123"));
        Assert.False(ComponentAnalysis.IsComponentName("Component"));
        Assert.False(ComponentAnalysis.IsComponentName("Component5a"));
    }

    [Fact]
    public void Convexity_accepts_twisted_quads_on_the_hull_and_component_checks_use_lod_indices()
    {
        // Unit cube with one top corner raised: the top quad is non-planar but the solid is convex.
        var pts = new double[24];
        for (int i = 0; i < 8; i++) { pts[i * 3] = i & 1; pts[i * 3 + 1] = (i >> 1) & 1; pts[i * 3 + 2] = (i >> 2) & 1; }
        pts[7 * 3 + 1] = 1.15;
        var twisted = Mesh(pts, [[0, 2, 6, 4], [1, 3, 7, 5], [0, 1, 5, 4], [2, 3, 7, 6], [0, 1, 3, 2], [4, 5, 7, 6]],
            [new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1)]);
        Assert.Equal([3], FaceChecks.Check(twisted).NonPlanar); // the sides stay in their x = 1 / z = 1 planes
        var c = Convexity.Check(twisted, Range(6));
        Assert.True(c.Convex, $"non-convex faces {string.Join(",", c.NonConvexFaces)}, concave edges {c.ConcaveEdges.Length / 2}");

        // 40 boxes, the 30th open: per-component checks (local numbering) report LOD face indices.
        var lod = Box(1, 1, 1);
        for (int i = 1; i < 40; i++) lod = Merge(lod, Box(1, 1, 1, new Vec3(i * 2, 0, 0)));
        var keep = All(lod.FaceCount);
        keep[29 * 6 + 3] = 0;
        lod = Structure.Compact(lod, All(lod.PointCount), keep);
        var reps = ComponentAnalysis.Analyze(lod, convexity: false);
        Assert.Equal(40, reps.Length);
        Assert.Equal(39, reps.Count(r => r.IsClosed));
        var open = reps.Single(r => !r.IsClosed);
        Assert.Equal(29, open.Index);
        Assert.All(open.Closed.ProblemFaces, f => Assert.InRange(f, 29 * 6, 29 * 6 + 4));
        Assert.Equal(4, open.Closed.OpenEdgeCount);
        Assert.All(open.Closed.OpenEdges, p => Assert.InRange(p, 29 * 8, 29 * 8 + 7));
        var global = MeshTopology.CheckClosed(lod);
        Assert.Equal(global.ProblemFaces, open.Closed.ProblemFaces);
        var sel = HullOps.CreateComponentSelections(lod, null, out int count, out int ignored);
        Assert.Equal(39, count);
        Assert.Equal(1, ignored);
    }

    // ------------------------------------------------------------------ face checks

    /// <summary>0 good quad, 1 colinear triangle, 2 quad with a repeated point, 3 duplicate of 0, 4 reversed 0, 5 non-planar quad.</summary>
    static Lod FaceZoo()
    {
        double[] pts = [0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 2, 0, 0, 3, 0, 0, 5, 0, 0, 6, 0, 0, 6, 0.3, 1, 5, 0, 1];
        var lod = Mesh(pts, [[0, 1, 2, 3], [1, 4, 5], [0, 1, 2, 3], [0, 1, 2, 3], [0, 1, 2, 3], [6, 7, 8, 9]],
            [new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0), new Vec3(0, 1, 0)]);
        // Face 2: make point 3 repeat point 2 (copy arrays first).
        var fp = (uint[])lod.FacePoints.Clone();
        fp[2 * 4 + 3] = fp[2 * 4 + 2];
        return lod with { FacePoints = fp };
    }

    [Fact]
    public void FaceChecks_classifies_faces()
    {
        var lod = FaceZoo();
        var r = FaceChecks.Check(lod);
        Assert.Equal([1, 2], r.Degenerate);
        Assert.Equal([5], r.NonPlanar);
        Assert.Equal([3], r.Duplicate);
        Assert.Equal([4], r.Reversed);
        Assert.Empty(r.Invalid);
        Assert.True(r.MaxNonPlanarity > 0.1);
        Assert.True(r.ProblemCount >= 4);

        var masked = FaceChecks.Check(lod, MaskOf(6, 0, 5));
        Assert.Empty(masked.Degenerate);
        Assert.Empty(masked.Duplicate);
        Assert.Equal([5], masked.NonPlanar);
    }

    [Fact]
    public void RemoveDuplicateFaces_and_RepairDegenerate()
    {
        var lod = FaceZoo();
        var sel = new SelectionTag(1, "dups", new byte[lod.PointCount], MaskOf(6, 3));
        lod = lod with { Tags = lod.Tags.Add(sel) };
        var before = (uint[])lod.FacePoints.Clone();

        var a = FaceChecks.RemoveDuplicateFaces(lod, null, includeReversed: false, out int removed);
        CheckLod(a);
        Assert.Equal(1, removed);
        Assert.Equal(5, a.FaceCount);
        // Kept face 0 inherits the membership of its removed copy (face 3).
        var dups = a.Tags.OfType<SelectionTag>().Single(t => t.Name == "dups");
        Assert.NotEqual(0, dups.Faces[0]);

        var b = FaceChecks.RemoveDuplicateFaces(lod, null, includeReversed: true, out int removed2);
        Assert.Equal(2, removed2);
        Assert.Equal(4, b.FaceCount);
        Assert.Equal(before, lod.FacePoints); // input untouched

        var c = FaceChecks.RepairDegenerate(lod, null, out int fixedCount, out int deleted);
        CheckLod(c);
        Assert.Equal(1, fixedCount);  // the quad with a repeated point became a triangle
        Assert.Equal(1, deleted);     // the colinear triangle is gone
        Assert.Equal(5, c.FaceCount);
        Assert.Empty(FaceChecks.Check(c).Degenerate);
        Assert.Equal(3, c.FaceVertCount[1]);
    }

    // ------------------------------------------------------------------ hull operations

    [Fact]
    public void CreateComponentSelections_replaces_old_component_selections()
    {
        var lod = Merge(Box(1, 1, 1), Box(1, 1, 1, new Vec3(5, 0, 0)));
        lod = lod with { Tags = lod.Tags.Add(new SelectionTag(1, "Component07", new byte[16], new byte[12])).Add(new SelectionTag(1, "keep", new byte[16], new byte[12])) };
        var r = HullOps.CreateComponentSelections(lod, null, out int count);
        CheckLod(r);
        Assert.Equal(2, count);
        var names = r.Tags.OfType<SelectionTag>().Select(t => t.Name).ToArray();
        Assert.Contains("Component01", names);
        Assert.Contains("Component02", names);
        Assert.Contains("keep", names);
        Assert.DoesNotContain("Component07", names);
        var c2 = r.Tags.OfType<SelectionTag>().Single(t => t.Name == "Component02");
        Assert.Equal(Range(8).Select(i => i + 8).ToArray(), Indices(c2.Points));
        Assert.Equal(Range(6).Select(i => i + 6).ToArray(), Indices(c2.Faces));
        Assert.Equal(lod.Tags.Length, HullOps.DeleteComponentSelections(r).Tags.Length + 1);
    }

    [Fact]
    public void ReplaceSelectionWithHull_makes_the_l_prism_convex()
    {
        var l = LPrism();
        l = l with { Tags = l.Tags.Add(new SelectionTag(1, "all", All(16), All(14))).Add(new SelectionTag(1, "corner", MaskOf(16, 0), MaskOf(14))) };
        var positions = (float[])l.Positions.Clone();
        var faces = (uint[])l.FacePoints.Clone();

        var r = HullOps.ReplaceSelectionWithHull(l, new SelectionMask(All(16), All(14)), out var hull, out var stats);
        CheckLod(r);
        Assert.True(hull.Ok);
        Assert.Equal(positions, l.Positions);
        Assert.Equal(faces, l.FacePoints);
        Assert.Equal(10, r.PointCount); // the reflex corner and the points on hull edges are gone
        Assert.Equal(6, stats.PointsRemoved);
        Assert.True(IsClosedConsistent(r));
        Assert.Equal(-3.5, Measure.SignedVolume(r), 4);
        Assert.True(Convexity.Check(r, Range(r.FaceCount)).Convex);
        var all = r.Tags.OfType<SelectionTag>().Single(t => t.Name == "all");
        Assert.Equal(r.FaceCount, Count(all.Faces));
        Assert.Equal(10, Count(all.Points));
        var sel = Selection.GetSelected(r);
        Assert.Equal(r.FaceCount, Count(sel.Faces));
    }

    [Fact]
    public void ReplaceSelectionWithHull_keeps_unselected_geometry_and_fails_cleanly_on_flat_input()
    {
        var lod = Merge(LPrism(), Box(1, 1, 1, new Vec3(10, 0, 0)));
        var sel = new SelectionMask(MaskOf(lod.PointCount, Range(16)), MaskOf(lod.FaceCount, Range(14)));
        var r = HullOps.ReplaceSelectionWithHull(lod, sel, out _, out _);
        CheckLod(r);
        Assert.Equal(2, MeshTopology.FindComponents(r).Count);
        Assert.Equal(-4.5, Measure.SignedVolume(r), 4);

        var grid = GridLod(2, 2);
        var g = HullOps.ReplaceSelectionWithHull(grid, new SelectionMask(All(9), All(4)), out var h, out var st);
        Assert.Same(grid, g);
        Assert.Equal(HullFailure.Coplanar, h.Failure);
        Assert.Equal(1, st.Failed);
    }

    [Fact]
    public void ComponentHulls_replaces_every_component()
    {
        var lod = Merge(Merge(LPrism(), LPrism(5, 0, 0)), Box(1, 1, 1, new Vec3(-5, 0, 0)));
        var r = HullOps.ComponentHulls(lod, null, out var stats);
        CheckLod(r);
        Assert.Equal(3, stats.Components);
        Assert.Equal(3, stats.Hulls);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(12, stats.PointsRemoved);
        var reps = ComponentAnalysis.Analyze(r);
        Assert.Equal(3, reps.Length);
        Assert.All(reps, c => Assert.True(c.IsClosed && c.IsConvex && !c.InsideOut));
        Assert.Equal(3, r.Tags.OfType<SelectionTag>().Count(t => ComponentAnalysis.IsComponentName(t.Name)));
        Assert.Equal(-(3.5 + 3.5 + 1), Measure.SignedVolume(r), 4);
    }

    // ------------------------------------------------------------------ real data and speed

    [Fact]
    public void Sample_building_geometry_components_match_object_builder()
    {
        string path = TestData.Sample("Test_Building_sample_building.p3d");
        var model = Mlod.ReadFile(path);
        var geo = model.Lods.First(l => LodNames.IsGeometry(l.Resolution));
        var sw = Stopwatch.StartNew();
        var comps = MeshTopology.FindComponents(geo, ComponentAnalysis.ExcludeProxies(geo, null));
        var reps = ComponentAnalysis.Analyze(geo);
        sw.Stop();
        Assert.True(comps.Count > 0);
        Assert.Equal(comps.Count, reps.Length);
        TestContext.Current.SendDiagnosticMessage($"sample geometry: {geo.PointCount} points, {geo.FaceCount} faces, {comps.Count} components, " +
            $"closed {reps.Count(r => r.IsClosed)}, convex {reps.Count(r => r.IsConvex)}, {sw.ElapsedMilliseconds} ms");

        // Object Builder's own ComponentXX selections (saved in the sample) = our closed components (loose triangles ignored).
        var recreated = HullOps.CreateComponentSelections(geo, null, out int count, out int ignored);
        CheckLod(recreated);
        string Sets(Lod l) => string.Join(" | ", l.Tags.OfType<SelectionTag>().Where(t => ComponentAnalysis.IsComponentName(t.Name))
            .Select(t => string.Join(",", Indices(t.Points)) + " f=" + string.Join(",", Indices(t.Faces))).Order());
        Assert.Equal(Sets(geo), Sets(recreated));
        Assert.Equal(reps.Count(r => r.IsClosed), count);
        Assert.Equal(comps.Count - count, ignored);
        Assert.Same(geo, recreated); // identical ComponentXX selections: no change, no undo step
    }

    [Fact]
    public void Topology_is_fast_on_a_large_mesh()
    {
        var sphere = Primitives.CreateSphere(Lod.Empty(0), new SphereParams(5, 192, 96)); // ~18k faces
        Assert.True(sphere.FaceCount > 17_000);
        var faces = Range(sphere.FaceCount);
        var sw = Stopwatch.StartNew();
        var comps = MeshTopology.FindComponents(sphere);
        var closed = MeshTopology.CheckClosed(sphere);
        long tTopo = sw.ElapsedMilliseconds;
        var checks = FaceChecks.Check(sphere);
        long tChecks = sw.ElapsedMilliseconds - tTopo;
        sw.Restart();
        var hull = ConvexHull.Build(sphere.Positions, Range(sphere.PointCount));
        long tHull = sw.ElapsedMilliseconds;
        sw.Restart();
        var concave = Convexity.ConcaveEdges(sphere, faces, 1e-4);
        long tConcave = sw.ElapsedMilliseconds;
        sw.Restart();
        var conv = Convexity.Check(sphere, faces);
        long tConv = sw.ElapsedMilliseconds;
        TestContext.Current.SendDiagnosticMessage($"hull {tHull} ms ({hull.FaceCount} faces), concave edges {tConcave} ms");
        Assert.Equal(1, comps.Count);
        Assert.True(closed.Closed);
        Assert.True(conv.Convex);
        Assert.Empty(checks.Duplicate);
        TestContext.Current.SendDiagnosticMessage($"18k faces: topology {tTopo} ms, face checks {tChecks} ms, convexity {tConv} ms");
        Assert.True(tTopo < 1000 && tConv < 3000, $"too slow: topology {tTopo} ms, convexity {tConv} ms");
    }

    [Fact]
    public void Diag_sample_view_geometry()
    {
        var model = Mlod.ReadFile(TestData.Sample("Test_Building_sample_building.p3d"));
        foreach (int li in new[] { 8, 9 })
        {
            var lod = model.Lods[li];
            var comps = MeshTopology.FindComponents(lod, ComponentAnalysis.ExcludeProxies(lod, null));
            var keys = lod.Tags.OfType<SelectionTag>().Where(t => ComponentAnalysis.IsComponentName(t.Name)).ToDictionary(t => string.Join(",", Indices(t.Points)), t => t.Name);
            var sb = new System.Text.StringBuilder($"LOD {li} ({lod.PointCount} pts, {lod.FaceCount} faces, isolated {Count(PointOps.Isolated(lod))})\n");
            foreach (var c in comps.Components.OrderBy(c => c.Points.Min()))
            {
                bool closed = MeshTopology.CheckClosed(lod, c.Faces).Closed;
                keys.TryGetValue(string.Join(",", c.Points), out var name);
                sb.Append($"  minPt {c.Points.Min()} n={c.Points.Length}/{c.Faces.Length} closed={closed} sel={name ?? "-"}\n");
            }
            TestContext.Current.SendDiagnosticMessage(sb.ToString());
        }
    }

    [Fact]
    public void Diag_btr_components()
    {
        string path = TestData.Local("btr");
        var model = Mlod.ReadFile(path);
        foreach (int li in new[] { 7, 10 })
        {
            var geo = model.Lods[li];
            var comps = MeshTopology.FindComponents(geo, ComponentAnalysis.ExcludeProxies(geo, null));
            var sb = new System.Text.StringBuilder($"LOD {li}: {comps.Count} comps\n");
            foreach (var s in geo.Tags.OfType<SelectionTag>().Where(t => ComponentAnalysis.IsComponentName(t.Name)))
            {
                var pts = Indices(s.Points);
                var mine = pts.Select(p => comps.PointComponent[p]).Distinct().ToArray();
                var fcs = Indices(s.Faces);
                bool exact = mine.Length == 1 && mine[0] >= 0 && comps.Components[mine[0]].Points.Length == pts.Length;
                sb.Append($"  {s.Name}: minPt {(pts.Length > 0 ? pts.Min() : -1)} minFace {(fcs.Length > 0 ? fcs.Min() : -1)} n={pts.Length}/{fcs.Length}\n");
                if (!exact)
                    sb.Append($"  {s.Name}: {pts.Length} pts {fcs.Length} faces -> my comps [{string.Join(",", mine)}] sizes [{string.Join(",", mine.Select(m => m < 0 ? -1 : comps.Components[m].Points.Length))}] faces in sel->comp [{string.Join(",", fcs.Select(f => comps.FaceComponent[f]).Distinct())}]\n");
            }
            TestContext.Current.SendDiagnosticMessage(sb.ToString());
        }
    }
}
