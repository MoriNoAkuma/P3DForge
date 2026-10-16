namespace P3DForge.Core.Materials;

/// <summary>Starting points for new materials.</summary>
public static class RvmatTemplates
{
    /// <summary>
    /// DayZ "Super" material (7 stages like the vanilla files). Pass existing texture paths for the normal map,
    /// ambient shadow and specular maps; null uses neutral procedural textures.
    /// </summary>
    public static Rvmat Super(string? nohq = null, string? smdi = null, string? ambientShadow = null)
    {
        var m = new Rvmat
        {
            Ambient = Rgba.White,
            Diffuse = Rgba.White,
            ForcedDiffuse = Rgba.Zero,
            Emmisive = Rgba.Black,
            Specular = new Rgba(0.2f, 0.2f, 0.2f, 1),
            SpecularPower = 50,
            PixelShaderID = "Super",
            VertexShaderID = "Super",
        };
        m.AddStage(1, nohq ?? "#(argb,8,8,3)color(0.5,0.5,1,1,NOHQ)");
        m.AddStage(2, "#(argb,8,8,3)color(0.5,0.5,0.5,0.5,DT)");
        m.AddStage(3, "#(argb,8,8,3)color(0,0,0,0,MC)");
        m.AddStage(4, ambientShadow ?? "#(argb,8,8,3)color(1,1,1,1,AS)");
        m.AddStage(5, smdi ?? "#(argb,8,8,3)color(1,0.3,0.5,1,SMDI)");
        var fresnel = m.AddStage(6, "#(ai,64,64,1)fresnel(1.5,0.2)");
        fresnel.UvSource = "none";
        fresnel.UvTransform = null;
        var env = m.AddStage(7, @"dz\data\data\env_land_co.paa");
        env.UvSource = "none";
        env.UvTransform = null;
        return m;
    }

    /// <summary>Texture name without its suffix and extension: "dz\x\body_co.paa" → "dz\x\body" (null for procedural/empty).</summary>
    public static string? BaseName(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture) || ProceduralTexture.IsProcedural(texture)) return null;
        var t = texture.Trim();
        var dot = t.LastIndexOf('.');
        var slash = t.LastIndexOfAny(['\\', '/']);
        if (dot > slash) t = t[..dot];
        var us = t.LastIndexOf('_');
        return us > slash + 1 ? t[..us] : t;
    }
}
