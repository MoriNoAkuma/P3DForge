using System.Numerics;
using System.Reflection;

namespace P3DForge.Core.Tests;

/// <summary>
/// Pure render maths of the app (P3DForge.App.Viewport.Rendering.RenderMath). The test project only references
/// Core, so the class is called by reflection from the built app assembly (newest P3DForge.dll under .build/ or
/// src/P3DForge.App/bin); the tests are skipped when no app build exists yet.
/// </summary>
public class RenderMathTests
{
    static readonly Lazy<Type?> RenderMathType = new(FindType);

    static Type? FindType()
    {
        var candidates = new List<string>();
        var build = TestData.Path_(".build");
        if (Directory.Exists(build))
            foreach (var dir in Directory.EnumerateDirectories(build))
            {
                var p = Path.Combine(dir, "bin", "P3DForge.App", "debug", "P3DForge.dll");
                if (File.Exists(p)) candidates.Add(p);
            }
        var bin = TestData.Path_("src", "P3DForge.App", "bin");
        if (Directory.Exists(bin)) candidates.AddRange(Directory.EnumerateFiles(bin, "P3DForge.dll", SearchOption.AllDirectories));
        foreach (var path in candidates.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var t = Assembly.LoadFrom(path).GetType("P3DForge.App.Viewport.Rendering.RenderMath");
                if (t is not null) return t;
            }
            catch
            {
                // try the next build
            }
        }
        return null;
    }

    static T Call<T>(string name, params object[] args)
    {
        var t = RenderMathType.Value;
        if (t is null) Assert.Skip("P3DForge app build not found (build src/P3DForge.App first)");
        var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(x => x.Name == name && x.GetParameters().Length == args.Length);
        return (T)m.Invoke(null, args)!;
    }

    static Vector3 RawToRender(Vector3 v) => new(v.X, v.Y, -v.Z);

    static void Near(Vector3 expected, Vector3 actual, float eps = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= eps, $"expected {expected}, got {actual}");

    static Matrix4x4 SampleTransform(bool mirror)
    {
        var m = Matrix4x4.CreateScale(1.5f, 0.7f, 2.2f)
            * Matrix4x4.CreateFromYawPitchRoll(0.6f, -0.35f, 0.2f)
            * Matrix4x4.CreateTranslation(3.5f, -1.25f, 7.75f);
        return mirror ? Matrix4x4.CreateScale(-1, 1, 1) * m : m;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProxyTransform_RawToRender_MatchesConvertingPoints(bool mirror)
    {
        var t = SampleTransform(mirror);
        var r = Call<Matrix4x4>("RawToRenderTransform", t);
        Vector3[] pts = [new(0, 0, 0), new(1, 2, 3), new(-4.5f, 0.25f, 9), new(0.1f, -7, -2)];
        foreach (var p in pts)
        {
            var expected = RawToRender(Vector3.Transform(p, t));     // transform in RAW, then convert
            var actual = Vector3.Transform(RawToRender(p), r);        // convert, then transform in RENDER
            Near(expected, actual);
        }
        // The z-mirror on both sides keeps the handedness of the transform.
        float dt = Call<float>("Determinant3", t), dr = Call<float>("Determinant3", r);
        Assert.Equal(MathF.Sign(dt), MathF.Sign(dr));
        Assert.Equal(mirror, dr < 0);
    }

    [Fact]
    public void ProxyTransform_IdentityStaysIdentity()
    {
        var r = Call<Matrix4x4>("RawToRenderTransform", Matrix4x4.Identity);
        Assert.Equal(Matrix4x4.Identity, r);
        var tr = Call<Matrix4x4>("RawToRenderTransform", Matrix4x4.CreateTranslation(1, 2, 3));
        Near(new Vector3(1, 2, -3), tr.Translation);
    }

    [Fact]
    public void NormalMatrix_KeepsNormalsPerpendicularUnderNonUniformScale()
    {
        var m = Call<Matrix4x4>("RawToRenderTransform", SampleTransform(false));
        var n = Call<Matrix4x4>("NormalMatrix", m);
        var normal = Vector3.Normalize(new Vector3(1, 1, 0));
        var tangent = Vector3.Normalize(new Vector3(1, -1, 0.0f));
        Assert.True(MathF.Abs(Vector3.Dot(normal, tangent)) < 1e-6f);
        var tn = Vector3.TransformNormal(normal, n);
        var tt = Vector3.TransformNormal(tangent, m);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Normalize(tn), Vector3.Normalize(tt))) < 1e-4f);
    }

    [Fact]
    public void SunDirection_AzimuthFromNorthClockwise_InRenderSpace()
    {
        Near(new Vector3(0, 0, -1), Call<Vector3>("SunDirection", 0f, 0f));   // north = RAW +Z = RENDER -Z
        Near(new Vector3(1, 0, 0), Call<Vector3>("SunDirection", 90f, 0f));   // east = +X
        Near(new Vector3(0, 0, 1), Call<Vector3>("SunDirection", 180f, 0f));
        Near(new Vector3(0, 1, 0), Call<Vector3>("SunDirection", 33f, 89.9f), 3e-3f);
        foreach (var (az, el) in new[] { (135f, 40f), (10f, 5f), (300f, 70f) })
        {
            var d = Call<Vector3>("SunDirection", az, el);
            Assert.Equal(1f, d.Length(), 4);
            var (a2, e2) = Call<(float, float)>("SunAngles", d);
            Assert.Equal(az, a2, 2);
            Assert.Equal(el, e2, 2);
        }
    }

    [Fact]
    public void CameraLight_ZeroAnglesComesFromBehindTheCamera()
    {
        var fwd = Vector3.Normalize(new Vector3(0.3f, -0.4f, -1));
        var d = Call<Vector3>("CameraLightDirection", 0f, 0f, fwd, Vector3.UnitY);
        Near(-fwd, d, 1e-4f);
        var right = Call<Vector3>("CameraLightDirection", 90f, 0f, fwd, Vector3.UnitY);
        Assert.True(Vector3.Dot(right, Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY))) > 0.99f);
    }

    [Fact]
    public void ShadowProjection_EnclosesTheBounds()
    {
        var min = new Vector3(-3, 0, -8);
        var max = new Vector3(5, 6, 2);
        var (center, radius) = Call<(Vector3, float)>("Sphere", min, max);
        foreach (var dir in new[] { Vector3.Normalize(new Vector3(0.4f, 0.8f, -0.3f)), Vector3.UnitY, Vector3.Normalize(new Vector3(-1, 0.1f, 0)) })
        {
            var vp = Call<Matrix4x4>("ShadowViewProjection", center, radius, dir, 2048);
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                var c = Vector4.Transform(new Vector4(p, 1), vp);
                Assert.InRange(c.X / c.W, -1.001f, 1.001f);
                Assert.InRange(c.Y / c.W, -1.001f, 1.001f);
                Assert.InRange(c.Z / c.W, 0f, 1f); // System.Numerics depth range
            }
            // Points nearer the sun get smaller depth.
            var near = Vector4.Transform(new Vector4(center + dir * radius * 0.5f, 1), vp);
            var far = Vector4.Transform(new Vector4(center - dir * radius * 0.5f, 1), vp);
            Assert.True(near.Z < far.Z);
        }
    }

    [Fact]
    public void Fov_GameValueAndConversions()
    {
        // DayZ / Arma initFov 0.75 rad ≈ 43° vertical; at 16:9 ≈ 70° horizontal.
        float h = Call<float>("HorizontalFov", 0.75f, 16f / 9f);
        Assert.Equal(69.98f, h * 180f / MathF.PI, 1);
        Assert.Equal(0.75f, Call<float>("VerticalFov", h, 16f / 9f), 4);
    }

    [Fact]
    public void Fresnel_DielectricF0()
    {
        Assert.Equal(0.04f, Call<float>("FresnelF0", 1.5f, 0f), 3);
        Assert.True(Call<float>("FresnelF0", 0.5f, 3f) > 0.5f); // metal-like (k > 0) reflects strongly
        Assert.Equal(1f, Call<float>("ExposureScale", 0f), 5);
        Assert.Equal(2f, Call<float>("ExposureScale", 1f), 5);
    }

    [Fact]
    public void TransformBounds_ContainsTransformedCorners()
    {
        var m = SampleTransform(true);
        var (bmin, bmax) = Call<(Vector3, Vector3)>("TransformBounds", new Vector3(-1, -2, -3), new Vector3(4, 5, 6), m);
        var p = Vector3.Transform(new Vector3(4, -2, 6), m);
        Assert.True(p.X >= bmin.X - 1e-4f && p.X <= bmax.X + 1e-4f && p.Y >= bmin.Y - 1e-4f && p.Y <= bmax.Y + 1e-4f && p.Z >= bmin.Z - 1e-4f && p.Z <= bmax.Z + 1e-4f);
    }
}
