using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using P3DForge.Core.Scene;

namespace P3DForge.App.Scene;

/// <summary>
/// Proxy model files loaded for display: resolved under <c>Settings.ProjectRoot</c>, loaded on background threads,
/// cached by file (+ last write time / size, re-checked on document load and on reload commands, never on ordinary
/// edits). UI thread API; <see cref="ModelsChanged"/> (coalesced) fires when loads finish.
/// </summary>
public static class ProxyLibrary
{
    /// <summary>A cached file. The fields are replaced on the UI thread only.</summary>
    public sealed class Entry
    {
        public required string File { get; init; }
        public required string Key { get; init; }
        /// <summary>Current model: a placeholder (no model, no error) while the first load runs.</summary>
        public required ProxyModel Model { get; set; }
        /// <summary>Proxies inside the display LOD (null while loading or when the model cannot be loaded).</summary>
        public IReadOnlyList<PlacedProxy>? Children { get; set; }
        /// <summary>Bounds of the display LOD (proxy model RAW), null without geometry.</summary>
        public (Vector3 Min, Vector3 Max)? Bounds { get; set; }
        public DateTime WriteTimeUtc { get; set; }
        public long Length { get; set; } = -1;
        public bool Loading { get; set; }
        public bool Loaded { get; set; }
        public int Version { get; set; }
        public long LastUse { get; set; }
        /// <summary>The MLOD source copy loaded instead of a missing / binarized project-root file (see
        /// <see cref="ProxyFiles.Alternatives"/>), or null.</summary>
        public string? LoadedFrom { get; set; }
        /// <summary>The file shown, opened and watched: <see cref="LoadedFrom"/> or <see cref="File"/>.</summary>
        public string Source => LoadedFrom ?? File;
        /// <summary>Folder of the edited model the last load looked near (alternatives depend on it).</summary>
        public string? HostDir { get; set; }
        /// <summary>The last load used or looked for an alternative: reload when the edited model's folder changes.</summary>
        public bool HostDependent { get; set; }
        /// <summary>The proxy path the entry was last loaded for (alternatives are derived from it).</summary>
        public string ProxyPath { get; set; } = "";
    }

    static string? HostFile => AppServices.Editor.Path;
    static string? DirOf(string? file) => string.IsNullOrEmpty(file) ? null : System.IO.Path.GetDirectoryName(file);

    static readonly Dictionary<string, Entry> byKey = new(StringComparer.Ordinal);
    static readonly Dictionary<string, Entry> byProxyPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProxyModel, Entry> ofModel = new();
    static readonly SemaphoreSlim gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    static string root = "";
    static long generation = 1;
    static bool changePending;
    static int pending;

    /// <summary>Raised on the UI thread (coalesced) when proxy models finished loading or were reloaded.</summary>
    public static event Action? ModelsChanged;

    /// <summary>Files currently being loaded.</summary>
    public static int PendingLoads => pending;

    public static IReadOnlyCollection<Entry> Entries => byKey.Values;

    static Dispatcher UiDispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>The cache entry of a proxy path (starts loading it when it is not cached yet).</summary>
    public static Entry Get(string proxyPath)
    {
        var projectRoot = AppServices.Settings.ProjectRoot ?? "";
        if (!string.Equals(root, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            root = projectRoot;
            byProxyPath.Clear();
        }
        if (byProxyPath.TryGetValue(proxyPath, out var e))
        {
            e.LastUse = generation;
            return e;
        }
        var file = ProxyFiles.Resolve(proxyPath, root);
        var key = ProxyFiles.Key(file);
        if (!byKey.TryGetValue(key, out e))
        {
            e = new Entry { File = file, Key = key, Model = new ProxyModel { Path = file, LodIndex = -1 } };
            byKey[key] = e;
            Load(e, proxyPath);
        }
        else if (e.HostDependent && !e.Loading && !string.Equals(e.HostDir, DirOf(HostFile), StringComparison.OrdinalIgnoreCase))
            Load(e, proxyPath); // another document: the source copy next to it may differ
        e.LastUse = generation;
        byProxyPath[proxyPath] = e;
        return e;
    }

    /// <summary>Bounds of a model's display LOD (cached per entry), or null.</summary>
    public static (Vector3 Min, Vector3 Max)? BoundsOf(ProxyModel model)
    {
        if (ofModel.TryGetValue(model, out var e) && ReferenceEquals(e.Model, model)) return e.Bounds;
        return model.Lod is { } lod ? ProxyPlacement.Bounds(lod) : null;
    }

    static void Load(Entry e, string proxyPath)
    {
        e.Loading = true;
        e.ProxyPath = proxyPath;
        Interlocked.Increment(ref pending);
        var file = e.File;
        var host = HostFile;
        _ = Task.Run(async () =>
        {
            ProxyFileResult r;
            bool alternative = false;
            IReadOnlyList<PlacedProxy>? children = null;
            (Vector3, Vector3)? bounds = null;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                r = ProxyFiles.LoadPreferSource(file, proxyPath, host, out alternative);
                if (r.Model is { } m && r.LodIndex >= 0)
                {
                    var lod = m.Lods[r.LodIndex];
                    children = ProxyPlacement.HasProxies(lod) ? ProxyPlacement.Find(lod) : [];
                    bounds = ProxyPlacement.Bounds(lod);
                }
            }
            catch (Exception ex)
            {
                r = new ProxyFileResult(file, null, -1, "cannot read: " + ex.Message, false, default, -1);
            }
            finally
            {
                gate.Release();
            }
            _ = UiDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                e.LoadedFrom = alternative ? r.File : null;
                e.HostDir = DirOf(host);
                e.HostDependent = alternative || r.Missing || r.Binarized || r.Error == ProxyFiles.OdolError;
                Apply(e, proxyPath, r, children, bounds);
            });
        });
    }

    static void Apply(Entry e, string proxyPath, ProxyFileResult r, IReadOnlyList<PlacedProxy>? children, (Vector3, Vector3)? bounds)
    {
        Interlocked.Decrement(ref pending);
        e.Loading = false;
        e.Loaded = true;
        e.Version++;
        e.WriteTimeUtc = r.WriteTimeUtc;
        e.Length = r.Length;
        e.Children = children;
        e.Bounds = bounds;
        e.Model = new ProxyModel
        {
            Path = r.Missing ? proxyPath : r.File,
            Model = r.Model,
            LodIndex = r.LodIndex,
            Error = r.Error,
            Binarized = r.Binarized,
            Version = e.Version,
        };
        ofModel.AddOrUpdate(e.Model, e);
        ScheduleChanged();
    }

    static void ScheduleChanged()
    {
        if (changePending) return;
        changePending = true;
        UiDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            changePending = false;
            ModelsChanged?.Invoke();
        });
    }

    /// <summary>
    /// Re-checks every cached file on a background thread and reloads those whose write time or size changed
    /// (missing files that appeared included). <paramref name="force"/> reloads everything.
    /// Calls <paramref name="done"/> on the UI thread with the number of reloaded files.
    /// </summary>
    public static void Refresh(bool force = false, Action<int>? done = null)
    {
        var snapshot = byKey.Values.Where(e => !e.Loading).Select(e => (Entry: e, File: e.Source, e.WriteTimeUtc, e.Length, Path: e.ProxyPath)).ToList();
        _ = Task.Run(() =>
        {
            var changed = new List<(Entry, string)>();
            foreach (var s in snapshot)
            {
                var (time, length) = ProxyFiles.Stamp(s.File);
                if (force || time != s.WriteTimeUtc || length != s.Length) changed.Add((s.Entry, s.Path));
            }
            UiDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                foreach (var (e, path) in changed)
                    if (!e.Loading && byKey.ContainsKey(e.Key)) Load(e, path);
                done?.Invoke(changed.Count);
            });
        });
    }

    /// <summary>A new document was loaded: drops files used by neither this nor the previous document.</summary>
    public static void OnDocumentLoaded()
    {
        long keepFrom = generation;
        generation++;
        List<string>? drop = null;
        foreach (var (k, e) in byKey)
            if (e.LastUse < keepFrom && !e.Loading) (drop ??= []).Add(k);
        if (drop is null) return;
        foreach (var k in drop) byKey.Remove(k);
        byProxyPath.Clear();
    }
}
