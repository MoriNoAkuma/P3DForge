using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Scene;

namespace P3DForge.App.Scene;

/// <summary>
/// Implementation behind <see cref="ProxyScene"/>: builds the instances of the active LOD (placement from the proxy
/// triangles, nested proxies flattened, models from <see cref="ProxyLibrary"/>), per-instance visibility (Objects
/// panel, kept per document, keyed by selection name) and selection (= the proxy triangle is selected in the editor).
/// Ordinary edits only recompute transforms; files are never reloaded here. UI thread only.
/// </summary>
public static class ProxySceneHost
{
    static EditorState Ed => AppServices.Editor;

    static bool installed;
    // placement cache (valid while positions, faces and the proxy selection tags are the same objects)
    static float[]? keyPositions;
    /// <summary>Animated positions of one LOD (model.cfg preview): proxy models follow their animated triangles.</summary>
    static float[]? previewPositions;
    static int previewLod = -1;
    static uint[]? keyFacePoints;
    static byte[]? keyVertCount;
    static uint[]? keyPointFlags;
    static readonly List<SelectionTag> keyTags = [];
    static List<PlacedProxy> placed = [];
    static bool[] triangleHidden = [];

    // flattened instances of the last build
    static List<FlatProxy> flat = [];
    static int[] topOf = []; // flat index → index in 'placed'

    static readonly Dictionary<string, HashSet<string>> hiddenByDoc = new(StringComparer.OrdinalIgnoreCase);
    static string lastSummary = "";

    /// <summary>Raised after every rebuild that changed the published list (Objects panel refresh).</summary>
    public static event Action? Rebuilt;

    /// <summary>Proxies placed in the active LOD (top level only).</summary>
    public static IReadOnlyList<PlacedProxy> TopLevel => placed;

    static string DocKey => Ed.Path ?? "<untitled>";

    static HashSet<string> Hidden
    {
        get
        {
            if (!hiddenByDoc.TryGetValue(DocKey, out var set)) hiddenByDoc[DocKey] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return set;
        }
    }

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Ed.Changed += OnChanged;
        ProxyLibrary.ModelsChanged += OnModelsChanged;
        Rebuild();
    }

    static void OnChanged(Change c)
    {
        if ((c & Change.Load) != 0)
        {
            ProxyLibrary.OnDocumentLoaded();
            // files cached for an earlier document (or this one, on Reload from Disk) may have changed on disk
            ProxyLibrary.Refresh(done: n =>
            {
                if (n > 0) Ed.Log($"Reloading {n} proxy model{(n == 1 ? "" : "s")} changed on disk");
            });
        }
        if ((c & (Change.Model | Change.ActiveLod | Change.Document | Change.Load)) != 0) Rebuild();
        else if ((c & Change.Selection) != 0) Rebuild(selectionOnly: true);
    }

    static void OnModelsChanged()
    {
        Rebuild();
        if (ProxyLibrary.PendingLoads > 0) return;
        var s = Summary();
        if (s.Length > 0 && s != lastSummary) Ed.Log(s);
        lastSummary = s;
    }

    /// <summary>"Proxies: 49 in LOD (12 models: 11 loaded (6 game ODOL), 1 missing)".</summary>
    public static string Summary()
    {
        var list = ProxyScene.Instances;
        if (list.Count == 0) return "";
        var models = new HashSet<ProxyModel>(ReferenceEqualityComparer.Instance);
        foreach (var i in list) models.Add(i.Model);
        int ok = 0, game = 0, odol = 0, missing = 0, failed = 0, loading = 0;
        foreach (var m in models)
        {
            if (m.Model is not null)
            {
                ok++;
                if (m.Binarized) game++;
            }
            else if (m.Error is null) loading++;
            else if (m.Error == ProxyFiles.OdolError) odol++;
            else if (m.Error.StartsWith("file not found", StringComparison.Ordinal)) missing++;
            else failed++;
        }
        int nested = list.Count(i => i.Depth > 0);
        var parts = new List<string> { game > 0 ? $"{ok} loaded ({game} game ODOL)" : $"{ok} loaded" };
        if (odol > 0) parts.Add($"{odol} unreadable ODOL");
        if (missing > 0) parts.Add($"{missing} missing");
        if (failed > 0) parts.Add($"{failed} failed");
        if (loading > 0) parts.Add($"{loading} loading");
        return $"Proxies: {list.Count - nested} in LOD{(nested > 0 ? $" + {nested} nested" : "")} ({models.Count} models: {string.Join(", ", parts)})";
    }

    /// <summary>Places proxies on <paramref name="positions"/> of LOD <paramref name="lodIndex"/> instead of its own (null = off).</summary>
    public static void SetPreview(int lodIndex, float[]? positions)
    {
        if (ReferenceEquals(previewPositions, positions) && previewLod == lodIndex) return;
        previewPositions = positions;
        previewLod = positions is null ? -1 : lodIndex;
        Rebuild();
    }

    static float[] PositionsOf(Lod lod) =>
        previewPositions is { } p && previewLod == Ed.ActiveLod && p.Length == lod.Positions.Length ? p : lod.Positions;

    static bool PlacementCurrent(Lod lod)
    {
        if (!ReferenceEquals(PositionsOf(lod), keyPositions) || !ReferenceEquals(lod.FacePoints, keyFacePoints) ||
            !ReferenceEquals(lod.FaceVertCount, keyVertCount) || !ReferenceEquals(lod.PointFlags, keyPointFlags)) return false;
        int n = 0;
        foreach (var t in lod.Tags)
        {
            if (t is not SelectionTag s || !Proxies.IsProxyName(s.Name)) continue;
            if (n >= keyTags.Count || !ReferenceEquals(keyTags[n], s)) return false;
            n++;
        }
        return n == keyTags.Count;
    }

    static void Place(Lod lod)
    {
        keyPositions = PositionsOf(lod);
        keyFacePoints = lod.FacePoints;
        keyVertCount = lod.FaceVertCount;
        keyPointFlags = lod.PointFlags;
        keyTags.Clear();
        foreach (var t in lod.Tags)
            if (t is SelectionTag s && Proxies.IsProxyName(s.Name)) keyTags.Add(s);
        placed = keyTags.Count == 0 ? [] : ProxyPlacement.Find(lod, ReferenceEquals(keyPositions, lod.Positions) ? null : keyPositions);
        triangleHidden = new bool[placed.Count];
        if (placed.Count == 0) return;
        var hidden = Selection.HiddenPoints(lod);
        for (int i = 0; i < placed.Count; i++)
        {
            var p = placed[i];
            triangleHidden[i] = H(hidden, p.A) || H(hidden, p.B) || H(hidden, p.C);
        }
    }

    static bool H(byte[] mask, uint i) => i < mask.Length && mask[i] != 0;

    /// <summary>Rebuilds the instances of the active LOD and publishes them when anything changed.</summary>
    public static void Rebuild() => Rebuild(selectionOnly: false);

    static void Rebuild(bool selectionOnly)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RebuildCore(selectionOnly);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > 25) Ed.Log($"[perf] proxy instances rebuild: {ms:0} ms ({flat.Count} instances)");
    }

    static void RebuildCore(bool selectionOnly)
    {
        var lod = Ed.Lod;
        bool current = PlacementCurrent(lod);
        if (!current) Place(lod);
        if (!selectionOnly || !current)
        {
            var rootKey = docFileKey = Ed.Path is { } path ? ProxyFiles.Key(path) : null;
            flat = placed.Count == 0 ? [] : ProxyFlatten.Flatten(placed, Lookup, rootKey);
            topOf = new int[flat.Count];
            for (int i = 0, k = 0; i < flat.Count; i++) topOf[i] = flat[i].Depth == 0 ? k++ : topOf[flat[i].Root];
        }
        Publish(lod);
    }

    static (string Key, IReadOnlyList<PlacedProxy>? Children) Lookup(string proxyPath)
    {
        var e = ProxyLibrary.Get(proxyPath);
        return (e.Key, e.Children);
    }

    static string? docFileKey;
    static ProxyModel? selfModel;

    /// <summary>A proxy of the edited model's own file is not drawn (it would show the saved copy over itself).</summary>
    static ProxyModel SelfModel(string file) =>
        selfModel is { } m && m.Path == file ? m : selfModel = new ProxyModel { Path = file, LodIndex = -1, Error = "the proxy refers to the edited model itself" };

    static void Publish(Lod lod)
    {
        var sel = Selection.PeekSelected(lod);
        var hidden = Hidden;
        var list = new ProxyInstance[flat.Count];
        var topSelected = new bool[placed.Count];
        if (sel is { } s)
            for (int i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                topSelected[i] = H(s.Points, p.A) && H(s.Points, p.B) && H(s.Points, p.C);
            }
        for (int i = 0; i < flat.Count; i++)
        {
            var f = flat[i];
            int top = topOf[i];
            // Only the Objects panel decides whether a proxy model is drawn: a proxy triangle hidden in the editor
            // (Hide, often saved in the file by Object Builder) is an editing convenience, not "hide the model".
            bool visible = !hidden.Contains(f.Name) && (f.Parent < 0 || list[f.Parent].Visible);
            var entry = ProxyLibrary.Get(f.Path);
            var model = docFileKey is not null && entry.Key == docFileKey ? SelfModel(entry.File) : entry.Model;
            list[i] = new ProxyInstance(f.Name, f.Path, f.Face, f.Depth, f.Transform, model, visible, topSelected[top]);
        }
        var old = ProxyScene.Instances;
        bool same = old.Count == list.Length;
        for (int i = 0; same && i < list.Length; i++) same = Equals(old[i], list[i]);
        if (same) return;
        ProxyScene.Publish(list);
        Rebuilt?.Invoke();
    }

    // ---- Objects panel API ----------------------------------------------------------------------------

    /// <summary>Index of the top-level instance that owns an instance (itself for top level), -1 when out of range.</summary>
    public static int RootOf(int instance) => instance >= 0 && instance < flat.Count ? flat[instance].Root : -1;

    /// <summary>The proxy triangle points (host LOD) of an instance's top-level proxy.</summary>
    public static PlacedProxy? TopLevelOf(int instance) => instance >= 0 && instance < topOf.Length && topOf[instance] < placed.Count ? placed[topOf[instance]] : null;

    /// <summary>Hidden in the editor (its triangle points are hidden), as opposed to hidden in the Objects panel.</summary>
    public static bool HiddenInEditor(int instance) => instance >= 0 && instance < topOf.Length && topOf[instance] < triangleHidden.Length && triangleHidden[topOf[instance]];

    public static bool IsHiddenByUser(string name) => Hidden.Contains(name);

    /// <summary>Shows / hides an instance (and its nested instances) in the viewport.</summary>
    public static void SetVisible(string name, bool visible)
    {
        if (visible) EnsureEnabled();
        if (visible ? !Hidden.Remove(name) : !Hidden.Add(name)) return;
        Publish(Ed.Lod);
    }

    /// <summary>Showing proxies is pointless while the "Proxies" master switch is off: turn it on (and remember that).</summary>
    static void EnsureEnabled()
    {
        if (ProxyScene.Enabled) return;
        Features.ObjectsFeature.SetOption("enabled", true);
        Ed.Log("Proxy models are drawn again (the Proxies switch in the Objects panel was off)");
    }

    /// <summary>Shows every instance (clears the hidden set of the document).</summary>
    /// <summary>Hides the selected instances (Objects panel sense; the edited model is not changed). Returns the count.</summary>
    public static int HideSelected()
    {
        int n = 0;
        foreach (var i in ProxyScene.Instances)
            if (i.Selected && i.Visible && Hidden.Add(i.Name)) n++;
        if (n > 0) Publish(Ed.Lod);
        return n;
    }

    public static void ShowAll()
    {
        EnsureEnabled();
        if (Hidden.Count == 0) return;
        Hidden.Clear();
        Publish(Ed.Lod);
        Ed.Log("All proxy models shown");
    }

    /// <summary>Hides every proxy model (then tick the ones to show in the Objects panel).</summary>
    public static void HideAll()
    {
        EnsureEnabled();
        int before = Hidden.Count;
        foreach (var f in flat)
            if (f.Parent < 0) Hidden.Add(f.Name);
        if (Hidden.Count != before) Publish(Ed.Lod);
        Ed.Log("All proxy models hidden — tick the ones you want to see in the Objects panel");
    }

    /// <summary>Shows / hides every top-level instance of one proxy model file (e.g. all eight wheels).</summary>
    public static void SetVisibleByModel(string proxyPath, bool visible)
    {
        if (visible) EnsureEnabled();
        int n = 0;
        foreach (var f in flat)
            if (f.Parent < 0 && string.Equals(f.Path, proxyPath, StringComparison.OrdinalIgnoreCase) && (visible ? Hidden.Remove(f.Name) : Hidden.Add(f.Name)))
                n++;
        if (n > 0) Publish(Ed.Lod);
    }

    /// <summary>Hides every top-level instance except <paramref name="name"/> (and its ancestors).</summary>
    public static void ShowOnly(string name)
    {
        EnsureEnabled();
        var set = Hidden;
        set.Clear();
        int keep = -1;
        for (int i = 0; i < flat.Count; i++)
            if (string.Equals(flat[i].Name, name, StringComparison.OrdinalIgnoreCase)) keep = i;
        var chain = new HashSet<int>();
        for (int i = keep; i >= 0; i = flat[i].Parent) chain.Add(i);
        for (int i = 0; i < flat.Count; i++)
        {
            if (chain.Contains(i)) continue;
            // hide siblings along the chain and every other top-level instance
            int parent = flat[i].Parent;
            if (parent < 0 || chain.Contains(parent)) set.Add(flat[i].Name);
        }
        Publish(Ed.Lod);
        Ed.Log($"Showing only {Core.Ops.Proxies.ShortLabel(name)}: {set.Count} other proxy model(s) hidden (Show All Proxies brings them back; the model itself is not changed)");
    }

    /// <summary>Selects the proxy triangle of an instance's top-level proxy in the editor (named selection).</summary>
    public static bool Select(int instance, SelectOp op)
    {
        int root = RootOf(instance);
        if (root < 0) return false;
        var name = flat[root].Name;
        // A hidden proxy triangle (Edit ▸ Hide, Object Builder "Hide") cannot be selected, yet its model is shown:
        // clicking the model shows the triangle again (one undo step) so the proxy can be selected and moved.
        var lod = Ed.Lod;
        if (lod.Selection(name) is { } s)
        {
            var hidden = Selection.HiddenPoints(lod);
            var mask = new byte[lod.PointCount];
            bool any = false;
            for (int p = 0; p < mask.Length && p < s.Points.Length; p++)
                if (s.Points[p] != 0 && hidden[p] != 0)
                {
                    mask[p] = 1;
                    any = true;
                }
            if (any)
            {
                Ed.EditLod("Unhide proxy", l => Selection.WithPointStateFlag(l, P3DForge.Core.PointFlags.Hidden, StateKind.Hidden, mask, false));
                Ed.Log($"{(name.StartsWith("proxy:", StringComparison.OrdinalIgnoreCase) ? name[6..] : name)}: its proxy triangle was hidden in the model, so it could not be selected — shown again (Ctrl+Z hides it again)");
            }
        }
        return SelectionOps.SelectNamed(name, op);
    }
}
