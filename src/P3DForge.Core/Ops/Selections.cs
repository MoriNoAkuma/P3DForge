using System.Collections.Immutable;
using P3DForge.Core.Model;

namespace P3DForge.Core.Ops;

/// <summary>
/// Pure named-selection operations (Object Builder "Named Selections" window semantics).
/// <para>
/// Every method returns a NEW <see cref="Lod"/>/<see cref="P3DModel"/>, or the very same instance when
/// nothing changes (so <c>EditorState.Edit</c> records no empty undo step). Inputs and their arrays are
/// never written; masks passed in are copied, never stored by reference.
/// </para>
/// A selection is addressed either by name (case-insensitive like the engine; the FIRST matching tag) or
/// by its index in <see cref="Lod.Tags"/> — the index form addresses one of several tags with the same
/// name, which real files contain (e.g. two "component20" in btr.p3d LOD 6).
/// </summary>
public static class NamedSelections
{
    /// <summary>Error message for an invalid selection name, or null when it is valid.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "The name must not be empty";
        if (name.Trim().Length == 0) return "The name must not be blank";
        if (name.StartsWith('#')) return "Names starting with \"#\" are reserved for special tags";
        if (name.Contains('\0')) return "The name must not contain NUL characters";
        if (!IsLatin1(name)) return "The name contains characters that cannot be stored in a P3D file (latin1 only)";
        return null;
    }

    public static bool IsLatin1(string s)
    {
        foreach (char c in s)
            if (c > 0xFF) return false;
        return true;
    }

    static void AssertName(string name)
    {
        var err = Validate(name);
        if (err is not null) throw new ArgumentException($"Invalid selection name \"{name}\": {err}");
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Index in <see cref="Lod.Tags"/> of the first selection named <paramref name="name"/> (case-insensitive), or -1.</summary>
    public static int Find(Lod lod, string name)
    {
        var tags = lod.Tags;
        for (int i = 0; i < tags.Length; i++)
            if (tags[i] is SelectionTag s && Same(s.Name, name)) return i;
        return -1;
    }

    /// <summary>True if a selection named <paramref name="name"/> exists (case-insensitive), ignoring the tag at <paramref name="exceptIndex"/>.</summary>
    public static bool Exists(Lod lod, string name, int exceptIndex = -1)
    {
        var tags = lod.Tags;
        for (int i = 0; i < tags.Length; i++)
            if (i != exceptIndex && tags[i] is SelectionTag s && Same(s.Name, name)) return true;
        return false;
    }

    static (int Index, SelectionTag Tag) Require(Lod lod, int tagIndex)
    {
        if (tagIndex < 0 || tagIndex >= lod.Tags.Length || lod.Tags[tagIndex] is not SelectionTag s)
            throw new ArgumentException($"Tag {tagIndex} is not a named selection");
        return (tagIndex, s);
    }

    static int RequireName(Lod lod, string name)
    {
        int i = Find(lod, name);
        if (i < 0) throw new ArgumentException($"Selection \"{name}\" does not exist");
        return i;
    }

    static void CheckMask(Lod lod, SelectionMask mask)
    {
        if (mask.Points.Length != lod.PointCount || mask.Faces.Length != lod.FaceCount)
            throw new ArgumentException($"Selection mask sized {mask.Points.Length}/{mask.Faces.Length}, LOD has {lod.PointCount}/{lod.FaceCount}");
    }

    /// <summary>Index after the last named-selection tag (where new selections are inserted), or the end.</summary>
    public static int InsertionIndex(Lod lod)
    {
        for (int i = lod.Tags.Length - 1; i >= 0; i--)
            if (lod.Tags[i] is SelectionTag) return i + 1;
        return lod.Tags.Length;
    }

    /// <summary>
    /// Creates a named selection holding <paramref name="mask"/> (non-zero bytes kept as they are, i.e. weights are
    /// preserved), inserted after the last existing named selection. Throws if the name is invalid or already used
    /// (case-insensitive) — use <see cref="Redefine(Lod, string, SelectionMask)"/> to replace an existing one.
    /// </summary>
    public static Lod Create(Lod lod, string name, SelectionMask mask)
    {
        AssertName(name);
        CheckMask(lod, mask);
        if (Exists(lod, name)) throw new ArgumentException($"Selection \"{name}\" already exists");
        var tag = new SelectionTag(1, name, (byte[])mask.Points.Clone(), (byte[])mask.Faces.Clone());
        return lod with { Tags = lod.Tags.Insert(InsertionIndex(lod), tag) };
    }

    /// <summary>Deletes EVERY selection named <paramref name="name"/> (case-insensitive). Same Lod if none matched.</summary>
    public static Lod Delete(Lod lod, string name)
    {
        var b = ImmutableArray.CreateBuilder<Tag>(lod.Tags.Length);
        foreach (var t in lod.Tags)
            if (!(t is SelectionTag s && Same(s.Name, name))) b.Add(t);
        return b.Count == lod.Tags.Length ? lod : lod with { Tags = b.ToImmutable() };
    }

    /// <summary>Deletes the selection tag at <paramref name="tagIndex"/>. Same Lod if it is not a selection.</summary>
    public static Lod DeleteAt(Lod lod, int tagIndex)
    {
        if (tagIndex < 0 || tagIndex >= lod.Tags.Length || lod.Tags[tagIndex] is not SelectionTag) return lod;
        return lod with { Tags = lod.Tags.RemoveAt(tagIndex) };
    }

    /// <summary>
    /// Renames a selection (keeps its tag position, active byte and contents). Throws if it does not exist, the new
    /// name is invalid or used by ANOTHER selection; a case-only rename is allowed. Same Lod if the name is unchanged.
    /// </summary>
    public static Lod Rename(Lod lod, string name, string newName) => Rename(lod, RequireName(lod, name), newName);

    /// <inheritdoc cref="Rename(Lod, string, string)"/>
    public static Lod Rename(Lod lod, int tagIndex, string newName)
    {
        var (i, tag) = Require(lod, tagIndex);
        if (tag.Name == newName) return lod;
        AssertName(newName);
        if (Exists(lod, newName, i)) throw new ArgumentException($"Selection \"{newName}\" already exists");
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Name = newName }) };
    }

    /// <summary>Replaces the contents of a selection with <paramref name="mask"/> ("Redefine"). Same Lod if identical.</summary>
    public static Lod Redefine(Lod lod, string name, SelectionMask mask) => Redefine(lod, RequireName(lod, name), mask);

    /// <inheritdoc cref="Redefine(Lod, string, SelectionMask)"/>
    public static Lod Redefine(Lod lod, int tagIndex, SelectionMask mask)
    {
        CheckMask(lod, mask);
        var (i, tag) = Require(lod, tagIndex);
        if (tag.Points.AsSpan().SequenceEqual(mask.Points) && tag.Faces.AsSpan().SequenceEqual(mask.Faces)) return lod;
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Points = (byte[])mask.Points.Clone(), Faces = (byte[])mask.Faces.Clone() }) };
    }

    /// <summary>
    /// Adds the elements of <paramref name="mask"/> to a selection. Elements already in the selection keep their
    /// weight; newly added ones get the mask's byte (normally 1 = full weight).
    /// </summary>
    public static Lod Add(Lod lod, string name, SelectionMask mask) => Add(lod, RequireName(lod, name), mask);

    /// <inheritdoc cref="Add(Lod, string, SelectionMask)"/>
    public static Lod Add(Lod lod, int tagIndex, SelectionMask mask)
    {
        CheckMask(lod, mask);
        var (i, tag) = Require(lod, tagIndex);
        byte[]? points = null, faces = null;
        for (int k = 0; k < mask.Points.Length; k++)
            if (mask.Points[k] != 0 && tag.Points[k] == 0) (points ??= (byte[])tag.Points.Clone())[k] = mask.Points[k];
        for (int k = 0; k < mask.Faces.Length; k++)
            if (mask.Faces[k] != 0 && tag.Faces[k] == 0) (faces ??= (byte[])tag.Faces.Clone())[k] = mask.Faces[k];
        if (points is null && faces is null) return lod;
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Points = points ?? tag.Points, Faces = faces ?? tag.Faces }) };
    }

    /// <summary>Removes the elements of <paramref name="mask"/> (non-zero bytes) from a selection.</summary>
    public static Lod Remove(Lod lod, string name, SelectionMask mask) => Remove(lod, RequireName(lod, name), mask);

    /// <inheritdoc cref="Remove(Lod, string, SelectionMask)"/>
    public static Lod Remove(Lod lod, int tagIndex, SelectionMask mask)
    {
        CheckMask(lod, mask);
        var (i, tag) = Require(lod, tagIndex);
        byte[]? points = null, faces = null;
        for (int k = 0; k < mask.Points.Length; k++)
            if (mask.Points[k] != 0 && tag.Points[k] != 0) (points ??= (byte[])tag.Points.Clone())[k] = 0;
        for (int k = 0; k < mask.Faces.Length; k++)
            if (mask.Faces[k] != 0 && tag.Faces[k] != 0) (faces ??= (byte[])tag.Faces.Clone())[k] = 0;
        if (points is null && faces is null) return lod;
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Points = points ?? tag.Points, Faces = faces ?? tag.Faces }) };
    }

    /// <summary>
    /// Sets the weight (0..1) of the points in <paramref name="pointMask"/> in a selection ("Weights…"). Points not yet
    /// in the selection are added with that weight. Weight 0 (or below 1/255) removes the points, together with the
    /// selection's faces that use any removed point. Throws on NaN / out-of-range weights.
    /// </summary>
    public static Lod SetWeight(Lod lod, string name, byte[] pointMask, float weight) => SetWeight(lod, RequireName(lod, name), pointMask, weight);

    /// <inheritdoc cref="SetWeight(Lod, string, byte[], float)"/>
    public static Lod SetWeight(Lod lod, int tagIndex, byte[] pointMask, float weight)
    {
        if (!(weight >= 0 && weight <= 1)) throw new ArgumentOutOfRangeException(nameof(weight), $"Weight {weight} is outside 0…1");
        if (pointMask.Length != lod.PointCount) throw new ArgumentException($"Point mask sized {pointMask.Length}, LOD has {lod.PointCount}");
        var (i, tag) = Require(lod, tagIndex);
        byte b = Weights.Encode(weight);
        byte[]? points = null;
        for (int k = 0; k < pointMask.Length; k++)
            if (pointMask[k] != 0 && tag.Points[k] != b) (points ??= (byte[])tag.Points.Clone())[k] = b;
        if (points is null) return lod;
        var faces = tag.Faces;
        if (b == 0)
        {
            var fp = lod.FacePoints;
            for (int f = 0; f < lod.FaceCount; f++)
            {
                if (tag.Faces[f] == 0) continue;
                int nv = lod.FaceVertCount[f];
                for (int k = 0; k < nv; k++)
                    if (points[fp[f * 4 + k]] == 0)
                    {
                        if (ReferenceEquals(faces, tag.Faces)) faces = (byte[])tag.Faces.Clone();
                        faces[f] = 0;
                        break;
                    }
            }
        }
        return lod with { Tags = lod.Tags.SetItem(i, tag with { Points = points, Faces = faces }) };
    }

    /// <summary>True if the selection has no point and no face.</summary>
    public static bool IsEmpty(SelectionTag tag) =>
        !tag.Points.AsSpan().ContainsAnyExcept((byte)0) && !tag.Faces.AsSpan().ContainsAnyExcept((byte)0);

    /// <summary>Removes every named selection without points and faces ("Delete Empty").</summary>
    public static Lod DeleteEmpty(Lod lod)
    {
        var b = ImmutableArray.CreateBuilder<Tag>(lod.Tags.Length);
        foreach (var t in lod.Tags)
            if (!(t is SelectionTag s && IsEmpty(s))) b.Add(t);
        return b.Count == lod.Tags.Length ? lod : lod with { Tags = b.ToImmutable() };
    }

    /// <summary>Number of non-zero point and face bytes of a selection (the panel caches it per array instance).</summary>
    public static (int Points, int Faces) Counts(SelectionTag tag) =>
        (SelectionMask.CountNonZero(tag.Points), SelectionMask.CountNonZero(tag.Faces));

    /// <summary>
    /// True if <paramref name="bytes"/> is non-zero at every index in <paramref name="indices"/> (and the list is not
    /// empty). Early-exits on the first miss, so checking hundreds of selections stays cheap.
    /// </summary>
    public static bool ContainsAll(byte[] bytes, ReadOnlySpan<int> indices)
    {
        if (indices.Length == 0) return false;
        foreach (int i in indices)
            if ((uint)i >= (uint)bytes.Length || bytes[i] == 0) return false;
        return true;
    }

    /// <summary>Indices of the non-zero bytes.</summary>
    public static int[] Indices(byte[] mask)
    {
        int n = SelectionMask.CountNonZero(mask);
        var o = new int[n];
        int j = 0;
        for (int i = 0; i < mask.Length && j < n; i++)
            if (mask[i] != 0) o[j++] = i;
        return o;
    }

    /// <summary>A name not used by any selection of <paramref name="lod"/>: base, base1, base2, …</summary>
    public static string UniqueName(Lod lod, string baseName)
    {
        if (!Exists(lod, baseName)) return baseName;
        for (int n = 1; ; n++)
        {
            var candidate = baseName + n;
            if (!Exists(lod, candidate)) return candidate;
        }
    }

    // ---- whole-model variants ("(All LODs)" menu items) ------------------------------------------

    /// <summary>Deletes the selection <paramref name="name"/> (every case-insensitive match) in every LOD. Same model if absent everywhere.</summary>
    public static P3DModel DeleteAll(P3DModel model, string name)
    {
        var lods = model.Lods;
        for (int i = 0; i < model.Lods.Length; i++)
        {
            var next = Delete(model.Lods[i], name);
            if (!ReferenceEquals(next, model.Lods[i])) lods = lods.SetItem(i, next);
        }
        return lods == model.Lods ? model : model with { Lods = lods };
    }

    /// <summary>
    /// Renames the selection <paramref name="oldName"/> to <paramref name="newName"/> in every LOD that has it.
    /// Atomic: throws (changing nothing) if the new name is invalid or used by another selection in any affected LOD.
    /// </summary>
    public static P3DModel RenameAll(P3DModel model, string oldName, string newName)
    {
        AssertName(newName);
        var lods = model.Lods;
        for (int i = 0; i < model.Lods.Length; i++)
        {
            var lod = model.Lods[i];
            int idx = Find(lod, oldName);
            if (idx < 0) continue;
            var next = Rename(lod, idx, newName);
            if (!ReferenceEquals(next, lod)) lods = lods.SetItem(i, next);
        }
        return lods == model.Lods ? model : model with { Lods = lods };
    }

    /// <summary>Indices of the LODs that contain a selection named <paramref name="name"/> (case-insensitive).</summary>
    public static int[] LodsWith(P3DModel model, string name)
    {
        var o = new List<int>();
        for (int i = 0; i < model.Lods.Length; i++)
            if (Find(model.Lods[i], name) >= 0) o.Add(i);
        return o.ToArray();
    }
}

/// <summary>
/// Pure LOD-list operations on a <see cref="P3DModel"/>: insert in sorted position, delete, change resolution,
/// sort, free-resolution suggestions and "Copy Selection to LODs". Inputs are never modified (a duplicated LOD
/// shares every array with its source).
/// </summary>
public static class LodList
{
    /// <summary>True if two resolution values denote the same LOD (compared as stored float32).</summary>
    public static bool SameResolution(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

    /// <summary>Index of the LOD with this resolution (ignoring <paramref name="exceptIndex"/>), or -1.</summary>
    public static int Find(P3DModel model, float resolution, int exceptIndex = -1)
    {
        for (int i = 0; i < model.Lods.Length; i++)
            if (i != exceptIndex && SameResolution(model.Lods[i].Resolution, resolution)) return i;
        return -1;
    }

    /// <summary>True if the LODs are in ascending resolution order.</summary>
    public static bool IsSorted(P3DModel model)
    {
        for (int i = 1; i < model.Lods.Length; i++)
            if (model.Lods[i - 1].Resolution > model.Lods[i].Resolution) return false;
        return true;
    }

    /// <summary>Where a LOD with <paramref name="resolution"/> goes so that ascending order is kept (after equal values).</summary>
    public static int SortedInsertIndex(IReadOnlyList<Lod> lods, float resolution)
    {
        int i = 0;
        while (i < lods.Count && lods[i].Resolution <= resolution) i++;
        return i;
    }

    /// <summary>
    /// Inserts <paramref name="lod"/>. In a sorted model it goes to its sorted position; in an unsorted one (files can
    /// be written in any order) right after the LOD at <paramref name="afterIndex"/> (default: the end).
    /// </summary>
    public static (P3DModel Model, int Index) Insert(P3DModel model, Lod lod, int afterIndex = int.MaxValue)
    {
        int index = IsSorted(model)
            ? SortedInsertIndex(model.Lods, lod.Resolution)
            : Math.Clamp(afterIndex == int.MaxValue ? model.Lods.Length : afterIndex + 1, 0, model.Lods.Length);
        return (model with { Lods = model.Lods.Insert(index, lod) }, index);
    }

    /// <summary>Removes the LOD at <paramref name="index"/>. A model always keeps at least one LOD.</summary>
    public static P3DModel Remove(P3DModel model, int index)
    {
        if (model.Lods.Length <= 1) throw new InvalidOperationException("A model must keep at least one LOD");
        if (index < 0 || index >= model.Lods.Length) throw new ArgumentOutOfRangeException(nameof(index), $"No LOD {index}");
        return model with { Lods = model.Lods.RemoveAt(index) };
    }

    /// <summary>
    /// Changes the resolution of the LOD at <paramref name="index"/>. If the model was sorted, the LOD moves to its
    /// new sorted position. Throws if another LOD already has that resolution.
    /// </summary>
    public static (P3DModel Model, int Index) SetResolution(P3DModel model, int index, float resolution)
    {
        if (index < 0 || index >= model.Lods.Length) throw new ArgumentOutOfRangeException(nameof(index), $"No LOD {index}");
        var lod = model.Lods[index];
        if (Find(model, resolution, index) >= 0) throw new ArgumentException($"LOD \"{LodNames.Name(resolution)}\" already exists");
        if (SameResolution(lod.Resolution, resolution)) return (model, index);
        var next = lod with { Resolution = resolution };
        if (!IsSorted(model)) return (model.WithLod(index, next), index);
        var rest = model.Lods.RemoveAt(index);
        int at = SortedInsertIndex(rest, resolution);
        return (model with { Lods = rest.Insert(at, next) }, at);
    }

    /// <summary>Sorts LODs by ascending resolution (stable). <paramref name="activeIndex"/> is mapped to its new position.</summary>
    public static (P3DModel Model, int ActiveIndex) Sort(P3DModel model, int activeIndex)
    {
        if (IsSorted(model)) return (model, activeIndex);
        var order = Enumerable.Range(0, model.Lods.Length)
            .OrderBy(i => model.Lods[i].Resolution)
            .ThenBy(i => i)
            .ToArray();
        var lods = order.Select(i => model.Lods[i]).ToImmutableArray();
        return (model with { Lods = lods }, Math.Max(0, Array.IndexOf(order, activeIndex)));
    }

    /// <summary>
    /// A free resolution "near" <paramref name="resolution"/>: the next free index of the same indexed family
    /// (1.000 → 2.000, ShadowVolume 0 → ShadowVolume 1, …). Single-valued types (Geometry, Memory, …) fall back to the
    /// first free Edit LOD, which binarization drops — the natural place for a working copy.
    /// </summary>
    public static float FreeResolutionLike(P3DModel model, float resolution)
    {
        var (type, index) = LodNames.TypeOf(resolution);
        if (type.IsFamily)
        {
            double span = type.RangeEnd!.Value - type.Base;
            for (double n = Math.Floor(index) + 1; n < span; n++)
            {
                float r = (float)(type.Base + n);
                if (Find(model, r) < 0) return r;
            }
        }
        for (int n = 0; n < 10000; n++)
        {
            float r = LodNames.ResolutionFor("edit", n);
            if (Find(model, r) < 0) return r;
        }
        throw new InvalidOperationException("No free LOD resolution");
    }

    /// <summary>Default resolution offered by "New LOD": the next visual LOD after the last one (or 0).</summary>
    public static float DefaultNewResolution(P3DModel model)
    {
        double max = -1;
        foreach (var l in model.Lods)
            if (LodNames.TypeOf(l.Resolution).Type.Id == "visual") max = Math.Max(max, Math.Floor(l.Resolution));
        for (double n = max + 1; n < 1000; n++)
            if (Find(model, (float)n) < 0) return (float)n;
        return FreeResolutionLike(model, 0);
    }

    /// <summary>
    /// Copies the masked points/faces of LOD <paramref name="source"/> into every LOD in <paramref name="targets"/>
    /// (named selections, UV sets and mass travel along; the copies become the selection of each target LOD).
    /// </summary>
    public static P3DModel CopySelectionTo(P3DModel model, int source, IEnumerable<int> targets, SelectionMask mask)
    {
        if (source < 0 || source >= model.Lods.Length) throw new ArgumentOutOfRangeException(nameof(source), $"No LOD {source}");
        var frag = Structure.Extract(model.Lods[source], mask.Points, mask.Faces);
        if (frag.PointCount == 0 && frag.FaceCount == 0) return model;
        var lods = model.Lods;
        foreach (int t in targets.Distinct())
        {
            if (t == source || t < 0 || t >= lods.Length) continue;
            lods = lods.SetItem(t, Structure.Append(lods[t], frag, selectNew: true));
        }
        return lods == model.Lods ? model : model with { Lods = lods };
    }

    /// <summary>
    /// Resolution for a dialog choice: indexed families take <paramref name="value"/> as index (visual: the resolution
    /// itself), single types ignore it, "custom" uses it as the raw value. Returns an error message when out of range.
    /// </summary>
    public static (float Resolution, string? Error) ResolutionFromChoice(string typeId, double value)
    {
        if (!double.IsFinite(value)) return (0, "Enter a number");
        if (typeId == "custom") return ((float)value, null);
        var type = LodNames.Types.FirstOrDefault(t => t.Id == typeId);
        if (type is null) return (0, $"Unknown LOD type {typeId}");
        if (!type.IsFamily) return ((float)type.Base, null);
        double span = type.RangeEnd!.Value - type.Base;
        if (value < 0 || value >= span) return (0, $"{(type.Id == "visual" ? "Resolution" : "Index")} must be ≥ 0 and < {span:0}");
        return ((float)(type.Base + value), null);
    }

    /// <summary>Dialog choice (type id + value) describing an existing resolution.</summary>
    public static (string TypeId, double Value) ChoiceFromResolution(float resolution)
    {
        var (type, index) = LodNames.TypeOf(resolution);
        if (type.Id == "unknown") return ("custom", resolution);
        return (type.Id, type.IsFamily ? index : 0);
    }
}
