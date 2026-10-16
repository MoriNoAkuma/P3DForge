using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Validation;

/// <summary>Automatic repairs named by <see cref="ValidationIssue.Fix"/>; each changes only the issue's LOD.</summary>
public static class ValidationFixes
{
    public const string SharpAll = "sharpAll";
    public const string ClearSurfaces = "clearSurfaces";
    public const string DeleteUvSet1 = "deleteUvSet1";
    /// <summary>Followed by the skeleton bones, '|'-separated.</summary>
    public const string NormalizeWeights = "normalizeWeights:";

    /// <summary>Short label for the button.</summary>
    public static string Label(string fix) => fix switch
    {
        SharpAll => "Make every edge sharp",
        ClearSurfaces => "Clear textures and materials",
        DeleteUvSet1 => "Delete UV set 1",
        _ when fix.StartsWith(NormalizeWeights, StringComparison.Ordinal) => "Scale bone weights to 100 %",
        _ => fix,
    };

    /// <summary>The model with the issue repaired (the same instance when there is nothing to do).</summary>
    public static P3DModel Apply(P3DModel model, ValidationIssue issue)
    {
        if (issue.Fix is not { } fix || issue.LodIndex < 0 || issue.LodIndex >= model.Lods.Length) return model;
        var lod = model.Lods[issue.LodIndex];
        var next = fix switch
        {
            SharpAll => FaceOps.SetSharpEdges(lod, MeshMath.Ones(lod.PointCount), sharp: true).Lod,
            ClearSurfaces => Attributes.SetFaceTextureMaterial(lod, MeshMath.Ones(lod.FaceCount), "", ""),
            DeleteUvSet1 => lod.UVSet(1) is null ? lod : Uv.DeleteSet(lod, 1),
            _ when fix.StartsWith(NormalizeWeights, StringComparison.Ordinal) => Normalize(lod, fix[NormalizeWeights.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries), issue.Points),
            _ => lod,
        };
        return ReferenceEquals(next, lod) ? model : model.WithLod(issue.LodIndex, next);
    }

    /// <summary>Scales the bone weights of <paramref name="points"/> so they add up to 100 %.</summary>
    static Lod Normalize(Lod lod, string[] bones, int[]? points)
    {
        if (points is null || points.Length == 0) return lod;
        var tagIndex = new List<int>();
        for (int i = 0; i < lod.Tags.Length; i++)
            if (lod.Tags[i] is SelectionTag s && bones.Any(b => string.Equals(b, s.Name, StringComparison.OrdinalIgnoreCase))) tagIndex.Add(i);
        var arrays = tagIndex.ToDictionary(i => i, i => (byte[])((SelectionTag)lod.Tags[i]).Points.Clone());
        bool changed = false;
        foreach (int p in points)
        {
            if (p < 0 || p >= lod.PointCount) continue;
            float sum = 0;
            foreach (var a in arrays.Values) sum += Weights.Decode(a[p]);
            if (sum <= 0) continue;
            foreach (var a in arrays.Values)
            {
                if (a[p] == 0) continue;
                byte b = Weights.Encode(Math.Min(1f, Weights.Decode(a[p]) / sum));
                if (b == 0) b = 255; // keep the point in the bone
                if (b != a[p])
                {
                    a[p] = b;
                    changed = true;
                }
            }
        }
        if (!changed) return lod;
        var tags = lod.Tags;
        foreach (var (i, a) in arrays) tags = tags.SetItem(i, (SelectionTag)tags[i] with { Points = a });
        return lod with { Tags = tags };
    }
}
