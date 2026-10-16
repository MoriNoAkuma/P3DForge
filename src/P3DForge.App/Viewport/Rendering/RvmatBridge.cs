using System.Numerics;
using P3DForge.App.Editor;
using P3DForge.Core.Materials;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// Converts rvmat materials of <see cref="MaterialStore.Shared"/> (files + unsaved editor overrides) into
/// <see cref="GpuMaterial"/>s for the Final shader: stage textures by role (Super family by slot, other shaders by
/// texture suffix), effective uvTransform / uvSource per stage, material colours, fresnel(n, k), environment stage,
/// alpha from the render flags / pixel shader.
/// </summary>
public static class RvmatBridge
{
    static readonly HashSet<string> logged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Material for a face material reference; a default material (logged once) when missing or broken.</summary>
    public static GpuMaterial? Load(string path)
    {
        Rvmat? m;
        string? error;
        try
        {
            MaterialStore.Shared.TryGet(path, out m, out error);
        }
        catch (Exception ex)
        {
            m = null;
            error = ex.Message;
        }
        if (m is null)
        {
            if (logged.Add(path)) AppServices.Editor.Log($"Material {path}: {error ?? "not found"} — drawn with the face texture only.", LogLevel.Warn);
            return new GpuMaterial { Path = path, Error = error ?? "not found" };
        }
        return Convert(path, m);
    }

    public static GpuMaterial Convert(string path, Rvmat m)
    {
        var tex = new string?[GpuMaterial.Slots];
        var uu = new Vector3[GpuMaterial.Slots];
        var vv = new Vector3[GpuMaterial.Slots];
        var src = new int[GpuMaterial.Slots];
        for (int i = 0; i < GpuMaterial.Slots; i++)
        {
            uu[i] = new Vector3(1, 0, 0);
            vv[i] = new Vector3(0, 1, 0);
        }
        void Slot(int slot, RvmatStage? st)
        {
            if (st is null || string.IsNullOrWhiteSpace(st.Texture)) return;
            tex[slot] = st.Texture;
            var t = st.EffectiveUvTransform;
            uu[slot] = new Vector3(t.Aside.X, t.Up.X, t.Pos.X);
            vv[slot] = new Vector3(t.Aside.Y, t.Up.Y, t.Pos.Y);
            src[slot] = st.EffectiveUvSet == 1 ? 1 : 0;
        }
        Slot(GpuMaterial.SlotCo, m.FindStage(StageRole.Diffuse));
        Slot(GpuMaterial.SlotNo, m.FindStage(StageRole.Normal));
        Slot(GpuMaterial.SlotDt, m.FindStage(StageRole.Detail));
        Slot(GpuMaterial.SlotMc, m.FindStage(StageRole.Macro));
        Slot(GpuMaterial.SlotAs, m.FindStage(StageRole.AmbientShadow));
        Slot(GpuMaterial.SlotSm, m.FindStage(StageRole.Specular));
        Slot(GpuMaterial.SlotMask, m.FindStageBySuffix("_mask"));
        // Stage 0 of a Super material is normally empty or the same texture as the face: the face texture wins.

        float f0 = -1;
        var fs = m.FindStage(StageRole.Fresnel);
        if (fs is not null && ProceduralTexture.TryParseFresnel(fs.Texture, out float n, out float k)) f0 = RenderMath.FresnelF0(n, k);
        float env = m.FindStage(StageRole.Environment) is not null ? 1f : 0f;
        var shader = m.PixelShaderID ?? "";
        bool glass = shader.StartsWith("Glass", StringComparison.OrdinalIgnoreCase);
        if (glass && env == 0) env = 1;
        if (glass && f0 < 0) f0 = 0.04f;

        int alpha = -1;
        float aref = 0.5f;
        if (m.HasRenderFlag("AlphaTest128")) (alpha, aref) = (GpuMaterial.AlphaTest, 128 / 255f);
        else if (m.HasRenderFlag("AlphaTest64")) (alpha, aref) = (GpuMaterial.AlphaTest, 64 / 255f);
        else if (m.HasRenderFlag("AlphaTest32")) (alpha, aref) = (GpuMaterial.AlphaTest, 32 / 255f);
        else if (glass || m.HasRenderFlag("AddBlend") || m.HasRenderFlag("NoZWrite")) alpha = GpuMaterial.AlphaBlend;
        bool twoSided = shader.StartsWith("Tree", StringComparison.OrdinalIgnoreCase) || shader.StartsWith("Grass", StringComparison.OrdinalIgnoreCase)
            || shader.Equals("SuperHair", StringComparison.OrdinalIgnoreCase);

        static Vector3 Rgb(Rgba c) => new(c.R, c.G, c.B);
        return new GpuMaterial
        {
            Path = path,
            Textures = tex,
            UvU = uu,
            UvV = vv,
            UvSource = src,
            Ambient = Rgb(m.Ambient),
            Diffuse = Rgb(m.Diffuse),
            Forced = Rgb(m.ForcedDiffuse),
            Emissive = Rgb(m.Emmisive),
            Specular = Rgb(m.Specular),
            SpecularPower = m.SpecularPower > 0 ? m.SpecularPower : 1,
            F0 = f0,
            Env = env,
            Alpha = alpha,
            AlphaRef = aref,
            TwoSided = twoSided,
            CutsAlpha = twoSided,
        };
    }
}
