namespace P3DForge.Core.Paa;

/// <summary>PAA pixel formats the writer can produce.</summary>
public enum PaaFormat
{
    Dxt1,
    Dxt5,
    Argb8888,
    Argb4444,
    Argb1555,
    Ai88,
}

public static class PaaFormats
{
    public static ushort TypeTag(PaaFormat f) => f switch
    {
        PaaFormat.Dxt1 => 0xFF01,
        PaaFormat.Dxt5 => 0xFF05,
        PaaFormat.Argb8888 => 0x8888,
        PaaFormat.Argb4444 => 0x4444,
        PaaFormat.Argb1555 => 0x1555,
        PaaFormat.Ai88 => 0x8080,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static bool IsDxt(PaaFormat f) => f is PaaFormat.Dxt1 or PaaFormat.Dxt5;

    public static string Name(PaaFormat f) => f switch
    {
        PaaFormat.Dxt1 => "DXT1",
        PaaFormat.Dxt5 => "DXT5",
        PaaFormat.Argb8888 => "ARGB8888",
        PaaFormat.Argb4444 => "ARGB4444",
        PaaFormat.Argb1555 => "ARGB1555",
        _ => "AI88",
    };

    /// <summary>Writable format closest to a decoder format name (<see cref="PaaHeader.Format"/>: DXT1..5, RGBA4444, RGBA5551, RGBA8888, AI88).</summary>
    public static PaaFormat FromHeaderFormat(string format) => format switch
    {
        "DXT1" => PaaFormat.Dxt1,
        "DXT2" or "DXT3" or "DXT4" or "DXT5" => PaaFormat.Dxt5,
        "RGBA4444" => PaaFormat.Argb4444,
        "RGBA5551" => PaaFormat.Argb1555,
        "RGBA8888" => PaaFormat.Argb8888,
        "AI88" => PaaFormat.Ai88,
        _ => PaaFormat.Dxt5,
    };

    /// <summary>Parses "DXT1", "dxt5", "ARGB4444", "AI88", … (null when unknown or "auto").</summary>
    public static PaaFormat? Parse(string? s)
    {
        foreach (var f in Enum.GetValues<PaaFormat>())
            if (string.Equals(Name(f), s?.Trim(), StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }
}

/// <summary>Mipmap filter (TexConvert.cfg "mipmapFilter"); unsupported BI filters fall back to <see cref="Box"/>.</summary>
public enum MipFilter
{
    /// <summary>2×2 box filter (gamma-aware for sRGB colour maps, alpha-weighted colour when transparent).</summary>
    Box,
    /// <summary>Box filter, then the RGB normal is renormalised (NormalizeNormalMap*).</summary>
    NormalizeNormal,
}

/// <summary>
/// Conversion rules for one file-name suffix, mirroring DayZ Tools' TexConvert.cfg (ImageToPAA): target format,
/// channel swizzle, colour space for mip filtering, plus display names of the SOURCE channels for the texture tools.
/// </summary>
/// <param name="Suffix">e.g. "_co" ("" = no recognised suffix).</param>
/// <param name="Kind">Human readable kind, e.g. "Normal map (high quality)".</param>
/// <param name="Format">Fixed format, or null = DXT1 when opaque / DXT5 with alpha.</param>
/// <param name="Srgb">Colour data in sRGB space (gamma-aware mip filtering); false = linear data.</param>
/// <param name="Filter">Mipmap filter.</param>
/// <param name="Swizzle">Stored channel codes [A, R, G, B] (0..3 = source A,R,G,B; +4 inverted; 8 = one; 9 = zero) or null.</param>
/// <param name="Channels">Meaning of the source R, G, B, A channels.</param>
public sealed record TextureHint(string Suffix, string Kind, PaaFormat? Format, bool Srgb, MipFilter Filter, byte[]? Swizzle, string[] Channels)
{
    public const byte SwA = 0, SwR = 1, SwG = 2, SwB = 3, SwInv = 4, SwOne = 8, SwZero = 9;

    /// <summary>Format for an image with/without alpha (null <see cref="Format"/> = DXT1 opaque, DXT5 alpha).</summary>
    public PaaFormat ResolveFormat(bool sourceHasAlpha, PaaFormat? requested = null) =>
        requested ?? Format ?? (sourceHasAlpha ? PaaFormat.Dxt5 : PaaFormat.Dxt1);

    /// <summary>True when the source alpha channel ends up in the stored texture.</summary>
    public bool StoresSourceAlpha => Swizzle is null || Array.IndexOf(Swizzle, SwA) >= 0 || Array.IndexOf(Swizzle, (byte)(SwA | SwInv)) >= 0;

    public string Describe(bool sourceHasAlpha) =>
        $"{Kind} → {PaaFormats.Name(ResolveFormat(sourceHasAlpha))}{(Swizzle is null ? "" : " (swizzled)")}{(Srgb ? ", sRGB mips" : ", linear mips")}";
}

/// <summary>Suffix → <see cref="TextureHint"/> table (DayZ Tools TexConvert.cfg, convertVersion 6).</summary>
public static class TextureHints
{
    static readonly string[] Rgba = ["Red", "Green", "Blue", "Alpha"];
    static readonly string[] Normal = ["Normal X", "Normal Y", "Normal Z", "(unused)"];

    static byte[] Sw(byte a, byte r, byte g, byte b) => [a, r, g, b];

    const byte A = TextureHint.SwA, R = TextureHint.SwR, G = TextureHint.SwG, B = TextureHint.SwB, Inv = TextureHint.SwInv, One = TextureHint.SwOne, Zero = TextureHint.SwZero;

    /// <summary>Hint for files without a recognised suffix: colour map, DXT1/DXT5 by alpha.</summary>
    public static readonly TextureHint Default = new("", "Texture", null, true, MipFilter.Box, null, Rgba);

    static readonly TextureHint[] All =
    [
        new("_co", "Colour map", null, true, MipFilter.Box, null, Rgba),
        new("_ca", "Colour map with alpha", null, true, MipFilter.Box, null, Rgba),
        new("_cat", "Colour map, alpha test", PaaFormat.Dxt5, true, MipFilter.Box, null, Rgba),
        new("_can", "Colour map, alpha noise", PaaFormat.Dxt5, true, MipFilter.Box, null, Rgba),
        new("_raw", "Raw colour", PaaFormat.Argb1555, false, MipFilter.Box, null, Rgba),
        new("_8888", "Uncompressed colour", PaaFormat.Argb8888, true, MipFilter.Box, null, Rgba),
        new("_4444", "ARGB4444", PaaFormat.Argb4444, false, MipFilter.Box, null, Rgba),
        new("_1555", "ARGB1555", PaaFormat.Argb1555, false, MipFilter.Box, null, Rgba),
        new("_88", "Grey + alpha", PaaFormat.Ai88, false, MipFilter.Box, null, ["Grey", "Grey", "Grey", "Alpha"]),
        new("_gs", "Grey + alpha", PaaFormat.Ai88, false, MipFilter.Box, null, ["Grey", "Grey", "Grey", "Alpha"]),
        new("_dxt1", "DXT1", PaaFormat.Dxt1, false, MipFilter.Box, null, Rgba),
        new("_dxt5", "DXT5", PaaFormat.Dxt5, false, MipFilter.Box, null, Rgba),
        new("_sky", "Sky", PaaFormat.Dxt5, false, MipFilter.Box, Sw(G | Inv, R, A | Inv, B), Rgba),
        new("_detail", "Detail map (grey in alpha)", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, A, A, A), ["(unused)", "(unused)", "(unused)", "Detail"]),
        new("_dt", "Detail map (grey in alpha)", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, A, A, A), ["(unused)", "(unused)", "(unused)", "Detail"]),
        new("_cdt", "Colour detail", PaaFormat.Dxt1, false, MipFilter.Box, null, Rgba),
        new("_lco", "Layer colour", PaaFormat.Dxt1, false, MipFilter.Box, null, Rgba),
        new("_mco", "Multiply colour", PaaFormat.Dxt1, false, MipFilter.Box, null, Rgba),
        new("_draftlco", "Layer colour draft", null, false, MipFilter.Box, null, Rgba),
        new("_lca", "Layer colour + alpha", PaaFormat.Dxt5, false, MipFilter.Box, null, Rgba),
        new("_mask", "Surface mask", PaaFormat.Dxt1, false, MipFilter.Box, null, Rgba),
        new("_pr", "Ambient occlusion (PRT)", PaaFormat.Dxt5, false, MipFilter.Box, null, Rgba),
        new("_ads", "Ambient/diffuse shadow", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, One, G, B), ["(unused)", "Ambient", "Diffuse", "(unused)"]),
        new("_adshq", "Ambient/diffuse shadow HQ", PaaFormat.Dxt5, false, MipFilter.Box, Sw(G, Zero, B, Zero), ["(unused)", "Ambient", "Diffuse", "(unused)"]),
        new("_dtsmdi", "Detail specular", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, R, G, B), Rgba),
        new("_mc", "Macro map", PaaFormat.Dxt5, false, MipFilter.Box, null, Rgba),
        new("_as", "Ambient shadow", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, One, G, One), ["(unused)", "Ambient shadow", "(unused)", "(unused)"]),
        new("_sm", "Specular map", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, R, G, B), Rgba),
        new("_smdi", "Specular (SMDI)", PaaFormat.Dxt1, false, MipFilter.Box, Sw(One, One, G, B), ["(unused)", "Specular", "Glossiness", "(unused)"]),
        new("_normalmap", "Normal map", null, false, MipFilter.NormalizeNormal, null, Normal),
        new("_no", "Normal map (DXT1)", PaaFormat.Dxt1, false, MipFilter.NormalizeNormal, Sw(One, R, G, B), Normal),
        new("_noex", "Normal map", null, false, MipFilter.NormalizeNormal, null, Normal),
        new("_non", "Normal map, noise", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, null, Normal),
        new("_nohq", "Normal map (high quality)", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, Sw(R | Inv, A | Inv, G, B), Normal),
        new("_novhq", "Normal map (very high quality)", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, Sw(R | Inv, One, G, One), Normal),
        new("_nofhq", "Normal map, fade (HQ)", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, Sw(R | Inv, A | Inv, G, B), Normal),
        new("_nof", "Normal map, fade", null, false, MipFilter.NormalizeNormal, null, Normal),
        new("_nofex", "Normal map, fade", null, false, MipFilter.NormalizeNormal, null, Normal),
        new("_ns", "Normal + specular", null, false, MipFilter.NormalizeNormal, null, ["Normal X", "Normal Y", "Normal Z", "Specular"]),
        new("_nsex", "Normal + specular", null, false, MipFilter.NormalizeNormal, null, ["Normal X", "Normal Y", "Normal Z", "Specular"]),
        new("_nshq", "Normal + specular (HQ)", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, Sw(R | Inv, A | Inv, G, B), Normal),
        new("_nopx", "Parallax normal map", PaaFormat.Dxt5, false, MipFilter.NormalizeNormal, Sw(R | Inv, A, G, One), ["Normal X", "Normal Y", "Normal Z", "Height"]),
    ];

    static readonly Dictionary<string, TextureHint> BySuffix = All.ToDictionary(h => h.Suffix, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TextureHint> Known => All;

    /// <summary>The suffix of a texture path ("x_nohq.png" → "_nohq"), or "" when the name has none.</summary>
    public static string SuffixOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        int i = name.LastIndexOf('_');
        return i < 0 ? "" : name[i..].ToLowerInvariant();
    }

    /// <summary>Rule for a file name / path by its suffix (TexConvert "*_suffix.*"), or <see cref="Default"/>.</summary>
    public static TextureHint ForPath(string path) => BySuffix.TryGetValue(SuffixOf(path), out var h) ? h : Default;
}
