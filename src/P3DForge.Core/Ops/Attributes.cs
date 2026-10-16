using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

public enum ResourceKind
{
    Texture,
    Material,
}

/// <summary>How <see cref="Attributes.ReplacePath"/> matches the "find" text against a path.</summary>
public enum PathMatch
{
    /// <summary>Every occurrence of the text inside the path is replaced.</summary>
    Substring,
    /// <summary>The whole path must equal the text; it is replaced by the new name.</summary>
    Exact,
    /// <summary>The path must start with the text (e.g. a folder); that prefix is replaced.</summary>
    Prefix,
    /// <summary>
    /// The whole path must match a mask with <c>*</c> (any run, also across folders) and <c>?</c> (one char). Each
    /// <c>*</c>/<c>?</c> in the new name takes the text captured by the corresponding wildcard of the mask, in order;
    /// a new name without wildcards replaces the whole path (Object Builder "Name to rename (can be mask)").
    /// </summary>
    Wildcard,
}

/// <summary>Options of Mass Texture &amp; Material Renaming.</summary>
public sealed record ReplaceOptions
{
    public bool Textures { get; init; } = true;
    public bool Materials { get; init; } = true;
    public bool CaseInsensitive { get; init; } = true;
    public PathMatch Match { get; init; } = PathMatch.Substring;
    /// <summary>Treats '/' in the find/replace text as '\' (P3D paths use backslashes).</summary>
    public bool NormalizeSlashes { get; init; } = true;
}

/// <summary>One distinct renamed path (aggregated over LODs) for the renaming preview.</summary>
public readonly record struct PathChange(ResourceKind Kind, string From, string To, int Faces, int Lods);

/// <summary>
/// AND / OR of a set of flag words. A bit field is uniform across the set exactly when its bits agree in AND and OR,
/// so one pass answers "common value or mixed" for every field of a multi-selection.
/// </summary>
public readonly record struct FlagSummary(int Count, uint And, uint Or)
{
    public static readonly FlagSummary Empty = new(0, uint.MaxValue, 0);

    public FlagSummary Add(uint flags) => new(Count + 1, And & flags, Or | flags);

    /// <summary>True when the elements disagree on any bit of <paramref name="mask"/>.</summary>
    public bool IsMixed(uint mask) => Count > 0 && ((And ^ Or) & mask) != 0;

    /// <summary>The common (unshifted) value of the field, or null when mixed or empty.</summary>
    public uint? Common(uint mask) => Count == 0 || IsMixed(mask) ? null : And & mask;

    /// <summary>The common state of one bit, or null when mixed or empty.</summary>
    public bool? Bit(uint bit) => Count == 0 || IsMixed(bit) ? null : (And & bit) != 0;
}

/// <summary>Faces of a multi-selection: flags summary and common texture/material (null = mixed or no faces).</summary>
public sealed record FaceSummary(int Count, int First, FlagSummary Flags, string? Texture, string? Material)
{
    public bool TextureMixed => Count > 0 && Texture is null;
    public bool MaterialMixed => Count > 0 && Material is null;
}

/// <summary>Points of a multi-selection: flags summary, centroid/bounds and hidden/locked counts.</summary>
public sealed record PointSummary(int Count, int First, FlagSummary Flags, Vec3 Centroid, Vec3 Min, Vec3 Max, int Hidden, int Locked);

/// <summary>Distinct texture or material of a model with its face count per LOD (index = LOD index).</summary>
public sealed record ResourceUse(ResourceKind Kind, string Path, int[] FacesPerLod)
{
    public int TotalFaces
    {
        get
        {
            int n = 0;
            foreach (var c in FacesPerLod) n += c;
            return n;
        }
    }

    public int LodCount
    {
        get
        {
            int n = 0;
            foreach (var c in FacesPerLod)
                if (c > 0) n++;
            return n;
        }
    }
}

/// <summary>Measuring of the current selection (RAW coordinates, model units = metres).</summary>
public sealed record Measurement
{
    public int Points { get; init; }
    public int Faces { get; init; }
    /// <summary>The first (up to) three selected point indices, in index order.</summary>
    public int[] FirstPoints { get; init; } = [];
    /// <summary>Exactly two points: the distance and the vector from the first to the second.</summary>
    public double? Distance { get; init; }
    public Vec3? Delta { get; init; }
    /// <summary>Exactly three points: the triangle angles (degrees) at each of <see cref="FirstPoints"/>.</summary>
    public double[]? Angles { get; init; }
    /// <summary>Face edges whose both end points are selected (each counted once) and their total length.</summary>
    public int Edges { get; init; }
    public double EdgeLength { get; init; }
    /// <summary>Total area of the selected faces.</summary>
    public double Area { get; init; }
    public Vec3? Centroid { get; init; }
    public Vec3? Min { get; init; }
    public Vec3? Max { get; init; }
    public Vec3? Size => Min is { } a && Max is { } b ? b - a : null;
}

/// <summary>
/// Face / point attributes (Object Builder Face Properties, Point Properties, Mass Texture &amp; Material Renaming,
/// Measuring). Pure and immutable: every edit returns a new <see cref="Lod"/>/<see cref="P3DModel"/> (or the same
/// instance when nothing changes) and never writes into the input arrays.
/// </summary>
public static class Attributes
{
    // ---- flags ------------------------------------------------------------------------------------------

    /// <summary>Replaces the bits of <paramref name="mask"/> with <paramref name="value"/> (already shifted) on the faces in <paramref name="faceMask"/>.</summary>
    public static Lod SetFaceFlags(Lod lod, byte[] faceMask, uint mask, uint value)
    {
        var flags = SetBits(lod.FaceFlags, faceMask, mask, value);
        return flags is null ? lod : lod with { FaceFlags = flags };
    }

    /// <summary>Replaces the bits of <paramref name="mask"/> with <paramref name="value"/> (already shifted) on the points in <paramref name="pointMask"/>.</summary>
    public static Lod SetPointFlags(Lod lod, byte[] pointMask, uint mask, uint value)
    {
        var flags = SetBits(lod.PointFlags, pointMask, mask, value);
        return flags is null ? lod : lod with { PointFlags = flags };
    }

    /// <summary>A modified copy of <paramref name="src"/>, or null when no element changes.</summary>
    static uint[]? SetBits(uint[] src, byte[] elemMask, uint mask, uint value)
    {
        if (elemMask.Length != src.Length) throw new ArgumentException("Mask length does not match the element count.");
        uint[]? copy = null;
        for (int i = 0; i < src.Length; i++)
        {
            if (elemMask[i] == 0) continue;
            uint next = Bits.Set(src[i], mask, value);
            if (next == src[i]) continue;
            copy ??= (uint[])src.Clone();
            copy[i] = next;
        }
        return copy;
    }

    // ---- mapping ----------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the UVs of face <paramref name="face"/> (u0, v0, u1, v1, … — one pair per face vertex) in the face data and
    /// in the stage-0 #UVSet# (which duplicates them); other UV sets are untouched. The same LOD when nothing changes.
    /// </summary>
    public static Lod SetFaceUV(Lod lod, int face, float[] uv)
    {
        if ((uint)face >= (uint)lod.FaceCount) throw new ArgumentOutOfRangeException(nameof(face));
        int n = lod.FaceVertCount[face];
        if (uv.Length != n * 2) throw new ArgumentException($"Face {face} has {n} vertices: expected {n * 2} UV values.");
        foreach (var x in uv)
            if (!float.IsFinite(x)) throw new ArgumentException("UV values must be finite numbers.");
        static bool Same(float[] a, int at, float[] b)
        {
            for (int i = 0; i < b.Length; i++)
                if (a[at + i] != b[i]) return false;
            return true;
        }
        var tags = lod.Tags;
        for (int t = 0; t < tags.Length; t++)
        {
            if (tags[t] is not UVSetTag u || u.Stage != 0 || u.UV.Length < lod.FaceCount * 8 || Same(u.UV, face * 8, uv)) continue;
            var copy = (float[])u.UV.Clone();
            uv.CopyTo(copy, face * 8);
            tags = tags.SetItem(t, u with { UV = copy });
        }
        bool faceSame = Same(lod.FaceUV, face * 8, uv);
        if (faceSame && tags == lod.Tags) return lod;
        var fuv = lod.FaceUV;
        if (!faceSame)
        {
            fuv = (float[])fuv.Clone();
            uv.CopyTo(fuv, face * 8);
        }
        return lod with { FaceUV = fuv, Tags = tags };
    }

    // ---- textures / materials ---------------------------------------------------------------------------

    /// <summary>Sets texture and/or material (null = unchanged) of the faces in <paramref name="faceMask"/>; the same LOD when nothing changes.</summary>
    public static Lod SetFaceTextureMaterial(Lod lod, byte[] faceMask, string? texture, string? material)
    {
        if (faceMask.Length != lod.FaceCount) throw new ArgumentException("Face mask length does not match the face count.");
        if (texture is not null && NeedsChange(lod, lod.FaceTexture, faceMask, texture) is false) texture = null;
        if (material is not null && NeedsChange(lod, lod.FaceMaterial, faceMask, material) is false) material = null;
        if (texture is null && material is null) return lod;
        return Structure.SetFaceTextures(lod, faceMask, texture, material);
    }

    static bool NeedsChange(Lod lod, uint[] idx, byte[] faceMask, string value)
    {
        for (int f = 0; f < idx.Length; f++)
            if (faceMask[f] != 0 && !string.Equals(lod.Strings[(int)idx[f]], value, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Comparison key of a model path: trimmed, '\' separators, no leading '\', lower case.</summary>
    public static string PathKey(string path)
    {
        var s = path.Trim().Replace('/', '\\').TrimStart('\\');
        return s.ToLowerInvariant();
    }

    /// <summary>Faces whose texture is <paramref name="texture"/> (compared with <see cref="PathKey"/>).</summary>
    public static byte[] FacesWithTexture(Lod lod, string texture) => FacesWith(lod, lod.FaceTexture, texture);

    /// <summary>Faces whose material is <paramref name="material"/> (compared with <see cref="PathKey"/>).</summary>
    public static byte[] FacesWithMaterial(Lod lod, string material) => FacesWith(lod, lod.FaceMaterial, material);

    static byte[] FacesWith(Lod lod, uint[] idx, string path)
    {
        var key = PathKey(path);
        var hit = new bool[lod.Strings.Length];
        for (int i = 0; i < hit.Length; i++) hit[i] = PathKey(lod.Strings[i]) == key;
        var mask = new byte[idx.Length];
        for (int f = 0; f < idx.Length; f++)
            if (hit[idx[f]]) mask[f] = 1;
        return mask;
    }

    /// <summary>Distinct textures and materials of the model (empty paths excluded) with face counts per LOD; sorted by kind, then path.</summary>
    public static List<ResourceUse> Usage(P3DModel model)
    {
        var byKey = new Dictionary<(ResourceKind, string), ResourceUse>();
        int nl = model.Lods.Length;
        for (int li = 0; li < nl; li++)
        {
            var lod = model.Lods[li];
            Count(lod, lod.FaceTexture, ResourceKind.Texture, li);
            Count(lod, lod.FaceMaterial, ResourceKind.Material, li);
        }

        void Count(Lod lod, uint[] idx, ResourceKind kind, int li)
        {
            var counts = new int[lod.Strings.Length];
            for (int f = 0; f < idx.Length; f++) counts[idx[f]]++;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                var path = lod.Strings[i];
                if (path.Trim().Length == 0) continue;
                var k = (kind, PathKey(path));
                if (!byKey.TryGetValue(k, out var use)) byKey[k] = use = new ResourceUse(kind, path, new int[nl]);
                use.FacesPerLod[li] += counts[i];
            }
        }

        var list = byKey.Values.ToList();
        list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // ---- mass renaming ----------------------------------------------------------------------------------

    /// <summary>The renamed path, or null when <paramref name="path"/> does not match (or would not change).</summary>
    public static string? ReplacePath(string path, string find, string replace, ReplaceOptions o) => Replacer(find, replace, o)(path);

    /// <summary>Builds the rename function once (compiles the wildcard mask); it returns null for non-matching paths.</summary>
    public static Func<string, string?> Replacer(string find, string replace, ReplaceOptions o)
    {
        if (o.NormalizeSlashes)
        {
            find = find.Replace('/', '\\');
            replace = replace.Replace('/', '\\');
        }
        if (find.Length == 0) return _ => null;
        var cmp = o.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Func<string, string?> fn = o.Match switch
        {
            PathMatch.Exact => p => string.Equals(p, find, cmp) ? replace : null,
            PathMatch.Prefix => p => p.StartsWith(find, cmp) ? replace + p[find.Length..] : null,
            PathMatch.Wildcard => WildcardReplacer(find, replace, o.CaseInsensitive),
            _ => p => p.Contains(find, cmp) ? p.Replace(find, replace, cmp) : null,
        };
        return p =>
        {
            if (p.Length == 0) return null;
            var r = fn(p);
            return r is null || string.Equals(r, p, StringComparison.Ordinal) ? null : r;
        };
    }

    static Func<string, string?> WildcardReplacer(string mask, string replace, bool ignoreCase)
    {
        var sb = new StringBuilder("^");
        foreach (char c in mask)
            sb.Append(c switch
            {
                '*' => "(.*?)",
                '?' => "(.)",
                _ => Regex.Escape(c.ToString()),
            });
        sb.Append('$');
        var rx = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
        bool templated = replace.Contains('*') || replace.Contains('?');
        return p =>
        {
            var m = rx.Match(p);
            if (!m.Success) return null;
            if (!templated) return replace;
            var o = new StringBuilder(replace.Length + p.Length);
            int g = 1;
            foreach (char c in replace)
            {
                if (c is '*' or '?')
                {
                    if (g < m.Groups.Count) o.Append(m.Groups[g].Value);
                    g++;
                }
                else o.Append(c);
            }
            return o.ToString();
        };
    }

    /// <summary>
    /// Mass Texture &amp; Material Renaming over every LOD. Returns the new model and the number of face slots changed
    /// (texture + material); the same model when nothing matches.
    /// </summary>
    public static (P3DModel Model, int Count) ReplacePaths(P3DModel model, string find, string replace, ReplaceOptions o) =>
        ReplacePaths(model, Replacer(find, replace, o), o);

    /// <summary><see cref="ReplacePaths(P3DModel, string, string, ReplaceOptions)"/> with a ready <see cref="Replacer"/>.</summary>
    public static (P3DModel Model, int Count) ReplacePaths(P3DModel model, Func<string, string?> fn, ReplaceOptions o)
    {
        int count = 0;
        ImmutableArray<Lod>.Builder? lods = null;
        for (int li = 0; li < model.Lods.Length; li++)
        {
            var lod = model.Lods[li];
            var map = MapPool(lod, fn);
            if (map is null) continue;
            var strings = lod.Strings;
            var newIndex = new uint[map.Length];
            Array.Fill(newIndex, uint.MaxValue);
            uint[]? tex = o.Textures ? Remap(lod.FaceTexture) : null;
            uint[]? mat = o.Materials ? Remap(lod.FaceMaterial) : null;
            if (tex is null && mat is null) continue;
            lods ??= model.Lods.ToBuilder();
            lods[li] = lod with { FaceTexture = tex ?? lod.FaceTexture, FaceMaterial = mat ?? lod.FaceMaterial, Strings = strings };

            uint[]? Remap(uint[] idx)
            {
                uint[]? copy = null;
                for (int f = 0; f < idx.Length; f++)
                {
                    uint s = idx[f];
                    if (map[s] is not { } to) continue;
                    if (newIndex[s] == uint.MaxValue) newIndex[s] = Lod.Intern(ref strings, to);
                    copy ??= (uint[])idx.Clone();
                    copy[f] = newIndex[s];
                    count++;
                }
                return copy;
            }
        }
        return lods is null ? (model, 0) : (model with { Lods = lods.ToImmutable() }, count);
    }

    /// <summary>Distinct renames that <see cref="ReplacePaths(P3DModel, string, string, ReplaceOptions)"/> would make, with face and LOD counts.</summary>
    public static List<PathChange> PreviewReplace(P3DModel model, string find, string replace, ReplaceOptions o)
    {
        var fn = Replacer(find, replace, o);
        var agg = new Dictionary<(ResourceKind, string), (string To, int Faces, int Lods)>();
        foreach (var lod in model.Lods)
        {
            var map = MapPool(lod, fn);
            if (map is null) continue;
            if (o.Textures) Add(lod, lod.FaceTexture, ResourceKind.Texture, map);
            if (o.Materials) Add(lod, lod.FaceMaterial, ResourceKind.Material, map);
        }

        void Add(Lod lod, uint[] idx, ResourceKind kind, string?[] map)
        {
            var counts = new int[map.Length];
            for (int f = 0; f < idx.Length; f++)
                if (map[idx[f]] is not null) counts[idx[f]]++;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                var k = (kind, lod.Strings[i]);
                agg[k] = agg.TryGetValue(k, out var a) ? (a.To, a.Faces + counts[i], a.Lods + 1) : (map[i]!, counts[i], 1);
            }
        }

        var list = new List<PathChange>(agg.Count);
        foreach (var (k, v) in agg) list.Add(new PathChange(k.Item1, k.Item2, v.To, v.Faces, v.Lods));
        list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.From, b.From, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>New name per pool entry (null = unchanged), or null when no entry changes.</summary>
    static string?[]? MapPool(Lod lod, Func<string, string?> fn)
    {
        string?[]? map = null;
        for (int i = 0; i < lod.Strings.Length; i++)
        {
            var to = fn(lod.Strings[i]);
            if (to is null) continue;
            map ??= new string?[lod.Strings.Length];
            map[i] = to;
        }
        return map;
    }

    // ---- summaries --------------------------------------------------------------------------------------

    /// <summary>Faces a face edit applies to: the selected faces, or (none selected) the faces whose every point is selected.</summary>
    public static byte[] EffectiveFaces(Lod lod, byte[] points, byte[] faces) =>
        faces.AsSpan().ContainsAnyExcept((byte)0) ? faces : Selection.FacesFromPoints(lod, points);

    public static FaceSummary SummarizeFaces(Lod lod, byte[] faceMask)
    {
        var flags = FlagSummary.Empty;
        int first = -1;
        uint tex = 0, mat = 0;
        bool texMixed = false, matMixed = false;
        for (int f = 0; f < faceMask.Length; f++)
        {
            if (faceMask[f] == 0) continue;
            flags = flags.Add(lod.FaceFlags[f]);
            uint t = lod.FaceTexture[f], m = lod.FaceMaterial[f];
            if (first < 0)
            {
                first = f;
                tex = t;
                mat = m;
                continue;
            }
            if (!texMixed && t != tex && !string.Equals(lod.Strings[(int)t], lod.Strings[(int)tex], StringComparison.Ordinal)) texMixed = true;
            if (!matMixed && m != mat && !string.Equals(lod.Strings[(int)m], lod.Strings[(int)mat], StringComparison.Ordinal)) matMixed = true;
        }
        if (first < 0) return new FaceSummary(0, -1, flags, null, null);
        return new FaceSummary(flags.Count, first, flags, texMixed ? null : lod.Strings[(int)tex], matMixed ? null : lod.Strings[(int)mat]);
    }

    public static PointSummary SummarizePoints(Lod lod, byte[] pointMask)
    {
        var flags = FlagSummary.Empty;
        int first = -1, hidden = 0, locked = 0;
        var hiddenMask = Selection.HiddenPoints(lod);
        var lockedMask = Selection.LockedPoints(lod);
        var pos = lod.Positions;
        double sx = 0, sy = 0, sz = 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int i = 0; i < pointMask.Length; i++)
        {
            if (pointMask[i] == 0) continue;
            if (first < 0) first = i;
            flags = flags.Add(lod.PointFlags[i]);
            if (hiddenMask[i] != 0) hidden++;
            if (lockedMask[i] != 0) locked++;
            double x = pos[i * 3], y = pos[i * 3 + 1], z = pos[i * 3 + 2];
            sx += x;
            sy += y;
            sz += z;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }
        int n = flags.Count;
        if (n == 0) return new PointSummary(0, -1, flags, Vec3.Zero, Vec3.Zero, Vec3.Zero, 0, 0);
        return new PointSummary(n, first, flags, new Vec3(sx / n, sy / n, sz / n), new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ), hidden, locked);
    }

    // ---- positions --------------------------------------------------------------------------------------

    /// <summary>Moves point <paramref name="index"/> to <paramref name="p"/>; the same LOD when it is already there.</summary>
    public static Lod SetPointPosition(Lod lod, int index, Vec3 p)
    {
        if ((uint)index >= (uint)lod.PointCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (!p.IsFinite) throw new ArgumentException("Coordinates must be finite numbers.");
        float x = (float)p.X, y = (float)p.Y, z = (float)p.Z;
        var src = lod.Positions;
        if (src[index * 3] == x && src[index * 3 + 1] == y && src[index * 3 + 2] == z) return lod;
        var pos = (float[])src.Clone();
        pos[index * 3] = x;
        pos[index * 3 + 1] = y;
        pos[index * 3 + 2] = z;
        return lod with { Positions = pos };
    }

    /// <summary>Translates the points in <paramref name="pointMask"/> by <paramref name="d"/>; the same LOD for a zero offset or no points.</summary>
    public static Lod OffsetPoints(Lod lod, byte[] pointMask, Vec3 d)
    {
        if (!d.IsFinite) throw new ArgumentException("Offset must be finite numbers.");
        if (d == Vec3.Zero || !pointMask.AsSpan().ContainsAnyExcept((byte)0)) return lod;
        var pos = (float[])lod.Positions.Clone();
        for (int i = 0; i < pointMask.Length; i++)
        {
            if (pointMask[i] == 0) continue;
            pos[i * 3] = (float)(pos[i * 3] + d.X);
            pos[i * 3 + 1] = (float)(pos[i * 3 + 1] + d.Y);
            pos[i * 3 + 2] = (float)(pos[i * 3 + 2] + d.Z);
        }
        return lod with { Positions = pos };
    }

    // ---- measuring --------------------------------------------------------------------------------------

    public static Vec3 Point(Lod lod, int i) => new(lod.Positions[i * 3], lod.Positions[i * 3 + 1], lod.Positions[i * 3 + 2]);

    /// <summary>Area of face <paramref name="f"/> (half the Newell normal length; projected area for non-planar quads).</summary>
    public static double FaceArea(Lod lod, int f) => MeshMath.FaceNewell(lod, f).Length * 0.5;

    /// <summary>Angle in degrees at <paramref name="at"/> between the rays to <paramref name="a"/> and <paramref name="b"/> (NaN when degenerate).</summary>
    public static double Angle(Vec3 at, Vec3 a, Vec3 b)
    {
        var u = a - at;
        var v = b - at;
        double lu = u.Length, lv = v.Length;
        if (lu == 0 || lv == 0) return double.NaN;
        double c = Math.Clamp(Vec3.Dot(u, v) / (lu * lv), -1, 1);
        return Math.Acos(c) * 180 / Math.PI;
    }

    /// <summary>Distance (2 points), triangle angles (3 points), selected edge length, face area, centroid and bounds.</summary>
    public static Measurement Measure(Lod lod, byte[] pointMask, byte[] faceMask)
    {
        var s = SummarizePoints(lod, pointMask);
        var firsts = new List<int>(3);
        for (int i = 0; i < pointMask.Length && firsts.Count < 3; i++)
            if (pointMask[i] != 0) firsts.Add(i);

        double? distance = null;
        Vec3? delta = null;
        double[]? angles = null;
        if (s.Count == 2)
        {
            var d = Point(lod, firsts[1]) - Point(lod, firsts[0]);
            delta = d;
            distance = d.Length;
        }
        else if (s.Count == 3)
        {
            Vec3 a = Point(lod, firsts[0]), b = Point(lod, firsts[1]), c = Point(lod, firsts[2]);
            angles = [Angle(a, b, c), Angle(b, a, c), Angle(c, a, b)];
        }

        int edges = 0;
        double edgeLength = 0;
        if (s.Count >= 2)
        {
            var seen = new HashSet<long>(Topology.EdgeKey.Comparer); // ulong's default hash (lo ^ hi) collides for nearby indices
            var fp = lod.FacePoints;
            for (int f = 0; f < lod.FaceCount; f++)
            {
                int n = lod.FaceVertCount[f];
                for (int k = 0; k < n; k++)
                {
                    uint a = fp[f * 4 + k], b = fp[f * 4 + (k + 1) % n];
                    if (pointMask[a] == 0 || pointMask[b] == 0 || a == b) continue;
                    if (!seen.Add(Topology.EdgeKey.Of(a, b))) continue;
                    edges++;
                    edgeLength += (Point(lod, (int)b) - Point(lod, (int)a)).Length;
                }
            }
        }

        int faces = 0;
        double area = 0;
        for (int f = 0; f < faceMask.Length; f++)
        {
            if (faceMask[f] == 0) continue;
            faces++;
            area += FaceArea(lod, f);
        }

        return new Measurement
        {
            Points = s.Count,
            Faces = faces,
            FirstPoints = firsts.ToArray(),
            Distance = distance,
            Delta = delta,
            Angles = angles,
            Edges = edges,
            EdgeLength = edgeLength,
            Area = area,
            Centroid = s.Count > 0 ? s.Centroid : null,
            Min = s.Count > 0 ? s.Min : null,
            Max = s.Count > 0 ? s.Max : null,
        };
    }
}
