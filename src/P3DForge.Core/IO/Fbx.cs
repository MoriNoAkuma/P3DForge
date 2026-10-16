using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace P3DForge.Core.IO;

/// <summary>
/// Binary FBX (Blender, Maya, 3ds Max…; versions 7.x) mesh import: every Mesh geometry placed by its Model's
/// transform chain (translation, pre-rotation, rotation XYZ, scaling, geometric offset), the file's axis system and
/// units, polygons with all UV layers (layer k → UV set k), and diffuse textures through materials. Each model becomes
/// a named selection. Axes end up as the Arma 3 Blender toolbox puts them: Y-up right-handed → P3D (x, y, −z).
/// ASCII FBX is not supported (export binary).
/// </summary>
public static class Fbx
{
    sealed class Node(string name)
    {
        public string Name = name;
        public List<object> Props = [];
        public List<Node> Children = [];
        public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
        public IEnumerable<Node> All(string name) => Children.Where(c => c.Name == name);
    }

    public static bool IsBinary(ReadOnlySpan<byte> data) => data.Length > 27 && data[..18].SequenceEqual("Kaydara FBX Binary"u8);

    public static ImportResult Read(byte[] data, ImportOptions options)
    {
        if (!IsBinary(data))
            throw new InvalidDataException(Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64)).Contains("FBX")
                ? "ASCII FBX is not supported — export the FBX as binary" : "Not an FBX file");
        int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(23));
        var root = new Node("");
        int pos = 27;
        while (pos < data.Length)
        {
            var n = ReadNode(data, ref pos, version);
            if (n is null) break;
            root.Children.Add(n);
        }
        return Build(root, options);
    }

    // ---- parsing ---------------------------------------------------------------------------------

    static Node? ReadNode(byte[] d, ref int pos, int version)
    {
        bool wide = version >= 7500;
        long end = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
        long count = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(pos + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
        int head = wide ? 24 : 12;
        int nameLen = d[pos + head];
        if (end == 0) // null record: end of a node list
        {
            pos += head + 1;
            return null;
        }
        if (end > d.Length || end <= pos) throw new InvalidDataException($"Bad FBX node at byte {pos}");
        var node = new Node(Encoding.ASCII.GetString(d, pos + head + 1, nameLen));
        int p = pos + head + 1 + nameLen;
        for (long i = 0; i < count; i++) node.Props.Add(ReadProperty(d, ref p));
        while (p < end)
        {
            var child = ReadNode(d, ref p, version);
            if (child is null) break;
            node.Children.Add(child);
        }
        pos = (int)end;
        return node;
    }

    static object ReadProperty(byte[] d, ref int p)
    {
        char type = (char)d[p++];
        switch (type)
        {
            case 'Y': p += 2; return (long)BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p - 2));
            case 'C': p += 1; return d[p - 1] != 0;
            case 'I': p += 4; return (long)BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p - 4));
            case 'F': p += 4; return (double)BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p - 4));
            case 'D': p += 8; return BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(p - 8));
            case 'L': p += 8; return BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p - 8));
            case 'S':
            case 'R':
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
                p += 4 + len;
                return type == 'S' ? Encoding.UTF8.GetString(d, p - len, len) : d.AsSpan(p - len, len).ToArray();
            }
            case 'f' or 'd' or 'l' or 'i' or 'b':
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
                int encoding = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
                int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8));
                p += 12;
                int elem = type is 'd' or 'l' ? 8 : type == 'b' ? 1 : 4;
                byte[] raw;
                if (encoding == 1)
                {
                    raw = new byte[n * elem];
                    using var z = new ZLibStream(new MemoryStream(d, p, size), CompressionMode.Decompress);
                    z.ReadExactly(raw);
                }
                else raw = d.AsSpan(p, n * elem).ToArray();
                p += size;
                switch (type)
                {
                    case 'd':
                    {
                        var a = new double[n];
                        for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
                        return a;
                    }
                    case 'f':
                    {
                        var a = new double[n];
                        for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
                        return a;
                    }
                    case 'i':
                    {
                        var a = new long[n];
                        for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
                        return a;
                    }
                    case 'l':
                    {
                        var a = new long[n];
                        for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(i * 8));
                        return a;
                    }
                    default: return raw;
                }
            }
            default: throw new InvalidDataException($"Unknown FBX property type '{type}' at byte {p - 1}");
        }
    }

    // ---- scene -----------------------------------------------------------------------------------

    static double[] Doubles(Node? n) => n?.Props.FirstOrDefault() as double[] ?? [];
    static long[] Longs(Node? n) => n?.Props.FirstOrDefault() as long[] ?? [];
    static string Str(Node? n) => n?.Props.FirstOrDefault() as string ?? "";
    static long Id(Node n) => n.Props.Count > 0 && n.Props[0] is long l ? l : 0;

    /// <summary>"Name\0\x01Class" → "Name".</summary>
    static string NameOf(Node n)
    {
        var s = n.Props.Count > 1 ? n.Props[1] as string ?? "" : "";
        int z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    /// <summary>Properties70 → name → values.</summary>
    static Dictionary<string, List<object>> P70(Node? n)
    {
        var r = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        if (n?.Child("Properties70") is not { } p) return r;
        foreach (var e in p.All("P"))
            if (e.Props.Count > 0 && e.Props[0] is string key) r[key] = e.Props.Skip(4).ToList();
        return r;
    }

    static double Num(Dictionary<string, List<object>> p, string key, double fallback) =>
        p.TryGetValue(key, out var v) && v.Count > 0 ? v[0] switch { double d => d, long l => l, _ => fallback } : fallback;

    static Vector3 Vec(Dictionary<string, List<object>> p, string key, Vector3 fallback)
    {
        if (!p.TryGetValue(key, out var v) || v.Count < 3) return fallback;
        static float F(object o) => o switch { double d => (float)d, long l => l, _ => 0 };
        return new Vector3(F(v[0]), F(v[1]), F(v[2]));
    }

    static Matrix4x4 EulerXyz(Vector3 deg)
    {
        const float k = MathF.PI / 180;
        // FBX eEulerXYZ: X first, then Y, then Z (row vectors: Rx · Ry · Rz)
        return Matrix4x4.CreateRotationX(deg.X * k) * Matrix4x4.CreateRotationY(deg.Y * k) * Matrix4x4.CreateRotationZ(deg.Z * k);
    }

    static ImportResult Build(Node root, ImportOptions options)
    {
        var warnings = new List<string>();
        var settings = P70(root.Child("GlobalSettings"));
        int up = (int)Num(settings, "UpAxis", 1), upSign = (int)Num(settings, "UpAxisSign", 1);
        int front = (int)Num(settings, "FrontAxis", 2), frontSign = (int)Num(settings, "FrontAxisSign", 1);
        int coord = (int)Num(settings, "CoordAxis", 0), coordSign = (int)Num(settings, "CoordAxisSign", 1);
        double unit = Num(settings, "UnitScaleFactor", 1) / 100.0 * options.Scale; // FBX units are centimetres × factor
        if (new HashSet<int> { up, front, coord }.Count != 3) (up, upSign, front, frontSign, coord, coordSign) = (1, 1, 2, 1, 0, 1);

        var objects = root.Child("Objects")?.Children ?? [];
        var byId = new Dictionary<long, Node>();
        foreach (var o in objects) byId[Id(o)] = o;
        var parentOf = new Dictionary<long, long>(); // model → parent model
        var geometryOf = new Dictionary<long, long>(); // geometry → model
        var materialsOf = new Dictionary<long, List<long>>(); // model → materials (in connection order)
        var textureOf = new Dictionary<long, long>(); // material → diffuse texture
        foreach (var c in root.Child("Connections")?.All("C") ?? [])
        {
            if (c.Props.Count < 3 || c.Props[1] is not long child || c.Props[2] is not long parent) continue;
            if (!byId.TryGetValue(child, out var cn)) continue;
            byId.TryGetValue(parent, out var pn);
            switch (cn.Name)
            {
                case "Geometry" when pn?.Name == "Model": geometryOf[child] = parent; break;
                case "Model" when pn?.Name == "Model": parentOf[child] = parent; break;
                case "Material" when pn?.Name == "Model":
                    if (!materialsOf.TryGetValue(parent, out var list)) materialsOf[parent] = list = [];
                    list.Add(child);
                    break;
                case "Texture" when pn?.Name == "Material":
                {
                    string prop = c.Props.Count > 3 ? c.Props[3] as string ?? "" : "";
                    if (prop.Length == 0 || prop.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) || !textureOf.ContainsKey(parent))
                        textureOf[parent] = child;
                    break;
                }
            }
        }

        var worldCache = new Dictionary<long, Matrix4x4>();
        Matrix4x4 World(long model)
        {
            if (worldCache.TryGetValue(model, out var w)) return w;
            var p = P70(byId[model]);
            var local = Matrix4x4.CreateScale(Vec(p, "Lcl Scaling", Vector3.One))
                        * EulerXyz(Vec(p, "Lcl Rotation", Vector3.Zero))
                        * EulerXyz(Vec(p, "PreRotation", Vector3.Zero))
                        * Matrix4x4.CreateTranslation(Vec(p, "Lcl Translation", Vector3.Zero));
            if (Num(p, "RotationOrder", 0) != 0) warnings.Add($"{NameOf(byId[model])}: rotation order other than XYZ is read as XYZ");
            w = parentOf.TryGetValue(model, out long parent) && byId.ContainsKey(parent) ? local * World(parent) : local;
            return worldCache[model] = w;
        }

        var b = new FragmentBuilder();
        var textures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int meshes = 0;
        foreach (var geom in objects.Where(o => o.Name == "Geometry" && o.Props.Count > 2 && o.Props[2] as string == "Mesh"))
        {
            long gid = Id(geom);
            Matrix4x4 m = Matrix4x4.Identity;
            string name = NameOf(geom);
            List<long> mats = [];
            if (geometryOf.TryGetValue(gid, out long model))
            {
                var p = P70(byId[model]);
                var geometric = Matrix4x4.CreateScale(Vec(p, "GeometricScaling", Vector3.One))
                                * EulerXyz(Vec(p, "GeometricRotation", Vector3.Zero))
                                * Matrix4x4.CreateTranslation(Vec(p, "GeometricTranslation", Vector3.Zero));
                m = geometric * World(model);
                name = NameOf(byId[model]);
                mats = materialsOf.GetValueOrDefault(model) ?? [];
            }
            meshes++;
            int group = b.Group(name);
            var verts = Doubles(geom.Child("Vertices"));
            var index = Longs(geom.Child("PolygonVertexIndex"));
            var points = new int[verts.Length / 3];
            for (int i = 0; i < points.Length; i++)
            {
                var v = Vector3.Transform(new Vector3((float)verts[i * 3], (float)verts[i * 3 + 1], (float)verts[i * 3 + 2]), m);
                Span<float> a = [v.X, v.Y, v.Z];
                // file axes → Y-up right-handed → P3D (x, y, −z)
                var yUp = new Vector3(coordSign * a[coord], upSign * a[up], frontSign * a[front]);
                points[i] = b.AddPoint(new Vector3(yUp.X, yUp.Y, -yUp.Z) * (float)unit);
            }

            var uvLayers = geom.All("LayerElementUV").OrderBy(l => l.Props.FirstOrDefault() is long k ? k : 0).ToList();
            b.UvSets = Math.Max(b.UvSets, Math.Max(1, uvLayers.Count));
            var matLayer = geom.Child("LayerElementMaterial");
            var matIndex = Longs(matLayer?.Child("Materials"));
            bool matAllSame = Str(matLayer?.Child("MappingInformationType")) != "ByPolygon";
            string TextureFor(int polygon)
            {
                int mi = matIndex.Length == 0 ? 0 : (int)(matAllSame ? matIndex[0] : polygon < matIndex.Length ? matIndex[polygon] : 0);
                if (mi < 0 || mi >= mats.Count || !textureOf.TryGetValue(mats[mi], out long tid) || !byId.TryGetValue(tid, out var tn)) return "";
                var file = Str(tn.Child("FileName"));
                if (file.Length == 0) file = Str(tn.Child("RelativeFilename"));
                if (file.Length == 0) return "";
                return options.Texture?.Invoke(file) ?? file;
            }

            int polygon = 0, start = 0;
            for (int i = 0; i < index.Length; i++)
            {
                if (index[i] >= 0) continue;
                int n = i - start + 1;
                var pts = new int[n];
                var uv = new float[uvLayers.Count == 0 ? 1 : uvLayers.Count][];
                for (int s = 0; s < uv.Length; s++) uv[s] = new float[n * 2];
                bool ok = true;
                for (int j = 0; j < n; j++)
                {
                    long vi = index[start + j];
                    if (vi < 0) vi = ~vi;
                    if (vi >= points.Length)
                    {
                        ok = false;
                        break;
                    }
                    pts[j] = points[vi];
                    for (int s = 0; s < uvLayers.Count; s++)
                        if (Uv(uvLayers[s], start + j, (int)vi, polygon) is { } t)
                        {
                            uv[s][j * 2] = t.X;
                            uv[s][j * 2 + 1] = 1 - t.Y;
                        }
                }
                if (ok)
                {
                    var tex = TextureFor(polygon);
                    if (tex.Length > 0) textures.Add(tex);
                    b.AddPolygon(pts, uv, tex, group);
                }
                else if (warnings.Count < 20) warnings.Add($"{name}: polygon {polygon} refers to a missing vertex");
                polygon++;
                start = i + 1;
            }
        }
        if (meshes == 0) warnings.Add("the file has no meshes");
        return new ImportResult(b.Build(), meshes, b.Polygons, b.Triangulated, [.. textures], warnings);
    }

    /// <summary>UV of one polygon corner from a LayerElementUV (by polygon vertex / control point / polygon; direct or indexed).</summary>
    static Vector2? Uv(Node layer, int corner, int vertex, int polygon)
    {
        var uv = Doubles(layer.Child("UV"));
        var idx = Longs(layer.Child("UVIndex"));
        string mapping = Str(layer.Child("MappingInformationType"));
        bool indexed = Str(layer.Child("ReferenceInformationType")) is "IndexToDirect" or "Index";
        int at = mapping switch
        {
            "ByControlPoint" or "ByVertice" or "ByVertex" => vertex,
            "ByPolygon" => polygon,
            "AllSame" => 0,
            _ => corner, // ByPolygonVertex
        };
        if (indexed)
        {
            if (at >= idx.Length) return null;
            at = (int)idx[at];
        }
        if (at < 0 || at * 2 + 1 >= uv.Length) return null;
        return new Vector2((float)uv[at * 2], (float)uv[at * 2 + 1]);
    }
}
