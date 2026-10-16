using System.Collections.Immutable;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>
/// GPU buffers for one LOD. Faces are triangulated by <see cref="MeshBuilder"/> into draw ranges grouped by
/// (texture, material) in a static geometry buffer (position, normal, UVs, tangent) plus a small per-vertex state
/// buffer (selection / proxy / hidden flags, weight) so selection changes only re-upload state. Unique edges,
/// points, proxy triads and normal lines are separate line buffers. Rebuilds are driven by array identity
/// (the model is immutable).
/// </summary>
public sealed class ModelRenderer : IDisposable
{
    static readonly Vector4 EdgeColor = new(0.08f, 0.09f, 0.11f, 0.55f);
    static readonly Vector4 EdgeSelected = new(1.0f, 0.62f, 0.25f, 1);
    // Alpha < 0.99 marks an unselected point: LineVs draws it 1 px smaller, selected points 1 px larger.
    static readonly Vector4 PointColor = new(0.85f, 0.87f, 0.92f, 0.985f);
    static readonly Vector4 PointSelected = new(1.0f, 0.35f, 0.1f, 1);
    static readonly Vector4 PointHidden = new(0.55f, 0.45f, 0.75f, 0.6f);

    public const int FlagSelected = 1, FlagProxy = 2, FlagHidden = 4;

    readonly GpuPrograms programs;
    readonly TextureCache textures;

    readonly int faceVao, geomVbo, stateVbo;
    int faceVertices;
    readonly int edgeVao, edgeVbo;
    int edgeVertices;
    readonly int pointVao, pointVbo;
    int pointCount;
    readonly int gridVao, gridVbo;
    int gridVertices;
    readonly int overlayVao, overlayVbo;
    int overlayVertices;
    readonly int proxyVao, proxyVbo;
    int proxyVertices;
    readonly int normalVao, normalVbo;
    int normalVertices;

    MeshRange[] ranges = [];

    // geometry cache keys (array identity)
    float[]? cPositions;
    uint[]? cFacePoints, cFlags, cFaceTexture, cFaceMaterial, cFaceNormals;
    float[]? cFaceUV, cNormals;
    ImmutableArray<string> cStrings;
    Tag? cHiddenTag;
    UVSetTag? cUvSet1;
    bool cShowHidden;
    List<SelectionTag> cProxyTags = [];
    // state cache keys
    byte[]? cSelPoints, cSelFaces;
    byte[]? cWeights;
    // normals cache
    bool cNormalsBuilt;

    int[] vertPoint = [];
    /// <summary>CPU copy of the uploaded face vertices (positions-only preview updates write into it).</summary>
    float[]? cGeom;
    float normalSign = 1;
    uint[] edgeA = [], edgeB = [];
    byte[] hiddenPoints = [], hiddenFaces = [];
    byte[]? proxyFaces;

    /// <summary>Triangle → face index.</summary>
    public int[] TriangleFace { get; private set; } = [];
    /// <summary>RENDER positions per point of the drawn LOD (preview positions if any).</summary>
    public float[] RenderPositions { get; private set; } = [];
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public bool HasGeometry => RenderPositions.Length > 0;
    /// <summary>Proxies of the drawn LOD (RAW frames), recomputed with the geometry.</summary>
    public IReadOnlyList<ProxyInfo> Proxies { get; private set; } = [];
    public int DrawRangeCount => ranges.Length;
    public IReadOnlyList<MeshRange> Ranges => ranges;
    public int FaceVertices => faceVertices;
    public TextureCache Textures => textures;
    /// <summary>Increments whenever the face geometry was rebuilt (shadow map cache key).</summary>
    public int GeometryVersion { get; private set; }
    /// <summary>Milliseconds spent in the last geometry rebuild.</summary>
    public double LastBuildMs { get; private set; }
    /// <summary>Lap times of the last rebuild (diagnostics).</summary>
    public string BuildBreakdown { get; private set; } = "";
    readonly System.Diagnostics.Stopwatch lap = new();
    readonly System.Text.StringBuilder laps = new();
    void Lap(string name)
    {
        laps.Append(name).Append('=').Append(lap.Elapsed.TotalMilliseconds.ToString("F0")).Append(' ');
        lap.Restart();
    }

    public ModelRenderer(GpuPrograms programs, TextureCache textures)
    {
        this.programs = programs;
        this.textures = textures;

        faceVao = GL.GenVertexArray();
        geomVbo = GL.GenBuffer();
        stateVbo = GL.GenBuffer();
        GL.BindVertexArray(faceVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, geomVbo);
        SetupMeshAttributes();
        GL.BindBuffer(BufferTarget.ArrayBuffer, stateVbo);
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(3);
        (edgeVao, edgeVbo) = MakeLineVao();
        (pointVao, pointVbo) = MakeLineVao();
        (gridVao, gridVbo) = MakeLineVao();
        (overlayVao, overlayVbo) = MakeLineVao();
        (proxyVao, proxyVbo) = MakeLineVao();
        (normalVao, normalVbo) = MakeLineVao();
        GL.BindVertexArray(0);
        BuildGrid(20, 1);
    }

    /// <summary>Attribute pointers of the <see cref="MeshData"/> layout for the bound VAO + ARRAY_BUFFER (all but the state attribute 3).</summary>
    public static void SetupMeshAttributes()
    {
        const int stride = MeshData.Stride * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.VertexAttribPointer(5, 2, VertexAttribPointerType.Float, false, stride, 12 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);
        GL.EnableVertexAttribArray(4);
        GL.EnableVertexAttribArray(5);
    }

    public static (int Vao, int Vbo) MakeLineVao()
    {
        int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        const int stride = 7 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        return (vao, vbo);
    }

    public static void Upload(int vbo, float[] data, int count = -1)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, (count < 0 ? data.Length : count) * sizeof(float), data, BufferUsageHint.DynamicDraw);
    }

    void BuildGrid(int half, float step)
    {
        var v = new List<float>();
        void Line(Vector3 a, Vector3 b, Vector4 c) => v.AddRange([a.X, a.Y, a.Z, c.X, c.Y, c.Z, c.W, b.X, b.Y, b.Z, c.X, c.Y, c.Z, c.W]);
        var minor = new Vector4(0.34f, 0.36f, 0.40f, 0.35f);
        for (int i = -half; i <= half; i++)
        {
            if (i == 0) continue;
            Line(new(i * step, 0, -half * step), new(i * step, 0, half * step), minor);
            Line(new(-half * step, 0, i * step), new(half * step, 0, i * step), minor);
        }
        Line(new(-half * step, 0, 0), new(half * step, 0, 0), new(0.85f, 0.3f, 0.3f, 0.8f)); // X
        Line(new(0, 0, -half * step), new(0, 0, half * step), new(0.3f, 0.45f, 0.9f, 0.8f)); // Z
        var data = v.ToArray();
        Upload(gridVbo, data);
        gridVertices = data.Length / 7;
    }

    /// <summary>
    /// Updates GPU data for <paramref name="lod"/>. <paramref name="preview"/> overrides positions (RAW);
    /// <paramref name="weights"/> = per-point weight mask for the heat map (null = off).
    /// </summary>
    public void Sync(Lod lod, float[]? preview, bool showHidden = false, byte[]? weights = null)
    {
        var positions = preview ?? lod.Positions;
        if (positions.Length < lod.PointCount * 3) positions = lod.Positions;
        var hiddenTag = lod.State(StateKind.Hidden);
        var proxyTags = SceneData.ProxyTags(lod);
        var uvSet1 = lod.UVSet(1);
        bool topologyChanged = !ReferenceEquals(lod.FacePoints, cFacePoints)
            || !ReferenceEquals(lod.PointFlags, cFlags) || !ReferenceEquals(hiddenTag, cHiddenTag) || !ReferenceEquals(lod.FaceUV, cFaceUV)
            || !ReferenceEquals(lod.FaceTexture, cFaceTexture) || !ReferenceEquals(lod.FaceMaterial, cFaceMaterial) || lod.Strings != cStrings
            || !ReferenceEquals(lod.Normals, cNormals) || !ReferenceEquals(lod.FaceNormals, cFaceNormals) || !ReferenceEquals(uvSet1, cUvSet1)
            || showHidden != cShowHidden || !SameRefs(proxyTags, cProxyTags);
        bool geometryChanged = topologyChanged || !ReferenceEquals(positions, cPositions);
        // Dragging (preview positions, nothing else changed): move the vertices of the existing buffers instead of
        // rebuilding the mesh — heavy models stay fluid. The full build follows when the edit is committed.
        if (geometryChanged && !topologyChanged && preview is not null && cGeom is not null && positions.Length == cPositions?.Length)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            laps.Clear();
            lap.Restart();
            cPositions = positions;
            UpdatePositions(lod, positions);
            Lap("positions");
            Proxies = SceneData.FindProxies(lod, proxyTags, positions);
            BuildProxyLines();
            GeometryVersion++;
            cNormalsBuilt = false;
            var selNow = Selection.PeekSelected(lod);
            if (!ReferenceEquals(selNow?.Points, cSelPoints) || !ReferenceEquals(selNow?.Faces, cSelFaces) || !ReferenceEquals(weights, cWeights))
            {
                cSelPoints = selNow?.Points;
                cSelFaces = selNow?.Faces;
                cWeights = weights;
                BuildState(lod, selNow, weights);
            }
            else BuildLines(lod, selNow);
            Lap("lines");
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
            BuildBreakdown = laps.ToString() + $"preview pts={lod.PointCount} faces={lod.FaceCount}";
            return;
        }
        if (geometryChanged)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            laps.Clear();
            lap.Restart();
            cPositions = positions;
            cFacePoints = lod.FacePoints;
            cFlags = lod.PointFlags;
            cHiddenTag = hiddenTag;
            cFaceUV = lod.FaceUV;
            cFaceTexture = lod.FaceTexture;
            cFaceMaterial = lod.FaceMaterial;
            cStrings = lod.Strings;
            cNormals = lod.Normals;
            cFaceNormals = lod.FaceNormals;
            cUvSet1 = uvSet1;
            cShowHidden = showHidden;
            cProxyTags = proxyTags;
            BuildGeometry(lod, positions, preview is not null, showHidden);
            Proxies = SceneData.FindProxies(lod, proxyTags, positions);
            BuildProxyLines();
            Lap("proxies");
            cNormalsBuilt = false;
            GeometryVersion++;
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
        }
        var sel = Selection.PeekSelected(lod);
        bool stateChanged = geometryChanged || !ReferenceEquals(sel?.Points, cSelPoints) || !ReferenceEquals(sel?.Faces, cSelFaces) || !ReferenceEquals(weights, cWeights);
        if (!stateChanged) return;
        cSelPoints = sel?.Points;
        cSelFaces = sel?.Faces;
        cWeights = weights;
        BuildState(lod, sel, weights);
        if (geometryChanged) Lap("state");
        BuildBreakdown = laps.ToString() + $"pts={lod.PointCount} faces={lod.FaceCount}";
    }

    static bool SameRefs(List<SelectionTag> a, List<SelectionTag> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    void BuildGeometry(Lod lod, float[] raw, bool flatNormals, bool showHidden)
    {
        hiddenPoints = Selection.HiddenPoints(lod);
        hiddenFaces = Selection.FacesTouching(lod, hiddenPoints);
        proxyFaces = SceneData.ProxyFaceMask(lod, cProxyTags);
        Lap("masks");
        var mesh = MeshBuilder.Build(lod, raw, flatNormals, showHidden ? null : hiddenFaces, hiddenPoints, edges: true);
        Lap("mesh");
        RenderPositions = mesh.RenderPositions;
        BoundsMin = mesh.Min;
        BoundsMax = mesh.Max;
        ranges = mesh.Ranges;
        normalSign = mesh.NormalSign;
        TriangleFace = mesh.TriangleFace;
        vertPoint = mesh.VertPoint;
        edgeA = mesh.EdgeA;
        edgeB = mesh.EdgeB;
        Upload(geomVbo, mesh.Geom, mesh.Vertices * MeshData.Stride);
        cGeom = mesh.Geom;
        faceVertices = mesh.Vertices;
        Lap("upload");
    }

    static Vector3 P(float[] rp, uint i) => new(rp[i * 3], rp[i * 3 + 1], rp[i * 3 + 2]);

    void BuildState(Lod lod, SelectionMask? sel, byte[]? weights)
    {
        int nv = faceVertices;
        var state = new float[nv * 2];
        var triFace = TriangleFace;
        var selFaces = sel?.Faces;
        for (int v = 0; v < nv; v++)
        {
            int f = triFace[v / 3];
            int flags = 0;
            if (selFaces is not null && f < selFaces.Length && selFaces[f] != 0) flags |= FlagSelected;
            if (proxyFaces is not null && proxyFaces[f] != 0) flags |= FlagProxy;
            if (hiddenFaces.Length > f && hiddenFaces[f] != 0) flags |= FlagHidden;
            state[v * 2] = flags;
            int p = vertPoint[v];
            state[v * 2 + 1] = weights is null || p >= weights.Length || weights[p] == 0 ? -1f : Weights.Decode(weights[p]);
        }
        Upload(stateVbo, state);
        BuildLines(lod, sel);
    }

    /// <summary>Edge and point buffers (positions + selection colours).</summary>
    void BuildLines(Lod lod, SelectionMask? sel)
    {
        // Edges: highlighted when both end points are selected.
        var rp = RenderPositions;
        var selPts = sel?.Points;
        var ev = new float[edgeA.Length * 14];
        for (int e = 0, o = 0; e < edgeA.Length; e++)
        {
            uint a = edgeA[e], b = edgeB[e];
            bool s = selPts is not null && a < selPts.Length && b < selPts.Length && selPts[a] != 0 && selPts[b] != 0;
            var c = s ? EdgeSelected : EdgeColor;
            o = PutVertex(ev, o, rp, a, c);
            o = PutVertex(ev, o, rp, b, c);
        }
        Upload(edgeVbo, ev);
        edgeVertices = edgeA.Length * 2;

        int n = lod.PointCount;
        var pv = new float[n * 7];
        int q = 0;
        for (int i = 0; i < n; i++)
        {
            bool hidden = hiddenPoints.Length > i && hiddenPoints[i] != 0;
            if (hidden && !cShowHidden) continue;
            var c = selPts is not null && i < selPts.Length && selPts[i] != 0 ? PointSelected : hidden ? PointHidden : PointColor;
            q = PutVertex(pv, q, rp, (uint)i, c);
        }
        Upload(pointVbo, pv, q);
        pointCount = q / 7;
    }

    /// <summary>
    /// New positions for the existing face vertices: render positions, bounds, vertex positions and flat normals (as a
    /// preview build has), then one upload. Tangents keep their last values until the next full build.
    /// </summary>
    void UpdatePositions(Lod lod, float[] raw)
    {
        int n = lod.PointCount;
        var rp = new float[n * 3];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            float x = raw[i * 3], y = raw[i * 3 + 1], z = -raw[i * 3 + 2];
            rp[i * 3] = x;
            rp[i * 3 + 1] = y;
            rp[i * 3 + 2] = z;
            if (i < hiddenPoints.Length && hiddenPoints[i] != 0) continue;
            if (!float.IsFinite(x + y + z)) continue;
            var v = new Vector3(x, y, z);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        if (min.X > max.X) min = max = Vector3.Zero;
        RenderPositions = rp;
        BoundsMin = min;
        BoundsMax = max;
        var geom = cGeom!;
        const int st = MeshData.Stride;
        int tris = faceVertices / 3;
        for (int t = 0; t < tris; t++)
        {
            int v0 = t * 3;
            var p0 = P(rp, (uint)vertPoint[v0]);
            var p1 = P(rp, (uint)vertPoint[v0 + 1]);
            var p2 = P(rp, (uint)vertPoint[v0 + 2]);
            var nrm = Vector3.Cross(p1 - p0, p2 - p0);
            nrm = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : Vector3.UnitY;
            Put(geom, v0 * st, p0, nrm);
            Put(geom, (v0 + 1) * st, p1, nrm);
            Put(geom, (v0 + 2) * st, p2, nrm);
        }
        Upload(geomVbo, geom, faceVertices * st);

        static void Put(float[] g, int o, Vector3 p, Vector3 n)
        {
            g[o] = p.X;
            g[o + 1] = p.Y;
            g[o + 2] = p.Z;
            g[o + 3] = n.X;
            g[o + 4] = n.Y;
            g[o + 5] = n.Z;
        }
    }

    static int PutVertex(float[] dst, int o, float[] rp, uint p, Vector4 c)
    {
        dst[o++] = rp[p * 3];
        dst[o++] = rp[p * 3 + 1];
        dst[o++] = rp[p * 3 + 2];
        dst[o++] = c.X;
        dst[o++] = c.Y;
        dst[o++] = c.Z;
        dst[o++] = c.W;
        return o;
    }

    void BuildProxyLines()
    {
        var v = new List<float>(Proxies.Count * 6 * 7);
        foreach (var p in Proxies)
        {
            if (p.Frame is not { } fr) continue;
            var c = Coords.RawToRender(fr.Center);
            float len = fr.Size;
            AddLine(v, c, c + Coords.RawToRender(fr.X) * len * 0.6f, new(0.95f, 0.30f, 0.30f, 1));
            AddLine(v, c, c + Coords.RawToRender(fr.Y) * len, new(0.35f, 0.90f, 0.35f, 1));
            AddLine(v, c, c + Coords.RawToRender(fr.Z) * len * 0.6f, new(0.35f, 0.55f, 1.0f, 1));
        }
        var data = v.ToArray();
        Upload(proxyVbo, data);
        proxyVertices = data.Length / 7;
    }

    static void AddLine(List<float> v, Vector3 a, Vector3 b, Vector4 c) =>
        v.AddRange([a.X, a.Y, a.Z, c.X, c.Y, c.Z, c.W, b.X, b.Y, b.Z, c.X, c.Y, c.Z, c.W]);

    /// <summary>Per-vertex normal lines (built lazily when shown).</summary>
    void BuildNormalLines(Lod lod)
    {
        cNormalsBuilt = true;
        var rp = RenderPositions;
        float size = Math.Max((BoundsMax - BoundsMin).Length() * 0.02f, 1e-3f);
        int nf = lod.FaceCount;
        var v = new List<float>(Math.Min(nf, 200_000) * 4 * 14);
        bool useFile = lod.Normals.Length >= 3 && lod.FaceNormals.Length >= nf * 4;
        var color = new Vector4(0.35f, 0.85f, 0.95f, 0.8f);
        for (int f = 0; f < nf && v.Count < 4_000_000; f++)
        {
            if (hiddenFaces.Length > f && hiddenFaces[f] != 0) continue;
            int nv = lod.FaceVertCount[f];
            for (int k = 0; k < nv; k++)
            {
                uint p = lod.FacePoints[f * 4 + k];
                Vector3 nrm;
                uint ni = useFile ? lod.FaceNormals[f * 4 + k] : uint.MaxValue;
                if (useFile && ni * 3 + 2 < lod.Normals.Length) nrm = new Vector3(lod.Normals[ni * 3], lod.Normals[ni * 3 + 1], -lod.Normals[ni * 3 + 2]) * normalSign;
                else continue;
                if (nrm.LengthSquared() < 1e-12f) continue;
                var a = P(rp, p);
                AddLine(v, a, a + Vector3.Normalize(nrm) * size, color);
            }
        }
        var data = v.ToArray();
        Upload(normalVbo, data);
        normalVertices = data.Length / 7;
    }

    public void SetOverlay(IEnumerable<OverlayLine> lines)
    {
        var v = new List<float>();
        foreach (var l in lines)
        {
            float r = ((l.Rgba >> 24) & 255) / 255f, g = ((l.Rgba >> 16) & 255) / 255f, b = ((l.Rgba >> 8) & 255) / 255f, a = (l.Rgba & 255) / 255f;
            v.AddRange([l.A.X, l.A.Y, l.A.Z, r, g, b, a, l.B.X, l.B.Y, l.B.Z, r, g, b, a]);
        }
        var data = v.ToArray();
        Upload(overlayVbo, data);
        overlayVertices = data.Length / 7;
    }

    public sealed record Options(
        Shading Shading,
        bool Culling,
        bool ShowPoints,
        bool ShowGrid,
        float BackTint,
        bool ShowProxies = true,
        bool ShowNormals = false,
        bool Weights = false,
        bool Ghost = false,
        bool Overlay = true,
        float PointSize = 4f,
        bool WireOverlay = false,
        bool SelectedPointsOnly = false);

    /// <summary>Uploads finished texture decodes; call once per frame before drawing (GL thread).</summary>
    public void PrepareFrame()
    {
        textures.BeginFrame();
        textures.ProcessUploads();
    }

    /// <summary>Common GL state of a pane (depth test, blending, point size).</summary>
    public static void BeginPane()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Enable(EnableCap.ProgramPointSize);
    }

    GlProgram UseLines(DrawContext dc)
    {
        var lp = programs.Line;
        lp.Use();
        lp.Set("uViewProj", dc.ViewProj);
        lp.Set("uUseModel", 0);
        lp.Set("uPointSize", 1f);
        lp.Set("uDim", 1f);
        lp.Set("uWire", 0);
        return lp;
    }

    /// <summary>Ground grid (call first in a pane).</summary>
    public void DrawGrid(DrawContext dc)
    {
        if (!dc.Options.ShowGrid || gridVertices == 0) return;
        UseLines(dc);
        GL.DepthMask(false);
        GL.BindVertexArray(gridVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, gridVertices);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
    }

    /// <summary>Faces of the LOD in the pane's shading (nothing in wireframe).</summary>
    public void DrawSurface(DrawContext dc)
    {
        if (dc.Options.Shading == Shading.Wire || faceVertices == 0) return;
        GL.BindVertexArray(faceVao);
        SurfacePass.Draw(dc, ranges, faceVertices, instance: null);
        GL.BindVertexArray(0);
    }

    /// <summary>Edges, normals, points, proxy triads and plugin overlays (after every surface of the pane).</summary>
    public void DrawLines(DrawContext dc, Lod? lodForNormals = null)
    {
        var opt = dc.Options;
        var lineProg = UseLines(dc);
        bool final = opt.Shading == Shading.Final;
        if (edgeVertices > 0 && (!final || opt.WireOverlay))
        {
            lineProg.Set("uWire", opt.Shading == Shading.Wire ? 1 : 0);
            lineProg.Set("uDim", opt.Shading is Shading.Textured or Shading.Final ? 0.35f : 1f);
            GL.BindVertexArray(edgeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, edgeVertices);
            dc.Stats.DrawCalls++;
            lineProg.Set("uWire", 0);
            lineProg.Set("uDim", 1f);
        }
        if (opt.ShowNormals && lodForNormals is not null)
        {
            if (!cNormalsBuilt) BuildNormalLines(lodForNormals);
            if (normalVertices > 0)
            {
                GL.BindVertexArray(normalVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, normalVertices);
            }
        }
        if ((opt.ShowPoints || opt.SelectedPointsOnly) && pointCount > 0)
        {
            lineProg.Set("uPointSize", opt.PointSize);
            lineProg.Set("uOnlySelected", opt.ShowPoints ? 0 : 1);
            GL.BindVertexArray(pointVao);
            GL.DrawArrays(PrimitiveType.Points, 0, pointCount);
            lineProg.Set("uPointSize", 1f);
            lineProg.Set("uOnlySelected", 0);
        }
        GL.Disable(EnableCap.DepthTest);
        if (opt.ShowProxies && proxyVertices > 0)
        {
            GL.BindVertexArray(proxyVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, proxyVertices);
        }
        if (opt.Overlay && overlayVertices > 0)
        {
            GL.BindVertexArray(overlayVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, overlayVertices);
        }
        GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
    }

    /// <summary>Background LOD (Object Builder "Show LOD at Background"): faint see-through yellow wireframe.</summary>
    public void DrawGhost(DrawContext dc)
    {
        if (edgeVertices == 0) return;
        var lineProg = UseLines(dc);
        GL.Disable(EnableCap.DepthTest);
        lineProg.Set("uWire", 2);
        GL.BindVertexArray(edgeVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, edgeVertices);
        lineProg.Set("uWire", 0);
        GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
    }

    /// <summary>Depth of the faces into the bound shadow map (Depth program already in use).</summary>
    public void DrawDepth(GlProgram depth)
    {
        if (faceVertices == 0) return;
        GL.BindVertexArray(faceVao);
        SurfacePass.DrawDepth(depth, textures, ranges, faceVertices);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        foreach (int vao in new[] { faceVao, edgeVao, pointVao, gridVao, overlayVao, proxyVao, normalVao }) GL.DeleteVertexArray(vao);
        foreach (int vbo in new[] { geomVbo, stateVbo, edgeVbo, pointVbo, gridVbo, overlayVbo, proxyVbo, normalVbo }) GL.DeleteBuffer(vbo);
    }
}
