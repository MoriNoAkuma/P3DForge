using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using P3DForge.Core.Model;
using P3DForge.Core.Paa;

namespace P3DForge.Core.IO;

/// <summary>
/// Display-only reader for binarized (ODOL) models — the format of every model under P:\DZ (DayZ: version 54), so that
/// vanilla proxies (magazines, optics, furniture…) can be drawn. It turns the chosen LODs into ordinary <see cref="Lod"/>s:
/// one point per render vertex, faces with UV set 0, normals, textures and materials (further UV sets as #UVSet# tags).
/// Named selections, properties, animations and the rest are skipped. Nothing here can write ODOL, and the result is
/// not meant to be saved as a source model.
/// Layout: community documentation and the MIT-licensed reader of github.com/jetelain/bis-file-formats (BIS.P3D/ODOL).
/// </summary>
public static class Odol
{
    /// <summary>Oldest / newest versions the reader understands (DayZ writes 54; Arma 3 up to 73 without encryption).</summary>
    public const int MinVersion = 44, MaxVersion = 73;

    /// <summary>The game's own files are version 54; DayZ Tools write 53 (older), 55 and 56 (current binarize). Their
    /// header and materials differ from Arma's files of the same numbers.</summary>
    public const int DayZVersion = 54;

    static bool IsDayZ(int v) => v is >= 53 and <= 56;

    /// <summary>Debugging aid: called with a step name and the reader position while a LOD is read.</summary>
    public static Action<string, int>? Trace { get; set; }

    public static bool IsOdol(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..4].SequenceEqual("ODOL"u8);

    /// <summary>
    /// Reads the model; LODs for which <paramref name="wantLod"/>(index, resolution) is false stay empty (resolution only).
    /// Null converts every LOD. Throws <see cref="InvalidDataException"/> on files it cannot read.
    /// </summary>
    public static P3DModel Read(byte[] data, Func<int, float, bool>? wantLod = null)
    {
        if (!IsOdol(data)) throw new InvalidDataException("Not an ODOL file");
        var r = new Reader(data, 4);
        int version = r.I32();
        r.Version = version;
        if (version < MinVersion || version > MaxVersion) throw new InvalidDataException($"ODOL version {version} is not supported");
        if (version >= 59) r.U32(); // app id
        if (version >= 58) r.Z(); // muzzle flash
        int n = r.I32();
        if (n is <= 0 or > 64) throw new InvalidDataException($"Bad LOD count {n}");
        var res = new float[n];
        for (int i = 0; i < n; i++) res[i] = r.F32();

        var boundingCenter = ReadModelInfo(r, version, n);
        if (r.Bool()) SkipAnimations(r, version);
        var starts = new uint[n];
        var ends = new uint[n];
        for (int i = 0; i < n; i++) starts[i] = r.U32();
        for (int i = 0; i < n; i++) ends[i] = r.U32();
        for (int i = 0; i < n; i++)
            // only the start matters for reading (some tools write ends past the end of the file)
            if (starts[i] >= data.Length)
                throw new InvalidDataException($"Bad LOD address table (LOD {i}: {starts[i]}..{ends[i]} of {data.Length})");

        var lods = ImmutableArray.CreateBuilder<Lod>(n);
        for (int i = 0; i < n; i++)
        {
            if (wantLod is not null && !wantLod(i, res[i]))
            {
                lods.Add(Lod.Empty(res[i]));
                continue;
            }
            lods.Add(ReadLod(new Reader(data, (int)starts[i]) { Version = version }, version, res[i], boundingCenter));
        }
        return new P3DModel(lods.MoveToImmutable());
    }

    /// <summary>
    /// Reads every LOD completely and compares where each one ends with the file's LOD address table; returns the
    /// problems (empty when the layout was understood exactly). For tests.
    /// </summary>
    public static List<string> Verify(byte[] data)
    {
        var problems = new List<string>();
        try
        {
            var r = new Reader(data, 4);
            int version = r.I32();
            r.Version = version;
            if (version >= 59) r.U32();
            if (version >= 58) r.Z();
            int n = r.I32();
            var res = new float[n];
            for (int i = 0; i < n; i++) res[i] = r.F32();
            var center = ReadModelInfo(r, version, n);
            Trace?.Invoke("model info", r.Position);
            if (r.Bool()) SkipAnimations(r, version);
            Trace?.Invoke("animations", r.Position);
            var starts = new uint[n];
            var ends = new uint[n];
            for (int i = 0; i < n; i++) starts[i] = r.U32();
            for (int i = 0; i < n; i++) ends[i] = r.U32();
            for (int i = 0; i < n; i++)
            {
                try
                {
                    ReadLod(new Reader(data, (int)starts[i]) { Version = version }, version, res[i], center, true, out int end);
                    if (end != ends[i]) problems.Add($"LOD {i} ({res[i]}): ends at {end}, table says {ends[i]}");
                }
                catch (Exception ex)
                {
                    problems.Add($"LOD {i} ({res[i]}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add("header: " + ex.Message);
        }
        return problems;
    }

    /// <summary>Resolutions of the LODs, without reading them.</summary>
    public static float[] Resolutions(byte[] data)
    {
        if (!IsOdol(data)) throw new InvalidDataException("Not an ODOL file");
        var r = new Reader(data, 4);
        int version = r.I32();
        if (version >= 59) r.U32();
        if (version >= 58) r.Z();
        int n = r.I32();
        if (n is <= 0 or > 64) throw new InvalidDataException($"Bad LOD count {n}");
        var res = new float[n];
        for (int i = 0; i < n; i++) res[i] = r.F32();
        return res;
    }

    // ---- header ---------------------------------------------------------------------------------

    /// <summary>Skips the model info; returns the bounding center (the binarizer moved every vertex by minus it).</summary>
    static (float X, float Y, float Z) ReadModelInfo(Reader r, int v, int lodCount)
    {
        bool dayz = IsDayZ(v);
        r.Skip(4 + 4 + 4 + 4 + 4 + 4); // special, bounding sphere, geometry sphere, remarks, and / or hints
        r.Skip(12 + 4 + 4 + 4); // aiming center, color, color type, view density
        r.Skip(24); // bbox
        if (v >= 70) r.Skip(4);
        if (v >= 71) r.Skip(4);
        if (v >= 52) r.Skip(24); // visual bbox
        var center = (r.F32(), r.F32(), r.F32());
        r.Skip(12 + 12 + 36); // geometry center, center of mass, inverse inertia
        r.Skip(4); // auto center, lock auto center, can occlude, can be occluded
        if (v >= 73 || dayz) r.Skip(1); // AI covers
        if (v >= 42) r.Skip(16);
        if (v >= 43) r.Skip(8);
        if (v >= 33) r.Skip(1);
        if (v >= 37) r.Skip(5);
        if (v >= 48) r.Skip(4);
        if (dayz && v >= 56) r.Skip(4); // DayZ Tools 56: 4 more bytes (zero in the files seen)
        if (dayz) r.Skip(4); // a float, FLT_MAX in the files seen
        r.Skip(1); // animated
        // skeleton
        if (r.Z().Length > 0)
        {
            if (v >= 23) r.Skip(1);
            int bones = r.I32();
            for (int i = 0; i < bones; i++)
            {
                r.Z();
                r.Z();
            }
            if (v > 40) r.Z();
        }
        r.Skip(1); // map type
        r.CompressedArray(4); // mass array
        r.Skip(16); // mass, inv mass, armor, inv armor
        if (v >= 72) r.Skip(4);
        // special LOD indices (memory, geometry, fire, view…; DayZ files have one fewer than Arma's), min shadow
        r.Skip(12 + (v >= 53 ? 1 : 0) + (v >= 54 ? 1 : 0) - (dayz ? 1 : 0) + 4);
        if (v >= 38) r.Skip(1);
        r.Z(); // class
        r.Z(); // damage
        r.Skip(1); // frequent
        if (v >= 31) r.Skip(4);
        if (dayz && v >= 55) r.Skip(1);
        if (v >= 57) r.Skip(12 * lodCount);
        return center;
    }

    static void SkipAnimations(Reader r, int v)
    {
        int classes = r.I32();
        var types = new uint[classes];
        for (int i = 0; i < classes; i++)
        {
            uint type = types[i] = r.U32();
            r.Z();
            r.Z();
            r.Skip(16);
            if (v >= 56 && !IsDayZ(v)) r.Skip(8); // Arma 56+ (DayZ Tools 56 has no such field)
            r.Skip(4);
            switch (type)
            {
                case <= 7: r.Skip(8); break;
                case 8: r.Skip(32); break;
                case 9: r.Skip(v >= 55 && !IsDayZ(v) ? 8 : 4); break; // hide value (+ an unused float in Arma 55+)
                default: throw new InvalidDataException($"Unknown animation type {type}");
            }
        }
        int lods = r.I32();
        for (int l = 0; l < lods; l++)
        {
            int bones = r.I32();
            for (int b = 0; b < bones; b++) r.Skip(4 * r.I32());
        }
        for (int l = 0; l < lods; l++)
            for (int k = 0; k < classes; k++)
                if (r.I32() != -1 && types[k] != 8 && types[k] != 9)
                    r.Skip(24);
    }

    // ---- LOD ------------------------------------------------------------------------------------

    sealed record Section(int Lower, int Upper, int Texture, int Material, string? MaterialName);

    static Lod ReadLod(Reader r, int v, float resolution, (float X, float Y, float Z) center) =>
        ReadLod(r, v, resolution, center, false, out _);

    static Lod ReadLod(Reader r, int v, float resolution, (float X, float Y, float Z) center, bool verify, out int end)
    {
        end = -1;
        int proxyCount = r.I32();
        var proxies = new List<(string Path, float[] M, int Index)>();
        for (int i = 0; i < proxyCount; i++)
        {
            string path = r.Z();
            var m = new float[12];
            for (int j = 0; j < 12; j++) m[j] = r.F32();
            int index = r.I32();
            r.Skip(8 + (v >= 40 ? 4 : 0)); // named selection, bone, section
            proxies.Add((path, m, index));
        }
        Trace?.Invoke("proxies", r.Position);
        r.Skip(4 * r.I32()); // sub-skeleton → skeleton
        int sets = r.I32();
        for (int i = 0; i < sets; i++) r.Skip(4 * r.I32());
        if (v >= 50) r.Skip(4);
        else r.CondensedArray(4);
        if (v >= 51) r.Skip(4);
        r.Skip(8 + 36 + 4); // hints, bmin, bmax, bcenter, bradius
        Trace?.Invoke("header", r.Position);
        int textureCount = r.I32();
        var textures = new string[textureCount];
        for (int i = 0; i < textureCount; i++) textures[i] = r.Z();
        Trace?.Invoke("textures", r.Position);
        int materialCount = r.I32();
        var materials = new string[materialCount];
        for (int i = 0; i < materialCount; i++) materials[i] = ReadMaterial(r);
        Trace?.Invoke("materials", r.Position);
        int indexSize = v >= 69 ? 4 : 2;
        r.CompressedArray(indexSize); // point → vertex
        r.CompressedArray(indexSize); // vertex → point

        Trace?.Invoke("vertex maps", r.Position);
        int faceCount = r.I32();
        r.Skip(4 + 2);
        var vertCount = new byte[faceCount];
        var facePoints = new uint[faceCount * 4];
        for (int f = 0; f < faceCount; f++)
        {
            int k = r.U8();
            if (k is < 3 or > 4) throw new InvalidDataException($"Face {f} has {k} vertices");
            vertCount[f] = (byte)k;
            for (int j = 0; j < k; j++) facePoints[f * 4 + j] = indexSize == 4 ? r.U32() : r.U16();
        }

        Trace?.Invoke("polygons", r.Position);
        int sectionCount = r.I32();
        var sections = new Section[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            int lower = r.I32(), upper = r.I32();
            r.Skip(4 + 4 + 4); // min bone, bone count, unused
            int texture = r.I16();
            r.Skip(4); // special
            int material = r.I32();
            string? materialName = material == -1 ? r.Z() : null;
            if (v >= 36)
            {
                r.Skip(4 * r.I32()); // area over texture
                if (v >= 67 && r.I32() >= 1) r.Skip(44);
            }
            else r.Skip(4);
            sections[i] = new Section(lower, upper, texture, material, materialName);
        }

        Trace?.Invoke("sections", r.Position);
        int selections = r.I32();
        for (int i = 0; i < selections; i++)
        {
            r.Z();
            r.CompressedArray(indexSize); // faces
            r.Skip(4 + 1);
            r.CompressedArray(4); // sections
            r.CompressedArray(indexSize); // vertices
            r.Compressed(r.I32()); // weights
        }
        Trace?.Invoke("selections", r.Position);
        int properties = r.I32();
        for (int i = 0; i < properties; i++)
        {
            r.Z();
            r.Z();
        }
        Trace?.Invoke("properties", r.Position);
        int frames = r.I32();
        for (int i = 0; i < frames; i++)
        {
            r.Skip(4);
            r.Skip(12 * r.I32());
        }
        Trace?.Invoke("frames", r.Position);
        r.Skip(4 + 4 + 4 + 1); // color top, color, special, vertex bone ref is simple
        r.Skip(4); // size of the rest
        if (v >= 50) r.CondensedArray(4); // clip flags

        Trace?.Invoke("clip", r.Position);
        var uvSets = new List<float[]> { ReadUvSet(r, v) };
        int uvSetCount = r.I32();
        for (int i = 1; i < uvSetCount; i++) uvSets.Add(ReadUvSet(r, v));

        Trace?.Invoke("uv sets", r.Position);
        var positions = r.CompressedArray(12, out int vertexCount);
        float[] pos = new float[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            pos[i * 3] = BinaryPrimitives.ReadSingleLittleEndian(positions.AsSpan(i * 12)) + center.X;
            pos[i * 3 + 1] = BinaryPrimitives.ReadSingleLittleEndian(positions.AsSpan(i * 12 + 4)) + center.Y;
            pos[i * 3 + 2] = BinaryPrimitives.ReadSingleLittleEndian(positions.AsSpan(i * 12 + 8)) + center.Z;
        }
        float[] normals;
        if (v >= 45)
        {
            var packed = r.CondensedArray(4, out int normalCount);
            normals = new float[vertexCount * 3];
            for (int i = 0; i < Math.Min(normalCount, vertexCount); i++)
            {
                uint p = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(packed.Length == 4 ? 0 : i * 4));
                normals[i * 3] = Unpack(p);
                normals[i * 3 + 1] = Unpack(p >> 10);
                normals[i * 3 + 2] = Unpack(p >> 20);
            }
        }
        else
        {
            var raw = r.CondensedArray(12, out int normalCount);
            normals = new float[vertexCount * 3];
            for (int i = 0; i < Math.Min(normalCount, vertexCount) * 3; i++)
                normals[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(raw.Length == 12 ? i % 3 * 4 : i * 4));
        }
        // ST coordinates and bone references are not needed for display; read only to verify the layout.
        if (verify)
        {
            r.CompressedArray(v >= 45 ? 8 : 24);
            r.CompressedArray(12);
            r.CompressedArray(32);
            if (v >= 67) r.Skip(4);
            if (v >= 68) r.Skip(1);
            end = r.Position;
        }

        for (int f = 0; f < faceCount; f++)
            for (int j = 0; j < vertCount[f]; j++)
                if (facePoints[f * 4 + j] >= vertexCount)
                    throw new InvalidDataException($"Face {f} uses vertex {facePoints[f * 4 + j]} of {vertexCount}");

        // Per-face texture / material from the sections (face ranges are byte offsets: 8 per triangle, +2 or +4 for a quad).
        var strings = ImmutableArray.Create("");
        var faceTexture = new uint[faceCount];
        var faceMaterial = new uint[faceCount];
        var offsets = new int[faceCount];
        for (int f = 0, o = 0; f < faceCount; f++)
        {
            offsets[f] = o;
            o += indexSize == 2 ? 8 : 16;
            if (vertCount[f] == 4) o += indexSize;
        }
        foreach (var s in sections)
        {
            uint tex = s.Texture >= 0 && s.Texture < textures.Length ? Lod.Intern(ref strings, Clean(textures[s.Texture])) : 0;
            string mat = s.MaterialName ?? (s.Material >= 0 && s.Material < materials.Length ? materials[s.Material] : "");
            uint matIndex = mat.Length > 0 ? Lod.Intern(ref strings, Clean(mat)) : 0;
            int first = Array.BinarySearch(offsets, s.Lower);
            if (first < 0) first = ~first;
            for (int f = first; f < faceCount && offsets[f] < s.Upper; f++)
            {
                faceTexture[f] = tex;
                faceMaterial[f] = matIndex;
            }
        }

        // ODOL polygons run the other way round than MLOD faces; reverse them so the editor's winding rules apply.
        var faceUv = new float[faceCount * 8];
        var faceNormals = new uint[faceCount * 4];
        var uv0 = uvSets[0];
        Span<uint> tmp = stackalloc uint[4];
        for (int f = 0; f < faceCount; f++)
        {
            int k = vertCount[f];
            for (int j = 0; j < k; j++) tmp[j] = facePoints[f * 4 + (k - 1 - j)];
            for (int j = 0; j < k; j++)
            {
                uint vi = tmp[j];
                facePoints[f * 4 + j] = vi;
                faceNormals[f * 4 + j] = vi;
                if (vi * 2 + 1 < uv0.Length)
                {
                    faceUv[f * 8 + j * 2] = uv0[vi * 2];
                    faceUv[f * 8 + j * 2 + 1] = uv0[vi * 2 + 1];
                }
            }
        }
        var tags = ImmutableArray.CreateBuilder<Tag>();
        if (proxies.Count > 0)
            AddProxyTriangles(proxies, center, ref pos, ref normals, ref vertCount, ref facePoints, ref faceNormals, ref faceUv,
                ref faceTexture, ref faceMaterial, tags);
        faceCount = vertCount.Length;
        vertexCount = pos.Length / 3;
        for (int s = 1; s < uvSets.Count; s++)
        {
            var src = uvSets[s];
            var uv = new float[faceCount * 8];
            for (int f = 0; f < faceCount; f++)
                for (int j = 0; j < vertCount[f]; j++)
                {
                    uint vi = facePoints[f * 4 + j];
                    if (vi * 2 + 1 >= src.Length) continue;
                    uv[f * 8 + j * 2] = src[vi * 2];
                    uv[f * 8 + j * 2 + 1] = src[vi * 2 + 1];
                }
            tags.Add(new UVSetTag(1, (uint)s, uv));
        }

        return new Lod
        {
            Resolution = resolution,
            Positions = pos,
            PointFlags = new uint[vertexCount],
            Normals = normals,
            FaceVertCount = vertCount,
            FacePoints = facePoints,
            FaceNormals = faceNormals,
            FaceUV = faceUv,
            FaceFlags = new uint[faceCount],
            FaceTexture = faceTexture,
            FaceMaterial = faceMaterial,
            Strings = strings,
            Tags = tags.ToImmutable(),
        };
    }

    /// <summary>
    /// Proxies are stored as a model path + placement matrix (axes X, Y, Z, origin; binarized coordinates). Each becomes
    /// what an MLOD has: a proxy triangle (origin, origin + 2·Y, origin + Z) and a "proxy:\path.NNN" selection, so
    /// the editor places nested proxies the usual way.
    /// </summary>
    static void AddProxyTriangles(List<(string Path, float[] M, int Index)> proxies, (float X, float Y, float Z) center,
        ref float[] pos, ref float[] normals, ref byte[] vertCount, ref uint[] facePoints, ref uint[] faceNormals, ref float[] faceUv,
        ref uint[] faceTexture, ref uint[] faceMaterial, ImmutableArray<Tag>.Builder tags)
    {
        int points0 = pos.Length / 3, faces0 = vertCount.Length, n = proxies.Count;
        int points = points0 + 3 * n, faces = faces0 + n;
        Array.Resize(ref pos, points * 3);
        Array.Resize(ref normals, points * 3);
        Array.Resize(ref vertCount, faces);
        Array.Resize(ref facePoints, faces * 4);
        Array.Resize(ref faceNormals, faces * 4);
        Array.Resize(ref faceUv, faces * 8);
        Array.Resize(ref faceTexture, faces);
        Array.Resize(ref faceMaterial, faces);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            var (path, m, index) = proxies[i];
            int a = points0 + 3 * i, f = faces0 + i;
            float ox = m[9] + center.X, oy = m[10] + center.Y, oz = m[11] + center.Z;
            float[] tri = [ox, oy, oz, ox + 2 * m[3], oy + 2 * m[4], oz + 2 * m[5], ox + m[6], oy + m[7], oz + m[8]];
            tri.CopyTo(pos, a * 3);
            vertCount[f] = 3;
            for (int j = 0; j < 3; j++) facePoints[f * 4 + j] = faceNormals[f * 4 + j] = (uint)(a + j);
            var clean = path.Trim().Replace('/', '\\');
            if (clean.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
            if (!clean.StartsWith('\\')) clean = "\\" + clean;
            var name = Ops.Proxies.SelectionName(clean, Math.Max(0, index));
            for (int k = 1; !used.Add(name); k++) name = Ops.Proxies.SelectionName(clean, Math.Max(0, index) + 1000 * k);
            var pointSel = new byte[points];
            var faceSel = new byte[faces];
            pointSel[a] = pointSel[a + 1] = pointSel[a + 2] = 1;
            faceSel[f] = 1;
            tags.Add(new SelectionTag(1, name, pointSel, faceSel));
        }
    }

    /// <summary>Game paths are stored lower case, sometimes with a leading backslash.</summary>
    static string Clean(string path) => path.Trim().TrimStart('\\');

    /// <summary>10-bit signed normal component (the binarizer stores the inverted normal, like MLOD files).</summary>
    static float Unpack(uint bits)
    {
        int x = (int)(bits & 0x3FF);
        if (x > 511) x -= 1024;
        return x * (-1f / 511);
    }

    /// <summary>u, v per vertex.</summary>
    static float[] ReadUvSet(Reader r, int v)
    {
        float minU = 0, minV = 0, maxU = 1, maxV = 1;
        bool discrete = v >= 45;
        if (discrete)
        {
            minU = r.F32();
            minV = r.F32();
            maxU = r.F32();
            maxV = r.F32();
        }
        int count = r.I32();
        bool fill = r.Bool();
        int stride = discrete ? 4 : 8;
        var bytes = fill ? r.Bytes(stride) : r.Compressed(count * stride);
        var uv = new float[count * 2];
        double du = maxU - minU, dv = maxV - minV;
        for (int i = 0; i < count; i++)
        {
            int o = fill ? 0 : i * stride;
            if (discrete)
            {
                uv[i * 2] = (float)(1.52587890625E-05 * (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(o)) + short.MaxValue) * du) + minU;
                uv[i * 2 + 1] = (float)(1.52587890625E-05 * (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(o + 2)) + short.MaxValue) * dv) + minV;
            }
            else
            {
                uv[i * 2] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o));
                uv[i * 2 + 1] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o + 4));
            }
        }
        return uv;
    }

    /// <summary>An embedded material; returns its .rvmat path.</summary>
    static string ReadMaterial(Reader r)
    {
        string name = r.Z();
        uint mv = r.U32();
        // colours, specular power, extra parameters of DayZ's material versions (v16: 56 bytes, v20: 104), then
        // pixel / vertex shader, main light, fog mode
        r.Skip(6 * 16 + 4);
        r.Skip(ExtraMaterialBytes(r, mv) + 4 * 4);
        if (mv == 3) r.Skip(1);
        if (mv >= 6) r.Z(); // surface
        if (mv >= 4) r.Skip(8);
        int stages = mv > 6 ? r.I32() : 0;
        int texGens = mv > 8 ? r.I32() : stages;
        void Stage()
        {
            if (mv >= 5) r.Skip(4);
            r.Z();
            if (mv >= 8) r.Skip(4);
            if (mv >= 11) r.Skip(1);
        }
        if (mv < 8)
            for (int i = 0; i < stages; i++)
            {
                r.Skip(4 + 48);
                Stage();
            }
        else
        {
            for (int i = 0; i < stages; i++) Stage();
            r.Skip(texGens * (4 + 48));
        }
        if (mv >= 10) Stage();
        return name;
    }

    /// <summary>
    /// Size of the block between the specular power and the shader ids. Known for DayZ's versions; for others the
    /// first size after which the rest of the material reads sensibly (small shader ids, a printable surface path,
    /// a small stage count) wins.
    /// </summary>
    static int ExtraMaterialBytes(Reader r, uint mv)
    {
        int known = mv switch { >= 20 => 104, 16 => 56, < 12 => 0, _ => -1 };
        if (known >= 0 && Plausible(r.Fork(), mv, known)) return known;
        for (int extra = 0; extra <= 256; extra += 4)
            if (Plausible(r.Fork(), mv, extra)) return extra;
        return Math.Max(known, 0);
    }

    static bool Plausible(Reader r, uint mv, int extra)
    {
        try
        {
            r.Skip(extra);
            uint ps = r.U32(), vs = r.U32();
            r.Skip(8); // main light, fog mode
            if (ps > 4096 || vs > 4096) return false;
            if (mv == 3) r.Skip(1);
            if (mv >= 6)
            {
                var surface = r.Z();
                if (surface.Length > 260 || surface.Any(c => c < 32 || c > 126)) return false;
            }
            if (mv >= 4) r.Skip(8);
            int stages = mv > 6 ? r.I32() : 0;
            int texGens = mv > 8 ? r.I32() : stages;
            if (stages is < 0 or > 64 || texGens is < 0 or > 64) return false;
            if (mv >= 8 && stages > 0)
            {
                if (mv >= 5 && r.U32() > 64) return false; // texture filter of the first stage
                var tex = r.Z();
                return tex.Length <= 260 && tex.All(c => c >= 32 && c <= 126);
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // ---- primitive reader -------------------------------------------------------------------------

    sealed class Reader(byte[] data, int pos)
    {
        public int Version;
        int p = pos;
        public int Position => p;

        /// <summary>An independent reader at the same position (for looking ahead).</summary>
        public Reader Fork() => new(data, p) { Version = Version };

        void Need(int n)
        {
            if (n < 0 || p + n > data.Length) throw new InvalidDataException($"Unexpected end of the ODOL file at byte {p}");
        }

        public void Skip(int n)
        {
            Need(n);
            p += n;
        }

        public byte U8()
        {
            Need(1);
            return data[p++];
        }

        public bool Bool() => U8() != 0;

        public short I16()
        {
            Need(2);
            var x = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p));
            p += 2;
            return x;
        }

        public ushort U16() => (ushort)I16();

        public int I32()
        {
            Need(4);
            var x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            p += 4;
            return x;
        }

        public uint U32() => (uint)I32();
        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public byte[] Bytes(int n)
        {
            Need(n);
            var b = data.AsSpan(p, n).ToArray();
            p += n;
            return b;
        }

        /// <summary>Zero-terminated string.</summary>
        public string Z()
        {
            int end = Array.IndexOf(data, (byte)0, p);
            if (end < 0) throw new InvalidDataException($"Unterminated string at byte {p}");
            var s = Encoding.Latin1.GetString(data, p, end - p);
            p = end + 1;
            return s;
        }

        /// <summary>A block of <paramref name="size"/> bytes, LZO compressed when it is 1024 bytes or more (v64+: flag byte).</summary>
        public byte[] Compressed(int size)
        {
            if (size < 0 || size > 512 << 20) throw new InvalidDataException($"Bad block size {size} at byte {p}");
            bool compressed = Version >= 64 ? Bool() : size >= 1024;
            if (!compressed) return Bytes(size);
            var dst = new byte[size];
            try
            {
                p += Lzo.Decompress(data.AsSpan(p), dst);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException($"Bad compressed block at byte {p}: {ex.Message}");
            }
            return dst;
        }

        public byte[] CompressedArray(int elementSize) => CompressedArray(elementSize, out _);

        public byte[] CompressedArray(int elementSize, out int count)
        {
            count = I32();
            if (count < 0 || (long)count * elementSize > 512 << 20) throw new InvalidDataException($"Bad array length {count} at byte {p}");
            return Compressed(count * elementSize);
        }

        /// <summary>Count, then one default element or the compressed elements.</summary>
        public byte[] CondensedArray(int elementSize, out int count)
        {
            count = I32();
            if (count < 0) throw new InvalidDataException($"Bad array length {count} at byte {p}");
            return Bool() ? Bytes(elementSize) : Compressed(count * elementSize);
        }

        public byte[] CondensedArray(int elementSize) => CondensedArray(elementSize, out _);
    }
}
