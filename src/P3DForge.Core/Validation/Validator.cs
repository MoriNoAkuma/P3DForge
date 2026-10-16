using P3DForge.Core.Model;
using P3DForge.Core.Topology;

namespace P3DForge.Core.Validation;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One finding. <see cref="LodIndex"/> = -1 for model-level issues. <see cref="Points"/>/<see cref="Faces"/> are the
/// offending elements of that LOD (what "select" selects); <see cref="Hint"/> says how to fix it.
/// </summary>
/// <see cref="Fix"/> names an automatic repair (<see cref="ValidationFixes"/>), null when it has to be fixed by hand.
public sealed record ValidationIssue(Severity Severity, string Rule, string Message, int LodIndex = -1, int[]? Points = null, int[]? Faces = null, string? Hint = null, string? Fix = null)
{
    public bool CanSelect => LodIndex >= 0 && (Points is { Length: > 0 } || Faces is { Length: > 0 });
    public bool CanFix => Fix is not null && LodIndex >= 0;
}

/// <summary>What the rules may look at. Expensive per-LOD analyses are computed once and shared between rules.</summary>
public sealed class ValidationContext
{
    public required P3DModel Model { get; init; }
    /// <summary>Model path (texture, material, proxy) → absolute file path under the project root; null = not a file.</summary>
    public Func<string, string?>? Resolve { get; init; }
    /// <summary>File existence test (the app caches it); null skips the missing-file checks.</summary>
    public Func<string, bool>? Exists { get; init; }
    public CancellationToken Cancel { get; init; }
    /// <summary>What the model is in game (from config.cpp); Unknown skips the kind-specific DayZ rules.</summary>
    public ModelKind Kind { get; init; }
    /// <summary>Where <see cref="Kind"/> comes from, for messages ("CfgWeapons ▸ AKM").</summary>
    public string? KindSource { get; init; }
    /// <summary>model.cfg skeleton bones of the model (own and inherited).</summary>
    public IReadOnlyList<string> SkeletonBones { get; init; } = [];
    /// <summary>Bones that model.cfg animations move.</summary>
    public IReadOnlyList<string> AnimatedBones { get; init; } = [];
    /// <summary>hiddenSelections[] of the model's config.cpp classes.</summary>
    public IReadOnlyList<string> HiddenSelections { get; init; } = [];

    readonly Dictionary<int, ComponentReport[]> components = [];
    readonly Dictionary<int, FaceCheckResult> faceChecks = [];

    /// <summary>Components (proxies excluded) of a LOD with closedness and convexity, cached.</summary>
    public ComponentReport[] Components(int lod)
    {
        lock (components)
        {
            if (!components.TryGetValue(lod, out var r))
                components[lod] = r = ComponentAnalysis.Analyze(Model.Lods[lod]);
            return r;
        }
    }

    /// <summary>Face checks of a LOD, cached.</summary>
    public FaceCheckResult FaceChecks(int lod)
    {
        lock (faceChecks)
        {
            if (!faceChecks.TryGetValue(lod, out var r))
                faceChecks[lod] = r = Topology.FaceChecks.Check(Model.Lods[lod]);
            return r;
        }
    }

    public LodType TypeOf(int lod) => LodNames.TypeOf(Model.Lods[lod].Resolution).Type;

    public string LodName(int lod) => LodNames.Name(Model.Lods[lod].Resolution);
}

/// <summary>A validation rule: a small class producing issues for a model.</summary>
public interface IValidationRule
{
    string Id { get; }
    string Title { get; }
    IEnumerable<ValidationIssue> Check(ValidationContext ctx);
}

/// <summary>Runs the DayZ model rules (<see cref="Rules"/>; add more by appending to the list).</summary>
public static class Validator
{
    public static List<IValidationRule> Rules { get; } =
    [
        new MissingGeometryRule(),
        new DuplicateResolutionRule(),
        new GeometryMassRule(),
        new ComponentClosedRule(),
        new ComponentConvexRule(),
        new ComponentSelectionsRule(),
        new DegenerateFacesRule(),
        new NonPlanarQuadsRule(),
        new PathRule(),
        new EmptySelectionsRule(),
        new UntexturedFacesRule(),
        new AutocenterRule(),
        new DayZMemoryPointsRule(),
        new ShadowVolumeRule(),
        new LodOrderRule(),
        new CollisionLodsRule(),
        new BonesInLodsRule(),
        new HiddenSelectionsRule(),
        new BoneWeightsRule(),
        new UvMappingRule(),
        new SectionsRule(),
    ];

    /// <summary>
    /// Runs every rule (or <paramref name="rules"/>) and returns the issues sorted by LOD, then severity (errors first).
    /// A rule that throws produces an Error issue instead of aborting the run.
    /// </summary>
    public static List<ValidationIssue> Run(ValidationContext ctx, IEnumerable<IValidationRule>? rules = null)
    {
        var all = new List<ValidationIssue>();
        foreach (var rule in rules ?? Rules)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            try
            {
                all.AddRange(rule.Check(ctx));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                all.Add(new ValidationIssue(Severity.Error, rule.Id, $"{rule.Title}: internal error — {ex.Message}"));
            }
        }
        return [.. all.Select((v, i) => (v, i)).OrderBy(t => t.v.LodIndex).ThenByDescending(t => t.v.Severity).ThenBy(t => t.i).Select(t => t.v)];
    }

    /// <summary>"2 errors, 5 warnings, 1 info".</summary>
    public static string Summary(IReadOnlyCollection<ValidationIssue> issues)
    {
        int e = issues.Count(i => i.Severity == Severity.Error), w = issues.Count(i => i.Severity == Severity.Warning), n = issues.Count - e - w;
        if (issues.Count == 0) return "no issues";
        return $"{e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")}, {n} info";
    }
}
