using System.Runtime.InteropServices;

namespace P3DForge.Core.Paa;

/// <summary>LZO1X decompression (lzo1x_1 / lzo1x_999 streams), used by PAA for large DXT mips.</summary>
public static class Lzo
{
    /// <summary>Decompresses into <paramref name="dst"/>, which must be sized to the exact output length.</summary>
    /// <returns>Compressed bytes consumed, including the end-of-stream marker.</returns>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int end = src.Length, outEnd = dst.Length, ip = 0, op = 0;
        int state = 0; // literals copied after the previous instruction: 1..3 after a match, 4 after a literal run
        if (end < 3) throw new InvalidDataException("LZO stream too short");
        if (src[ip] > 17)
        {
            int count = src[ip++] - 17;
            if (ip + count > end) throw Truncated();
            if (op + count > outEnd) throw Overrun();
            src.Slice(ip, count).CopyTo(dst[op..]);
            ip += count;
            op += count;
            state = count < 4 ? count : 4;
        }
        while (true)
        {
            if (ip >= end) throw Truncated();
            int t = src[ip++];
            int matchPos, length, next;
            if (t < 16)
            {
                if (state == 0)
                {
                    int count = t;
                    if (count == 0)
                    {
                        while (ip < end && src[ip] == 0)
                        {
                            count += 255;
                            ip++;
                        }
                        if (ip >= end) throw Truncated();
                        count += 15 + src[ip++];
                    }
                    count += 3;
                    if (ip + count > end) throw Truncated();
                    if (op + count > outEnd) throw Overrun();
                    src.Slice(ip, count).CopyTo(dst[op..]);
                    ip += count;
                    op += count;
                    state = 4;
                    continue;
                }
                if (ip >= end) throw Truncated();
                if (state == 4)
                {
                    matchPos = op - 0x801 - (t >> 2) - (src[ip++] << 2);
                    length = 3;
                }
                else
                {
                    matchPos = op - 1 - (t >> 2) - (src[ip++] << 2);
                    length = 2;
                }
                next = t & 3;
            }
            else if (t >= 64)
            {
                if (ip >= end) throw Truncated();
                matchPos = op - 1 - ((t >> 2) & 7) - (src[ip++] << 3);
                length = (t >> 5) + 1;
                next = t & 3;
            }
            else
            {
                bool m3 = t >= 32;
                length = m3 ? t & 31 : t & 7;
                if (length == 0)
                {
                    while (ip < end && src[ip] == 0)
                    {
                        length += 255;
                        ip++;
                    }
                    if (ip >= end) throw Truncated();
                    length += (m3 ? 31 : 7) + src[ip++];
                }
                length += 2;
                if (ip + 2 > end) throw Truncated();
                int word = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                next = word & 3;
                if (m3) matchPos = op - 1 - (word >> 2);
                else
                {
                    int distance = ((t & 8) << 11) + (word >> 2);
                    if (distance == 0)
                    {
                        if (length != 3) throw new InvalidDataException("LZO end-of-stream marker is malformed");
                        if (op != outEnd) throw new InvalidDataException($"LZO stream produced {op} of {outEnd} bytes");
                        return ip;
                    }
                    matchPos = op - distance - 0x4000;
                }
            }
            if (matchPos < 0) throw new InvalidDataException("LZO back-reference before start of output");
            if (op + length > outEnd) throw Overrun();
            if (op - matchPos >= length) dst.Slice(matchPos, length).CopyTo(dst[op..]);
            else
                for (int i = 0; i < length; i++) dst[op + i] = dst[matchPos + i]; // overlapping match repeats the pattern
            op += length;
            if (next != 0)
            {
                if (ip + next > end) throw Truncated();
                if (op + next > outEnd) throw Overrun();
                for (int i = 0; i < next; i++) dst[op++] = src[ip++];
            }
            state = next;
        }
    }

    static InvalidDataException Truncated() => new("LZO stream is truncated");
    static InvalidDataException Overrun() => new("LZO stream overruns the expected output size");
}

/// <summary>Bohemia LZSS (4 KiB window, 0x20 filler, byte-sum checksum), used for non-DXT PAA mips and PBOs.</summary>
public static class Lzss
{
    const int Window = 4096;
    const byte Filler = 0x20;

    /// <summary>Decompresses into <paramref name="dst"/> (exact size) and verifies the trailing checksum.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int end = src.Length, outEnd = dst.Length, ip = 0, op = 0;
        while (op < outEnd)
        {
            if (ip >= end) throw Truncated();
            int flags = src[ip++];
            for (int bit = 1; bit < 0x100 && op < outEnd; bit <<= 1)
            {
                if ((flags & bit) != 0)
                {
                    if (ip >= end) throw Truncated();
                    dst[op++] = src[ip++];
                    continue;
                }
                if (ip + 2 > end) throw Truncated();
                int high = src[ip + 1];
                int distance = src[ip] | ((high & 0xF0) << 4);
                if (distance == 0) distance = Window;
                int stop = op + (high & 0x0F) + 3;
                ip += 2;
                if (stop > outEnd) throw new InvalidDataException("LZSS stream overruns the expected output size");
                for (int from = op - distance; op < stop; op++, from++) dst[op] = from < 0 ? Filler : dst[from];
            }
        }
        if (ip + 4 > end) throw Truncated();
        uint stored = (uint)(src[ip] | (src[ip + 1] << 8) | (src[ip + 2] << 16) | (src[ip + 3] << 24));
        uint sum = 0, high7 = 0;
        foreach (byte b in dst)
        {
            sum += b;
            high7 += (uint)(b >> 7);
        }
        uint signedSum = sum - 256 * high7;
        if (stored != sum && stored != signedSum) throw new InvalidDataException($"LZSS checksum mismatch (stored 0x{stored:x8}, computed 0x{sum:x8})");
        return ip + 4;
    }

    static InvalidDataException Truncated() => new("LZSS stream is truncated");
}

/// <summary>DXT1/3/5 (BC1/2/3) block decoding to RGBA8, rows top-first; partial blocks are cropped.</summary>
public static class Dxt
{
    public static int DataSize(int blockBytes, int width, int height) => ((width + 3) >> 2) * ((height + 3) >> 2) * blockBytes;

    public static void DecodeDxt1(ReadOnlySpan<byte> src, int width, int height, Span<byte> outRgba)
    {
        Check(src, 8, width, height);
        var out32 = MemoryMarshal.Cast<byte, uint>(outRgba);
        Span<uint> pal = stackalloc uint[4];
        int bx0 = (width + 3) >> 2, by0 = (height + 3) >> 2, p = 0;
        for (int by = 0; by < by0; by++)
        {
            int y0 = by << 2, rows = Math.Min(4, height - y0);
            for (int bx = 0; bx < bx0; bx++, p += 8)
            {
                int x0 = bx << 2, cols = Math.Min(4, width - x0);
                int c0 = src[p] | (src[p + 1] << 8), c1 = src[p + 2] | (src[p + 3] << 8);
                ColorPalette(c0, c1, c0 > c1, 0xFF, pal);
                int o = y0 * width + x0;
                for (int y = 0; y < rows; y++, o += width)
                {
                    int bits = src[p + 4 + y];
                    for (int x = 0; x < cols; x++) out32[o + x] = pal[(bits >> (x << 1)) & 3];
                }
            }
        }
    }

    public static void DecodeDxt3(ReadOnlySpan<byte> src, int width, int height, Span<byte> outRgba)
    {
        Check(src, 16, width, height);
        var out32 = MemoryMarshal.Cast<byte, uint>(outRgba);
        Span<uint> pal = stackalloc uint[4];
        int bx0 = (width + 3) >> 2, by0 = (height + 3) >> 2, p = 0;
        for (int by = 0; by < by0; by++)
        {
            int y0 = by << 2, rows = Math.Min(4, height - y0);
            for (int bx = 0; bx < bx0; bx++, p += 16)
            {
                int x0 = bx << 2, cols = Math.Min(4, width - x0);
                ColorPalette(src[p + 8] | (src[p + 9] << 8), src[p + 10] | (src[p + 11] << 8), true, 0, pal);
                int o = y0 * width + x0;
                for (int y = 0; y < rows; y++, o += width)
                {
                    int bits = src[p + 12 + y];
                    int alphas = src[p + 2 * y] | (src[p + 2 * y + 1] << 8);
                    for (int x = 0; x < cols; x++)
                        out32[o + x] = pal[(bits >> (x << 1)) & 3] | ((uint)(((alphas >> (x << 2)) & 15) * 17) << 24);
                }
            }
        }
    }

    public static void DecodeDxt5(ReadOnlySpan<byte> src, int width, int height, Span<byte> outRgba)
    {
        Check(src, 16, width, height);
        var out32 = MemoryMarshal.Cast<byte, uint>(outRgba);
        Span<uint> pal = stackalloc uint[4];
        Span<uint> alpha = stackalloc uint[8];
        int bx0 = (width + 3) >> 2, by0 = (height + 3) >> 2, p = 0;
        for (int by = 0; by < by0; by++)
        {
            int y0 = by << 2, rows = Math.Min(4, height - y0);
            for (int bx = 0; bx < bx0; bx++, p += 16)
            {
                int x0 = bx << 2, cols = Math.Min(4, width - x0);
                AlphaPalette(src[p], src[p + 1], alpha);
                int lowBits = src[p + 2] | (src[p + 3] << 8) | (src[p + 4] << 16);
                int highBits = src[p + 5] | (src[p + 6] << 8) | (src[p + 7] << 16);
                ColorPalette(src[p + 8] | (src[p + 9] << 8), src[p + 10] | (src[p + 11] << 8), true, 0, pal);
                int o = y0 * width + x0;
                for (int y = 0; y < rows; y++, o += width)
                {
                    int bits = src[p + 12 + y];
                    int alphas = y < 2 ? lowBits >> (12 * y) : highBits >> (12 * (y - 2));
                    for (int x = 0; x < cols; x++) out32[o + x] = pal[(bits >> (x << 1)) & 3] | alpha[(alphas >> (3 * x)) & 7];
                }
            }
        }
    }

    static void Check(ReadOnlySpan<byte> src, int blockBytes, int w, int h)
    {
        if (src.Length < DataSize(blockBytes, w, h)) throw new InvalidDataException("DXT data is truncated");
    }

    static uint Pack(int r, int g, int b, int a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
    static int E5(int v) => (v << 3) | (v >> 2);
    static int E6(int v) => (v << 2) | (v >> 4);

    static void ColorPalette(int c0, int c1, bool four, int a, Span<uint> pal)
    {
        int r0 = E5(c0 >> 11), g0 = E6((c0 >> 5) & 0x3F), b0 = E5(c0 & 0x1F);
        int r1 = E5(c1 >> 11), g1 = E6((c1 >> 5) & 0x3F), b1 = E5(c1 & 0x1F);
        pal[0] = Pack(r0, g0, b0, a);
        pal[1] = Pack(r1, g1, b1, a);
        if (four)
        {
            pal[2] = Pack((2 * r0 + r1 + 1) / 3, (2 * g0 + g1 + 1) / 3, (2 * b0 + b1 + 1) / 3, a);
            pal[3] = Pack((r0 + 2 * r1 + 1) / 3, (g0 + 2 * g1 + 1) / 3, (b0 + 2 * b1 + 1) / 3, a);
        }
        else
        {
            pal[2] = Pack((r0 + r1 + 1) >> 1, (g0 + g1 + 1) >> 1, (b0 + b1 + 1) >> 1, a);
            pal[3] = 0;
        }
    }

    static void AlphaPalette(int a0, int a1, Span<uint> w)
    {
        w[0] = (uint)a0 << 24;
        w[1] = (uint)a1 << 24;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) w[i + 1] = (uint)(((7 - i) * a0 + i * a1 + 3) / 7) << 24;
        else
        {
            for (int i = 1; i < 5; i++) w[i + 1] = (uint)(((5 - i) * a0 + i * a1 + 2) / 5) << 24;
            w[6] = 0;
            w[7] = 0xFFu << 24;
        }
    }
}

/// <summary>Non-DXT PAA pixel formats → RGBA8. Source words are little-endian.</summary>
public static class Pixels
{
    /// <summary>0x4444: 16-bit AAAA RRRR GGGG BBBB.</summary>
    public static void Argb4444(ReadOnlySpan<byte> s, int count, Span<byte> o)
    {
        for (int i = 0; i < count; i++)
        {
            int lo = s[i * 2], hi = s[i * 2 + 1];
            o[i * 4] = (byte)((hi & 0x0F) * 17);
            o[i * 4 + 1] = (byte)((lo >> 4) * 17);
            o[i * 4 + 2] = (byte)((lo & 0x0F) * 17);
            o[i * 4 + 3] = (byte)((hi >> 4) * 17);
        }
    }

    /// <summary>0x1555: 16-bit A RRRRR GGGGG BBBBB.</summary>
    public static void Argb1555(ReadOnlySpan<byte> s, int count, Span<byte> o)
    {
        for (int i = 0; i < count; i++)
        {
            int v = s[i * 2] | (s[i * 2 + 1] << 8);
            int r = (v >> 10) & 0x1F, g = (v >> 5) & 0x1F, b = v & 0x1F;
            o[i * 4] = (byte)((r << 3) | (r >> 2));
            o[i * 4 + 1] = (byte)((g << 3) | (g >> 2));
            o[i * 4 + 2] = (byte)((b << 3) | (b >> 2));
            o[i * 4 + 3] = (byte)((v & 0x8000) != 0 ? 0xFF : 0);
        }
    }

    /// <summary>0x8888: 32-bit 0xAARRGGBB (bytes B, G, R, A).</summary>
    public static void Argb8888(ReadOnlySpan<byte> s, int count, Span<byte> o)
    {
        for (int i = 0; i < count * 4; i += 4)
        {
            o[i] = s[i + 2];
            o[i + 1] = s[i + 1];
            o[i + 2] = s[i];
            o[i + 3] = s[i + 3];
        }
    }

    /// <summary>0x8080 AI88: intensity in the low byte, alpha in the high byte.</summary>
    public static void Ai88(ReadOnlySpan<byte> s, int count, Span<byte> o)
    {
        for (int i = 0; i < count; i++)
        {
            byte intensity = s[i * 2];
            o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = intensity;
            o[i * 4 + 3] = s[i * 2 + 1];
        }
    }
}

/// <summary>
/// Undoes the TexConvert channel swizzle ("GGATZIWS"). Codes per stored channel: 0..3 = source A, R, G, B;
/// +4 = inverted; 8 = constant 1; 9 = constant 0 (e.g. _nohq normal maps store A = 1-R).
/// </summary>
public static class Swizzle
{
    static readonly int[] SourceToRgba = [3, 0, 1, 2];
    const int Lost = -1;

    public static void ApplyInverse(Span<byte> rgba, ReadOnlySpan<byte> swizzle)
    {
        Span<int> from = [Lost, Lost, Lost, Lost];
        Span<int> xor = [0, 0, 0, 0];
        for (int stored = 0; stored < 4; stored++)
        {
            int code = swizzle[stored];
            if (code >= 8) continue;
            int source = SourceToRgba[code & 3];
            if (from[source] != Lost && stored != source) continue;
            from[source] = stored;
            xor[source] = (code & 4) != 0 ? 0xFF : 0;
        }
        int recovered = 0, only = 0;
        for (int c = 0; c < 3; c++)
            if (from[c] != Lost)
            {
                recovered++;
                only = c;
            }
        for (int c = 0; c < 3; c++)
        {
            if (from[c] != Lost) continue;
            from[c] = recovered == 1 ? from[only] : c;
            xor[c] = recovered == 1 ? xor[only] : 0;
        }
        bool opaque = from[3] == Lost;
        if (!opaque && from[0] == 0 && from[1] == 1 && from[2] == 2 && from[3] == 3 && xor[0] == 0 && xor[1] == 0 && xor[2] == 0 && xor[3] == 0) return;
        int fr = from[0], fg = from[1], fb = from[2], fa = from[3];
        byte xr = (byte)xor[0], xg = (byte)xor[1], xb = (byte)xor[2], xa = (byte)xor[3];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = (byte)(rgba[i + fr] ^ xr), g = (byte)(rgba[i + fg] ^ xg), b = (byte)(rgba[i + fb] ^ xb);
            byte a = opaque ? (byte)255 : (byte)(rgba[i + fa] ^ xa);
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }
    }
}

/// <summary>Bohemia procedural texture strings, e.g. <c>#(argb,8,8,3)color(1,0,0,1,co)</c>.</summary>
public static partial class ProceduralTexture
{
    const int MaxEdge = 64;

    [System.Text.RegularExpressions.GeneratedRegex(@"^#\(\s*([a-z0-9]+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*\d+\s*)?\)\s*([a-z_][a-z0-9_]*)\s*\(([^)]*)\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    /// <summary>Renders the <c>color(r,g,b[,a][,tag])</c> generator to a solid RGBA8 image; null when unsupported.</summary>
    public static PaaImage? Parse(string name)
    {
        var m = Pattern().Match(name.Trim());
        if (!m.Success || !m.Groups[4].Value.Equals("color", StringComparison.OrdinalIgnoreCase)) return null;
        var values = new List<double>();
        foreach (var part in m.Groups[5].Value.Split(','))
        {
            if (values.Count == 4 || !double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) break;
            values.Add(v);
        }
        if (values.Count < 3) return null;
        double r = values[0], g = values[1], b = values[2], a = values.Count > 3 ? values[3] : 1;
        string format = m.Groups[1].Value.ToLowerInvariant();
        if (format == "rgb") a = 1;
        if (format is "ai" or "i") r = g = b = 0.299 * r + 0.587 * g + 0.114 * b;
        int w = Math.Clamp(int.Parse(m.Groups[2].Value), 1, MaxEdge), h = Math.Clamp(int.Parse(m.Groups[3].Value), 1, MaxEdge);
        static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = B(r);
            rgba[i + 1] = B(g);
            rgba[i + 2] = B(b);
            rgba[i + 3] = B(a);
        }
        return new PaaImage(w, h, rgba, B(a) < 255, "procedural", 0);
    }
}
