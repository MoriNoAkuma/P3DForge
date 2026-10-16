using System.Collections.Immutable;

namespace P3DForge.Core.Model;

/// <summary>
/// One LOD of an MLOD model, stored as structure-of-arrays.
/// <para>
/// PERSISTENCE CONVENTION (critical for undo): a <see cref="Lod"/> and its arrays are immutable once
/// they are part of a committed document. Operations never write into an existing array; they copy the
/// arrays they change (<c>(float[])arr.Clone()</c>) and return a new Lod via a <c>with</c> expression.
/// Unchanged arrays are shared between undo snapshots, so a step costs only what changed.
/// </para>
/// Faces have 4 vertex slots each; slot 3 is unused for triangles but its bytes are preserved.
/// </summary>
public sealed record Lod
{
    /// <summary>LOD resolution / type value (see <see cref="LodNames"/>).</summary>
    public float Resolution { get; init; }
    public uint Major { get; init; } = 28;
    public uint Minor { get; init; } = 256;
    /// <summary>Unknown header flags (0 in every known file); preserved verbatim.</summary>
    public uint HeaderFlags { get; init; }

    /// <summary>xyz per point, raw P3D (left-handed) coordinates.</summary>
    public float[] Positions { get; init; } = [];
    public uint[] PointFlags { get; init; } = [];
    /// <summary>xyz per normal.</summary>
    public float[] Normals { get; init; } = [];

    /// <summary>3 or 4 per face.</summary>
    public byte[] FaceVertCount { get; init; } = [];
    /// <summary>Point index per vertex slot, stride 4.</summary>
    public uint[] FacePoints { get; init; } = [];
    /// <summary>Normal index per vertex slot, stride 4.</summary>
    public uint[] FaceNormals { get; init; } = [];
    /// <summary>(u, v) per vertex slot, stride 8 — UV set 0 as stored in the face records.</summary>
    public float[] FaceUV { get; init; } = [];
    public uint[] FaceFlags { get; init; } = [];
    /// <summary>Index into <see cref="Strings"/>.</summary>
    public uint[] FaceTexture { get; init; } = [];
    /// <summary>Index into <see cref="Strings"/>.</summary>
    public uint[] FaceMaterial { get; init; } = [];

    /// <summary>Pool of texture/material paths. Index 0 is always "".</summary>
    public ImmutableArray<string> Strings { get; init; } = [""];

    /// <summary>Tags in file order (excluding the terminating #EndOfFile#).</summary>
    public ImmutableArray<Tag> Tags { get; init; } = [];

    public int PointCount => PointFlags.Length;
    public int FaceCount => FaceVertCount.Length;
    public int NormalCount => Normals.Length / 3;

    /// <summary>An empty LOD.</summary>
    public static Lod Empty(float resolution) => new() { Resolution = resolution };

    public string Texture(int face) => Strings[(int)FaceTexture[face]];
    public string Material(int face) => Strings[(int)FaceMaterial[face]];

    /// <summary>The face arrays as one value (for fragments and bulk copies).</summary>
    public FaceArrays Faces => new(FaceVertCount, FacePoints, FaceNormals, FaceUV, FaceFlags, FaceTexture, FaceMaterial);

    /// <summary>Returns a copy with the given face arrays.</summary>
    public Lod WithFaces(FaceArrays f) => this with
    {
        FaceVertCount = f.VertCount,
        FacePoints = f.Points,
        FaceNormals = f.Normals,
        FaceUV = f.UV,
        FaceFlags = f.Flags,
        FaceTexture = f.Texture,
        FaceMaterial = f.Material,
    };

    // ---- tag queries ------------------------------------------------------------------------------

    public IEnumerable<SelectionTag> Selections => Tags.OfType<SelectionTag>();

    /// <summary>Case-insensitive named selection lookup (like the engine).</summary>
    public SelectionTag? Selection(string name)
    {
        foreach (var t in Tags)
            if (t is SelectionTag s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    public IEnumerable<PropertyTag> Properties => Tags.OfType<PropertyTag>();

    /// <summary>Value of the first property with this key (case-insensitive), or null.</summary>
    public string? Property(string key)
    {
        foreach (var t in Tags)
            if (t is PropertyTag p && string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public IEnumerable<UVSetTag> UVSets => Tags.OfType<UVSetTag>();

    public UVSetTag? UVSet(uint stage)
    {
        foreach (var t in Tags)
            if (t is UVSetTag u && u.Stage == stage) return u;
        return null;
    }

    public MassTag? MassTag => Tags.OfType<MassTag>().FirstOrDefault();
    public SharpEdgesTag? SharpEdges => Tags.OfType<SharpEdgesTag>().FirstOrDefault();
    public IEnumerable<AnimationTag> Animations => Tags.OfType<AnimationTag>();

    public StateTag? State(StateKind kind)
    {
        foreach (var t in Tags)
            if (t is StateTag s && s.Kind == kind) return s;
        return null;
    }

    /// <summary>Index of <paramref name="s"/> in <see cref="Strings"/>, or -1.</summary>
    public int FindString(string s) => Strings.IndexOf(s);

    /// <summary>Returns the pool index for <paramref name="s"/>, appending it (to a copy of the pool) if needed.</summary>
    public static uint Intern(ref ImmutableArray<string> strings, string s)
    {
        int i = strings.IndexOf(s);
        if (i >= 0) return (uint)i;
        strings = strings.Add(s);
        return (uint)(strings.Length - 1);
    }
}

/// <summary>Face arrays (structure of arrays). Stride 4 for points/normals, 8 for UVs.</summary>
public readonly record struct FaceArrays(
    byte[] VertCount,
    uint[] Points,
    uint[] Normals,
    float[] UV,
    uint[] Flags,
    uint[] Texture,
    uint[] Material)
{
    public int Count => VertCount.Length;

    /// <summary>Zero-filled arrays for <paramref name="count"/> faces.</summary>
    public static FaceArrays Allocate(int count) => new(
        new byte[count], new uint[count * 4], new uint[count * 4], new float[count * 8],
        new uint[count], new uint[count], new uint[count]);
}

/// <summary>The whole document: an ordered list of LODs. Immutable like <see cref="Lod"/>.</summary>
public sealed record P3DModel(ImmutableArray<Lod> Lods, uint Version = 257)
{
    public P3DModel WithLod(int index, Lod lod) => this with { Lods = Lods.SetItem(index, lod) };

    public static P3DModel Empty() => new([Lod.Empty(1f)]);
}
