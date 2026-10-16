namespace P3DForge.Core.Imaging;

/// <summary>
/// An 8-bit RGBA image, rows top-first (the layout of <see cref="Paa.PaaImage.Rgba"/>). The pixel array is owned by
/// the record; operations in this namespace return new images and never write into their input.
/// </summary>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba)
{
    public static RgbaImage Create(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"invalid image size {width}x{height}");
        return new RgbaImage(width, height, new byte[width * height * 4]);
    }

    /// <summary>Wraps decoded PAA pixels (no copy).</summary>
    public static RgbaImage From(Paa.PaaImage image) => new(image.Width, image.Height, image.Rgba);

    public int PixelCount => Width * Height;

    /// <summary>True when every alpha byte is 255.</summary>
    public bool IsOpaque()
    {
        var p = Rgba;
        for (int i = 3; i < p.Length; i += 4)
            if (p[i] != 255) return false;
        return true;
    }

    public RgbaImage Copy() => this with { Rgba = (byte[])Rgba.Clone() };

    public bool HasPowerOfTwoSize => IsPowerOfTwo(Width) && IsPowerOfTwo(Height);

    public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>Nearest power of two (ties round up), clamped to [1, max].</summary>
    public static int NearestPowerOfTwo(int v, int max = 8192)
    {
        if (v <= 1) return 1;
        int up = 1;
        while (up < v) up <<= 1;
        int down = up >> 1;
        int r = v - down < up - v ? down : up;
        return Math.Min(r, max);
    }
}
