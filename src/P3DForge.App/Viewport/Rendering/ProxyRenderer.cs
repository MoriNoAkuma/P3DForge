using System.Collections.Concurrent;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using P3DForge.App.Scene;
using P3DForge.Core.Model;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// Draws the proxy models of <see cref="ProxyScene"/> with the main model's shading. GPU meshes are cached per
/// (ProxyModel.Path, Version, LodIndex) and built on a background thread (CPU triangulation) then uploaded on the
/// GL thread; every instance is drawn with its RAW→RAW transform converted to RENDER (S·T·S).
/// </summary>
public sealed class ProxyRenderer : IDisposable
{
    sealed class GpuMesh : IDisposable
    {
        public int Vao, Vbo, EdgeVao, EdgeVbo, Vertices, EdgeVertices;
        public MeshRange[] Ranges = [];
        public Vector3 Min, Max;

        public void Dispose()
        {
            GL.DeleteVertexArray(Vao);
            GL.DeleteVertexArray(EdgeVao);
            GL.DeleteBuffer(Vbo);
            GL.DeleteBuffer(EdgeVbo);
        }
    }

    sealed class Entry(string path, int version, int lodIndex)
    {
        public readonly string Path = path;
        public readonly int Version = version;
        public readonly int LodIndex = lodIndex;
        public GpuMesh? Mesh;
        public MeshData? Built;
        public float[]? BuiltEdges;
        public string? Error;
        public long LastUsed;
    }

    readonly record struct Item(GpuMesh Mesh, InstanceDraw Draw);

    readonly Dictionary<(string, int, int), Entry> cache = [];
    readonly ConcurrentQueue<Entry> ready = new();
    readonly Action requestRedraw;
    readonly List<Item> items = [];
    long frame;
    int building;

    /// <summary>RENDER bounds of the drawn instances (valid when <see cref="Count"/> &gt; 0).</summary>
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    /// <summary>Instances drawn this frame.</summary>
    public int Count => items.Count;
    /// <summary>Meshes still building.</summary>
    public int Building => Volatile.Read(ref building);
    public int CachedMeshes => cache.Count;
    /// <summary>Changes whenever the drawn set (meshes, transforms, visibility) changed — shadow map cache key.</summary>
    public int Version { get; private set; }
    int lastSignature;

    /// <param name="requestRedraw">Called from any thread when a background build finished.</param>
    public ProxyRenderer(Action requestRedraw) => this.requestRedraw = requestRedraw;

    /// <summary>Uploads finished builds and resolves the instances to draw this frame (GL thread, once per frame).</summary>
    public void Prepare(IReadOnlyList<ProxyInstance> instances, bool enabled, bool selectionTint)
    {
        frame++;
        while (ready.TryDequeue(out var done))
        {
            if (!cache.TryGetValue((done.Path, done.Version, done.LodIndex), out var cur) || !ReferenceEquals(cur, done)) continue;
            if (done.Built is { } md) done.Mesh = Upload(md, done.BuiltEdges ?? []);
            done.Built = null;
            done.BuiltEdges = null;
        }
        items.Clear();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var sig = new HashCode();
        if (enabled)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                if (!inst.Visible) continue;
                var pm = inst.Model;
                var lod = pm.Lod;
                if (lod is null || lod.FaceCount == 0) continue;
                var key = (pm.Path, pm.Version, pm.LodIndex);
                if (!cache.TryGetValue(key, out var e))
                {
                    e = new Entry(pm.Path, pm.Version, pm.LodIndex);
                    cache[key] = e;
                    StartBuild(e, lod);
                }
                e.LastUsed = frame;
                if (e.Mesh is not { } mesh) continue;
                var m = RenderMath.RawToRenderTransform(inst.Transform);
                int flags = inst.Selected && selectionTint ? ModelRenderer.FlagSelected : 0;
                items.Add(new Item(mesh, new InstanceDraw(m, RenderMath.NormalMatrix(m), RenderMath.Determinant3(m) < 0, flags)));
                var (bmin, bmax) = RenderMath.TransformBounds(mesh.Min, mesh.Max, m);
                min = Vector3.Min(min, bmin);
                max = Vector3.Max(max, bmax);
                sig.Add(mesh.Vao);
                sig.Add(m);
            }
        }
        BoundsMin = min;
        BoundsMax = max;
        int s = sig.ToHashCode();
        if (s != lastSignature)
        {
            lastSignature = s;
            Version++;
        }
        Evict();
    }

    void StartBuild(Entry e, Lod lod)
    {
        Interlocked.Increment(ref building);
        Task.Run(() =>
        {
            try
            {
                // Nested proxy triangles are helpers: the nested models are separate instances.
                var skip = SceneData.ProxyFaceMask(lod, SceneData.ProxyTags(lod));
                var md = MeshBuilder.Build(lod, lod.Positions, false, skip, null, edges: true);
                var ev = new float[md.EdgeA.Length * 6];
                var rp = md.RenderPositions;
                for (int i = 0, o = 0; i < md.EdgeA.Length; i++)
                {
                    uint a = md.EdgeA[i], b = md.EdgeB[i];
                    ev[o++] = rp[a * 3];
                    ev[o++] = rp[a * 3 + 1];
                    ev[o++] = rp[a * 3 + 2];
                    ev[o++] = rp[b * 3];
                    ev[o++] = rp[b * 3 + 1];
                    ev[o++] = rp[b * 3 + 2];
                }
                e.Built = md;
                e.BuiltEdges = ev;
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
            }
            finally
            {
                Interlocked.Decrement(ref building);
                ready.Enqueue(e);
                requestRedraw();
            }
        });
    }

    static GpuMesh Upload(MeshData md, float[] edges)
    {
        var g = new GpuMesh
        {
            Vao = GL.GenVertexArray(),
            Vbo = GL.GenBuffer(),
            EdgeVao = GL.GenVertexArray(),
            EdgeVbo = GL.GenBuffer(),
            Vertices = md.Vertices,
            EdgeVertices = edges.Length / 3,
            Ranges = md.Ranges,
            Min = md.Min,
            Max = md.Max,
        };
        GL.BindVertexArray(g.Vao);
        ModelRenderer.Upload(g.Vbo, md.Geom, md.Vertices * MeshData.Stride);
        ModelRenderer.SetupMeshAttributes();
        GL.DisableVertexAttribArray(3); // state comes from a constant attribute per instance
        GL.BindVertexArray(g.EdgeVao);
        ModelRenderer.Upload(g.EdgeVbo, edges);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1); // colour = constant attribute
        GL.BindVertexArray(0);
        return g;
    }

    /// <summary>Keeps meshes of the last ~2 minutes of frames; drops older ones when many are cached.</summary>
    void Evict()
    {
        if (cache.Count <= 96) return;
        List<(string, int, int)>? drop = null;
        foreach (var (k, e) in cache)
            if (frame - e.LastUsed > 600 && e.Built is null) (drop ??= []).Add(k);
        if (drop is null) return;
        foreach (var k in drop)
            if (cache.Remove(k, out var e)) e.Mesh?.Dispose();
    }

    /// <summary>Faces of every instance in the pane's shading (wireframe: edges).</summary>
    public void DrawSurface(DrawContext dc)
    {
        if (items.Count == 0) return;
        if (dc.Options.Shading == Shading.Wire)
        {
            DrawEdges(dc, wire: true);
            return;
        }
        foreach (var it in items)
        {
            GL.BindVertexArray(it.Mesh.Vao);
            SurfacePass.Draw(dc, it.Mesh.Ranges, it.Mesh.Vertices, it.Draw);
            if (dc.BlendPass != 1) dc.Stats.Instances++;
        }
        GL.BindVertexArray(0);
    }

    /// <summary>Edges of every instance: wireframe mode or the Final wireframe overlay.</summary>
    public void DrawEdges(DrawContext dc, bool wire)
    {
        if (items.Count == 0) return;
        var lp = dc.Programs.Line;
        lp.Use();
        lp.Set("uViewProj", dc.ViewProj);
        lp.Set("uPointSize", 1f);
        lp.Set("uDim", 1f);
        lp.Set("uWire", 0);
        lp.Set("uUseModel", 1);
        foreach (var it in items)
        {
            if (it.Mesh.EdgeVertices == 0) continue;
            bool sel = (it.Draw.Flags & ModelRenderer.FlagSelected) != 0;
            var c = sel ? new Vector4(1.0f, 0.62f, 0.25f, 1) : wire ? new Vector4(0.62f, 0.78f, 0.86f, 1) : new Vector4(0.05f, 0.06f, 0.08f, 0.45f);
            GL.VertexAttrib4(1, c.X, c.Y, c.Z, c.W);
            lp.Set("uModel", it.Draw.Model);
            GL.BindVertexArray(it.Mesh.EdgeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, it.Mesh.EdgeVertices);
            dc.Stats.DrawCalls++;
        }
        lp.Set("uUseModel", 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Shadow depth of every instance (depth program in use).</summary>
    public void DrawDepth(GlProgram depth, TextureCache textures, MaterialLibrary materials)
    {
        foreach (var it in items)
        {
            GL.FrontFace(it.Draw.Mirror ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            GL.BindVertexArray(it.Mesh.Vao);
            SurfacePass.DrawDepth(depth, textures, it.Mesh.Ranges, it.Mesh.Vertices, materials, it.Draw);
        }
        GL.FrontFace(FrontFaceDirection.Ccw);
        depth.Set("uUseModel", 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Drops every cached mesh (e.g. on context loss).</summary>
    public void Clear()
    {
        foreach (var e in cache.Values) e.Mesh?.Dispose();
        cache.Clear();
        items.Clear();
    }

    public void Dispose() => Clear();
}
