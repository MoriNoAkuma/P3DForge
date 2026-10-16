using System.Buffers.Binary;

namespace P3DForge.Core.Paa;

/// <summary>One entry of the PAA mip table (file order = largest first).</summary>
/// <param name="Width">Width in pixels (the LZO flag bit 0x8000 of DXT mips is removed).</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Offset">Byte offset of the stored pixel data (just past the 7-byte mip header).</param>
/// <param name="StoredSize">Size of the stored (possibly compressed) data.</param>
/// <param name="Compression">"none", "lzo" (DXT only) or "lzss" (non-DXT only).</param>
public sealed record PaaMip(int Width, int Height, int Offset, int StoredSize, string Compression);

/// <summary>Everything readable from a PAA file without decompressing pixel data.</summary>
/// <param name="TypeTag">Raw 16-bit type tag, e.g. 0xFF05 for DXT5.</param>
/// <param name="Format">DXT1..DXT5, RGBA4444, RGBA5551, RGBA8888 or AI88.</param>
/// <param name="Mips">Mip table, largest first.</param>
/// <param name="HasAlpha">True when the "GGATGALF" flags declare transparency.</param>
/// <param name="AvgColor">"GGATCGVA" average colour as 0xAARRGGBB, or null.</param>
/// <param name="MaxColor">"GGATCXAM" maximum colour as 0xAARRGGBB, or null.</param>
public sealed record PaaHeader(ushort TypeTag, string Format, IReadOnlyList<PaaMip> Mips, bool HasAlpha, uint? AvgColor, uint? MaxColor)
{
    /// <summary>"GGATGALF" alpha flags (1 = interpolated alpha, 2 = 1-bit alpha), or null.</summary>
    public uint? Flags { get; init; }
    /// <summary>"GGATZIWS" channel swizzle for the stored [r,g,b,a] channels, or null (see <see cref="Swizzle"/>).</summary>
    public byte[]? Swizzle { get; init; }
}

/// <summary>A decoded image: RGBA8, rows top-first (matches the DirectX UV convention of P3D).</summary>
public sealed record PaaImage(int Width, int Height, byte[] Rgba, bool HasAlpha, string Format, int MipIndex);

/// <summary>
/// Bohemia PAA texture decoding. Layout: uint16 typeTag; TAGG* ("GGAT" + reversed 4-char name + uint32 size + data);
/// uint16 paletteEntries + 3 bytes each; mips (uint16 width, uint16 height, uint24 size, data) until a 0/0 terminator.
/// Ported from the visually verified TypeScript decoder (legacy-ts/src/paa).
/// </summary>
public static class Paa
{
    const ushort LzoWidthFlag = 0x8000;
    const int TaggHeaderSize = 12;
    const int MipHeaderSize = 7;
    const int MaxLzoRatio = 256, MaxLzssRatio = 9, RatioSlack = 1024;

    static string? FormatName(ushort tag) => tag switch
    {
        0xFF01 => "DXT1",
        0xFF02 => "DXT2",
        0xFF03 => "DXT3",
        0xFF04 => "DXT4",
        0xFF05 => "DXT5",
        0x4444 => "RGBA4444",
        0x1555 => "RGBA5551",
        0x8888 => "RGBA8888",
        0x8080 => "AI88",
        _ => null,
    };

    public static bool IsDxt(ushort tag) => tag is >= 0xFF01 and <= 0xFF05;

    /// <summary>Uncompressed size in bytes of one mip level.</summary>
    public static int RawMipSize(ushort tag, int width, int height) =>
        IsDxt(tag) ? Dxt.DataSize(tag == 0xFF01 ? 8 : 16, width, height) : width * height * (tag == 0x8888 ? 4 : 2);

    /// <summary>Parses header, TAGGs, palette and mip table without decompressing anything.</summary>
    /// <exception cref="InvalidDataException">Not a PAA file, or truncated/corrupt structure.</exception>
    public static PaaHeader ReadHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) throw new InvalidDataException("Not a PAA file: too small");
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d);
        string format = FormatName(tag) ?? throw new InvalidDataException($"Not a PAA file: unknown type tag 0x{tag:x4}");

        uint? avg = null, max = null, flags = null;
        byte[]? swizzle = null;
        int pos = 2;
        while (pos + TaggHeaderSize <= d.Length && d.Slice(pos, 4).SequenceEqual("GGAT"u8))
        {
            var name = d.Slice(pos + 4, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 8)..]);
            int data = pos + TaggHeaderSize;
            if (size < 0 || data + size > d.Length) throw new InvalidDataException($"Truncated PAA: TAGG needs {size} bytes");
            if (size >= 4)
            {
                if (name.SequenceEqual("CGVA"u8)) avg = Bgra(d, data);
                else if (name.SequenceEqual("CXAM"u8)) max = Bgra(d, data);
                else if (name.SequenceEqual("GALF"u8)) flags = BinaryPrimitives.ReadUInt32LittleEndian(d[data..]);
                else if (name.SequenceEqual("ZIWS"u8)) swizzle = [d[data + 1], d[data + 2], d[data + 3], d[data]]; // stored A,R,G,B
            }
            pos = data + size;
        }

        if (pos + 2 > d.Length) throw new InvalidDataException("Truncated PAA: missing palette");
        pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]) * 3;

        bool dxt = IsDxt(tag);
        var mips = new List<PaaMip>();
        while (true)
        {
            if (pos + 4 > d.Length) throw new InvalidDataException("Truncated PAA: missing mip table terminator");
            int storedWidth = BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 2)..]);
            if (storedWidth == 0 && height == 0) break;
            if (pos + MipHeaderSize > d.Length) throw new InvalidDataException($"Truncated PAA: mip {mips.Count} header");
            int storedSize = d[pos + 4] | (d[pos + 5] << 8) | (d[pos + 6] << 16);
            int offset = pos + MipHeaderSize;
            if (offset + storedSize > d.Length) throw new InvalidDataException($"Truncated PAA: mip {mips.Count} needs {storedSize} bytes");
            bool lzo = dxt && (storedWidth & LzoWidthFlag) != 0;
            int width = dxt ? storedWidth & ~LzoWidthFlag : storedWidth;
            if (width == 0 || height == 0) throw new InvalidDataException($"Corrupt PAA: mip {mips.Count} is {width}x{height}");
            string comp = lzo ? "lzo" : dxt || storedSize == RawMipSize(tag, width, height) ? "none" : "lzss";
            mips.Add(new PaaMip(width, height, offset, storedSize, comp));
            pos = offset + storedSize;
        }
        if (mips.Count == 0) throw new InvalidDataException("PAA has no mip levels");
        return new PaaHeader(tag, format, mips, flags is { } f && (f & 3) != 0, avg, max) { Flags = flags, Swizzle = swizzle };
    }

    static uint Bgra(ReadOnlySpan<byte> d, int pos) => (uint)(d[pos] | (d[pos + 1] << 8) | (d[pos + 2] << 16) | (d[pos + 3] << 24));

    /// <summary>Decompresses and decodes one mip to RGBA8 (rows top-first); a "GGATZIWS" swizzle is undone.</summary>
    public static PaaImage DecodeMip(ReadOnlySpan<byte> data, PaaHeader header, int mipIndex)
    {
        if ((uint)mipIndex >= (uint)header.Mips.Count) throw new ArgumentOutOfRangeException(nameof(mipIndex));
        var mip = header.Mips[mipIndex];
        int size = RawMipSize(header.TypeTag, mip.Width, mip.Height);
        ReadOnlySpan<byte> raw;
        byte[]? buffer = null;
        if (mip.Compression == "none")
        {
            if (mip.StoredSize < size) throw new InvalidDataException($"Corrupt PAA: {mip.Width}x{mip.Height} mip has {mip.StoredSize} of {size} bytes");
            raw = data.Slice(mip.Offset, size);
        }
        else
        {
            int ratio = mip.Compression == "lzo" ? MaxLzoRatio : MaxLzssRatio;
            if (size > (long)mip.StoredSize * ratio + RatioSlack) throw new InvalidDataException($"Corrupt PAA: {mip.Width}x{mip.Height} mip cannot decompress from {mip.StoredSize} bytes");
            buffer = new byte[size];
            var src = data.Slice(mip.Offset, mip.StoredSize);
            if (mip.Compression == "lzo") Lzo.Decompress(src, buffer);
            else Lzss.Decompress(src, buffer);
            raw = buffer;
        }
        var rgba = new byte[mip.Width * mip.Height * 4];
        int count = mip.Width * mip.Height;
        switch (header.TypeTag)
        {
            case 0xFF01: Dxt.DecodeDxt1(raw, mip.Width, mip.Height, rgba); break;
            case 0xFF02 or 0xFF03: Dxt.DecodeDxt3(raw, mip.Width, mip.Height, rgba); break;
            case 0xFF04 or 0xFF05: Dxt.DecodeDxt5(raw, mip.Width, mip.Height, rgba); break;
            case 0x4444: Pixels.Argb4444(raw, count, rgba); break;
            case 0x1555: Pixels.Argb1555(raw, count, rgba); break;
            case 0x8888: Pixels.Argb8888(raw, count, rgba); break;
            case 0x8080: Pixels.Ai88(raw, count, rgba); break;
            default: throw new InvalidDataException($"Unsupported PAA type tag 0x{header.TypeTag:x4}");
        }
        if (header.Swizzle is { } sw) Swizzle.ApplyInverse(rgba, sw);
        return new PaaImage(mip.Width, mip.Height, rgba, header.HasAlpha, header.Format, mipIndex);
    }

    /// <summary>
    /// Decodes the largest mip with max(w,h) &lt;= <paramref name="maxSize"/> (or the smallest one when none fits);
    /// only that mip is decompressed.
    /// </summary>
    public static PaaImage DecodeRgba(ReadOnlySpan<byte> data, int maxSize = 1024)
    {
        var header = ReadHeader(data);
        int index = PickMip(header.Mips, maxSize);
        var img = DecodeMip(data, header, index);
        var rgba = img.Rgba;
        bool transparent = false;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255)
            {
                transparent = true;
                break;
            }
        // Without alpha flags the engine renders the texture opaque (e.g. DXT1 3-colour blocks in an opaque map).
        bool hasAlpha = transparent && (header.Flags is null || header.HasAlpha);
        if (transparent && !hasAlpha)
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        return img with { HasAlpha = hasAlpha };
    }

    /// <summary>Index of the largest mip with max(w,h) &lt;= maxSize, or of the smallest mip when none fits.</summary>
    public static int PickMip(IReadOnlyList<PaaMip> mips, int maxSize)
    {
        int best = -1, bestEdge = -1, smallest = 0, smallestEdge = int.MaxValue;
        for (int i = 0; i < mips.Count; i++)
        {
            int edge = Math.Max(mips[i].Width, mips[i].Height);
            if (edge <= maxSize && edge > bestEdge)
            {
                best = i;
                bestEdge = edge;
            }
            if (edge < smallestEdge)
            {
                smallest = i;
                smallestEdge = edge;
            }
        }
        return best >= 0 ? best : smallest;
    }
}
