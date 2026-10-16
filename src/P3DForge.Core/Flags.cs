namespace P3DForge.Core;

/// <summary>Point flag bits (docs/FORMAT.md).</summary>
public static class PointFlags
{
    public const uint LandMask = 0x0000000F;
    public const uint OnLand = 0x1;
    public const uint UnderLand = 0x2;
    public const uint AboveLand = 0x4;
    public const uint KeepLand = 0x8;
    public const uint LightMask = 0x000000F0;
    public const uint NoLight = 0x10;
    public const uint Ambient = 0x20;
    public const uint FullLight = 0x40;
    public const uint HalfLight = 0x80;
    public const uint DecalMask = 0x00000300;
    public const uint Decal = 0x100;
    public const uint VDecal = 0x200;
    public const uint FogMask = 0x00003000;
    public const uint NoFog = 0x1000;
    public const uint SkyFog = 0x2000;
    public const uint UserMask = 0x00FF0000;
    public const int UserShift = 16;
    public const uint SpecialMask = 0x0F000000;
    public const uint Hidden = 0x01000000;
    public const uint Locked = 0x02000000;

    public static uint UserValue(uint flags) => (flags & UserMask) >> UserShift;
}

/// <summary>Face flag bits (docs/FORMAT.md).</summary>
public static class FaceFlags
{
    public const uint NoLight = 0x1;
    public const uint Ambient = 0x2;
    public const uint FullLight = 0x4;
    public const uint BothSidesLight = 0x20;
    public const uint SkyLight = 0x80;
    public const uint ReverseLight = 0x100000;
    public const uint FlatLight = 0x200000;
    public const uint LightMask = 0x003000A7;
    public const uint IsShadow = 0x8;
    public const uint NoShadow = 0x10;
    public const uint ShadowMask = 0x18;
    public const uint ZBiasMask = 0x300;
    public const uint ZBiasStep = 0x100;
    public const uint DisableTexMerge = 0x01000000;
    public const uint UserMask = 0xFE000000;
    public const int UserShift = 25;

    public static uint ZBias(uint flags) => (flags & ZBiasMask) >> 8;
    public static uint UserValue(uint flags) => (flags & UserMask) >> UserShift;
}

public static class Bits
{
    /// <summary>Replaces the bits of <paramref name="mask"/> in <paramref name="flags"/> with <paramref name="value"/> (already shifted).</summary>
    public static uint Set(uint flags, uint mask, uint value) => (flags & ~mask) | (value & mask);
}

/// <summary>
/// Named-selection weight encoding: byte 0 = not selected, 1 = weight 1.0, other b = (256 - b) / 255.
/// </summary>
public static class Weights
{
    public static float Decode(byte b) => b switch
    {
        0 => 0f,
        1 => 1f,
        _ => (256 - b) / 255f,
    };

    /// <summary>Encodes a weight in [0, 1]; weights below 1/255 become 0 (unselected).</summary>
    public static byte Encode(float w)
    {
        if (!(w > 0)) return 0;
        if (w >= 1) return 1;
        int b = (int)MathF.Round(256 - 255 * w, MidpointRounding.AwayFromZero);
        if (b >= 256) return 0;
        return (byte)(b <= 1 ? 1 : b);
    }

    /// <summary>Byte → weight lookup table for hot loops.</summary>
    public static readonly float[] Table = Enumerable.Range(0, 256).Select(i => Decode((byte)i)).ToArray();
}
