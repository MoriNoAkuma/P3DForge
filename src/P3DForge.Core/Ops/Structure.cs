using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>A LOD-independent piece of geometry with its per-element data (copy/paste, import, primitives).</summary>
public sealed record Fragment
{
    public float[] Positions { get; init; } = [];
    public uint[] PointFlags { get; init; } = [];
    /// <summary>Own normal table; face normal indices refer to it. May be empty (normals are recalculated).</summary>
    public float[] Normals { get; init; } = [];
    public FaceArrays Faces { get; init; } = FaceArrays.Allocate(0);
    /// <summary>Pool for <see cref="Faces"/> texture/material indices (index 0 should be "").</summary>
    public ImmutableArray<string> Strings { get; init; } = [""];
    public ImmutableArray<NamedWeights> Selections { get; init; } = [];
    public ImmutableArray<(uint Stage, float[] UV)> UVSets { get; init; } = [];
    public float[]? Mass { get; init; }

    public int PointCount => PointFlags.Length;
    public int FaceCount => Faces.Count;

    public static Fragment Allocate(int points, int faces) => new()
    {
        Positions = new float[points * 3],
        PointFlags = new uint[points],
        Faces = FaceArrays.Allocate(faces),
    };
}

/// <summary>Named selection weights carried by a fragment.</summary>
public sealed record NamedWeights(string Name, byte[] Points, byte[] Faces);

/// <summary>
/// Fundamental structural operations. Every op is pure: returns a new Lod and never writes into the
/// arrays of its input. All per-point/per-face tag data (named selections, #Selected#/#Hidden#/#Lock#,
/// UV sets, mass, animation frames, sharp edges) is remapped consistently.
/// </summary>
public static class Structure
{
    /// <summary>Faces that use at least one point of <paramref name="pointMask"/>.</summary>
    public static byte[] FacesUsingPoints(Lod lod, byte[] pointMask) => Selection.FacesTouching(lod, pointMask);

    /// <summary>Points referenced by any face in <paramref name="faceMask"/> (or by any face when null).</summary>
    public static byte[] PointsUsedByFaces(Lod lod, byte[]? faceMask = null)
    {
        var o = new byte[lod.PointCount];
        for (int f = 0; f < lod.FaceCount; f++)
        {
            if (faceMask is not null && faceMask[f] == 0) continue;
            for (int k = 0; k < lod.FaceVertCount[f]; k++) o[lod.FacePoints[f * 4 + k]] = 1;
        }
        return o;
    }

    static byte[] FilterBytes(byte[] src, byte[] keep, int count)
    {
        var o = new byte[count];
        int j = 0;
        for (int i = 0; i < src.Length; i++)
            if (keep[i] != 0) o[j++] = src[i];
        return o;
    }

    /// <summary>
    /// Removes every point with keepPoints[i]==0 and every face with keepFaces[f]==0, remapping all indices
    /// and tags. Kept faces must reference only kept points (throws otherwise). Normals used by kept faces
    /// are kept and remapped; unused normals are dropped.
    /// </summary>
    public static Lod Compact(Lod lod, byte[] keepPoints, byte[] keepFaces)
    {
        int n = lod.PointCount, f = lod.FaceCount;
        var pointMap = new int[n];
        int nPts = 0;
        for (int i = 0; i < n; i++) pointMap[i] = keepPoints[i] != 0 ? nPts++ : -1;
        int nFaces = 0;
        for (int i = 0; i < f; i++)
            if (keepFaces[i] != 0) nFaces++;

        int nNormals = lod.NormalCount;
        var normalMap = new int[nNormals];
        Array.Fill(normalMap, -1);
        int nNrm = 0;
        for (int i = 0; i < f; i++)
        {
            if (keepFaces[i] == 0) continue;
            for (int k = 0; k < lod.FaceVertCount[i]; k++)
            {
                uint ni = lod.FaceNormals[i * 4 + k];
                if (ni < nNormals && normalMap[ni] < 0) normalMap[ni] = nNrm++;
            }
        }
        var normals = new float[nNrm * 3];
        for (int i = 0; i < nNormals; i++)
            if (normalMap[i] >= 0) Array.Copy(lod.Normals, i * 3, normals, normalMap[i] * 3, 3);

        var positions = new float[nPts * 3];
        var flags = new uint[nPts];
        for (int i = 0; i < n; i++)
        {
            int j = pointMap[i];
            if (j < 0) continue;
            Array.Copy(lod.Positions, i * 3, positions, j * 3, 3);
            flags[j] = lod.PointFlags[i];
        }

        var fa = FaceArrays.Allocate(nFaces);
        int q = 0;
        for (int i = 0; i < f; i++)
        {
            if (keepFaces[i] == 0) continue;
            int nv = lod.FaceVertCount[i];
            fa.VertCount[q] = (byte)nv;
            for (int k = 0; k < nv; k++)
            {
                int pi = pointMap[lod.FacePoints[i * 4 + k]];
                if (pi < 0) throw new InvalidOperationException($"Compact: kept face {i} references removed point {lod.FacePoints[i * 4 + k]}");
                fa.Points[q * 4 + k] = (uint)pi;
                uint ni = lod.FaceNormals[i * 4 + k];
                fa.Normals[q * 4 + k] = ni < nNormals ? (uint)normalMap[ni] : 0;
                fa.UV[q * 8 + k * 2] = lod.FaceUV[i * 8 + k * 2];
                fa.UV[q * 8 + k * 2 + 1] = lod.FaceUV[i * 8 + k * 2 + 1];
            }
            fa.Flags[q] = lod.FaceFlags[i];
            fa.Texture[q] = lod.FaceTexture[i];
            fa.Material[q] = lod.FaceMaterial[i];
            q++;
        }

        var tags = lod.Tags.Select(t => t switch
        {
            SelectionTag s => s with { Points = FilterBytes(s.Points, keepPoints, nPts), Faces = FilterBytes(s.Faces, keepFaces, nFaces) },
            StateTag s => s with { Points = FilterBytes(s.Points, keepPoints, nPts), Faces = FilterBytes(s.Faces, keepFaces, nFaces) },
            UVSetTag u => u with { UV = FilterFaceFloats(u.UV, keepFaces, nFaces) },
            MassTag m => m with { Mass = FilterPointFloats(m.Mass, pointMap, nPts, 1) },
            AnimationTag a => a with { Positions = FilterPointFloats(a.Positions, pointMap, nPts, 3) },
            SharpEdgesTag e => e with { Edges = RemapEdges(e.Edges, pointMap) },
            _ => t,
        }).ToImmutableArray();

        return (lod with { Positions = positions, PointFlags = flags, Normals = normals, Tags = tags }).WithFaces(fa);
    }

    static float[] FilterFaceFloats(float[] uv, byte[] keepFaces, int nFaces)
    {
        var o = new float[nFaces * 8];
        int q = 0;
        for (int i = 0; i < keepFaces.Length; i++)
            if (keepFaces[i] != 0) Array.Copy(uv, i * 8, o, 8 * q++, 8);
        return o;
    }

    static float[] FilterPointFloats(float[] src, int[] pointMap, int nPts, int stride)
    {
        var o = new float[nPts * stride];
        for (int i = 0; i < pointMap.Length; i++)
            if (pointMap[i] >= 0) Array.Copy(src, i * stride, o, pointMap[i] * stride, stride);
        return o;
    }

    static uint[] RemapEdges(uint[] edges, int[] pointMap)
    {
        var o = new List<uint>(edges.Length);
        for (int i = 0; i + 1 < edges.Length; i += 2)
        {
            int a = edges[i] < pointMap.Length ? pointMap[edges[i]] : -1;
            int b = edges[i + 1] < pointMap.Length ? pointMap[edges[i + 1]] : -1;
            if (a >= 0 && b >= 0)
            {
                o.Add((uint)a);
                o.Add((uint)b);
            }
        }
        return o.ToArray();
    }

    /// <summary>Copies the points in <paramref name="pointMask"/> (plus points used by faces in <paramref name="faceMask"/>) and those faces into a fragment.</summary>
    public static Fragment Extract(Lod lod, byte[] pointMask, byte[] faceMask)
    {
        var keepPts = PointsUsedByFaces(lod, faceMask);
        for (int i = 0; i < lod.PointCount; i++)
            if (pointMask[i] != 0) keepPts[i] = 1;
        var keepFaces = new byte[lod.FaceCount];
        for (int i = 0; i < keepFaces.Length; i++) keepFaces[i] = faceMask[i] != 0 ? (byte)1 : (byte)0;
        var sub = Compact(lod, keepPts, keepFaces);

        // Re-pool strings so the fragment does not carry the whole pool.
        var strings = ImmutableArray.CreateBuilder<string>();
        strings.Add("");
        var map = new Dictionary<uint, uint> { [0] = 0 };
        uint Remap(uint i)
        {
            if (!map.TryGetValue(i, out uint r))
            {
                r = (uint)strings.Count;
                strings.Add(sub.Strings[(int)i]);
                map[i] = r;
            }
            return r;
        }
        var tex = new uint[sub.FaceCount];
        var mat = new uint[sub.FaceCount];
        for (int i = 0; i < sub.FaceCount; i++)
        {
            tex[i] = Remap(sub.FaceTexture[i]);
            mat[i] = Remap(sub.FaceMaterial[i]);
        }

        return new Fragment
        {
            Positions = sub.Positions,
            PointFlags = sub.PointFlags,
            Normals = sub.Normals,
            Faces = sub.Faces with { Texture = tex, Material = mat },
            Strings = strings.ToImmutable(),
            Selections = sub.Selections
                .Where(s => s.Points.AsSpan().ContainsAnyExcept((byte)0) || s.Faces.AsSpan().ContainsAnyExcept((byte)0))
                .Select(s => new NamedWeights(s.Name, s.Points, s.Faces))
                .ToImmutableArray(),
            UVSets = sub.UVSets.Select(u => (u.Stage, u.UV)).ToImmutableArray(),
            Mass = sub.MassTag?.Mass,
        };
    }

    static T[] Concat<T>(T[] a, T[] b)
    {
        var o = new T[a.Length + b.Length];
        a.CopyTo(o, 0);
        b.CopyTo(o, a.Length);
        return o;
    }

    /// <summary>
    /// Appends a fragment to a LOD. Named selections merge by name (case-insensitive); missing selections and
    /// UV stages are created. New points are [oldPointCount, …), new faces [oldFaceCount, …).
    /// </summary>
    /// <param name="lod">Target LOD (not modified).</param>
    /// <param name="frag">Geometry to append.</param>
    /// <param name="selectNew">Make the appended elements the current selection (everything else deselected).</param>
    public static Lod Append(Lod lod, Fragment frag, bool selectNew = true)
    {
        int n0 = lod.PointCount, f0 = lod.FaceCount, m0 = lod.NormalCount;
        int fn = frag.PointCount, ff = frag.FaceCount;

        var strings = lod.Strings;
        var strMap = new uint[frag.Strings.Length];
        for (int i = 0; i < frag.Strings.Length; i++) strMap[i] = Lod.Intern(ref strings, frag.Strings[i]);

        var fa = FaceArrays.Allocate(f0 + ff);
        lod.FaceVertCount.CopyTo(fa.VertCount, 0);
        lod.FacePoints.CopyTo(fa.Points, 0);
        lod.FaceNormals.CopyTo(fa.Normals, 0);
        lod.FaceUV.CopyTo(fa.UV, 0);
        lod.FaceFlags.CopyTo(fa.Flags, 0);
        lod.FaceTexture.CopyTo(fa.Texture, 0);
        lod.FaceMaterial.CopyTo(fa.Material, 0);
        int fragNormals = frag.Normals.Length / 3;
        for (int i = 0; i < ff; i++)
        {
            int d = f0 + i, nv = frag.Faces.VertCount[i];
            fa.VertCount[d] = (byte)nv;
            for (int k = 0; k < nv; k++)
            {
                fa.Points[d * 4 + k] = frag.Faces.Points[i * 4 + k] + (uint)n0;
                uint ni = frag.Faces.Normals[i * 4 + k];
                fa.Normals[d * 4 + k] = ni < fragNormals ? ni + (uint)m0 : 0;
                fa.UV[d * 8 + k * 2] = frag.Faces.UV[i * 8 + k * 2];
                fa.UV[d * 8 + k * 2 + 1] = frag.Faces.UV[i * 8 + k * 2 + 1];
            }
            fa.Flags[d] = frag.Faces.Flags[i];
            fa.Texture[d] = strMap[frag.Faces.Texture[i]];
            fa.Material[d] = strMap[frag.Faces.Material[i]];
        }

        var fragSel = new Dictionary<string, NamedWeights>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in frag.Selections) fragSel.TryAdd(s.Name, s);
        var fragUV = frag.UVSets.ToDictionary(u => u.Stage, u => u.UV);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zerosP = new byte[fn];
        var zerosF = new byte[ff];
        var onesP = Enumerable.Repeat((byte)1, fn).ToArray();
        var onesF = Enumerable.Repeat((byte)1, ff).ToArray();
        float[] UvOf(uint stage) => fragUV.TryGetValue(stage, out var uv) ? uv : stage == 0 ? frag.Faces.UV : new float[ff * 8];

        bool hasSelected = false;
        var tags = ImmutableArray.CreateBuilder<Tag>(lod.Tags.Length + frag.Selections.Length + 2);
        foreach (var t in lod.Tags)
        {
            switch (t)
            {
                case SelectionTag s:
                {
                    fragSel.TryGetValue(s.Name, out var w);
                    if (w is not null) used.Add(s.Name);
                    tags.Add(s with { Points = Concat(s.Points, w?.Points ?? zerosP), Faces = Concat(s.Faces, w?.Faces ?? zerosF) });
                    break;
                }
                case StateTag { Kind: StateKind.Selected } s:
                    hasSelected = true;
                    tags.Add(selectNew
                        ? s with { Points = Concat(new byte[n0], onesP), Faces = Concat(new byte[f0], onesF) }
                        : s with { Points = Concat(s.Points, zerosP), Faces = Concat(s.Faces, zerosF) });
                    break;
                case StateTag s:
                    tags.Add(s with { Points = Concat(s.Points, zerosP), Faces = Concat(s.Faces, zerosF) });
                    break;
                case UVSetTag u:
                    tags.Add(u with { UV = Concat(u.UV, UvOf(u.Stage)) });
                    break;
                case MassTag m:
                    tags.Add(m with { Mass = Concat(m.Mass, frag.Mass ?? new float[fn]) });
                    break;
                case AnimationTag a:
                    tags.Add(a with { Positions = Concat(a.Positions, frag.Positions) });
                    break;
                default:
                    tags.Add(t);
                    break;
            }
        }

        // Selections only the fragment has: insert after the last named selection.
        var newSel = new List<Tag>();
        foreach (var s in frag.Selections)
        {
            if (!used.Add(s.Name)) continue;
            newSel.Add(new SelectionTag(1, s.Name, Concat(new byte[n0], s.Points), Concat(new byte[f0], s.Faces)));
        }
        if (newSel.Count > 0)
        {
            int at = -1;
            for (int i = 0; i < tags.Count; i++)
                if (tags[i] is SelectionTag) at = i;
            tags.InsertRange(at < 0 ? tags.Count : at + 1, newSel);
        }
        foreach (var (stage, uv) in frag.UVSets)
        {
            if (tags.Any(t => t is UVSetTag u && u.Stage == stage)) continue;
            tags.Add(new UVSetTag(1, stage, Concat(new float[f0 * 8], uv)));
        }
        if (selectNew && !hasSelected && (fn > 0 || ff > 0))
            tags.Insert(0, new StateTag(1, StateKind.Selected, Concat(new byte[n0], onesP), Concat(new byte[f0], onesF)));
        if (frag.Mass is not null && !tags.Any(t => t is MassTag))
            tags.Insert(0, new MassTag(1, Concat(new float[n0], frag.Mass)));

        return (lod with
        {
            Positions = Concat(lod.Positions, frag.Positions),
            PointFlags = Concat(lod.PointFlags, frag.PointFlags),
            Normals = Concat(lod.Normals, frag.Normals),
            Strings = strings,
            Tags = tags.ToImmutable(),
        }).WithFaces(fa);
    }

    /// <summary>
    /// Deletes the selection. Points mode: selected points and every face using one, plus selected faces.
    /// Faces mode: selected faces, then selected points no remaining face uses.
    /// </summary>
    public static Lod DeleteSelection(Lod lod, byte[] points, byte[] faces, bool facesMode)
    {
        var keepFaces = new byte[lod.FaceCount];
        var keepPoints = new byte[lod.PointCount];
        if (!facesMode)
        {
            var touched = FacesUsingPoints(lod, points);
            for (int f = 0; f < keepFaces.Length; f++) keepFaces[f] = touched[f] == 0 && faces[f] == 0 ? (byte)1 : (byte)0;
            for (int i = 0; i < keepPoints.Length; i++) keepPoints[i] = points[i] == 0 ? (byte)1 : (byte)0;
        }
        else
        {
            for (int f = 0; f < keepFaces.Length; f++) keepFaces[f] = faces[f] == 0 ? (byte)1 : (byte)0;
            var stillUsed = PointsUsedByFaces(lod, keepFaces);
            for (int i = 0; i < keepPoints.Length; i++) keepPoints[i] = points[i] == 0 || stillUsed[i] != 0 ? (byte)1 : (byte)0;
        }
        return Compact(lod, keepPoints, keepFaces);
    }

    /// <summary>
    /// Applies a column-major 4×4 matrix (raw P3D coordinates) to the points in <paramref name="mask"/> (all
    /// when null). With <paramref name="useWeights"/>, encoded weights act as soft-selection factors.
    /// </summary>
    public static Lod TransformPoints(Lod lod, byte[]? mask, ReadOnlySpan<double> m, bool useWeights = false)
    {
        var p = (float[])lod.Positions.Clone();
        for (int i = 0; i < lod.PointCount; i++)
        {
            byte b = mask is null ? (byte)1 : mask[i];
            if (b == 0) continue;
            double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
            double nx = m[0] * x + m[4] * y + m[8] * z + m[12];
            double ny = m[1] * x + m[5] * y + m[9] * z + m[13];
            double nz = m[2] * x + m[6] * y + m[10] * z + m[14];
            double w = useWeights && b != 1 ? (256 - b) / 255.0 : 1;
            p[i * 3] = (float)(x + (nx - x) * w);
            p[i * 3 + 1] = (float)(y + (ny - y) * w);
            p[i * 3 + 2] = (float)(z + (nz - z) * w);
        }
        return lod with { Positions = p };
    }

    /// <summary>
    /// Recalculates smooth per-point normals in the P3D stored convention (normalize Σ cross(P1-P0, P2-P0),
    /// pointing into a correctly wound mesh). Afterwards NormalCount == PointCount and each used slot
    /// references its point's normal.
    /// </summary>
    public static Lod RecalcNormals(Lod lod)
    {
        int n = lod.PointCount;
        var acc = new double[n * 3];
        var p = lod.Positions;
        var fp = lod.FacePoints;
        for (int f = 0; f < lod.FaceCount; f++)
        {
            int nv = lod.FaceVertCount[f];
            double nx = 0, ny = 0, nz = 0;
            uint a = fp[f * 4];
            for (int k = 1; k + 1 < nv; k++)
            {
                uint b = fp[f * 4 + k], c = fp[f * 4 + k + 1];
                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                nx += uy * vz - uz * vy;
                ny += uz * vx - ux * vz;
                nz += ux * vy - uy * vx;
            }
            for (int k = 0; k < nv; k++)
            {
                uint q = fp[f * 4 + k];
                acc[q * 3] += nx;
                acc[q * 3 + 1] += ny;
                acc[q * 3 + 2] += nz;
            }
        }
        var normals = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            double x = acc[i * 3], y = acc[i * 3 + 1], z = acc[i * 3 + 2];
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l > 0)
            {
                normals[i * 3] = (float)(x / l);
                normals[i * 3 + 1] = (float)(y / l);
                normals[i * 3 + 2] = (float)(z / l);
            }
            else normals[i * 3 + 1] = 1;
        }
        var faceNormals = new uint[lod.FaceCount * 4];
        for (int f = 0; f < lod.FaceCount; f++)
            for (int k = 0; k < lod.FaceVertCount[f]; k++) faceNormals[f * 4 + k] = fp[f * 4 + k];
        return lod with { Normals = normals, FaceNormals = faceNormals };
    }

    /// <summary>Sets texture and/or material (null = unchanged) of the faces in <paramref name="faceMask"/>.</summary>
    public static Lod SetFaceTextures(Lod lod, byte[] faceMask, string? texture, string? material)
    {
        var strings = lod.Strings;
        var next = lod;
        if (texture is not null)
        {
            uint ti = Lod.Intern(ref strings, texture);
            var arr = (uint[])lod.FaceTexture.Clone();
            for (int f = 0; f < arr.Length; f++)
                if (faceMask[f] != 0) arr[f] = ti;
            next = next with { FaceTexture = arr };
        }
        if (material is not null)
        {
            uint mi = Lod.Intern(ref strings, material);
            var arr = (uint[])lod.FaceMaterial.Clone();
            for (int f = 0; f < arr.Length; f++)
                if (faceMask[f] != 0) arr[f] = mi;
            next = next with { FaceMaterial = arr };
        }
        return next with { Strings = strings };
    }
}
