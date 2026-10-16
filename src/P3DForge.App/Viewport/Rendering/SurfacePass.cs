using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>Per-draw instance data (proxy models): RENDER→RENDER transform + state flags for the tint.</summary>
public readonly record struct InstanceDraw(Matrix4x4 Model, Matrix4x4 NormalMatrix, bool Mirror, int Flags);

/// <summary>
/// Draws the face ranges of a mesh VAO (bound by the caller) in the pane's shading: Solid / Textured with the
/// editing face program, Final with the material program (opaque + alpha-tested ranges first, blended ranges
/// after them without depth writes). Also the shadow depth pass.
/// </summary>
public static class SurfacePass
{
    static int[] modes = new int[64];
    static float[] refs = new float[64];

    static GlProgram Begin(DrawContext dc)
    {
        var opt = dc.Options;
        bool final = opt.Shading == Shading.Final;
        var p = final ? dc.Programs.Final : dc.Programs.Face;
        p.Use();
        p.Set("uViewProj", dc.ViewProj);
        p.Set("uEye", dc.Eye);
        dc.Shadow.Apply(p);
        if (final)
        {
            dc.FinalLight.Apply(p);
            var f = dc.Final;
            p.Set("uToggles", f.Toggles);
            p.Set("uStage", f.Stage);
            p.Set("uExposure", f.Exposure);
            p.Set("uFlipGreen", f.FlipGreen ? 1 : 0);
            p.Set("uSelTint", 0.45f);
            p.Set("uHideProxyFaces", 1);
            p.Set("uBackTint", 0f);
        }
        else
        {
            dc.EditLight.Apply(p);
            p.Set("uBackTint", opt.BackTint);
            p.Set("uWeights", opt.Weights ? 1 : 0);
            p.Set("uOpacity", 1f);
            p.Set("uShowProxies", opt.ShowProxies ? 1 : 0);
            p.Set("uBaseColor", new Vector4(0.72f, 0.74f, 0.78f, 1));
            p.Set("uSelTint", opt.Shading == Shading.Textured ? 0.5f : 0.78f);
        }
        return p;
    }

    static void SetInstance(GlProgram p, InstanceDraw? instance)
    {
        if (instance is { } inst)
        {
            p.Set("uUseModel", 1);
            p.Set("uModel", inst.Model);
            p.Set("uNormalMat", inst.NormalMatrix);
            GL.FrontFace(inst.Mirror ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            GL.VertexAttrib2(3, (float)inst.Flags, -1f);
        }
        else
        {
            p.Set("uUseModel", 0);
            GL.FrontFace(FrontFaceDirection.Ccw);
        }
    }

    /// <summary>Draws one mesh (its VAO bound). <paramref name="instance"/> = null for the edited LOD (state buffer bound).</summary>
    public static void Draw(DrawContext dc, IReadOnlyList<MeshRange> ranges, int totalVertices, InstanceDraw? instance)
    {
        if (totalVertices == 0) return;
        var opt = dc.Options;
        if (dc.BlendPass == 1 && (opt.Shading is not (Shading.Final or Shading.Textured) || !AnyBlend(dc, ranges))) return;
        var p = Begin(dc);
        SetInstance(p, instance);
        // the red back-face hint is for the edited LOD; a proxy model cannot be fixed here
        float backTint = instance is null ? opt.BackTint : 0f;
        if (opt.Shading != Shading.Final) p.Set("uBackTint", backTint);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1f, 1f);
        GL.CullFace(TriangleFace.Back);
        GL.ActiveTexture(TextureUnit.Texture0);
        switch (opt.Shading)
        {
            case Shading.Solid:
                SetCull(opt.Culling);
                p.Set("uUseTex", 0);
                p.Set("uAlphaTest", 0f);
                p.Set("uAlphaMul", 0f);
                GL.DrawArrays(PrimitiveType.Triangles, 0, totalVertices);
                dc.Stats.Add(totalVertices);
                break;
            case Shading.Textured:
                SetCull(opt.Culling);
                // pass 0: opaque and cut-out textures; pass 1: blended ones (glass) over them, without depth writes
                for (int pass = 0; pass < 2; pass++)
                {
                    if (dc.BlendPass >= 0 && pass != dc.BlendPass) continue;
                    if (pass == 1)
                    {
                        GL.DepthMask(false);
                        SetCull(true); // glass is one-sided in the game: an inner layer facing inward is not seen from outside
                    }
                    foreach (var r in ranges)
                    {
                        var b = dc.Textures.Get(r.Texture);
                        var m = dc.Materials.Get(r.Material);
                        // the rvmat's alpha flags (AlphaTest32…128, glass) win over the texture, as in Final
                        var (mode, aref) = MaterialLibrary.AlphaOf(m, b);
                        bool blend = mode == GpuMaterial.AlphaBlend;
                        if (blend != (pass == 1)) continue;
                        // two-sided materials (ghillie, foliage, nets) show their backs on purpose: no red hint
                        p.Set("uBackTint", mode == GpuMaterial.AlphaTest || m.TwoSided ? 0f : backTint);
                        p.Set("uUseTex", b.Handle != 0 ? 1 : 0);
                        p.Set("uSrgb", b.Srgb ? 1 : 0);
                        p.Set("uAlphaTest", blend ? 0.02f : mode == GpuMaterial.AlphaTest ? aref : 0f);
                        p.Set("uAlphaMul", blend ? 1f : 0f);
                        GL.BindTexture(TextureTarget.Texture2D, b.Handle);
                        GL.DrawArrays(PrimitiveType.Triangles, r.First, r.Count);
                        dc.Stats.Add(r.Count);
                    }
                    GL.DepthMask(true);
                    SetCull(opt.Culling);
                }
                p.Set("uAlphaMul", 0f);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                break;
            case Shading.Final:
                DrawFinal(dc, p, ranges);
                break;
        }
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.Disable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
        if (instance is not null) GL.VertexAttrib2(3, 0f, -1f);
    }

    static void SetCull(bool cull)
    {
        if (cull) GL.Enable(EnableCap.CullFace);
        else GL.Disable(EnableCap.CullFace);
    }

    static bool AnyBlend(DrawContext dc, IReadOnlyList<MeshRange> ranges)
    {
        bool final = dc.Options.Shading == Shading.Final;
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            var m = dc.Materials.Get(r.Material);
            if (!final)
            {
                if (MaterialLibrary.AlphaOf(m, dc.Textures.Get(r.Texture)).Mode == GpuMaterial.AlphaBlend) return true;
                continue;
            }
            var coName = string.IsNullOrWhiteSpace(r.Texture) ? m.Textures[GpuMaterial.SlotCo] : r.Texture;
            var co = string.IsNullOrWhiteSpace(coName) ? default : dc.Textures.Get(coName, linear: false);
            if (MaterialLibrary.AlphaOf(m, co).Mode == GpuMaterial.AlphaBlend) return true;
        }
        return false;
    }

    static void DrawFinal(DrawContext dc, GlProgram p, IReadOnlyList<MeshRange> ranges)
    {
        int n = ranges.Count;
        if (modes.Length < n)
        {
            modes = new int[n * 2];
            refs = new float[n * 2];
        }
        bool anyBlend = false;
        for (int i = 0; i < n; i++)
        {
            var r = ranges[i];
            var m = dc.Materials.Get(r.Material);
            var coName = string.IsNullOrWhiteSpace(r.Texture) ? m.Textures[GpuMaterial.SlotCo] : r.Texture;
            var co = string.IsNullOrWhiteSpace(coName) ? default : dc.Textures.Get(coName, linear: false);
            (modes[i], refs[i]) = MaterialLibrary.AlphaOf(m, co);
            anyBlend |= modes[i] == GpuMaterial.AlphaBlend;
        }
        int backFaces = dc.Final.BackFaces; // 0 auto, 1 show, 2 cull
        for (int pass = 0; pass < (anyBlend ? 2 : 1); pass++)
        {
            if (dc.BlendPass >= 0 && pass != dc.BlendPass) continue;
            if (pass == 1) GL.DepthMask(false);
            for (int i = 0; i < n; i++)
            {
                bool blend = modes[i] == GpuMaterial.AlphaBlend;
                if (blend != (pass == 1)) continue;
                var r = ranges[i];
                var m = dc.Materials.Get(r.Material);
                dc.Materials.BindFinal(p, dc.Textures, r.Texture, m, out _);
                p.Set("uAlphaTest", modes[i] == GpuMaterial.AlphaTest ? refs[i] : blend ? 0.02f : 0f);
                p.Set("uAlphaMul", blend ? 1f : 0f);
                // auto: one-sided like the game for opaque and blended faces (a vehicle's inner glass layer faces inward
                // and is invisible from outside); cut-out faces (foliage, nets) stay two-sided
                SetCull(backFaces == 2 || (backFaces == 0 && modes[i] != GpuMaterial.AlphaTest && !m.TwoSided));
                GL.DrawArrays(PrimitiveType.Triangles, r.First, r.Count);
                dc.Stats.Add(r.Count);
            }
        }
        GL.DepthMask(true);
        for (int s = GpuMaterial.Slots - 1; s >= 0; s--)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + s);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
    }

    /// <summary>Shadow depth pass of one mesh (VAO bound, depth program in use, light matrix set).</summary>
    public static void DrawDepth(GlProgram depth, TextureCache textures, IReadOnlyList<MeshRange> ranges, int totalVertices,
        MaterialLibrary? materials = null, InstanceDraw? instance = null)
    {
        if (totalVertices == 0) return;
        if (instance is { } inst)
        {
            depth.Set("uUseModel", 1);
            depth.Set("uModel", inst.Model);
        }
        else depth.Set("uUseModel", 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        int runStart = -1, runEnd = -1;
        void Flush()
        {
            if (runStart < 0) return;
            depth.Set("uAlphaTest", 0f);
            GL.DrawArrays(PrimitiveType.Triangles, runStart, runEnd - runStart);
            runStart = runEnd = -1;
        }
        foreach (var r in ranges)
        {
            var m = materials?.Get(r.Material) ?? GpuMaterial.Default;
            var coName = string.IsNullOrWhiteSpace(r.Texture) ? m.Textures[GpuMaterial.SlotCo] : r.Texture;
            var co = string.IsNullOrWhiteSpace(coName) ? default : textures.Get(coName, linear: false);
            var (mode, aref) = MaterialLibrary.AlphaOf(m, co);
            if (mode == GpuMaterial.AlphaBlend) continue; // glass etc. casts no shadow
            if (mode == GpuMaterial.AlphaOpaque || co.Handle == 0)
            {
                if (runStart >= 0 && runEnd == r.First) runEnd += r.Count;
                else
                {
                    Flush();
                    runStart = r.First;
                    runEnd = r.First + r.Count;
                }
                continue;
            }
            Flush();
            GL.BindTexture(TextureTarget.Texture2D, co.Handle);
            depth.Set("uAlphaTest", aref);
            GL.DrawArrays(PrimitiveType.Triangles, r.First, r.Count);
        }
        Flush();
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }
}
