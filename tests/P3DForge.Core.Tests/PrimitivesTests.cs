using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using static P3DForge.Core.Tests.OpsTestHelpers;

namespace P3DForge.Core.Tests;

public class PrimitivesTests
{
    static Lod Empty() => Lod.Empty(0);

    [Fact]
    public void Box_is_closed_outward_selected_and_named()
    {
        var lod = Primitives.CreateBox(Empty(), new BoxParams(1, 2, 3) { Selection = "myBox" });
        CheckLod(lod);
        Assert.Equal(8, lod.PointCount);
        Assert.Equal(6, lod.FaceCount);
        Assert.True(IsClosedConsistent(lod));
        Assert.Equal(-6, SignedVolume(lod), 5);
        var sel = Selection.GetSelected(lod);
        Assert.Equal(8, Count(sel.Points));
        Assert.Equal(6, Count(sel.Faces));
        Assert.Equal(8, Count(lod.Selection("mybox")!.Points));
        Assert.Equal(6, Count(lod.Selection("MYBOX")!.Faces));
        for (int f = 0; f < 6; f++)
        {
            Assert.Equal(4, lod.FaceVertCount[f]);
            Assert.All(FaceUVs(lod, f), u => Assert.InRange(u, 0f, 1f));
        }
    }

    [Fact]
    public void Segmented_box_shares_edge_points()
    {
        var b = Primitives.BuildBox(new BoxParams(2, 3, 4, 2, 3, 4));
        Assert.Equal(3 * 4 * 5 - 1 * 2 * 3, b.PointCount);
        Assert.Equal(2 * (2 * 3 + 3 * 4 + 2 * 4), b.FaceCount);
        var lod = Primitives.CreateBox(Empty(), new BoxParams(2, 3, 4, 2, 3, 4));
        CheckLod(lod);
        Assert.True(IsClosedConsistent(lod));
        Assert.Equal(-24, SignedVolume(lod), 4);
    }

    [Fact]
    public void Cylinder_with_caps_is_closed_without_caps_open_and_outward()
    {
        var lod = Primitives.CreateCylinder(Empty(), new CylinderParams(0.5, 2, 12, 3));
        CheckLod(lod);
        Assert.Equal(12 * 3 + 24, lod.FaceCount);
        Assert.True(IsClosedConsistent(lod));
        double polyArea = 0.5 * 12 * 0.25 * Math.Sin(2 * Math.PI / 12);
        Assert.Equal(-polyArea * 2, SignedVolume(lod), 4);
        var open = Primitives.CreateCylinder(Empty(), new CylinderParams(0.5, 2, 8, 1, Caps: false));
        CheckLod(open);
        Assert.Equal(8, open.FaceCount);
        Assert.False(IsClosedConsistent(open));
        for (int f = 0; f < open.FaceCount; f++)
        {
            var n = MeshMath.FaceNewell(open, f);
            double cx = 0, cz = 0;
            foreach (uint p in FacePts(open, f))
            {
                cx += open.Positions[p * 3];
                cz += open.Positions[p * 3 + 2];
            }
            Assert.True(-(n.X * cx + n.Z * cz) > 0);
        }
    }

    [Fact]
    public void Sphere_is_closed_and_outward_with_triangle_poles()
    {
        var lod = Primitives.CreateSphere(Empty(), new SphereParams(1, 16, 8));
        CheckLod(lod);
        Assert.True(IsClosedConsistent(lod));
        Assert.InRange(SignedVolume(lod), -4.19, -3.5);
        Assert.Equal(32, Enumerable.Range(0, lod.FaceCount).Count(f => lod.FaceVertCount[f] == 3));
        Assert.Equal(16 * 8, lod.FaceCount);
    }

    [Fact]
    public void Plane_and_circle_face_up()
    {
        foreach (var lod in new[]
        {
            Primitives.CreatePlane(Empty(), new PlaneParams(2, 3, 4, 2)),
            Primitives.CreateCircle(Empty(), new CircleParams(1, 10, true)),
            Primitives.CreateCircle(Empty(), new CircleParams(1, 9, false)),
            Primitives.CreateCircle(Empty(), new CircleParams(1, 10, false)),
        })
        {
            CheckLod(lod);
            for (int f = 0; f < lod.FaceCount; f++) Assert.True(MeshMath.FaceNewell(lod, f).Y < 0);
        }
        var plane = Primitives.CreatePlane(Empty(), new PlaneParams(2, 3, 4, 2));
        Assert.Equal(8, plane.FaceCount);
        Assert.Equal(15, plane.PointCount);
        var disc = Primitives.CreateCircle(Empty(), new CircleParams(1, 9, false));
        double area = 0;
        for (int f = 0; f < disc.FaceCount; f++) area += -MeshMath.FaceNewell(disc, f).Y / 2;
        Assert.Equal(0.5 * 9 * Math.Sin(2 * Math.PI / 9), area, 5);
        Assert.Equal(9, disc.PointCount);
    }

    [Fact]
    public void Places_at_the_pin_with_alignment()
    {
        var at = new Vec3(10, 5, -3);
        double[] Ys(Lod l) => Enumerable.Range(0, l.PointCount).Select(i => (double)l.Positions[i * 3 + 1]).ToArray();
        var c = Primitives.CreateBox(Empty(), new BoxParams(1, 2, 1) { At = at });
        Assert.Equal(4, Ys(c).Min(), 5);
        Assert.Equal(6, Ys(c).Max(), 5);
        Assert.Equal(5, Ys(Primitives.CreateBox(Empty(), new BoxParams(1, 2, 1) { At = at, Align = PrimitiveAlign.Bottom })).Min(), 5);
        Assert.Equal(5, Ys(Primitives.CreateBox(Empty(), new BoxParams(1, 2, 1) { At = at, Align = PrimitiveAlign.Top })).Max(), 5);
        var xs = Enumerable.Range(0, 8).Select(i => (double)c.Positions[i * 3]).ToArray();
        Assert.Equal(10, (xs.Min() + xs.Max()) / 2, 5);
    }

    [Fact]
    public void Appends_into_a_rich_LOD_keeping_tags_consistent()
    {
        var cube = RichCube();
        var lod = Primitives.CreateCylinder(cube, new CylinderParams(0.2, 1, 6, 1) { Selection = "top" });
        CheckLod(lod);
        Assert.Equal(8 + 14, lod.PointCount);
        Assert.Equal(6 + 18, lod.FaceCount);
        var uv0 = lod.UVSet(0)!.UV;
        for (int f = 6; f < lod.FaceCount; f++) Assert.Equal(FaceUVs(lod, f), FaceUVs(lod, f, uv0));
        Assert.Equal([1f, 2, 3, 4, 5, 6, 7, 8], lod.MassTag!.Mass.AsSpan(0, 8).ToArray());
        Assert.Equal(0f, lod.MassTag!.Mass[10]);
        var top = lod.Selection("top")!;
        Assert.Equal([0, 0, 1, 1, 0, 0, 128, 255], top.Points.AsSpan(0, 8).ToArray());
        Assert.Equal(4 + 14, Count(top.Points));
        var sel = Selection.GetSelected(lod);
        Assert.Equal(0, Count(sel.Points.AsSpan(0, 8).ToArray()));
        Assert.Equal(14, Count(sel.Points));
    }

    [Fact]
    public void Rejects_invalid_parameters()
    {
        Assert.Throws<ArgumentException>(() => Primitives.CreateBox(Empty(), new BoxParams(0, 1, 1)));
        Assert.Throws<ArgumentException>(() => Primitives.CreateCylinder(Empty(), new CylinderParams(1, 1, 2, 1)));
        Assert.Throws<ArgumentException>(() => Primitives.CreateSphere(Empty(), new SphereParams(1, 8, 1)));
        Assert.Throws<ArgumentException>(() => Primitives.CreatePlane(Empty(), new PlaneParams(double.NaN, 1)));
        Assert.Throws<ArgumentException>(() => Primitives.CreatePlane(Empty(), new PlaneParams(1, 1, 0, 1)));
    }

    [Fact]
    public void Create_point_extends_per_point_tags()
    {
        var lod = Primitives.CreatePoint(RichCube(), new Vec3(1, 2, 3));
        CheckLod(lod);
        Assert.Equal(9, lod.PointCount);
        Assert.Equal([1f, 2f, 3f], Pos(lod, 8));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 1], Selection.GetSelected(lod).Points);
        Assert.Equal(9, lod.MassTag!.Mass.Length);
        Assert.Equal(27, lod.Animations.First().Positions.Length);
        var fromEmpty = Primitives.CreatePoint(Empty(), Vec3.Zero);
        CheckLod(fromEmpty);
        Assert.Equal(1, fromEmpty.PointCount);
    }

    [Fact]
    public void Create_face_closes_a_hole_consistently_with_neighbours()
    {
        var holed = FaceOps.Remove(RichCube(), MaskOf(6, 3));
        int[] pts = [1, 5, 6, 2];
        foreach (var order in new[] { pts, [6, 1, 2, 5], [2, 1, 5, 6], [5, 2, 1, 6] })
        {
            var r = Primitives.CreateFace(holed, order);
            CheckLod(r.Lod);
            Assert.Equal(FaceOrientationSource.Neighbours, r.OrientedBy);
            Assert.True(r.Convex);
            Assert.True(IsClosedConsistent(r.Lod));
            Assert.Equal(-1, SignedVolume(r.Lod), 5);
            Assert.Equal(5, r.Face);
            var sel = Selection.GetSelected(r.Lod);
            Assert.Equal([0, 0, 0, 0, 0, 1], sel.Faces);
            Assert.Equal(4, Count(sel.Points));
            Assert.Equal(6, r.Lod.Selection("top")!.Faces.Length);
            Assert.Equal(48, r.Lod.UVSet(1)!.UV.Length);
        }
        var outside = Primitives.CreateFace(holed, pts, new Vec3(-1, 0, 0));
        Assert.Equal(FaceOrientationSource.View, outside.OrientedBy);
        Assert.Equal(-1, SignedVolume(outside.Lod), 5);
        var inside = Primitives.CreateFace(holed, pts, new Vec3(1, 0, 0));
        Assert.Equal(-1.0 / 3, SignedVolume(inside.Lod), 5);
        var pref = Primitives.CreateFace(holed, pts, new Vec3(1, 0, 0), preferNeighbours: true);
        Assert.Equal(FaceOrientationSource.Neighbours, pref.OrientedBy);
        Assert.Equal(-1, SignedVolume(pref.Lod), 5);
    }

    [Fact]
    public void Create_face_faces_the_viewer_without_neighbours()
    {
        var b = Primitives.CreatePoint(Primitives.CreatePoint(Primitives.CreatePoint(Empty(), Vec3.Zero), new Vec3(1, 0, 0)), new Vec3(0, 1, 0));
        var r = Primitives.CreateFace(b, [0, 1, 2], new Vec3(0, 0, 1));
        CheckLod(r.Lod);
        Assert.Equal(FaceOrientationSource.View, r.OrientedBy);
        Assert.True(MeshMath.FaceNewell(r.Lod, 0).Z > 0);
        Assert.True(MeshMath.FaceNewell(Primitives.CreateFace(b, [0, 1, 2], new Vec3(0, 0, -1)).Lod, 0).Z < 0);
        Assert.Equal(9, r.Lod.Normals.Length);
    }

    [Fact]
    public void Create_face_orders_four_points_as_a_convex_quad()
    {
        var lod = Empty();
        foreach (var p in new[] { new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0.2) }) lod = Primitives.CreatePoint(lod, p);
        var r = Primitives.CreateFace(lod, [0, 1, 2, 3]);
        CheckLod(r.Lod);
        var q = FacePts(r.Lod, 0).ToList();
        Assert.Equal(2, Math.Abs(q.IndexOf(0) - q.IndexOf(1)));
        Assert.True(r.Planarity > 0.05);
        Assert.Equal([0f, 0, 1, 0, 1, 1, 0, 1], FaceUVs(r.Lod, 0));
    }

    [Fact]
    public void Create_face_rejects_bad_input()
    {
        var g = GridLod(2, 2);
        Assert.Throws<ArgumentException>(() => Primitives.CreateFace(g, [0, 1]));
        Assert.Contains("collinear", Assert.Throws<ArgumentException>(() => Primitives.CreateFace(g, [0, 1, 2])).Message);
        Assert.Throws<ArgumentException>(() => Primitives.CreateFace(g, [0, 0, 4]));
        Assert.Throws<ArgumentException>(() => Primitives.CreateFace(g, [0, 1, 99]));
    }

    [Fact]
    public void Proxy_paths_names_and_parsing()
    {
        Assert.Equal(@"\dz\gear\camping\wooden_log", Proxies.NormalizePath(@"P:\dz\gear\camping\wooden_log.p3d"));
        Assert.Equal(@"\DZ\gear\x", Proxies.NormalizePath("p:/DZ/gear/x.P3D"));
        Assert.Equal(@"\dz\gear\x", Proxies.NormalizePath(@"dz\gear\x"));
        Assert.Equal(@"\dz\gear\x", Proxies.NormalizePath(@"\dz\gear\x.p3d"));
        Assert.Equal(@"\mod\x", Proxies.NormalizePath(@"D:\work\mod\x.p3d", @"D:\work"));
        Assert.Equal(@"\other\x", Proxies.NormalizePath(@"E:\other\x.p3d", @"P:\"));
        Assert.Equal(@"proxy:\dz\x.001", Proxies.SelectionName(@"\dz\x", 1));
        Assert.Equal((@"\dz\x", 12), Proxies.Parse(@"proxy:\dz\x.012"));
        Assert.False(Proxies.IsProxyName("proxyx"));
        Assert.Equal(-1, Proxies.Parse(@"proxy:\dz\x").Index);
    }

    [Fact]
    public void Proxy_creation_uses_the_OB_triangle_and_auto_index()
    {
        var a = Proxies.Create(GridLod(1, 1), @"\dz\gear\x", null, new Vec3(1, 2, 3));
        CheckLod(a.Lod);
        Assert.Equal(@"proxy:\dz\gear\x.001", a.Name);
        int f = a.Lod.FaceCount - 1;
        Assert.Equal(new[] { new[] { 1f, 2, 3 }, [1f, 4, 3], [1f, 2, 4] }, FacePts(a.Lod, f).Select(p => Pos(a.Lod, (int)p)).ToArray());
        var sel = a.Lod.Selection(a.Name)!;
        Assert.Equal(3, Count(sel.Points));
        Assert.Equal(1, Count(sel.Faces));
        Assert.Equal("", a.Lod.Texture(f));
        Assert.Equal(1, Selection.GetSelected(a.Lod).Faces[^1]);
        var b = Proxies.Create(a.Lod, @"\dz\gear\x", null);
        Assert.Equal(@"proxy:\dz\gear\x.002", b.Name);
        Assert.Equal(3, Proxies.NextIndex(b.Lod, @"dz\gear\X"));
        var c = Proxies.Create(b.Lod, @"\dz\other", 7);
        Assert.Equal(@"proxy:\dz\other.007", c.Name);
        Assert.Contains("exists", Assert.Throws<ArgumentException>(() => Proxies.Create(c.Lod, @"\dz\other", 7)).Message);
        Assert.Throws<ArgumentException>(() => Proxies.Create(c.Lod, "", null));
        CheckLod(c.Lod);
        // frame of the created triangle: origin at the pin, up +Y, forward +Z
        var tri = FacePts(a.Lod, f).Select(p => Vec3.At(a.Lod.Positions, p)).ToArray();
        var frame = Proxies.ComputeFrame(tri[0], tri[1], tri[2])!.Value;
        Assert.Equal(new Vec3(1, 2, 3), frame.Center);
        Assert.Equal(new Vec3(0, 1, 0), frame.Y);
        Assert.Equal(new Vec3(0, 0, 1), frame.Z);
        Assert.Equal(1, frame.X.X, 9);
        Assert.Equal(2, frame.Size, 9);
    }
}
