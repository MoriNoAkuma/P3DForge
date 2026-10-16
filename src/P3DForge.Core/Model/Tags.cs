namespace P3DForge.Core.Model;

/// <summary>
/// A tag of a LOD's TAGG section. Tags are immutable records; the order of <see cref="Lod.Tags"/> is
/// preserved so that unmodified files are written back byte for byte.
/// </summary>
/// <param name="Active">The "active" byte stored before the tag name (1 in practically every file).</param>
public abstract record Tag(byte Active)
{
    /// <summary>The name written to disk ("#Mass#", a selection name, ...).</summary>
    public abstract string DiskName { get; }
}

/// <summary>A named selection. Bytes are encoded weights (see <see cref="Weights"/>): 0 = not selected.</summary>
public sealed record SelectionTag(byte Active, string Name, byte[] Points, byte[] Faces) : Tag(Active)
{
    public override string DiskName => Name;
}

/// <summary><c>#Property#</c> — one named property. <paramref name="Raw"/> keeps a non-canonical 128-byte payload verbatim.</summary>
public sealed record PropertyTag(byte Active, string Key, string Value, byte[]? Raw) : Tag(Active)
{
    public override string DiskName => TagNames.Property;
}

/// <summary><c>#UVSet#</c> — <paramref name="UV"/> holds 8 floats (4 slots × u,v) per face; only used slots are serialized.</summary>
public sealed record UVSetTag(byte Active, uint Stage, float[] UV) : Tag(Active)
{
    public override string DiskName => TagNames.UVSet;
}

/// <summary><c>#Mass#</c> — one float per point.</summary>
public sealed record MassTag(byte Active, float[] Mass) : Tag(Active)
{
    public override string DiskName => TagNames.Mass;
}

/// <summary><c>#SharpEdges#</c> — flat list of point index pairs.</summary>
public sealed record SharpEdgesTag(byte Active, uint[] Edges) : Tag(Active)
{
    public override string DiskName => TagNames.SharpEdges;
}

public enum StateKind
{
    Selected,
    Hidden,
    Lock,
}

/// <summary><c>#Selected#</c>, <c>#Hidden#</c>, <c>#Lock#</c> — same layout as a named selection.</summary>
public sealed record StateTag(byte Active, StateKind Kind, byte[] Points, byte[] Faces) : Tag(Active)
{
    public override string DiskName => Kind switch
    {
        StateKind.Selected => TagNames.Selected,
        StateKind.Hidden => TagNames.Hidden,
        _ => TagNames.Lock,
    };
}

/// <summary><c>#Animation#</c> — one vertex-animation frame (xyz per point).</summary>
public sealed record AnimationTag(byte Active, float Time, float[] Positions) : Tag(Active)
{
    public override string DiskName => TagNames.Animation;
}

/// <summary>Any tag P3D Forge does not interpret (or whose size did not match); kept verbatim.</summary>
public sealed record UnknownTag(byte Active, string Name, byte[] Data) : Tag(Active)
{
    public override string DiskName => Name;
}

/// <summary>On-disk names of the interpreted special tags.</summary>
public static class TagNames
{
    public const string Property = "#Property#";
    public const string UVSet = "#UVSet#";
    public const string Mass = "#Mass#";
    public const string SharpEdges = "#SharpEdges#";
    public const string Selected = "#Selected#";
    public const string Hidden = "#Hidden#";
    public const string Lock = "#Lock#";
    public const string Animation = "#Animation#";
    public const string EndOfFile = "#EndOfFile#";
}
