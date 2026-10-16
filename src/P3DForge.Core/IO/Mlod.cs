using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using P3DForge.Core.Model;

namespace P3DForge.Core.IO;

/// <summary>Structural error in an MLOD file.</summary>
public sealed class MlodException(string message, long offset) : Exception($"{message} (at byte {offset})")
{
    public long Offset { get; } = offset;
}

/// <summary>
/// Lossless MLOD (P3DM v28) reader and writer — see docs/FORMAT.md.
/// Guarantee: for every file read without warnings, <c>Write(Read(bytes))</c> reproduces <c>bytes</c> exactly
/// (tag order, active bytes, unused 4th vertex slots, raw property padding, header flags, NaN payloads).
/// </summary>
public static class Mlod
{
    const uint SigMlod = 0x444F4C4D; // "MLOD"
    const uint SigP3dm = 0x4D443350; // "P3DM"
    const uint SigTagg = 0x47474154; // "TAGG"
    const uint SigSp3x = 0x58335053; // "SP3X"

    /// <summary>Latin-1: 1 byte = 1 char, so arbitrary bytes survive a round-trip.</summary>
    public static readonly Encoding Latin1 = Encoding.Latin1;

    // ---------------------------------------------------------------------------------------------
    // Reader
    // ---------------------------------------------------------------------------------------------

    /// <summary>Parses an MLOD file. Non-fatal problems go to <paramref name="warnings"/>.</summary>
    public static P3DModel Read(ReadOnlySpan<byte> data, List<string>? warnings = null)
    {
        var r = new Reader(data, warnings);
        return r.ReadModel();
    }

    public static P3DModel ReadFile(string path, List<string>? warnings = null) => Read(File.ReadAllBytes(path), warnings);

    ref struct Reader(ReadOnlySpan<byte> data, List<string>? warnings)
    {
        readonly ReadOnlySpan<byte> d = data;
        int o;

        void Need(long n, string what)
        {
            if (n < 0 || o + n > d.Length) throw new MlodException($"Unexpected end of file while reading {what}", o);
        }

        uint U32()
        {
            Need(4, "uint32");
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
            o += 4;
            return v;
        }

        int AsciizEnd()
        {
            int e = d[o..].IndexOf((byte)0);
            if (e < 0) throw new MlodException("Unterminated string", o);
            return o + e;
        }

        void Warn(string m) => warnings?.Add(m);

        public P3DModel ReadModel()
        {
            if (d.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(d) != SigMlod)
            {
                bool odol = d.Length >= 4 && d[..4].SequenceEqual("ODOL"u8);
                throw new MlodException(odol ? "Binarized (ODOL) model — only MLOD source models can be edited" : "Not an MLOD file", 0);
            }
            o = 4;
            uint version = U32();
            uint lodCount = U32();
            if (lodCount > 10000) throw new MlodException($"Implausible LOD count {lodCount}", 8);
            var lods = ImmutableArray.CreateBuilder<Lod>((int)lodCount);
            for (int li = 0; li < lodCount; li++) lods.Add(ReadLod(li));
            if (o != d.Length) Warn($"{d.Length - o} trailing bytes after the last LOD (dropped)");
            return new P3DModel(lods.MoveToImmutable(), version);
        }

        Lod ReadLod(int li)
        {
            Need(28, "LOD header");
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
            if (sig == SigSp3x) throw new MlodException("Legacy SP3X LODs are not supported", o);
            if (sig != SigP3dm) throw new MlodException($"LOD {li}: bad signature", o);
            o += 4;
            uint major = U32(), minor = U32();
            int nPoints = checked((int)U32()), nNormals = checked((int)U32()), nFaces = checked((int)U32());
            uint headerFlags = U32();
            if (major != 28) Warn($"LOD {li}: unexpected P3DM version {major}.{minor}");

            // Points: copy float BIT PATTERNS (never through float arithmetic) to keep NaN payloads.
            Need((long)nPoints * 16, "points");
            var raw = MemoryMarshal.Cast<byte, uint>(d.Slice(o, nPoints * 16));
            var positions = new float[nPoints * 3];
            var posBits = MemoryMarshal.Cast<float, uint>(positions.AsSpan());
            var pointFlags = new uint[nPoints];
            for (int i = 0; i < nPoints; i++)
            {
                posBits[i * 3] = raw[i * 4];
                posBits[i * 3 + 1] = raw[i * 4 + 1];
                posBits[i * 3 + 2] = raw[i * 4 + 2];
                pointFlags[i] = raw[i * 4 + 3];
            }
            o += nPoints * 16;

            Need((long)nNormals * 12, "normals");
            var normals = new float[nNormals * 3];
            d.Slice(o, nNormals * 12).CopyTo(MemoryMarshal.AsBytes(normals.AsSpan()));
            o += nNormals * 12;

            var fa = FaceArrays.Allocate(nFaces);
            var uvBits = MemoryMarshal.Cast<float, uint>(fa.UV.AsSpan());
            var strings = ImmutableArray.CreateBuilder<string>();
            strings.Add("");
            var pool = new Dictionary<string, uint>(StringComparer.Ordinal) { [""] = 0 };
            int lastTexStart = -1, lastTexLen = 0, lastMatStart = -1, lastMatLen = 0;
            uint lastTex = 0, lastMat = 0;
            for (int f = 0; f < nFaces; f++)
            {
                Need(72, $"face {f}");
                uint nv = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
                if (nv is not (3 or 4)) throw new MlodException($"LOD {li} face {f}: {nv} vertices (only 3 or 4 allowed)", o);
                fa.VertCount[f] = (byte)nv;
                var rec = MemoryMarshal.Cast<byte, uint>(d.Slice(o + 4, 68));
                for (int k = 0; k < 4; k++)
                {
                    fa.Points[f * 4 + k] = rec[k * 4];
                    fa.Normals[f * 4 + k] = rec[k * 4 + 1];
                    uvBits[f * 8 + k * 2] = rec[k * 4 + 2];
                    uvBits[f * 8 + k * 2 + 1] = rec[k * 4 + 3];
                }
                fa.Flags[f] = rec[16];
                o += 72;

                int e = AsciizEnd(), n = e - o;
                if (lastTexStart >= 0 && n == lastTexLen && d.Slice(o, n).SequenceEqual(d.Slice(lastTexStart, n))) fa.Texture[f] = lastTex;
                else fa.Texture[f] = lastTex = n == 0 ? 0 : Intern(d.Slice(o, n), strings, pool);
                lastTexStart = o;
                lastTexLen = n;
                o = e + 1;

                e = AsciizEnd();
                n = e - o;
                if (lastMatStart >= 0 && n == lastMatLen && d.Slice(o, n).SequenceEqual(d.Slice(lastMatStart, n))) fa.Material[f] = lastMat;
                else fa.Material[f] = lastMat = n == 0 ? 0 : Intern(d.Slice(o, n), strings, pool);
                lastMatStart = o;
                lastMatLen = n;
                o = e + 1;
            }
            for (int f = 0; f < nFaces; f++)
            {
                bool bad = false;
                for (int k = 0; k < fa.VertCount[f]; k++)
                    if (fa.Points[f * 4 + k] >= nPoints) bad = true;
                if (bad)
                {
                    Warn($"LOD {li} face {f}: point index out of range ({nPoints} points)");
                    break;
                }
            }

            Need(4, "TAGG");
            if (BinaryPrimitives.ReadUInt32LittleEndian(d[o..]) != SigTagg) throw new MlodException($"LOD {li}: missing TAGG section", o);
            o += 4;

            var tags = ImmutableArray.CreateBuilder<Tag>();
            while (true)
            {
                Need(1, "tag");
                byte active = d[o++];
                int ne = AsciizEnd();
                string name = Latin1.GetString(d[o..ne]);
                o = ne + 1;
                int size = checked((int)U32());
                Need(size, $"tag \"{name}\" data");
                var data = d.Slice(o, size);
                o += size;
                if (name == TagNames.EndOfFile)
                {
                    if (size != 0) Warn($"LOD {li}: #EndOfFile# carries {size} bytes of data (dropped)");
                    break;
                }
                tags.Add(ParseTag(data, active, name, nPoints, fa, li));
            }

            Need(4, "resolution");
            float resolution = BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
            o += 4;

            return new Lod
            {
                Resolution = resolution,
                Major = major,
                Minor = minor,
                HeaderFlags = headerFlags,
                Positions = positions,
                PointFlags = pointFlags,
                Normals = normals,
                Strings = strings.ToImmutable(),
                Tags = tags.ToImmutable(),
            }.WithFaces(fa);
        }

        static uint Intern(ReadOnlySpan<byte> bytes, ImmutableArray<string>.Builder strings, Dictionary<string, uint> pool)
        {
            string s = Latin1.GetString(bytes);
            if (!pool.TryGetValue(s, out uint idx))
            {
                idx = (uint)strings.Count;
                strings.Add(s);
                pool[s] = idx;
            }
            return idx;
        }

        Tag ParseTag(ReadOnlySpan<byte> data, byte active, string name, int nPoints, FaceArrays fa, int li)
        {
            int nFaces = fa.Count;
            int size = data.Length;

            if (!name.StartsWith('#'))
            {
                if (size != nPoints + nFaces) return Unknown(data, active, name, li, $"has {size} bytes, expected {nPoints + nFaces}");
                return new SelectionTag(active, name, data[..nPoints].ToArray(), data[nPoints..].ToArray());
            }

            switch (name)
            {
                case TagNames.Property:
                {
                    if (size != 128) return Unknown(data, active, name, li, $"has {size} bytes, expected 128");
                    var (key, kOk) = Field(data[..64]);
                    var (value, vOk) = Field(data[64..]);
                    return new PropertyTag(active, key, value, kOk && vOk ? null : data.ToArray());
                }
                case TagNames.UVSet:
                {
                    if (size < 4) return Unknown(data, active, name, li, "is too short");
                    int used = 0;
                    for (int f = 0; f < nFaces; f++) used += fa.VertCount[f];
                    if (size != 4 + used * 8) return Unknown(data, active, name, li, $"has {size} bytes, expected {4 + used * 8}");
                    uint stage = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    var src = MemoryMarshal.Cast<byte, uint>(data[4..]);
                    var uv = new float[nFaces * 8];
                    var bits = MemoryMarshal.Cast<float, uint>(uv.AsSpan());
                    int s = 0;
                    for (int f = 0; f < nFaces; f++)
                        for (int k = 0; k < fa.VertCount[f]; k++)
                        {
                            bits[f * 8 + k * 2] = src[s++];
                            bits[f * 8 + k * 2 + 1] = src[s++];
                        }
                    return new UVSetTag(active, stage, uv);
                }
                case TagNames.Mass:
                    if (size != nPoints * 4) return Unknown(data, active, name, li, $"has {size} bytes, expected {nPoints * 4}");
                    return new MassTag(active, MemoryMarshal.Cast<byte, float>(data).ToArray());
                case TagNames.SharpEdges:
                    if (size % 8 != 0) return Unknown(data, active, name, li, "size is not a multiple of 8");
                    return new SharpEdgesTag(active, MemoryMarshal.Cast<byte, uint>(data).ToArray());
                case TagNames.Selected:
                case TagNames.Hidden:
                case TagNames.Lock:
                {
                    if (size != nPoints + nFaces) return Unknown(data, active, name, li, $"has {size} bytes, expected {nPoints + nFaces}");
                    var kind = name == TagNames.Selected ? StateKind.Selected : name == TagNames.Hidden ? StateKind.Hidden : StateKind.Lock;
                    return new StateTag(active, kind, data[..nPoints].ToArray(), data[nPoints..].ToArray());
                }
                case TagNames.Animation:
                    if (size != 4 + nPoints * 12) return Unknown(data, active, name, li, $"has {size} bytes, expected {4 + nPoints * 12}");
                    return new AnimationTag(active, BinaryPrimitives.ReadSingleLittleEndian(data), MemoryMarshal.Cast<byte, float>(data[4..]).ToArray());
                default:
                    return Unknown(data, active, name, li, null);
            }
        }

        Tag Unknown(ReadOnlySpan<byte> data, byte active, string name, int li, string? why)
        {
            if (why != null) Warn($"LOD {li}: tag \"{name}\" {why}; kept verbatim");
            return new UnknownTag(active, name, data.ToArray());
        }

        /// <summary>Reads a 64-byte NUL-padded field; canonical = has a NUL and only zeros after it.</summary>
        static (string Text, bool Canonical) Field(ReadOnlySpan<byte> f)
        {
            int e = f.IndexOf((byte)0);
            if (e < 0) return (Latin1.GetString(f), false);
            bool canonical = !f[(e + 1)..].ContainsAnyExcept((byte)0);
            return (Latin1.GetString(f[..e]), canonical);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Writer
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> if the LOD's arrays are inconsistent (which would produce a
    /// corrupt file). The writer runs this for every LOD before writing anything.
    /// </summary>
    public static void AssertIntegrity(Lod lod, string label = "LOD")
    {
        int n = lod.PointCount, f = lod.FaceCount;
        void Fail(string m) => throw new InvalidDataException($"{label}: {m}");
        if (lod.Positions.Length != n * 3) Fail($"positions length {lod.Positions.Length} != 3×{n}");
        if (lod.Normals.Length % 3 != 0) Fail("normals length is not a multiple of 3");
        if (lod.FacePoints.Length != f * 4 || lod.FaceNormals.Length != f * 4) Fail("face point/normal arrays length mismatch");
        if (lod.FaceUV.Length != f * 8) Fail("faceUV length mismatch");
        if (lod.FaceFlags.Length != f || lod.FaceTexture.Length != f || lod.FaceMaterial.Length != f) Fail("face array length mismatch");
        int ns = lod.Strings.Length;
        if (ns == 0 || lod.Strings[0] != "") Fail("string pool must start with \"\"");
        for (int i = 0; i < f; i++)
        {
            int nv = lod.FaceVertCount[i];
            if (nv is not (3 or 4)) Fail($"face {i} has {nv} vertices");
            for (int k = 0; k < nv; k++)
                if (lod.FacePoints[i * 4 + k] >= n) Fail($"face {i} references point {lod.FacePoints[i * 4 + k]} of {n}");
            if (lod.FaceTexture[i] >= ns || lod.FaceMaterial[i] >= ns) Fail($"face {i} has an invalid string index");
        }
        foreach (var t in lod.Tags)
        {
            switch (t)
            {
                case SelectionTag s when s.Points.Length != n || s.Faces.Length != f:
                    Fail($"selection \"{s.Name}\" sized for {s.Points.Length}/{s.Faces.Length}, LOD has {n}/{f}");
                    break;
                case StateTag s when s.Points.Length != n || s.Faces.Length != f:
                    Fail($"{s.DiskName} sized for {s.Points.Length}/{s.Faces.Length}, LOD has {n}/{f}");
                    break;
                case UVSetTag u when u.UV.Length != f * 8:
                    Fail($"UV set {u.Stage} sized for {u.UV.Length / 8} faces, LOD has {f}");
                    break;
                case MassTag m when m.Mass.Length != n:
                    Fail($"mass sized for {m.Mass.Length} points, LOD has {n}");
                    break;
                case AnimationTag a when a.Positions.Length != n * 3:
                    Fail($"animation frame sized for {a.Positions.Length / 3} points, LOD has {n}");
                    break;
                case SharpEdgesTag e when e.Edges.Length % 2 != 0:
                    Fail("sharp edges list has odd length");
                    break;
                case PropertyTag p when p.Key.Length > 64 || p.Value.Length > 64:
                    Fail($"property \"{p.Key}\" key/value longer than 64 characters");
                    break;
            }
            if (t is SelectionTag sel && sel.Name.Length == 0) Fail("named selection with an empty name");
        }
    }

    static int UsedSlots(Lod lod)
    {
        int used = 0;
        foreach (byte nv in lod.FaceVertCount) used += nv;
        return used;
    }

    static int TagDataSize(Tag t, int usedSlots) => t switch
    {
        SelectionTag s => s.Points.Length + s.Faces.Length,
        StateTag s => s.Points.Length + s.Faces.Length,
        PropertyTag => 128,
        UVSetTag => 4 + usedSlots * 8,
        MassTag m => m.Mass.Length * 4,
        SharpEdgesTag e => e.Edges.Length * 4,
        AnimationTag a => 4 + a.Positions.Length * 4,
        UnknownTag u => u.Data.Length,
        _ => throw new NotSupportedException(t.GetType().Name),
    };

    static int Latin1Length(string s)
    {
        foreach (char c in s)
            if (c > 0xFF) throw new InvalidDataException($"Character U+{(int)c:X4} in \"{s}\" cannot be stored in a P3D file");
        return s.Length;
    }

    /// <summary>Serializes a model. Throws <see cref="InvalidDataException"/> if a LOD is inconsistent (nothing is written).</summary>
    public static byte[] Write(P3DModel model)
    {
        long size = 12;
        var used = new int[model.Lods.Length];
        for (int li = 0; li < model.Lods.Length; li++)
        {
            var lod = model.Lods[li];
            AssertIntegrity(lod, $"LOD {li}");
            used[li] = UsedSlots(lod);
            size += 28 + (long)lod.PointCount * 16 + lod.Normals.Length * 4L;
            for (int f = 0; f < lod.FaceCount; f++)
                size += 72 + Latin1Length(lod.Texture(f)) + 1 + Latin1Length(lod.Material(f)) + 1;
            size += 4;
            foreach (var t in lod.Tags) size += 1 + Latin1Length(t.DiskName) + 1 + 4 + TagDataSize(t, used[li]);
            size += 1 + TagNames.EndOfFile.Length + 1 + 4 + 4;
        }

        var outBuf = new byte[checked((int)size)];
        var w = new Writer(outBuf);
        w.U32(SigMlod);
        w.U32(model.Version);
        w.U32((uint)model.Lods.Length);
        for (int li = 0; li < model.Lods.Length; li++) WriteLod(ref w, model.Lods[li], used[li]);
        if (w.Offset != size) throw new InvalidOperationException($"internal writer error: wrote {w.Offset} of {size} bytes");
        return outBuf;
    }

    public static void WriteFile(string path, P3DModel model) => File.WriteAllBytes(path, Write(model));

    static void WriteLod(ref Writer w, Lod lod, int usedSlots)
    {
        int n = lod.PointCount, f = lod.FaceCount;
        w.U32(SigP3dm);
        w.U32(lod.Major);
        w.U32(lod.Minor);
        w.U32((uint)n);
        w.U32((uint)lod.NormalCount);
        w.U32((uint)f);
        w.U32(lod.HeaderFlags);

        var posBits = MemoryMarshal.Cast<float, uint>(lod.Positions.AsSpan());
        var pts = MemoryMarshal.Cast<byte, uint>(w.Take(n * 16));
        for (int i = 0; i < n; i++)
        {
            pts[i * 4] = posBits[i * 3];
            pts[i * 4 + 1] = posBits[i * 3 + 1];
            pts[i * 4 + 2] = posBits[i * 3 + 2];
            pts[i * 4 + 3] = lod.PointFlags[i];
        }
        MemoryMarshal.AsBytes(lod.Normals.AsSpan()).CopyTo(w.Take(lod.Normals.Length * 4));

        var uvBits = MemoryMarshal.Cast<float, uint>(lod.FaceUV.AsSpan());
        for (int i = 0; i < f; i++)
        {
            var rec = MemoryMarshal.Cast<byte, uint>(w.Take(72));
            rec[0] = lod.FaceVertCount[i];
            for (int k = 0; k < 4; k++)
            {
                rec[1 + k * 4] = lod.FacePoints[i * 4 + k];
                rec[2 + k * 4] = lod.FaceNormals[i * 4 + k];
                rec[3 + k * 4] = uvBits[i * 8 + k * 2];
                rec[4 + k * 4] = uvBits[i * 8 + k * 2 + 1];
            }
            rec[17] = lod.FaceFlags[i];
            w.Asciiz(lod.Texture(i));
            w.Asciiz(lod.Material(i));
        }

        w.U32(SigTagg);
        foreach (var t in lod.Tags)
        {
            w.Byte(t.Active);
            w.Asciiz(t.DiskName);
            int ds = TagDataSize(t, usedSlots);
            w.U32((uint)ds);
            WriteTagData(t, lod, w.Take(ds));
        }
        w.Byte(1);
        w.Asciiz(TagNames.EndOfFile);
        w.U32(0);
        w.F32(lod.Resolution);
    }

    /// <summary>The exact on-disk data bytes of one tag (without its header).</summary>
    public static byte[] SerializeTagData(Tag t, Lod lod)
    {
        var buf = new byte[TagDataSize(t, UsedSlots(lod))];
        WriteTagData(t, lod, buf);
        return buf;
    }

    static void WriteTagData(Tag t, Lod lod, Span<byte> dst)
    {
        switch (t)
        {
            case SelectionTag s:
                s.Points.CopyTo(dst);
                s.Faces.CopyTo(dst[s.Points.Length..]);
                break;
            case StateTag s:
                s.Points.CopyTo(dst);
                s.Faces.CopyTo(dst[s.Points.Length..]);
                break;
            case PropertyTag p:
                if (p.Raw is { Length: 128 } raw && RawMatches(raw, p.Key, p.Value))
                {
                    raw.CopyTo(dst);
                    break;
                }
                dst[..128].Clear();
                Latin1.GetBytes(p.Key, dst[..64]);
                Latin1.GetBytes(p.Value, dst[64..128]);
                break;
            case UVSetTag u:
            {
                BinaryPrimitives.WriteUInt32LittleEndian(dst, u.Stage);
                var bits = MemoryMarshal.Cast<float, uint>(u.UV.AsSpan());
                var outU = MemoryMarshal.Cast<byte, uint>(dst[4..]);
                int s = 0;
                for (int f = 0; f < lod.FaceCount; f++)
                    for (int k = 0; k < lod.FaceVertCount[f]; k++)
                    {
                        outU[s++] = bits[f * 8 + k * 2];
                        outU[s++] = bits[f * 8 + k * 2 + 1];
                    }
                break;
            }
            case MassTag m:
                MemoryMarshal.AsBytes(m.Mass.AsSpan()).CopyTo(dst);
                break;
            case SharpEdgesTag e:
                MemoryMarshal.AsBytes(e.Edges.AsSpan()).CopyTo(dst);
                break;
            case AnimationTag a:
                BinaryPrimitives.WriteSingleLittleEndian(dst, a.Time);
                MemoryMarshal.AsBytes(a.Positions.AsSpan()).CopyTo(dst[4..]);
                break;
            case UnknownTag u:
                u.Data.CopyTo(dst);
                break;
        }
    }

    /// <summary>True if the raw 128 bytes still decode to the current key/value (then they are written verbatim).</summary>
    static bool RawMatches(byte[] raw, string key, string value)
    {
        static string Text(ReadOnlySpan<byte> f)
        {
            int e = f.IndexOf((byte)0);
            return Latin1.GetString(e < 0 ? f : f[..e]);
        }
        return Text(raw.AsSpan(0, 64)) == key && Text(raw.AsSpan(64, 64)) == value;
    }

    ref struct Writer(Span<byte> buf)
    {
        readonly Span<byte> b = buf;
        public int Offset;

        public Span<byte> Take(int n)
        {
            var s = b.Slice(Offset, n);
            Offset += n;
            return s;
        }

        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
        public void Byte(byte v) => b[Offset++] = v;

        public void Asciiz(string s)
        {
            Latin1.GetBytes(s, Take(s.Length));
            b[Offset++] = 0;
        }
    }
}
