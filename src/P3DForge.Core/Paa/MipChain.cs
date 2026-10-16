using P3DForge.Core.Imaging;

namespace P3DForge.Core.Paa;

/// <summary>
/// Mipmap generation: 2×2 box filter (2×1 / 1×2 once an edge reaches 1). sRGB colour maps are averaged in linear
/// light; colour is alpha-weighted when the block is partly transparent (no dark fringes on cut-outs); alpha and data
/// maps (_nohq, _smdi, _as, _mask, …) are averaged linearly; normal maps are renormalised.
/// </summary>
public static class MipChain
{
    static readonly float[] ToLinear = new float[256];
    const int LutSize = 16384;
    static readonly byte[] ToSrgb = new byte[LutSize + 1];

    static MipChain()
    {
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            ToLinear[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        for (int i = 0; i <= LutSize; i++)
        {
            double l = (double)i / LutSize;
            double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            ToSrgb[i] = (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
        }
    }

    /// <summary>
    /// The chain from <paramref name="image"/> (level 0, not copied) down to min(w,h) == 4 for DXT
    /// (<paramref name="dxt"/>) or 1×1 otherwise.
    /// </summary>
    public static List<RgbaImage> Build(RgbaImage image, bool srgb, MipFilter filter, bool dxt)
    {
        var list = new List<RgbaImage> { image };
        var cur = image;
        while (true)
        {
            bool more = dxt ? Math.Min(cur.Width, cur.Height) > 4 : cur.Width > 1 || cur.Height > 1;
            if (!more) break;
            cur = Downsample(cur, srgb, filter == MipFilter.NormalizeNormal);
            list.Add(cur);
        }
        return list;
    }

    /// <summary>One level down (each dimension halved, never below 1).</summary>
    public static RgbaImage Downsample(RgbaImage src, bool srgb, bool normalize)
    {
        int sw = src.Width, sh = src.Height;
        int w = Math.Max(1, sw >> 1), h = Math.Max(1, sh >> 1);
        int fx = sw > 1 ? 2 : 1, fy = sh > 1 ? 2 : 1;
        var s = src.Rgba;
        var d = new byte[w * h * 4];
        void Row(int y)
        {
            Span<int> idx = stackalloc int[4];
            for (int x = 0; x < w; x++)
            {
                int n = 0;
                for (int dy = 0; dy < fy; dy++)
                    for (int dx = 0; dx < fx; dx++)
                        idx[n++] = (((y * fy + dy) * sw) + x * fx + dx) << 2;
                int o = (y * w + x) << 2;
                int aSum = 0;
                for (int k = 0; k < n; k++) aSum += s[idx[k] + 3];
                d[o + 3] = (byte)((aSum + (n >> 1)) / n);
                bool weighted = aSum > 0 && aSum < 255 * n;
                if (srgb)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float acc = 0, wsum = 0;
                        for (int k = 0; k < n; k++)
                        {
                            float wgt = weighted ? s[idx[k] + 3] : 1f;
                            acc += ToLinear[s[idx[k] + c]] * wgt;
                            wsum += wgt;
                        }
                        d[o + c] = ToSrgb[(int)(acc / wsum * LutSize + 0.5f)];
                    }
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int acc = 0;
                        for (int k = 0; k < n; k++) acc += s[idx[k] + c];
                        d[o + c] = (byte)((acc + (n >> 1)) / n);
                    }
                    if (normalize) Normalize(d, o, s, idx[..n]);
                }
            }
        }
        if (w * h >= 16384) Parallel.For(0, h, Row);
        else
            for (int y = 0; y < h; y++) Row(y);
        return new RgbaImage(w, h, d);
    }

    static void Normalize(byte[] d, int o, byte[] s, ReadOnlySpan<int> idx)
    {
        float x = 0, y = 0, z = 0;
        foreach (int i in idx)
        {
            x += s[i] / 127.5f - 1f;
            y += s[i + 1] / 127.5f - 1f;
            z += s[i + 2] / 127.5f - 1f;
        }
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (len < 1e-6f) return;
        d[o] = (byte)Math.Clamp((int)MathF.Round((x / len + 1f) * 127.5f), 0, 255);
        d[o + 1] = (byte)Math.Clamp((int)MathF.Round((y / len + 1f) * 127.5f), 0, 255);
        d[o + 2] = (byte)Math.Clamp((int)MathF.Round((z / len + 1f) * 127.5f), 0, 255);
    }
}
