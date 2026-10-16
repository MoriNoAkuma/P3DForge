using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.IO;

/// <summary>How imported coordinates and texture references become P3D data.</summary>
public sealed record ImportOptions
{
    /// <summary>Extra scale on top of the file's units (OBJ has none: 1 unit = 1 m).</summary>
    public double Scale { get; init; } = 1;
    /// <summary>OBJ only: the file is Z-up (Blender's own axes, "Up: Z" export). FBX files state their axes.</summary>
    public bool ZUp { get; init; }
    /// <summary>Maps a texture file reference (absolute, or relative to the imported file) to the path stored in the model.</summary>
    public Func<string, string>? Texture { get; init; }
}

/// <summary>Result of an import: the geometry and what was found.</summary>
public sealed record ImportResult(Fragment Fragment, int Objects, int Polygons, int Triangulated, IReadOnlyList<string> Textures, IReadOnlyList<string> Warnings);

/// <summary>
/// Collects points and polygons in P3D RAW coordinates and turns them into a <see cref="Fragment"/>: quads stay quads,
/// longer polygons become triangle fans, each object / group becomes a named selection, textures go to the string pool.
/// </summary>
public sealed class FragmentBuilder
{
    readonly List<float> positions = [];
    readonly List<(int[] Points, float[][] Uv, string Texture, int Group)> faces = [];
    readonly List<string> groups = [];
    readonly Dictionary<string, int> groupIndex = new(StringComparer.OrdinalIgnoreCase);
    public int Triangulated { get; private set; }
    public int Polygons { get; private set; }
    public int UvSets { get; set; } = 1;

    public int PointCount => positions.Count / 3;

    public int AddPoint(Vector3 raw)
    {
        positions.Add(raw.X);
        positions.Add(raw.Y);
        positions.Add(raw.Z);
        return PointCount - 1;
    }

    public int Group(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "imported" : name.Trim();
        if (groupIndex.TryGetValue(name, out int g)) return g;
        groups.Add(name);
        return groupIndex[name] = groups.Count - 1;
    }

    /// <summary>A polygon in the source winding; <paramref name="uv"/>[set][vertex * 2] (P3D v grows downward).</summary>
    public void AddPolygon(int[] points, float[][] uv, string texture, int group)
    {
        int n = points.Length;
        if (n < 3) return;
        Polygons++;
        if (n <= 4)
        {
            faces.Add((points, uv, texture, group));
            return;
        }
        Triangulated++;
        for (int k = 1; k + 1 < n; k++)
        {
            int[] idx = [0, k, k + 1];
            var sub = new float[uv.Length][];
            for (int s = 0; s < uv.Length; s++)
            {
                sub[s] = new float[6];
                for (int j = 0; j < 3; j++)
                {
                    sub[s][j * 2] = uv[s][idx[j] * 2];
                    sub[s][j * 2 + 1] = uv[s][idx[j] * 2 + 1];
                }
            }
            faces.Add(([points[0], points[k], points[k + 1]], sub, texture, group));
        }
    }

    public Fragment Build()
    {
        int nf = faces.Count, np = PointCount;
        var frag = Fragment.Allocate(np, nf);
        positions.CopyTo(frag.Positions);
        var strings = ImmutableArray.Create("");
        var extra = new float[Math.Max(0, UvSets - 1)][];
        for (int s = 0; s < extra.Length; s++) extra[s] = new float[nf * 8];
        var pointSel = groups.Select(_ => new byte[np]).ToArray();
        var faceSel = groups.Select(_ => new byte[nf]).ToArray();
        var fa = frag.Faces;
        for (int f = 0; f < nf; f++)
        {
            var (pts, uv, tex, g) = faces[f];
            fa.VertCount[f] = (byte)pts.Length;
            for (int j = 0; j < pts.Length; j++)
            {
                fa.Points[f * 4 + j] = (uint)pts[j];
                if (uv.Length > 0)
                {
                    fa.UV[f * 8 + j * 2] = uv[0][j * 2];
                    fa.UV[f * 8 + j * 2 + 1] = uv[0][j * 2 + 1];
                }
                for (int s = 0; s < extra.Length && s + 1 < uv.Length; s++)
                {
                    extra[s][f * 8 + j * 2] = uv[s + 1][j * 2];
                    extra[s][f * 8 + j * 2 + 1] = uv[s + 1][j * 2 + 1];
                }
                pointSel[g][pts[j]] = 1;
            }
            fa.Texture[f] = tex.Length == 0 ? 0 : Lod.Intern(ref strings, tex);
            faceSel[g][f] = 1;
        }
        var selections = ImmutableArray.CreateBuilder<NamedWeights>();
        for (int g = 0; g < groups.Count; g++)
            if (faceSel[g].AsSpan().Contains((byte)1)) selections.Add(new NamedWeights(groups[g], pointSel[g], faceSel[g]));
        var uvSets = ImmutableArray.CreateBuilder<(uint, float[])>();
        for (int s = 0; s < extra.Length; s++) uvSets.Add(((uint)(s + 1), extra[s]));
        return frag with { Strings = strings, Selections = selections.ToImmutable(), UVSets = uvSets.ToImmutable() };
    }
}

/// <summary>
/// Wavefront OBJ: import (v / vt / f, o / g → named selections, usemtl + mtllib map_Kd → textures) and export.
/// Axes as the Arma 3 Blender toolbox: Blender (x, y, z) = P3D (x, z, y). OBJ files are Y-up (Blender's default OBJ
/// export) unless <see cref="ImportOptions.ZUp"/>; V is flipped (OBJ v grows upward).
/// </summary>
public static class Obj
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ImportResult Read(string text, string? folder, ImportOptions options)
    {
        var b = new FragmentBuilder();
        var warnings = new List<string>();
        var v = new List<Vector3>();
        var vt = new List<Vector2>();
        var pointOf = new Dictionary<int, int>(); // OBJ vertex index → fragment point
        var materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // material → texture
        var textures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string texture = "";
        int group = -1, objects = 0;
        float scale = (float)options.Scale;
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    v.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vt" when t.Length >= 3:
                    vt.Add(new Vector2(F(t[1]), F(t[2])));
                    break;
                case "o":
                case "g":
                    if (t.Length > 1)
                    {
                        group = b.Group(string.Join(' ', t.Skip(1)));
                        objects++;
                    }
                    break;
                case "usemtl":
                    texture = t.Length > 1 && materials.TryGetValue(t[1], out var tex) ? tex : "";
                    break;
                case "mtllib" when folder is not null && t.Length > 1:
                    ReadMtl(Path.Combine(folder, string.Join(' ', t.Skip(1))), options, materials, warnings);
                    break;
                case "f" when t.Length >= 4:
                {
                    if (group < 0)
                    {
                        group = b.Group("imported");
                        objects++;
                    }
                    int n = t.Length - 1;
                    var pts = new int[n];
                    var uv = new float[n * 2];
                    bool ok = true;
                    for (int j = 0; j < n && ok; j++)
                    {
                        var parts = t[j + 1].Split('/');
                        int vi = Index(parts[0], v.Count);
                        if (vi < 0)
                        {
                            ok = false;
                            break;
                        }
                        if (!pointOf.TryGetValue(vi, out int p)) pointOf[vi] = p = b.AddPoint(ToP3d(v[vi], options.ZUp) * scale);
                        pts[j] = p;
                        int ti = parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], vt.Count) : -1;
                        if (ti >= 0)
                        {
                            uv[j * 2] = vt[ti].X;
                            uv[j * 2 + 1] = 1 - vt[ti].Y;
                        }
                    }
                    if (!ok)
                    {
                        if (warnings.Count < 20) warnings.Add($"line {lineNo}: face refers to a missing vertex");
                        break;
                    }
                    if (texture.Length > 0) textures.Add(texture);
                    b.AddPolygon(pts, [uv], texture, group);
                    break;
                }
            }
        }
        if (b.Polygons == 0) warnings.Add("the file has no faces");
        return new ImportResult(b.Build(), objects, b.Polygons, b.Triangulated, [.. textures], warnings);
    }

    /// <summary>Y-up right-handed (or Z-up) → P3D RAW (left-handed, Y up). The mirror keeps the faces' outside.</summary>
    static Vector3 ToP3d(Vector3 p, bool zUp) => zUp ? new(p.X, p.Z, p.Y) : new(p.X, p.Y, -p.Z);

    static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);

    /// <summary>1-based (negative = from the end) → 0-based; -1 when out of range.</summary>
    static int Index(string s, int count)
    {
        if (!int.TryParse(s, NumberStyles.Integer, Inv, out int i)) return -1;
        i = i > 0 ? i - 1 : count + i;
        return i >= 0 && i < count ? i : -1;
    }

    static void ReadMtl(string path, ImportOptions options, Dictionary<string, string> materials, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            warnings.Add($"material library not found: {Path.GetFileName(path)}");
            return;
        }
        string? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("newmtl ", StringComparison.Ordinal)) current = line[7..].Trim();
            else if (current is not null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
            {
                // the file name is the last token (options such as -s 1 1 1 come first)
                var parts = line[7..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var file = parts.Length > 0 ? parts[^1] : "";
                if (parts.Length > 0 && !parts[0].StartsWith('-')) file = line[7..].Trim(); // a name with spaces
                var full = Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(path)!, file);
                materials[current] = options.Texture?.Invoke(full) ?? full;
            }
        }
    }

    // ---- export ---------------------------------------------------------------------------------

    /// <summary>
    /// The faces of <paramref name="lod"/> (those set in <paramref name="faces"/>, or all) as OBJ + MTL text: Y-up,
    /// one material per texture (map_Kd = <paramref name="textureFile"/> of the stored path, e.g. the file on P:\).
    /// </summary>
    public static (string Obj, string Mtl) Write(Lod lod, byte[]? faces, string name, string mtlFileName, Func<string, string> textureFile)
    {
        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        obj.AppendLine("# P3D Forge export").Append("mtllib ").AppendLine(mtlFileName).Append("o ").AppendLine(name);
        var used = new int[lod.PointCount];
        Array.Fill(used, -1);
        int nv = 0;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces is not null && faces[f] == 0) continue;
            for (int j = 0; j < lod.FaceVertCount[f]; j++)
            {
                int p = (int)lod.FacePoints[f * 4 + j];
                if (used[p] >= 0) continue;
                used[p] = ++nv;
                var x = lod.Positions[p * 3];
                var y = lod.Positions[p * 3 + 1];
                var z = lod.Positions[p * 3 + 2];
                obj.Append(Inv, $"v {x:R} {y:R} {-z:R}\n");
            }
        }
        int vt = 0;
        var matNames = new Dictionary<uint, string>();
        uint current = uint.MaxValue;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faces is not null && faces[f] == 0) continue;
            int n = lod.FaceVertCount[f];
            for (int j = 0; j < n; j++)
                obj.Append(Inv, $"vt {lod.FaceUV[f * 8 + j * 2]:R} {1 - lod.FaceUV[f * 8 + j * 2 + 1]:R}\n");
            uint tex = lod.FaceTexture[f];
            if (tex != current)
            {
                current = tex;
                if (!matNames.TryGetValue(tex, out var m))
                {
                    var path = lod.Strings[(int)tex];
                    m = path.Length == 0 ? "untextured" : $"m{matNames.Count}_{SafeName(Path.GetFileNameWithoutExtension(path))}";
                    matNames[tex] = m;
                    mtl.Append("newmtl ").AppendLine(m).AppendLine("Kd 1 1 1");
                    if (path.Length > 0 && !path.StartsWith('#')) mtl.Append("map_Kd ").AppendLine(textureFile(path));
                    mtl.AppendLine();
                }
                obj.Append("usemtl ").AppendLine(m);
            }
            obj.Append('f');
            for (int j = 0; j < n; j++)
                obj.Append(' ').Append(used[lod.FacePoints[f * 4 + j]]).Append('/').Append(vt + j + 1);
            obj.Append('\n');
            vt += n;
        }
        return (obj.ToString(), mtl.ToString());
    }

    static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }
}
