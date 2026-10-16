using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// One directional shadow map (depth texture with hardware comparison for PCF) fitted to the bounds of the model
/// and its proxies. Re-rendered only when its inputs change (light direction, geometry, instances, size).
/// </summary>
public sealed class ShadowMap : IDisposable
{
    int fbo, tex, size;
    object? key;

    public int Texture => tex;
    public int Size => size;
    public Matrix4x4 LightViewProj { get; private set; } = Matrix4x4.Identity;
    /// <summary>World units per shadow texel (normal-offset bias scale).</summary>
    public float TexelWorld { get; private set; } = 0.01f;
    public int Renders { get; private set; }

    void Ensure(int wanted)
    {
        if (tex != 0 && size == wanted) return;
        Release();
        size = wanted;
        tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, size, size, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, new[] { 1f, 1f, 1f, 1f });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        key = null;
    }

    /// <summary>
    /// Renders the map when <paramref name="cacheKey"/> changed. <paramref name="draw"/> issues the depth draws (depth
    /// program in use, light matrix set). Restores <paramref name="restoreFramebuffer"/> and the viewport afterwards.
    /// </summary>
    public void Update(int mapSize, Vector3 boundsMin, Vector3 boundsMax, Vector3 towardsLight, object cacheKey,
        GlProgram depth, Action<GlProgram> draw, int restoreFramebuffer, (int X, int Y, int W, int H) restoreViewport)
    {
        if (tex != 0 && size == mapSize && Equals(key, cacheKey)) return;
        Ensure(mapSize);
        key = cacheKey;
        var (center, radius) = RenderMath.Sphere(boundsMin, boundsMax);
        LightViewProj = RenderMath.ShadowViewProjection(center, radius, towardsLight, size);
        TexelWorld = radius * 2f / size;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.Viewport(0, 0, size, size);
        GL.Disable(EnableCap.ScissorTest);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(2.0f, 4.0f);
        depth.Use();
        depth.Set("uLightViewProj", LightViewProj);
        draw(depth);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.Enable(EnableCap.Blend);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, restoreFramebuffer);
        GL.Viewport(restoreViewport.X, restoreViewport.Y, restoreViewport.W, restoreViewport.H);
        Renders++;
    }

    /// <summary>Forces a re-render on the next <see cref="Update"/>.</summary>
    public void Invalidate() => key = null;

    void Release()
    {
        if (fbo != 0) GL.DeleteFramebuffer(fbo);
        if (tex != 0) GL.DeleteTexture(tex);
        fbo = tex = 0;
    }

    public void Dispose() => Release();
}

/// <summary>GPU frame time via GL_TIME_ELAPSED queries (ping-pong; results read one frame late, never stalling).</summary>
public sealed class GpuTimer : IDisposable
{
    readonly int[] queries = new int[3];
    readonly bool[] pending = new bool[3];
    int current;
    bool active;

    /// <summary>Last measured GPU time in milliseconds (NaN until available).</summary>
    public double LastMs { get; private set; } = double.NaN;

    public GpuTimer()
    {
        GL.GenQueries(queries.Length, queries);
    }

    public void Begin()
    {
        // Collect finished results first.
        for (int i = 0; i < queries.Length; i++)
        {
            if (!pending[i]) continue;
            GL.GetQueryObject(queries[i], GetQueryObjectParam.QueryResultAvailable, out int ok);
            if (ok == 0) continue;
            GL.GetQueryObject(queries[i], GetQueryObjectParam.QueryResult, out long ns);
            LastMs = ns / 1e6;
            pending[i] = false;
        }
        if (pending[current]) return; // all busy: skip this frame
        GL.BeginQuery(QueryTarget.TimeElapsed, queries[current]);
        active = true;
    }

    public void End()
    {
        if (!active) return;
        GL.EndQuery(QueryTarget.TimeElapsed);
        pending[current] = true;
        current = (current + 1) % queries.Length;
        active = false;
    }

    public void Dispose() => GL.DeleteQueries(queries.Length, queries);
}
