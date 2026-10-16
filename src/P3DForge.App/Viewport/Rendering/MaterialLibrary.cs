using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>Render-side description of a face material (rvmat Super-shader stages reduced to what the Final shader uses).</summary>
public sealed class GpuMaterial
{
    public const int Slots = 7; // 0 co, 1 nohq, 2 dt, 3 mc, 4 as, 5 smdi, 6 mask
    public const int SlotCo = 0, SlotNo = 1, SlotDt = 2, SlotMc = 3, SlotAs = 4, SlotSm = 5, SlotMask = 6;
    public const int AlphaOpaque = 0, AlphaTest = 1, AlphaBlend = 2;

    public static readonly GpuMaterial Default = new() { Path = "" };

    public required string Path { get; init; }
    /// <summary>Texture per slot (null = none). Slot 0 = the stage-0 texture of the material; the face texture wins when set.</summary>
    public string?[] Textures { get; init; } = new string?[Slots];
    public Vector3[] UvU { get; init; } = Fill(new Vector3(1, 0, 0));
    public Vector3[] UvV { get; init; } = Fill(new Vector3(0, 1, 0));
    public int[] UvSource { get; init; } = new int[Slots];
    public Vector3 Ambient { get; init; } = Vector3.One;
    public Vector3 Diffuse { get; init; } = Vector3.One;
    public Vector3 Forced { get; init; } = Vector3.Zero;
    public Vector3 Emissive { get; init; } = Vector3.Zero;
    public Vector3 Specular { get; init; } = new(0.08f);
    public float SpecularPower { get; init; } = 40f;
    /// <summary>Fresnel F0 from a fresnel(n, k) stage; negative = none.</summary>
    public float F0 { get; init; } = -1f;
    /// <summary>Environment reflection amount (0 = no env-map stage).</summary>
    public float Env { get; init; }
    /// <summary>Alpha mode forced by the material's render flags (-1 = derive from the texture name).</summary>
    public int Alpha { get; init; } = -1;
    public float AlphaRef { get; init; } = 0.5f;
    /// <summary>Material asks for no back-face culling.</summary>
    public bool TwoSided { get; init; }
    /// <summary>The shader cuts by the texture alpha whatever the texture name (hair, tree, grass shaders).</summary>
    public bool CutsAlpha { get; init; }
    /// <summary>Why parts of the material could not be used (shown in tooltips / logs).</summary>
    public string? Error { get; init; }

    static Vector3[] Fill(Vector3 v)
    {
        var a = new Vector3[Slots];
        Array.Fill(a, v);
        return a;
    }
}

/// <summary>
/// Resolves face material paths to <see cref="GpuMaterial"/>s (cached; rebuilt when the source reports a change) and
/// binds them for the Final shader. The material source is pluggable (<see cref="Source"/>) so the rvmat model
/// of the materials feature can be connected without the renderer depending on it at compile time.
/// </summary>
public sealed class MaterialLibrary
{
    readonly Dictionary<string, GpuMaterial> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a material (path as stored in the face) or returns null when it cannot be resolved.</summary>
    public static Func<string, GpuMaterial?>? Source { get; set; }

    /// <summary>Bumped whenever cached materials were dropped (renderers compare it to refresh).</summary>
    public int Version { get; private set; }

    public GpuMaterial Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return GpuMaterial.Default;
        if (cache.TryGetValue(path, out var m)) return m;
        try
        {
            m = (Source ?? RvmatBridge.Load)(path) ?? GpuMaterial.Default;
        }
        catch (Exception ex)
        {
            m = new GpuMaterial { Path = path, Error = ex.Message };
        }
        cache[path] = m;
        return m;
    }

    /// <summary>Drops one material (null = all) so it is rebuilt on next use.</summary>
    public void Invalidate(string? path = null)
    {
        if (path is null) cache.Clear();
        else cache.Remove(path);
        Version++;
    }

    /// <summary>
    /// Alpha handling of a range: material render flags first, else the texture (_ca name or PAA alpha flags), else
    /// a shader that cuts by alpha anyway (hair, foliage).
    /// </summary>
    public static (int Mode, float Ref) AlphaOf(GpuMaterial m, TextureCache.Binding co)
    {
        if (m.Alpha >= 0) return (m.Alpha, m.AlphaRef);
        if (co.Blend) return (GpuMaterial.AlphaBlend, 0.02f);
        return co.AlphaTest || (m.CutsAlpha && co.Real) ? (GpuMaterial.AlphaTest, 0.5f) : (GpuMaterial.AlphaOpaque, 0f);
    }

    /// <summary>Binds textures + uniforms of one range for the Final program; returns the alpha mode.</summary>
    public int BindFinal(GlProgram p, TextureCache textures, string faceTexture, GpuMaterial m, out float alphaRef)
    {
        int has = 0;
        TextureCache.Binding co = default;
        for (int s = 0; s < GpuMaterial.Slots; s++)
        {
            string? name = s == GpuMaterial.SlotCo && !string.IsNullOrWhiteSpace(faceTexture) ? faceTexture : m.Textures[s];
            var b = string.IsNullOrWhiteSpace(name) ? default : textures.Get(name, linear: s != GpuMaterial.SlotCo && s != GpuMaterial.SlotMc);
            if (s == GpuMaterial.SlotCo) co = b;
            GL.ActiveTexture(TextureUnit.Texture0 + s);
            GL.BindTexture(TextureTarget.Texture2D, b.Handle);
            // A missing optional map simply drops out; a missing _co still shows its checker placeholder.
            if (b.Handle != 0 && (b.Real || s == GpuMaterial.SlotCo)) has |= 1 << s;
            p.Set(UvU[s], m.UvU[s]);
            p.Set(UvV[s], m.UvV[s]);
            p.Set(UvSrc[s], m.UvSource[s]);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        p.Set("uHas", has);
        p.Set("uAmbient", m.Ambient);
        p.Set("uDiffuse", m.Diffuse);
        p.Set("uForced", m.Forced);
        p.Set("uEmissive", m.Emissive);
        p.Set("uSpecColor", m.Specular);
        p.Set("uSpecPower", m.SpecularPower);
        p.Set("uF0", m.F0);
        p.Set("uEnv", m.Env);
        var (mode, r) = AlphaOf(m, co);
        alphaRef = r;
        return mode;
    }

    static readonly string[] UvU = [.. Enumerable.Range(0, GpuMaterial.Slots).Select(i => $"uUvU[{i}]")];
    static readonly string[] UvV = [.. Enumerable.Range(0, GpuMaterial.Slots).Select(i => $"uUvV[{i}]")];
    static readonly string[] UvSrc = [.. Enumerable.Range(0, GpuMaterial.Slots).Select(i => $"uUvSrc[{i}]")];
}
