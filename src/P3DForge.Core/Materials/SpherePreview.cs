using System.Numerics;

namespace P3DForge.Core.Materials;

/// <summary>An RGBA8 image used as preview input (decoded texture, usually a small mip).</summary>
public sealed record PreviewImage(int Width, int Height, byte[] Rgba)
{
    /// <summary>1×1 image of a colour (procedural "#(argb,8,8,3)color(...)" textures).</summary>
    public static PreviewImage Solid(Rgba c) =>
        new(1, 1, [ToByte(c.R), ToByte(c.G), ToByte(c.B), ToByte(c.A)]);

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
}

/// <summary>Inputs of <see cref="SpherePreview.RenderBgra"/>.</summary>
public sealed class SphereInputs
{
    /// <summary>Base colour texture (_co / _ca, sRGB); null = <see cref="DiffuseColor"/> only.</summary>
    public PreviewImage? Diffuse { get; set; }
    /// <summary>Normal map (_nohq); null = smooth sphere.</summary>
    public PreviewImage? Normal { get; set; }
    /// <summary>Specular map (_smdi: G = specular, B = gloss); null = full specular.</summary>
    public PreviewImage? Smdi { get; set; }
    public Rgba DiffuseColor { get; set; } = Rgba.White;
    public Rgba Ambient { get; set; } = Rgba.White;
    public Rgba Specular { get; set; } = new(0.2f, 0.2f, 0.2f, 1);
    public Rgba Emissive { get; set; } = Rgba.Black;
    public float SpecularPower { get; set; } = 50;
    /// <summary>Texture repeats across the visible hemisphere (U, V).</summary>
    public Vector2 Tiling { get; set; } = Vector2.One;

    /// <summary>Inputs from a material's colours (textures are set by the caller, which decodes them).</summary>
    public static SphereInputs FromMaterial(Rvmat m) => new()
    {
        DiffuseColor = m.Diffuse,
        Ambient = m.Ambient,
        Specular = m.Specular,
        Emissive = m.Emmisive,
        SpecularPower = m.SpecularPower <= 0 ? 1 : m.SpecularPower,
    };
}

/// <summary>CPU-rendered material preview: a lit sphere (fixed sun from the upper left + ambient), BGRA32, straight alpha.</summary>
public static class SpherePreview
{
    static readonly float[] ToLinear = BuildToLinear();
    static readonly byte[] ToSrgb = BuildToSrgb();

    static float[] BuildToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            var c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    static byte[] BuildToSrgb()
    {
        var t = new byte[4096];
        for (int i = 0; i < t.Length; i++)
        {
            var c = i / 4095f;
            var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
            t[i] = (byte)Math.Clamp((int)MathF.Round(s * 255), 0, 255);
        }
        return t;
    }

    static byte Encode(float linear) => ToSrgb[Math.Clamp((int)(linear * 4095f + 0.5f), 0, 4095)];

    /// <summary>Renders a size×size BGRA32 image (transparent outside the sphere, anti-aliased edge).</summary>
    public static byte[] RenderBgra(int size, SphereInputs input)
    {
        size = Math.Clamp(size, 8, 1024);
        var outp = new byte[size * size * 4];
        var light = Vector3.Normalize(new Vector3(-0.45f, 0.55f, 0.75f));
        var view = Vector3.UnitZ;
        var half = Vector3.Normalize(light + view);
        var dc = new Vector3(input.DiffuseColor.R, input.DiffuseColor.G, input.DiffuseColor.B);
        var amb = new Vector3(input.Ambient.R, input.Ambient.G, input.Ambient.B) * 0.32f;
        var spec = new Vector3(input.Specular.R, input.Specular.G, input.Specular.B);
        var emis = new Vector3(input.Emissive.R, input.Emissive.G, input.Emissive.B);
        var basePower = MathF.Max(1, input.SpecularPower);
        var tiling = input.Tiling;
        var inv = 2f / size;
        for (int py = 0; py < size; py++)
        {
            var y = 1 - (py + 0.5f) * inv;
            for (int px = 0; px < size; px++)
            {
                var x = (px + 0.5f) * inv - 1;
                var r2 = x * x + y * y;
                var r = MathF.Sqrt(r2);
                var alpha = Math.Clamp((1 - r) * size * 0.5f + 0.5f, 0, 1);
                if (alpha <= 0) continue;
                var z = MathF.Sqrt(MathF.Max(0, 1 - MathF.Min(r2, 1)));
                var n = new Vector3(x, y, z);
                if (r2 > 1) n = Vector3.Normalize(n);
                var phi = MathF.Atan2(n.X, n.Z);
                var theta = MathF.Asin(Math.Clamp(n.Y, -1, 1));
                var u = (phi / MathF.PI + 0.5f) * tiling.X;
                var v = (0.5f - theta / MathF.PI) * tiling.Y;

                if (input.Normal is { } nm)
                {
                    var s = Sample(nm, u, v);
                    var nx = s.X * 2 - 1;
                    var ny = s.Y * 2 - 1;
                    var nz = MathF.Sqrt(MathF.Max(0, 1 - nx * nx - ny * ny));
                    var sp = MathF.Sin(phi);
                    var cp = MathF.Cos(phi);
                    var tangent = new Vector3(cp, 0, -sp);
                    var down = new Vector3(sp * MathF.Sin(theta), -MathF.Cos(theta), cp * MathF.Sin(theta));
                    var pn = tangent * nx - down * ny + n * nz;
                    if (pn.LengthSquared() > 1e-8f) n = Vector3.Normalize(pn);
                }

                Vector3 albedo;
                if (input.Diffuse is { } dm)
                {
                    var s = Sample(dm, u, v, linear: true);
                    albedo = new Vector3(s.X, s.Y, s.Z) * dc;
                }
                else albedo = dc * 0.8f;

                float specMask = 1, gloss = 1;
                if (input.Smdi is { } sm)
                {
                    var s = Sample(sm, u, v);
                    specMask = s.Y;
                    gloss = s.Z;
                }

                var ndl = MathF.Max(0, Vector3.Dot(n, light));
                var color = albedo * (amb + new Vector3(ndl));
                if (ndl > 0)
                {
                    var power = basePower * (0.25f + gloss);
                    var ndh = MathF.Max(0, Vector3.Dot(n, half));
                    color += spec * (specMask * MathF.Pow(ndh, power) * MathF.Min(1, ndl * 4));
                }
                color += emis;

                var o = (py * size + px) * 4;
                outp[o] = Encode(color.Z);
                outp[o + 1] = Encode(color.Y);
                outp[o + 2] = Encode(color.X);
                outp[o + 3] = (byte)(alpha * 255 + 0.5f);
            }
        }
        return outp;
    }

    /// <summary>Bilinear, wrapping sample; RGB in 0..1 (linear when <paramref name="linear"/>, else raw).</summary>
    static Vector3 Sample(PreviewImage img, float u, float v, bool linear = false)
    {
        int w = img.Width, h = img.Height;
        if (w <= 0 || h <= 0 || img.Rgba.Length < w * h * 4) return Vector3.One;
        var fx = (u - MathF.Floor(u)) * w - 0.5f;
        var fy = (v - MathF.Floor(v)) * h - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var a = Texel(img, x0, y0, linear);
        var b = Texel(img, x0 + 1, y0, linear);
        var c = Texel(img, x0, y0 + 1, linear);
        var d = Texel(img, x0 + 1, y0 + 1, linear);
        return Vector3.Lerp(Vector3.Lerp(a, b, tx), Vector3.Lerp(c, d, tx), ty);
    }

    static Vector3 Texel(PreviewImage img, int x, int y, bool linear)
    {
        x %= img.Width;
        if (x < 0) x += img.Width;
        y %= img.Height;
        if (y < 0) y += img.Height;
        var o = (y * img.Width + x) * 4;
        var p = img.Rgba;
        return linear
            ? new Vector3(ToLinear[p[o]], ToLinear[p[o + 1]], ToLinear[p[o + 2]])
            : new Vector3(p[o] / 255f, p[o + 1] / 255f, p[o + 2] / 255f);
    }
}
