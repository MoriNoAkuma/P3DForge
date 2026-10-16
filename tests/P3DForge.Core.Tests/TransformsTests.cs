using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class TransformsTests
{
    static Lod Box(double cx = 0, double cy = 0, double cz = 0) =>
        Primitives.CreateBox(Lod.Empty(0), new BoxParams(1, 1, 1) { At = new Vec3(cx, cy, cz) });

    static Vec3 P(Lod lod, int i) => new(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2]);

    static void Near(Vec3 expected, Vec3 actual, double eps = 1e-6)
    {
        Assert.True((expected - actual).Length < eps, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void MultiplyAppliesRightOperandFirst()
    {
        var m = Transforms.Multiply(Transforms.Translate(1, 0, 0), Transforms.Scale(2, 2, 2));
        Near(new Vec3(3, 2, 2), Transforms.Apply(m, new Vec3(1, 1, 1)));
    }

    [Fact]
    public void RotateFollowsRightHandRuleOnRawNumbers()
    {
        var m = Transforms.Rotate(new Vec3(0, 0, 1), Math.PI / 2);
        Near(new Vec3(0, 1, 0), Transforms.Apply(m, new Vec3(1, 0, 0)));
        var y = Transforms.Rotate(new Vec3(0, 1, 0), Math.PI / 2);
        Near(new Vec3(0, 0, -1), Transforms.Apply(y, new Vec3(1, 0, 0)));
    }

    [Fact]
    public void RotateAboutAndScaleAboutKeepTheCenterFixed()
    {
        var c = new Vec3(2, 3, 4);
        Near(c, Transforms.Apply(Transforms.RotateAbout(c, new Vec3(1, 1, 0), 0.7), c));
        Near(c, Transforms.Apply(Transforms.ScaleAbout(c, 2, 3, 4), c));
        Near(new Vec3(4, 3, 4), Transforms.Apply(Transforms.ScaleAbout(c, 2, 1, 1), new Vec3(3, 3, 4)));
    }

    [Fact]
    public void ApplyMovesOnlyMaskedPointsAndKeepsSourceUntouched()
    {
        var lod = Box();
        var before = (float[])lod.Positions.Clone();
        var mask = new byte[lod.PointCount];
        mask[0] = 1;
        var o = Transforms.Apply(lod, mask, Transforms.Translate(0, 2, 0));
        Assert.Equal(before, lod.Positions);
        Near(P(lod, 0) + new Vec3(0, 2, 0), P(o, 0));
        for (int i = 1; i < lod.PointCount; i++) Near(P(lod, i), P(o, i));
        Assert.Same(lod.FacePoints, o.FacePoints);
    }

    [Fact]
    public void TransformPositionsMatchesStructureTransformPoints()
    {
        var lod = Box(1, 2, 3);
        var mask = new byte[lod.PointCount];
        for (int i = 0; i < mask.Length; i += 2) mask[i] = i % 4 == 0 ? (byte)1 : Weights.Encode(0.5f);
        var m = Transforms.RotateAbout(new Vec3(1, 1, 1), new Vec3(0.3, 1, 0.2), 1.1);
        foreach (bool weights in new[] { false, true })
        {
            var dst = new float[lod.Positions.Length];
            Transforms.TransformPositions(lod.Positions, dst, mask, m, weights);
            Assert.Equal(Structure.TransformPoints(lod, mask, m, weights).Positions, dst);
        }
    }

    [Fact]
    public void WeightsActAsSoftSelection()
    {
        var lod = Box();
        var mask = new byte[lod.PointCount];
        mask[0] = Weights.Encode(0.5f);
        var o = Transforms.Apply(lod, mask, Transforms.Translate(0, 1, 0), useWeights: true);
        Assert.Equal(lod.Positions[1] + Weights.Decode(mask[0]), o.Positions[1], 5);
        var hard = Transforms.Apply(lod, mask, Transforms.Translate(0, 1, 0));
        Assert.Equal(lod.Positions[1] + 1, hard.Positions[1], 5);
    }

    [Fact]
    public void MirrorReversesFacesAndKeepsOutwardWinding()
    {
        var lod = Box();
        double v0 = OpsTestHelpers.SignedVolume(lod);
        var m = Transforms.MirrorAbout(new Vec3(0.25, 0, 0), 0);
        Assert.True(Transforms.FlipsWinding(m));
        var o = Transforms.Apply(lod, null, m);
        OpsTestHelpers.CheckLod(o);
        Assert.Equal(v0, OpsTestHelpers.SignedVolume(o), 6);
        Near(new Vec3(0.5 - P(lod, 0).X, P(lod, 0).Y, P(lod, 0).Z), P(o, 0));
        // UVs travel with their vertices
        int f = 0, n = lod.FaceVertCount[f];
        for (int k = 0; k < n; k++)
        {
            int kk = n - 1 - k;
            Assert.Equal(lod.FacePoints[f * 4 + k], o.FacePoints[f * 4 + kk]);
            Assert.Equal(lod.FaceUV[f * 8 + k * 2], o.FaceUV[f * 8 + kk * 2]);
            Assert.Equal(lod.FaceUV[f * 8 + k * 2 + 1], o.FaceUV[f * 8 + kk * 2 + 1]);
        }
        var back = Transforms.Apply(o, null, m);
        Assert.Equal(lod.FacePoints, back.FacePoints);
        for (int i = 0; i < lod.PointCount; i++) Near(P(lod, i), P(back, i), 1e-5);
    }

    [Fact]
    public void MirrorOfPartReversesOnlyFullyTransformedFaces()
    {
        var two = Structure.Append(Box(), Structure.Extract(Box(3, 0, 0), MeshMath.Ones(8), MeshMath.Ones(6)), selectNew: false);
        var mask = new byte[two.PointCount];
        for (int i = 8; i < 16; i++) mask[i] = 1;
        var o = Transforms.Apply(two, mask, Transforms.MirrorAbout(new Vec3(3, 0, 0), 1));
        for (int f = 0; f < 6; f++)
            for (int k = 0; k < 4; k++) Assert.Equal(two.FacePoints[f * 4 + k], o.FacePoints[f * 4 + k]);
        Assert.NotEqual(two.FacePoints[6 * 4], o.FacePoints[6 * 4]);
        Assert.Equal(OpsTestHelpers.SignedVolume(two), OpsTestHelpers.SignedVolume(o), 6);
    }

    [Fact]
    public void IdentityReturnsSameLod()
    {
        var lod = Box();
        Assert.Same(lod, Transforms.Apply(lod, null, Transforms.Identity()));
    }

    [Fact]
    public void CenterAllUsesGeometryLodAndApplyAllMovesEveryLod()
    {
        var visual = Box(5, 5, 5);
        var geo = Box(1, 2, 3) with { Resolution = 1e13f };
        var model = new P3DModel([visual, geo]);
        Assert.Equal(1, Transforms.CenterReferenceLod(model));
        var off = Transforms.CenterAllOffset(model)!.Value;
        Near(new Vec3(-1, -2, -3), off);
        var moved = Transforms.ApplyAll(model, Transforms.Translate(off));
        Near(Vec3.Zero, Transforms.Center(moved.Lods[1], null)!.Value);
        Near(new Vec3(4, 3, 2), Transforms.Center(moved.Lods[0], null)!.Value);
        Assert.Equal(0, Transforms.CenterReferenceLod(new P3DModel([visual])));
    }

    [Fact]
    public void CenterOfMaskIsBoundingBoxCenter()
    {
        var lod = Box(1, 1, 1);
        Near(new Vec3(1, 1, 1), Transforms.Center(lod, null)!.Value);
        Assert.Null(Transforms.Center(lod, new byte[lod.PointCount]));
    }

    // ---- drag math ----------------------------------------------------------------------------------

    /// <summary>Orthographic front-style ray: looking along +Z at screen point (x, y).</summary>
    static (Vec3, Vec3) Ray(double x, double y) => (new Vec3(x, y, -10), new Vec3(0, 0, 1));

    [Fact]
    public void RayPlaneAndClosestOnLine()
    {
        Near(new Vec3(1, 2, 5), Transforms.RayPlane(new Vec3(1, 2, -10), new Vec3(0, 0, 3), new Vec3(0, 0, 5), new Vec3(0, 0, 1))!.Value);
        Assert.Null(Transforms.RayPlane(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 0, 5), new Vec3(0, 0, 1)));
        Assert.Equal(2, Transforms.ClosestOnLine(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(2, 5, -10), new Vec3(0, 0, 1)), 9);
        Assert.True(double.IsNaN(Transforms.ClosestOnLine(Vec3.Zero, new Vec3(0, 0, 1), new Vec3(2, 5, -10), new Vec3(0, 0, 1))));
    }

    [Fact]
    public void DragDeltaFollowsMouseInViewPlane()
    {
        var c = new Vec3(0, 0, 3);
        var vd = new Vec3(0, 0, 1);
        Near(new Vec3(2, -1, 0), Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), false, false, false));
        Near(new Vec3(2, -1, 0), Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), true, true, true));
    }

    [Fact]
    public void DragDeltaHonoursAxisLocks()
    {
        var c = new Vec3(0, 0, 3);
        var vd = new Vec3(0, 0, 1);
        Near(new Vec3(2, 0, 0), Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), true, false, false));
        Near(new Vec3(0, -1, 0), Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), false, true, false));
        // Z points into the screen: no movement
        Near(Vec3.Zero, Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), false, false, true));
        // X+Z plane seen edge-on from the front: only X follows
        Near(new Vec3(2, 0, 0), Transforms.DragDelta(c, vd, Ray(1, 1), Ray(3, 0), true, false, true));
        // Top view (looking down -Y) with X+Z locked = the view plane
        static (Vec3, Vec3) Down(double x, double z) => (new Vec3(x, 10, z), new Vec3(0, -1, 0));
        Near(new Vec3(1, 0, 2), Transforms.DragDelta(Vec3.Zero, new Vec3(0, -1, 0), Down(0, 0), Down(1, 2), true, false, true));
    }

    [Fact]
    public void DragDeltaPerspectiveMovesInPlaneThroughCenter()
    {
        // camera at origin looking along +Z, centre 10 m away: a ray 0.1 rad to the side hits ~1 m off-centre
        var c = new Vec3(0, 0, 10);
        var d = Transforms.DragDelta(c, new Vec3(0, 0, 1), (Vec3.Zero, new Vec3(0, 0, 1)), (Vec3.Zero, new Vec3(0.1, 0, 1)), false, false, false);
        Near(new Vec3(1, 0, 0), d);
    }

    [Fact]
    public void RotationAxisAndScaleFactors()
    {
        Near(new Vec3(0, 0, 1), Transforms.RotationAxis(new Vec3(0, 0, 2), false, false, false));
        Near(new Vec3(0, -1, 0), Transforms.RotationAxis(new Vec3(0, -1, 0), false, true, false));
        Near(new Vec3(1, 0, 0), Transforms.RotationAxis(new Vec3(0.5, 0, 0.5), true, false, false));
        Near(new Vec3(2, 2, 2), Transforms.ScaleFactors(2, false, false, false));
        Near(new Vec3(2, 1, 2), Transforms.ScaleFactors(2, true, false, true));
    }

    [Fact]
    public void PositiveRotationAboutViewAxisIsCounterClockwiseOnScreen()
    {
        // front camera: RAW right = +X, up = +Y, forward = +Z (left-handed: right × up = forward)
        var m = Transforms.Rotate(Transforms.RotationAxis(new Vec3(0, 0, 1), false, false, false), 0.1);
        var p = Transforms.Apply(m, new Vec3(1, 0, 0));
        Assert.True(p.Y > 0); // a point on the right moves up
    }
}
