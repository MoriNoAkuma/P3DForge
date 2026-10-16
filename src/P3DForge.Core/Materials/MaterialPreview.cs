using System.Numerics;
using PaaCodec = P3DForge.Core.Paa.Paa;

namespace P3DForge.Core.Materials;

/// <summary>
/// Preview sphere of a material (library thumbnails): picks the base colour (_co / _ca stage, else the texture next to
/// the _nohq / _smdi named like the Super shader's face texture, else the diffuse colour), _nohq and _smdi, loads them
/// through a caller-supplied loader (so the caller can cache), and renders with <see cref="SpherePreview"/>.
/// Thread-safe (pure); run it on a worker thread.
/// </summary>
public static class MaterialPreview
{
    /// <summary>Texture names whose files hold the material's look (diffuse, normal, specular); entries may be null.</summary>
    public static (string? Diffuse, string? Normal, string? Smdi) Textures(Rvmat m, Func<string, bool>? exists = null)
    {
        var diffuse = m.FindStage(StageRole.Diffuse)?.Texture;
        var normal = m.FindStage(StageRole.Normal)?.Texture;
        var smdi = m.FindStageBySuffix("_smdi")?.Texture ?? m.FindStage(StageRole.Specular)?.Texture;
        if (diffuse is null && exists is not null)
        {
            // Super / Multi take the colour map from the face texture: guess it from the sibling maps (x_nohq.paa → x_co.paa)
            foreach (var sibling in new[] { normal, smdi })
            {
                if (sibling is null || ProceduralTexture.IsProcedural(sibling)) continue;
                var guess = GuessColorMap(sibling, exists);
                if (guess is null) continue;
                diffuse = guess;
                break;
            }
        }
        return (diffuse, normal, smdi);
    }

    /// <summary>"dir\x_nohq.paa" → "dir\x_co.paa" (or _ca) when <paramref name="exists"/> says that file exists.</summary>
    public static string? GuessColorMap(string texture, Func<string, bool> exists)
    {
        var suffix = TextureNames.Suffix(texture);
        if (string.IsNullOrEmpty(suffix)) return null;
        var stem = Path.GetFileNameWithoutExtension(texture);
        var cut = stem.LastIndexOf('_');
        if (cut <= 0) return null;
        var dir = texture[..(texture.Length - Path.GetFileName(texture).Length)];
        foreach (var s in new[] { "_co", "_ca" })
        {
            var candidate = dir + stem[..cut] + s + ".paa";
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Sphere inputs for a material; <paramref name="load"/> returns a small decoded image for a texture
    /// name (null = missing).</summary>
    public static SphereInputs Inputs(Rvmat m, Func<string, PreviewImage?> load, Func<string, bool>? exists = null)
    {
        var input = SphereInputs.FromMaterial(m);
        var (d, n, s) = Textures(m, exists);
        if (d is not null) input.Diffuse = load(d);
        if (n is not null) input.Normal = load(n);
        if (s is not null) input.Smdi = load(s);
        // procedural diffuse = colour; Super's DT/MC stages are not colour maps
        var stage = m.FindStage(StageRole.Normal) ?? m.FindStage(StageRole.Diffuse);
        if (stage is not null)
        {
            var t = stage.EffectiveUvTransform;
            float su = new Vector2(t.Aside.X, t.Aside.Y).Length(), sv = new Vector2(t.Up.X, t.Up.Y).Length();
            if (su > 0.01f && sv > 0.01f) input.Tiling = new Vector2(Math.Clamp(su, 0.25f, 8f), Math.Clamp(sv, 0.25f, 8f));
        }
        return input;
    }

    /// <summary>BGRA32 preview sphere (size × size).</summary>
    public static byte[] Render(Rvmat m, int size, Func<string, PreviewImage?> load, Func<string, bool>? exists = null) =>
        SpherePreview.RenderBgra(size, Inputs(m, load, exists));

    /// <summary>Loads a texture name for previews: procedural colour → 1×1; a .paa (resolved by <paramref name="resolve"/>)
    /// → its largest mip not above <paramref name="maxSize"/>. Null when missing / unsupported / corrupt.</summary>
    public static PreviewImage? LoadTexture(string texture, Func<string, string?> resolve, int maxSize = 128)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        if (ProceduralTexture.TryParseColor(texture, out var c, out _)) return PreviewImage.Solid(c);
        if (ProceduralTexture.IsProcedural(texture)) return null;
        var full = resolve(texture);
        if (full is null) return null;
        if (!Path.GetExtension(full).Equals(".paa", StringComparison.OrdinalIgnoreCase))
        {
            var paa = Path.ChangeExtension(full, ".paa");
            if (File.Exists(paa)) full = paa;
            else return null;
        }
        try
        {
            if (!File.Exists(full)) return null;
            var img = PaaCodec.DecodeRgba(File.ReadAllBytes(full), maxSize);
            return new PreviewImage(img.Width, img.Height, img.Rgba);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
