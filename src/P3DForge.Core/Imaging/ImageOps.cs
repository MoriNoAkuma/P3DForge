namespace P3DForge.Core.Imaging;

/// <summary>
/// Tone adjustment of one channel, applied as a 256-entry lookup table in this order: input levels
/// (<see cref="InBlack"/>..<see cref="InWhite"/> → 0..1), gamma (x^(1/gamma), &gt; 1 brightens mid-tones), output levels,
/// brightness (added), contrast (around 0.5), invert.
/// </summary>
public sealed record ChannelAdjust
{
    public static readonly ChannelAdjust None = new();

    /// <summary>-1..1, added to the value (1 = +255).</summary>
    public double Brightness { get; init; }
    /// <summary>-1..1 (0 = unchanged, -1 = flat grey, → 1 = hard threshold).</summary>
    public double Contrast { get; init; }
    /// <summary>0.1..10 (1 = unchanged).</summary>
    public double Gamma { get; init; } = 1;
    public int InBlack { get; init; }
    public int InWhite { get; init; } = 255;
    public int OutBlack { get; init; }
    public int OutWhite { get; init; } = 255;
    public bool Invert { get; init; }

    public bool IsIdentity => this == None;

    public byte[] BuildLut()
    {
        var lut = new byte[256];
        double inRange = Math.Max(1, InWhite - InBlack);
        double gamma = Math.Clamp(Gamma, 0.01, 100);
        double c = Math.Clamp(Contrast, -1, 1);
        double factor = c >= 0 ? 1 / (1 - Math.Min(c, 0.995)) : 1 + c;
        for (int v = 0; v < 256; v++)
        {
            double x = Math.Clamp((v - InBlack) / inRange, 0, 1);
            x = Math.Pow(x, 1 / gamma);
            x = (OutBlack + x * (OutWhite - OutBlack)) / 255.0;
            x += Brightness;
            x = (x - 0.5) * factor + 0.5;
            x = Math.Clamp(x, 0, 1);
            if (Invert) x = 1 - x;
            lut[v] = (byte)Math.Round(x * 255);
        }
        return lut;
    }
}

/// <summary>Pure image operations used by the texture tools (new images, inputs untouched).</summary>
public static class ImageOps
{
    /// <summary>Applies per-channel adjustments (index 0..3 = R, G, B, A; null/identity entries are skipped).</summary>
    public static RgbaImage Adjust(RgbaImage src, IReadOnlyList<ChannelAdjust?> channels)
    {
        var luts = new byte[]?[4];
        bool any = false;
        for (int c = 0; c < 4 && c < channels.Count; c++)
            if (channels[c] is { IsIdentity: false } a)
            {
                luts[c] = a.BuildLut();
                any = true;
            }
        var d = (byte[])src.Rgba.Clone();
        if (!any) return src with { Rgba = d };
        for (int c = 0; c < 4; c++)
        {
            if (luts[c] is not { } lut) continue;
            for (int i = c; i < d.Length; i += 4) d[i] = lut[d[i]];
        }
        return src with { Rgba = d };
    }

    /// <summary>
    /// Preview of one channel: <paramref name="channel"/> 0..3 = R, G, B, A shown as grey; -1 = RGB (opaque);
    /// -2 = unchanged RGBA.
    /// </summary>
    public static RgbaImage Isolate(RgbaImage src, int channel)
    {
        var s = src.Rgba;
        var d = new byte[s.Length];
        if (channel == -2) s.CopyTo(d, 0);
        else if (channel < 0)
            for (int i = 0; i < s.Length; i += 4)
            {
                d[i] = s[i];
                d[i + 1] = s[i + 1];
                d[i + 2] = s[i + 2];
                d[i + 3] = 255;
            }
        else
            for (int i = 0; i < s.Length; i += 4)
            {
                byte v = s[i + channel];
                d[i] = d[i + 1] = d[i + 2] = v;
                d[i + 3] = 255;
            }
        return src with { Rgba = d };
    }

    /// <summary>
    /// Resamples to <paramref name="width"/>×<paramref name="height"/> with a separable tent filter whose support
    /// widens when shrinking (area-like downscale, bilinear upscale).
    /// </summary>
    public static RgbaImage Resize(RgbaImage src, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (width == src.Width && height == src.Height) return src.Copy();
        var (xs, xw) = Contributions(src.Width, width);
        var (ys, yw) = Contributions(src.Height, height);
        int sw = src.Width, sh = src.Height;
        var s = src.Rgba;
        var tmp = new float[sh * width * 4];
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                var w = xw[x];
                for (int k = 0; k < w.Length; k++)
                {
                    int o = (y * sw + xs[x] + k) * 4;
                    float f = w[k];
                    r += s[o] * f;
                    g += s[o + 1] * f;
                    b += s[o + 2] * f;
                    a += s[o + 3] * f;
                }
                int t = (y * width + x) * 4;
                tmp[t] = r;
                tmp[t + 1] = g;
                tmp[t + 2] = b;
                tmp[t + 3] = a;
            }
        });
        var d = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            var w = yw[y];
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 4; c++)
                {
                    float acc = 0;
                    for (int k = 0; k < w.Length; k++) acc += tmp[((ys[y] + k) * width + x) * 4 + c] * w[k];
                    d[(y * width + x) * 4 + c] = (byte)Math.Clamp((int)(acc + 0.5f), 0, 255);
                }
        });
        return new RgbaImage(width, height, d);
    }

    static (int[] Start, float[][] Weights) Contributions(int srcSize, int dstSize)
    {
        double scale = (double)srcSize / dstSize, radius = Math.Max(1.0, scale);
        var start = new int[dstSize];
        var weights = new float[dstSize][];
        for (int i = 0; i < dstSize; i++)
        {
            double center = (i + 0.5) * scale - 0.5;
            int lo = (int)Math.Floor(center - radius + 1e-9), hi = (int)Math.Ceiling(center + radius - 1e-9);
            lo = Math.Max(lo, 0);
            hi = Math.Min(hi, srcSize - 1);
            if (hi < lo) hi = lo;
            var w = new float[hi - lo + 1];
            double sum = 0;
            for (int k = lo; k <= hi; k++)
            {
                double v = Math.Max(0, 1 - Math.Abs(k - center) / radius);
                w[k - lo] = (float)v;
                sum += v;
            }
            if (sum <= 0)
            {
                w = [1f];
                lo = Math.Clamp((int)Math.Round(center), 0, srcSize - 1);
                sum = 1;
            }
            for (int k = 0; k < w.Length; k++) w[k] = (float)(w[k] / sum);
            start[i] = lo;
            weights[i] = w;
        }
        return (start, weights);
    }
}
