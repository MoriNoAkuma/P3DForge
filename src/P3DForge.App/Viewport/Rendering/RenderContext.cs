using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace P3DForge.App.Viewport.Rendering;

public enum Shading { Wire, Solid, Textured, Final }

/// <summary>The shader programs of one GL context, shared by every renderer drawing into it.</summary>
public sealed class GpuPrograms : IDisposable
{
    public const int UnitShadow = 7;

    public GlProgram Face { get; } = new(Shaders.FaceVs, Shaders.FaceFs) { Name = "face" };
    public GlProgram Final { get; } = new(Shaders.FaceVs, Shaders.FinalFs) { Name = "final" };
    public GlProgram Line { get; } = new(Shaders.LineVs, Shaders.LineFs) { Name = "line" };
    public GlProgram Depth { get; } = new(Shaders.DepthVs, Shaders.DepthFs) { Name = "depth" };

    public GpuPrograms()
    {
        Face.Use();
        Face.Set("uTex", 0);
        Face.Set("uShadowMap", UnitShadow);
        Final.Use();
        string[] slots = ["uTexCo", "uTexNo", "uTexDt", "uTexMc", "uTexAs", "uTexSm", "uTexMask"];
        for (int i = 0; i < slots.Length; i++) Final.Set(slots[i], i);
        Final.Set("uShadowMap", UnitShadow);
        Depth.Use();
        Depth.Set("uTex", 0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        Face.Dispose();
        Final.Dispose();
        Line.Dispose();
        Depth.Dispose();
    }
}

/// <summary>Light rig values of one frame (RENDER space), uploaded to every lit program.</summary>
public sealed record LightParams(
    Vector3 SunDir,
    Vector3 SunColor,
    Vector3 Sky,
    Vector3 Ground,
    float Fill,
    float Specular)
{
    public static readonly LightParams Headlight = new(Vector3.UnitY, Vector3.Zero, new(0.40f), new(0.22f), 0.68f, 0f);

    public void Apply(GlProgram p)
    {
        p.Set("uSunDir", SunDir);
        p.Set("uSunColor", SunColor);
        p.Set("uSkyColor", Sky);
        p.Set("uGroundColor", Ground);
        p.Set("uFill", Fill);
        p.Set("uSpecular", Specular);
    }
}

/// <summary>Shadow map binding of one frame (texture handle 0 = no shadows).</summary>
public readonly record struct ShadowParams(int Texture, Matrix4x4 LightViewProj, int Size, float Softness, float Bias, float Strength)
{
    public bool Enabled => Texture != 0;

    public void Apply(GlProgram p)
    {
        p.Set("uShadows", Enabled ? 1 : 0);
        if (!Enabled) return;
        GL.ActiveTexture(TextureUnit.Texture0 + GpuPrograms.UnitShadow);
        GL.BindTexture(TextureTarget.Texture2D, Texture);
        GL.ActiveTexture(TextureUnit.Texture0);
        p.Set("uLightViewProj", LightViewProj);
        p.Set("uShadowSoft", Softness);
        p.Set("uShadowTexel", new Vector2(1f / Math.Max(1, Size)));
        p.Set("uShadowBias", Bias);
        p.Set("uShadowStrength", Strength);
    }
}

/// <summary>Final-mode switches (flags view.final.*).</summary>
public sealed record FinalParams(
    int Toggles,
    int Stage,
    float Exposure,
    bool FlipGreen,
    int BackFaces) // 0 auto (cull opaque), 1 show, 2 cull
{
    public const int Diffuse = 1, Specular = 2, Ambient = 4, Env = 8, Emissive = 16, Shadows = 32;
    public static readonly FinalParams Default = new(Diffuse | Specular | Ambient | Env | Emissive | Shadows, 0, 1f, false, 0);
}

/// <summary>Counters of one frame (stats overlay).</summary>
public sealed class FrameStats
{
    public int DrawCalls;
    public long Triangles;
    public int Instances;

    public void Reset()
    {
        DrawCalls = 0;
        Triangles = 0;
        Instances = 0;
    }

    public void Add(int vertices)
    {
        DrawCalls++;
        Triangles += vertices / 3;
    }
}

/// <summary>Everything a renderer needs to draw one pane.</summary>
public sealed class DrawContext
{
    public required GpuPrograms Programs { get; init; }
    public required TextureCache Textures { get; init; }
    public required MaterialLibrary Materials { get; init; }
    public Matrix4x4 ViewProj { get; set; }
    public Vector3 Eye { get; set; }
    public ModelRenderer.Options Options { get; set; } = new(Shading.Textured, false, false, true, 0.28f);
    public LightParams EditLight { get; set; } = LightParams.Headlight;
    public LightParams FinalLight { get; set; } = LightParams.Headlight;
    public ShadowParams Shadow { get; set; }
    public FinalParams Final { get; set; } = FinalParams.Default;
    /// <summary>
    /// Final mode only: -1 = draw opaque/alpha-tested and blended ranges, 0 = only opaque/alpha-tested, 1 = only blended.
    /// The pane draws pass 0 for the model and every proxy, then pass 1, so glass blends over proxies behind it.
    /// </summary>
    public int BlendPass { get; set; } = -1;
    public FrameStats Stats { get; } = new();
}
