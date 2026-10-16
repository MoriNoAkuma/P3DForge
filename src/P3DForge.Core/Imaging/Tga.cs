using System.Buffers.Binary;

namespace P3DForge.Core.Imaging;

/// <summary>
/// Truevision TGA reader (uncompressed and RLE; true-colour 15/16/24/32-bit, grey 8/16-bit, colour-mapped 8-bit;
/// any origin) and a 32-bit uncompressed top-left writer.
/// </summary>
public static class Tga
{
    public static RgbaImage Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 18) throw new InvalidDataException("Not a TGA file: too small");
        int idLength = d[0], mapType = d[1], type = d[2];
        int mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(d[3..]), mapLength = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]), mapBits = d[7];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]), h = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        int bits = d[16], desc = d[17];
        bool rle = type is 9 or 10 or 11;
        int baseType = type & 7;
        if (baseType is not (1 or 2 or 3)) throw new InvalidDataException($"Unsupported TGA image type {type}");
        if (w == 0 || h == 0) throw new InvalidDataException("TGA has no pixels");
        int bpp = (bits + 7) >> 3;
        if (baseType == 2 && bits is not (15 or 16 or 24 or 32)) throw new InvalidDataException($"Unsupported TGA depth {bits}");
        if (baseType == 3 && bits is not (8 or 16)) throw new InvalidDataException($"Unsupported grey TGA depth {bits}");
        if (baseType == 1 && (bits != 8 || mapType != 1)) throw new InvalidDataException("Unsupported colour-mapped TGA");

        int pos = 18 + idLength;
        byte[]? palette = null;
        if (mapType == 1)
        {
            int entry = (mapBits + 7) >> 3;
            if (pos + mapLength * entry > d.Length) throw new InvalidDataException("Truncated TGA colour map");
            palette = new byte[(mapFirst + mapLength) * 4];
            for (int i = 0; i < mapLength; i++) Pixel(d[(pos + i * entry)..], mapBits, palette.AsSpan((mapFirst + i) * 4));
            pos += mapLength * entry;
        }

        // decode to a flat array of source-order pixels (bpp bytes each)
        int count = w * h;
        var raw = new byte[count * bpp];
        if (!rle)
        {
            if (pos + raw.Length > d.Length) throw new InvalidDataException("Truncated TGA pixel data");
            d.Slice(pos, raw.Length).CopyTo(raw);
        }
        else
        {
            int o = 0;
            while (o < raw.Length)
            {
                if (pos >= d.Length) throw new InvalidDataException("Truncated TGA RLE data");
                int head = d[pos++], n = (head & 0x7F) + 1;
                if (o + n * bpp > raw.Length) throw new InvalidDataException("Corrupt TGA RLE packet");
                if ((head & 0x80) != 0)
                {
                    if (pos + bpp > d.Length) throw new InvalidDataException("Truncated TGA RLE data");
                    var px = d.Slice(pos, bpp);
                    for (int k = 0; k < n; k++, o += bpp) px.CopyTo(raw.AsSpan(o));
                    pos += bpp;
                }
                else
                {
                    if (pos + n * bpp > d.Length) throw new InvalidDataException("Truncated TGA RLE data");
                    d.Slice(pos, n * bpp).CopyTo(raw.AsSpan(o));
                    pos += n * bpp;
                    o += n * bpp;
                }
            }
        }

        var img = RgbaImage.Create(w, h);
        var dst = img.Rgba;
        bool topFirst = (desc & 0x20) != 0, rightToLeft = (desc & 0x10) != 0;
        for (int y = 0; y < h; y++)
        {
            int dy = topFirst ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int dx = rightToLeft ? w - 1 - x : x;
                var s = raw.AsSpan((y * w + x) * bpp, bpp);
                var t = dst.AsSpan((dy * w + dx) * 4, 4);
                if (baseType == 1)
                {
                    int i = s[0];
                    if (palette is null || i * 4 + 4 > palette.Length) throw new InvalidDataException("TGA colour index out of range");
                    palette.AsSpan(i * 4, 4).CopyTo(t);
                }
                else if (baseType == 3)
                {
                    t[0] = t[1] = t[2] = s[0];
                    t[3] = bits == 16 ? s[1] : (byte)255;
                }
                else Pixel(s, bits, t);
            }
        }
        // 32-bit files whose alpha is all zero (alpha bits not declared) are opaque images
        if (bits == 32 && (desc & 0x0F) == 0)
        {
            bool allZero = true;
            for (int i = 3; i < dst.Length && allZero; i += 4) allZero = dst[i] == 0;
            if (allZero)
                for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
        }
        return img;
    }

    static void Pixel(ReadOnlySpan<byte> s, int bits, Span<byte> t)
    {
        switch (bits)
        {
            case 32:
                t[0] = s[2];
                t[1] = s[1];
                t[2] = s[0];
                t[3] = s[3];
                break;
            case 24:
                t[0] = s[2];
                t[1] = s[1];
                t[2] = s[0];
                t[3] = 255;
                break;
            default: // 15/16-bit ARRRRRGG GGGBBBBB
                int v = s[0] | (s[1] << 8);
                int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                t[0] = (byte)((r << 3) | (r >> 2));
                t[1] = (byte)((g << 3) | (g >> 2));
                t[2] = (byte)((b << 3) | (b >> 2));
                t[3] = 255; // the attribute bit is unreliable in practice: 15/16-bit TGAs are treated as opaque
                break;
        }
    }

    /// <summary>32-bit BGRA, uncompressed, top-left origin, 8 alpha bits.</summary>
    public static byte[] Write(RgbaImage img)
    {
        if (img.Width > 65535 || img.Height > 65535) throw new ArgumentException("image too large for TGA");
        var d = new byte[18 + img.Width * img.Height * 4];
        d[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), (ushort)img.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), (ushort)img.Height);
        d[16] = 32;
        d[17] = 0x28;
        var s = img.Rgba;
        for (int i = 0, o = 18; i < img.Width * img.Height * 4; i += 4, o += 4)
        {
            d[o] = s[i + 2];
            d[o + 1] = s[i + 1];
            d[o + 2] = s[i];
            d[o + 3] = s[i + 3];
        }
        return d;
    }
}
