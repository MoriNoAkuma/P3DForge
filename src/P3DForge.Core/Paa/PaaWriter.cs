using System.Buffers.Binary;
using P3DForge.Core.Imaging;

namespace P3DForge.Core.Paa;

/// <summary>Options for <see cref="PaaWriter.Encode(RgbaImage, PaaEncodeOptions?)"/>.</summary>
public sealed record PaaEncodeOptions
{
    /// <summary>Forced format; null = from <see cref="Hint"/> (DXT1 when opaque, DXT5 with alpha for colour maps).</summary>
    public PaaFormat? Format { get; init; }
    /// <summary>Suffix rules (swizzle, colour space, mip filter); null = <see cref="TextureHints.Default"/>.</summary>
    public TextureHint? Hint { get; init; }
    /// <summary>Generate the mip chain (ImageToPAA always does).</summary>
    public bool Mipmaps { get; init; } = true;
    /// <summary>LZO-compress DXT mips wider than 128 pixels (ImageToPAA layout; required for 4096² DXT5).</summary>
    public bool Lzo { get; init; } = true;

    /// <summary>Options for a target file name: the suffix picks the rules ("x_nohq.paa" → swizzled DXT5).</summary>
    public static PaaEncodeOptions ForPath(string targetPath, PaaFormat? format = null) => new() { Hint = TextureHints.ForPath(targetPath), Format = format };
}

/// <summary>
/// Writes Bohemia PAA textures with the layout of DayZ Tools' ImageToPAA (verified on its output):
/// <c>uint16 type; TAGG AVGC; TAGG MAXC; [TAGG FLAG]; [TAGG SWIZ]; TAGG OFFS (16 mip offsets); uint16 0 (palette);
/// mips (uint16 w [|0x8000 = LZO], uint16 h, uint24 size, data); uint16 0, uint16 0; uint16 0</c>.
/// DXT mips go down to min(w,h) = 4 and are LZO-compressed when wider than 128; ARGB/AI mips go down to 1×1 and are
/// always LZSS-compressed (signed checksum). The pixel input is RGBA8, rows top-first (as decoded by <see cref="Paa"/>).
/// </summary>
public static class PaaWriter
{
    public const int MaxEdge = 8192;
    const int MaxMipData = 0xFFFFFF;

    /// <summary>Why <paramref name="width"/>×<paramref name="height"/> cannot be written as <paramref name="format"/>, or null.</summary>
    public static string? CheckSize(int width, int height, PaaFormat format)
    {
        if (!RgbaImage.IsPowerOfTwo(width) || !RgbaImage.IsPowerOfTwo(height))
            return $"PAA textures need power-of-two sizes (e.g. 512, 1024, 2048); this image is {width}×{height}";
        if (width > MaxEdge || height > MaxEdge) return $"{width}×{height} is larger than {MaxEdge}×{MaxEdge}";
        if (PaaFormats.IsDxt(format) && (width < 4 || height < 4)) return $"DXT textures must be at least 4×4 ({width}×{height})";
        return null;
    }

    /// <summary>Encodes <paramref name="image"/> using the rules of <paramref name="targetPath"/>'s suffix.</summary>
    public static byte[] Encode(RgbaImage image, string targetPath, PaaFormat? format = null) => Encode(image, PaaEncodeOptions.ForPath(targetPath, format));

    /// <summary>Encodes a complete PAA file.</summary>
    /// <exception cref="ArgumentException">Invalid size (not a power of two, too large, too small for DXT).</exception>
    public static byte[] Encode(RgbaImage image, PaaEncodeOptions? options = null)
    {
        options ??= new PaaEncodeOptions();
        var hint = options.Hint ?? TextureHints.Default;
        bool sourceAlpha = !image.IsOpaque();
        var format = hint.ResolveFormat(sourceAlpha, options.Format);
        if (CheckSize(image.Width, image.Height, format) is { } error) throw new ArgumentException(error);
        if (image.Rgba.Length < image.Width * image.Height * 4) throw new ArgumentException("pixel buffer is too small");

        bool dxt = PaaFormats.IsDxt(format);
        var levels = options.Mipmaps ? MipChain.Build(image, hint.Srgb, hint.Filter, dxt) : [image];
        var stored = new RgbaImage[levels.Count];
        for (int i = 0; i < levels.Count; i++) stored[i] = ApplySwizzle(levels[i], hint.Swizzle);
        bool storedAlpha = !stored[0].IsOpaque();

        // encode (+ compress) every mip; large mips are internally parallel, small ones run side by side
        var data = new byte[levels.Count][];
        var lzo = new bool[levels.Count];
        Parallel.For(0, levels.Count, i =>
        {
            var raw = EncodePixels(stored[i], format, storedAlpha);
            if (dxt)
            {
                if (options.Lzo && stored[i].Width > 128)
                {
                    var packed = LzoEncoder.Compress(raw);
                    if (packed.Length < raw.Length)
                    {
                        raw = packed;
                        lzo[i] = true;
                    }
                }
            }
            else raw = LzssEncoder.Compress(raw);
            data[i] = raw;
        });

        uint? flag = storedAlpha || sourceAlpha ? format is PaaFormat.Dxt1 or PaaFormat.Argb1555 ? 2u : 1u : null;
        return Assemble(format, levels, data, lzo, Average(image), flag, hint.Swizzle);
    }

    static byte[] Assemble(PaaFormat format, List<RgbaImage> levels, byte[][] data, bool[] lzo, uint average, uint? flag, byte[]? swizzle)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(PaaFormats.TypeTag(format));
        Tagg(w, "AVGC", average);
        Tagg(w, "MAXC", 0xFFFFFFFFu);
        if (flag is { } f) Tagg(w, "FLAG", f);
        if (swizzle is not null) Tagg(w, "SWIZ", (uint)(swizzle[0] | (swizzle[1] << 8) | (swizzle[2] << 16) | (swizzle[3] << 24)));
        int offsTable = (int)ms.Position + 12;
        w.Write("GGATSFFO"u8);
        w.Write(64);
        w.Write(new byte[64]);
        w.Write((ushort)0); // no palette

        Span<byte> offsets = stackalloc byte[64];
        for (int i = 0; i < levels.Count; i++)
        {
            if (data[i].Length > MaxMipData)
                throw new ArgumentException($"mip {levels[i].Width}×{levels[i].Height} needs {data[i].Length} bytes, more than a PAA mip can hold (16 MB)");
            if (i < 16) BinaryPrimitives.WriteUInt32LittleEndian(offsets[(i * 4)..], (uint)ms.Position);
            w.Write((ushort)(levels[i].Width | (lzo[i] ? 0x8000 : 0)));
            w.Write((ushort)levels[i].Height);
            int size = data[i].Length;
            w.Write((byte)size);
            w.Write((byte)(size >> 8));
            w.Write((byte)(size >> 16));
            w.Write(data[i]);
        }
        w.Write(0u); // terminator (0×0 mip)
        w.Write((ushort)0);
        w.Flush();
        var bytes = ms.ToArray();
        offsets.CopyTo(bytes.AsSpan(offsTable));
        return bytes;
    }

    static void Tagg(BinaryWriter w, string name, uint value)
    {
        w.Write("GGAT"u8);
        Span<byte> n = stackalloc byte[4];
        for (int i = 0; i < 4; i++) n[i] = (byte)name[3 - i]; // stored reversed: "AVGC" → "CGVA"
        w.Write(n);
        w.Write(4);
        w.Write(value);
    }

    /// <summary>Average colour of the source image as 0xAARRGGBB (the AVGC tag).</summary>
    public static uint Average(RgbaImage image)
    {
        long r = 0, g = 0, b = 0, a = 0;
        var p = image.Rgba;
        int n = image.Width * image.Height;
        for (int i = 0; i < n * 4; i += 4)
        {
            r += p[i];
            g += p[i + 1];
            b += p[i + 2];
            a += p[i + 3];
        }
        uint Avg(long v) => (uint)((v + n / 2) / n);
        return (Avg(a) << 24) | (Avg(r) << 16) | (Avg(g) << 8) | Avg(b);
    }

    /// <summary>Applies a TexConvert swizzle ([A,R,G,B] codes) to a copy of <paramref name="image"/>.</summary>
    public static RgbaImage ApplySwizzle(RgbaImage image, byte[]? swizzle)
    {
        if (swizzle is null) return image;
        var s = image.Rgba;
        var d = new byte[s.Length];
        // stored channel order in RGBA memory: R=1, G=2, B=3 → offsets 0,1,2; A → 3
        Span<int> target = [3, 0, 1, 2];
        Span<int> srcOffset = [3, 0, 1, 2]; // code & 3: 0 = A, 1 = R, 2 = G, 3 = B
        for (int ch = 0; ch < 4; ch++)
        {
            int code = swizzle[ch], t = target[ch];
            if (code >= 8)
            {
                byte constant = code == TextureHint.SwZero ? (byte)0 : (byte)255;
                for (int i = t; i < d.Length; i += 4) d[i] = constant;
                continue;
            }
            int so = srcOffset[code & 3];
            byte xor = (code & 4) != 0 ? (byte)255 : (byte)0;
            for (int i = 0; i < d.Length; i += 4) d[i + t] = (byte)(s[i + so] ^ xor);
        }
        return new RgbaImage(image.Width, image.Height, d);
    }

    /// <summary>Raw (uncompressed) pixel data of one mip in <paramref name="format"/>.</summary>
    public static byte[] EncodePixels(RgbaImage mip, PaaFormat format, bool alpha)
    {
        var s = mip.Rgba;
        int n = mip.Width * mip.Height;
        switch (format)
        {
            case PaaFormat.Dxt1: return DxtEncoder.EncodeDxt1(mip, alpha);
            case PaaFormat.Dxt5: return DxtEncoder.EncodeDxt5(mip);
            case PaaFormat.Argb8888:
            {
                var d = new byte[n * 4];
                for (int i = 0; i < n * 4; i += 4)
                {
                    d[i] = s[i + 2];
                    d[i + 1] = s[i + 1];
                    d[i + 2] = s[i];
                    d[i + 3] = s[i + 3];
                }
                return d;
            }
            case PaaFormat.Argb4444:
            {
                var d = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    int r = Q4(s[i * 4]), g = Q4(s[i * 4 + 1]), b = Q4(s[i * 4 + 2]), a = Q4(s[i * 4 + 3]);
                    d[i * 2] = (byte)((g << 4) | b);
                    d[i * 2 + 1] = (byte)((a << 4) | r);
                }
                return d;
            }
            case PaaFormat.Argb1555:
            {
                var d = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    int v = (s[i * 4 + 3] >= 128 ? 0x8000 : 0) | (Q5(s[i * 4]) << 10) | (Q5(s[i * 4 + 1]) << 5) | Q5(s[i * 4 + 2]);
                    d[i * 2] = (byte)v;
                    d[i * 2 + 1] = (byte)(v >> 8);
                }
                return d;
            }
            default: // AI88: intensity (Rec. 601 luma) + alpha
            {
                var d = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    d[i * 2] = (byte)((s[i * 4] * 299 + s[i * 4 + 1] * 587 + s[i * 4 + 2] * 114 + 500) / 1000);
                    d[i * 2 + 1] = s[i * 4 + 3];
                }
                return d;
            }
        }
    }

    static int Q4(int v) => (v * 15 + 127) / 255;
    static int Q5(int v) => (v * 31 + 127) / 255;
}
