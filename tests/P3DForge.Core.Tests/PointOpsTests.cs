using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class PointOpsTests
{
    [Fact]
    public void Merging_a_quad_edge_turns_quads_into_triangles_and_keeps_corner_UVs()
    {
        var g = GridLod(2, 1); // points 0..5, quads (0,1,4,3) (1,2,5,4)
        var r = PointOps.Merge(g, MaskOf(6, 1, 4));
        CheckLod(r.Lod);
        Assert.Equal(1, r.RemovedPoints);
        Assert.Equal(0, r.RemovedFaces);
        Assert.Equal(2, r.CollapsedQuads);
        Assert.Equal(5, r.Lod.PointCount);
        Assert.Equal(3, r.Lod.FaceVertCount[0]);
        Assert.Equal(3, r.Lod.FaceVertCount[1]);
        Assert.Equal([1f, 0f, 0.5f], Pos(r.Lod, 1)); // centroid, keeps index 1
        Assert.Equal([0u, 1u, 3u], FacePts(r.Lod, 0));
        var uv = FaceUVs(g, 0);
        Assert.Equal([uv[0], uv[1], uv[2], uv[3], uv[6], uv[7]], FaceUVs(r.Lod, 0));
        Assert.Equal([1u, 2u, 4u], FacePts(r.Lod, 1)); // (1,2,5) after compaction 5 → 4
    }

    [Fact]
    public void Merge_takes_max_weights_sums_mass_averages_animation_and_removes_degenerate_faces()
    {
        var cube = RichCube();
        var sel = Selection.WithSelected(cube, new SelectionMask(MaskOf(8, 0, 1, 2, 3), MaskOf(6, 0)));
        var r = PointOps.Merge(sel, MaskOf(8, 0, 1, 2, 3));
        CheckLod(r.Lod);
        Assert.Equal(5, r.Lod.PointCount);
        Assert.Equal(1, r.RemovedFaces);
        Assert.Equal(5, r.Lod.FaceCount);
        Assert.Equal(4, r.CollapsedQuads);
        Assert.Equal(1, r.Lod.Selection("top")!.Points[0]);
        Assert.Equal(1 + 2 + 3 + 4, r.Lod.MassTag!.Mass[0]);
        var anim = r.Lod.Animations.First().Positions;
        Assert.Equal(0.6, anim[0], 5);
        Assert.Equal(0.6, anim[1], 5);
        Assert.Equal(0.1, anim[2], 5);
        Assert.Equal([0.5f, 0.5f, 0f], Pos(r.Lod, 0));
        Assert.Equal([3u, 4u], r.Lod.SharpEdges!.Edges);
        Assert.Equal(1, Selection.GetSelected(r.Lod).Points[0]);
        Assert.True(IsClosedConsistent(r.Lod));
        Assert.True(SignedVolume(r.Lod) < 0);
        var w = PointOps.Merge(cube, MaskOf(8, 6, 7));
        Assert.Equal(Weights.Decode(128), Weights.Decode(w.Lod.Selection("top")!.Points[6]), 6);
    }

    [Fact]
    public void Merge_to_target_and_no_op_cases()
    {
        var g = GridLod(1, 1);
        var r = PointOps.Merge(g, MaskOf(4, 0, 1), new Vec3(5, 5, 5));
        Assert.Equal([5f, 5f, 5f], Pos(r.Lod, 0));
        Assert.Same(g, PointOps.Merge(g, MaskOf(4, 2)).Lod);
        Assert.Same(g, PointOps.Merge(g, new byte[4]).Lod);
        var all = PointOps.Merge(g, MaskOf(4, 0, 1, 2, 3));
        Assert.Equal(0, all.Lod.FaceCount);
        Assert.Equal(1, all.Lod.PointCount);
        CheckLod(all.Lod);
        Assert.Throws<ArgumentException>(() => PointOps.MergeGroups(g, [0, 2, 2, 3]));
    }

    [Fact]
    public void Merge_near_welds_an_exploded_box()
    {
        var box = Primitives.CreateBox(Lod.Empty(0), new BoxParams(1, 1, 1, 2, 2, 2));
        int n = box.FaceCount * 4;
        var pos = new float[n * 3];
        var fp = new uint[box.FaceCount * 4];
        for (int f = 0; f < box.FaceCount; f++)
            for (int k = 0; k < 4; k++)
            {
                uint p = box.FacePoints[f * 4 + k];
                Array.Copy(box.Positions, p * 3, pos, (f * 4 + k) * 3, 3);
                fp[f * 4 + k] = (uint)(f * 4 + k);
            }
        var exploded = Lod.Empty(0).WithFaces(box.Faces with { Points = fp, Normals = new uint[box.FaceCount * 4] }) with
        {
            Positions = pos,
            PointFlags = new uint[n],
        };
        Assert.False(IsClosedConsistent(exploded));
        var r = PointOps.MergeNear(exploded, null, 1e-4);
        CheckLod(r.Lod);
        Assert.Equal(box.PointCount, r.Lod.PointCount);
        Assert.Equal(box.FaceCount, r.Lod.FaceCount);
        Assert.True(IsClosedConsistent(r.Lod));
        Assert.Equal(-1, SignedVolume(r.Lod), 5);
    }

    [Fact]
    public void Cluster_near_matches_brute_force()
    {
        const int n = 3000;
        var pos = new float[n * 3];
        uint s = 12345;
        double Rnd() => (s = unchecked(s * 1103515245 + 12345)) / 4294967296.0;
        for (int i = 0; i < n * 3; i++) pos[i] = (float)(Math.Floor(Rnd() * 40) / 10);
        var mask = RandomMask(n, 7, 0.8);
        foreach (double d in new[] { 0, 0.05, 0.11, 0.3 })
        {
            var rep = PointOps.ClusterNear(pos, mask, d);
            var reference = Enumerable.Range(0, n).ToArray();
            var done = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (mask[i] == 0 || done[i]) continue;
                done[i] = true;
                for (int j = i + 1; j < n; j++)
                {
                    if (mask[j] == 0 || done[j]) continue;
                    double dx = (double)pos[j * 3] - pos[i * 3], dy = (double)pos[j * 3 + 1] - pos[i * 3 + 1], dz = (double)pos[j * 3 + 2] - pos[i * 3 + 2];
                    if (dx * dx + dy * dy + dz * dz <= d * d)
                    {
                        done[j] = true;
                        reference[j] = i;
                    }
                }
            }
            Assert.Equal(reference, rep);
        }
    }

    [Fact]
    public void Merge_near_only_merges_selected_points()
    {
        var lod = Primitives.CreatePoint(GridLod(1, 1), new Vec3(0, 0, 0.0001));
        lod = Primitives.CreatePoint(lod, new Vec3(1, 0, 0.0001));
        var r = PointOps.MergeNear(lod, MaskOf(lod.PointCount, 0, 4), 0.001);
        Assert.Equal(1, r.RemovedPoints);
        Assert.Equal(5, r.Lod.PointCount);
        CheckLod(r.Lod);
    }

    [Fact]
    public void Closest_point_distance_matches_brute_force()
    {
        const int n = 800;
        var pos = new float[n * 3];
        uint s = 99;
        double Rnd() => (s = unchecked(s * 1103515245 + 12345)) / 4294967296.0;
        for (int i = 0; i < n * 3; i++) pos[i] = (float)(Rnd() * 10);
        Array.Copy(pos, 0, pos, 30, 3); // a coincident pair must be ignored
        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = (double)pos[i * 3] - pos[j * 3], dy = (double)pos[i * 3 + 1] - pos[j * 3 + 1], dz = (double)pos[i * 3 + 2] - pos[j * 3 + 2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > 0 && d < best) best = d;
            }
        Assert.Equal(best, PointOps.ClosestPointDistance(pos, null)!.Value, 9);
        Assert.Null(PointOps.ClosestPointDistance(new float[3], null));
        Assert.Null(PointOps.ClosestPointDistance(new float[6], null));
    }

    [Fact]
    public void Flatten_projects_onto_planes()
    {
        var cube = RichCube();
        var f = PointOps.Flatten(cube, MaskOf(8, 0, 1, 2, 3), new Vec3(0, 0, 1));
        CheckLod(f);
        foreach (int i in new[] { 0, 1, 2, 3 }) Assert.Equal(0f, f.Positions[i * 3 + 2]);
        var pin = PointOps.Flatten(cube, MaskOf(8, 0, 1, 4, 5), new Vec3(0, 1, 0), new Vec3(9, 0.25, 9));
        foreach (int i in new[] { 0, 1, 4, 5 }) Assert.Equal(0.25f, pin.Positions[i * 3 + 1]);
        Assert.Equal(1f, pin.Positions[2 * 3 + 1]);
        var mid = PointOps.Flatten(cube, MaskOf(8, 0, 6), new Vec3(0, 1, 0));
        Assert.Equal(0.5f, mid.Positions[1]);
        Assert.Equal(0.5f, mid.Positions[6 * 3 + 1]);
        Assert.Same(cube, PointOps.Flatten(cube, new byte[8], new Vec3(0, 1, 0)));
        Assert.Throws<ArgumentException>(() => PointOps.Flatten(cube, MaskOf(8, 0), Vec3.Zero));
    }

    [Fact]
    public void Snap_to_grid()
    {
        var lod = Primitives.CreatePoint(Lod.Empty(0), new Vec3(0.26, -0.74, 1.01));
        lod = Primitives.CreatePoint(lod, new Vec3(0.5, 0.5, 0.5));
        var r = PointOps.SnapToGrid(lod, MaskOf(2, 0, 1), 0.5);
        Assert.Equal(1, r.Moved);
        Assert.Equal([0.5f, -0.5f, 1f], Pos(r.Lod, 0));
        Assert.Equal(0, PointOps.SnapToGrid(r.Lod, MaskOf(2, 0, 1), 0.5).Moved);
        Assert.Throws<ArgumentException>(() => PointOps.SnapToGrid(lod, MaskOf(2, 0), 0));
    }

    [Fact]
    public void Isolated_points_and_navigation()
    {
        var lod = Primitives.CreatePoint(GridLod(1, 1), new Vec3(5, 5, 5));
        lod = Primitives.CreatePoint(lod, new Vec3(6, 5, 5));
        Assert.Equal([0, 0, 0, 0, 1, 1], PointOps.Isolated(lod));
        Assert.Equal([0, 0, 0, 0, 1, 0], PointOps.Isolated(lod, MaskOf(6, 5)));
        var blocked = MaskOf(6, 0, 2);
        Assert.Equal(1, PointOps.Navigate(new byte[6], blocked, PointNavigation.First));
        Assert.Equal(3, PointOps.Navigate(MaskOf(6, 1), blocked, PointNavigation.Next));
        Assert.Equal(1, PointOps.Navigate(MaskOf(6, 3), blocked, PointNavigation.Previous));
        Assert.Equal(-1, PointOps.Navigate(MaskOf(6, 5), blocked, PointNavigation.Next));
        Assert.Equal(-1, PointOps.Navigate(MaskOf(6, 1), blocked, PointNavigation.Previous));
        Assert.Equal(1, PointOps.Navigate(new byte[6], blocked, PointNavigation.Next));
        Assert.Equal(5, PointOps.Navigate(new byte[6], blocked, PointNavigation.Previous));
    }
}
