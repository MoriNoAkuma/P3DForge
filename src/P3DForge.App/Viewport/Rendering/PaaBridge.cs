using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>A decoded RGBA8 image, rows top-first (DirectX convention: sample (u, v) directly).</summary>
/// <param name="Blend">
/// The alpha is meant to be blended (see-through glass, smooth edges) rather than cut at 0.5: the PAA declares
/// "alpha transparency" (FLAG bit 0, not the alpha-key bit 1), or an image / procedural colour has partial alpha.
/// </param>
/// <param name="Keyed">
/// The PAA declares an alpha key (FLAG bit 1): its alpha cuts out whatever the name says, e.g. a _co ghillie / hair
/// texture saved as DXT1 with 1-bit alpha.
/// </param>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba, bool HasAlpha, bool Blend = false, bool Keyed = false);

/// <summary>
/// The single entry point from the renderer to the PAA decoder (<c>P3DForge.Core.Paa.Paa.DecodeRgba</c>) and the
/// procedural texture parser (<c>P3DForge.Core.Paa.ProceduralTexture.Parse</c>); a built-in "color(r,g,b,a)"
/// parser is the fallback for expressions the Core parser rejects.
/// </summary>
public static partial class PaaBridge
{

    /// <summary>True when the Core PAA decoder is available.</summary>
    public static bool DecoderAvailable => true;

    /// <summary>Decodes a .paa (largest mip ≤ <paramref name="maxSize"/>); null when no decoder is available. Throws on corrupt data.</summary>
    public static RgbaImage? DecodePaa(byte[] data, int maxSize)
    {
        var i = P3DForge.Core.Paa.Paa.DecodeRgba(data, maxSize);
        uint? flags = P3DForge.Core.Paa.Paa.ReadHeader(data).Flags;
        // FLAG bit 0 = interpolated alpha (the engine blends it), bit 1 = alpha key (cut out)
        bool blend = i.HasAlpha && (flags is { } f ? (f & 1) != 0 && (f & 2) == 0 : PartialAlpha(i.Rgba));
        bool keyed = i.HasAlpha && flags is { } k && (k & 2) != 0;
        return new RgbaImage(i.Width, i.Height, i.Rgba, i.HasAlpha, blend, keyed);
    }

    /// <summary>More than 2 % of the pixels are neither (almost) transparent nor opaque.</summary>
    public static bool PartialAlpha(byte[] rgba)
    {
        int n = rgba.Length / 4, partial = 0, step = Math.Max(1, n / 65536);
        int samples = 0;
        for (int i = 0; i < n; i += step)
        {
            byte a = rgba[i * 4 + 3];
            if (a is > 8 and < 247) partial++;
            samples++;
        }
        return partial * 50 > samples;
    }

    /// <summary>Decodes a PNG/JPEG/BMP (WPF codecs; any thread).</summary>
    public static RgbaImage DecodeImage(byte[] data, int maxSize)
    {
        using var ms = new MemoryStream(data);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource src = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int max = Math.Max(src.PixelWidth, src.PixelHeight);
        if (maxSize > 0 && max > maxSize)
        {
            double s = (double)maxSize / max;
            src = new TransformedBitmap(src, new ScaleTransform(s, s));
        }
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        bool alpha = false;
        for (int i = 0; i < px.Length; i += 4)
        {
            (px[i], px[i + 2]) = (px[i + 2], px[i]); // BGRA → RGBA
            alpha |= px[i + 3] != 255;
        }
        return new RgbaImage(w, h, px, alpha, alpha && PartialAlpha(px));
    }

    /// <summary>Procedural texture ("#(argb,8,8,3)color(r,g,b,a,co)") or null when it cannot be parsed.</summary>
    public static RgbaImage? Procedural(string name)
    {
        try
        {
            if (P3DForge.Core.Paa.ProceduralTexture.Parse(name) is { } img)
                return new RgbaImage(img.Width, img.Height, img.Rgba, img.HasAlpha, img.HasAlpha && PartialAlpha(img.Rgba));
        }
        catch
        {
            // fall through to the built-in colour parser
        }
        var m = ColorRx().Match(name);
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split(',');
        Span<byte> c = [128, 128, 128, 255];
        for (int i = 0; i < 4 && i < parts.Length; i++)
            if (float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                c[i] = (byte)Math.Clamp(MathF.Round(f * 255), 0, 255);
        var px = new byte[8 * 8 * 4];
        for (int i = 0; i < px.Length; i += 4) c.CopyTo(px.AsSpan(i, 4));
        return new RgbaImage(8, 8, px, c[3] != 255);
    }

    [GeneratedRegex(@"color\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ColorRx();

}
