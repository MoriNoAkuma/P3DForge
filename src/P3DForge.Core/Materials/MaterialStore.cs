namespace P3DForge.Core.Materials;

/// <summary>
/// Resolves, loads and caches .rvmat files and holds unsaved in-memory edits ("overrides").
/// <list type="bullet">
/// <item>Keys are lower-case absolute paths (<see cref="Key"/>); every method accepts model references
/// ("dz\x.rvmat", "\dz\x.rvmat", "P:\dz\x.rvmat"), absolute paths or keys.</item>
/// <item><see cref="Get"/> returns the override when there is one, else the cached file (loaded once; text or raP).
/// Returned objects are shared: never modify them — Clone(), edit, then <see cref="SetOverride"/>.</item>
/// <item>Files are re-checked only by <see cref="CheckForChanges"/> (call it on app activation / a slow timer), so
/// <see cref="Get"/> is cheap enough for per-frame use. Thread-safe; <see cref="Changed"/> fires on the thread that
/// caused the change (the UI thread in the app).</item>
/// </list>
/// </summary>
public sealed class MaterialStore
{
    /// <summary>The application-wide store (the app keeps <see cref="ProjectRootProvider"/> pointed at its settings).</summary>
    public static MaterialStore Shared { get; } = new();

    sealed class Entry
    {
        public Rvmat? Material;
        public string? Error;
        public bool Exists;
        public DateTime Mtime;
        public long Length;
    }

    readonly object sync = new();
    readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    readonly Dictionary<string, Rvmat> overrides = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> versions = new(StringComparer.Ordinal);
    string root;

    public MaterialStore(string? projectRoot = null) => root = projectRoot ?? @"P:\";

    /// <summary>Supplies the project root dynamically (wins over <see cref="ProjectRoot"/>'s stored value when it returns a non-empty string).</summary>
    public Func<string?>? ProjectRootProvider { get; set; }

    /// <summary>Root for relative material paths (the work drive, e.g. "P:\").</summary>
    public string ProjectRoot
    {
        get
        {
            try
            {
                if (ProjectRootProvider?.Invoke() is { Length: > 0 } r) return r;
            }
            catch
            {
                // provider failure: fall back to the stored root
            }
            return root;
        }
        set => root = value ?? "";
    }

    /// <summary>Raised with the key of a material whose override or file changed (UI: re-read via <see cref="Get"/>).</summary>
    public event Action<string>? Changed;

    /// <summary>Absolute path of a material reference; null for "" and procedural ("#...") references.</summary>
    public string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Trim().Replace('/', '\\');
        if (s.StartsWith('#')) return null;
        string full;
        if ((s.Length > 2 && char.IsLetter(s[0]) && s[1] == ':') || s.StartsWith(@"\\", StringComparison.Ordinal)) full = s;
        else full = ProjectRoot.TrimEnd('\\', '/') + "\\" + s.TrimStart('\\');
        try
        {
            return Path.GetFullPath(full);
        }
        catch
        {
            return full;
        }
    }

    /// <summary>Cache key (lower-case absolute path) of a material reference; null for "" / procedural references.</summary>
    public string? Key(string? path) => Resolve(path)?.ToLowerInvariant();

    /// <summary>The material: unsaved override if any, else the file (cached). Null when missing or unreadable (see <see cref="GetError"/>).</summary>
    public Rvmat? Get(string? path)
    {
        TryGet(path, out var m, out _);
        return m;
    }

    /// <summary>Like <see cref="Get"/>, also reporting why the file could not be used ("file not found", parse error).</summary>
    public bool TryGet(string? path, out Rvmat? material, out string? error)
    {
        material = null;
        error = null;
        var full = Resolve(path);
        if (full is null)
        {
            error = "no material";
            return false;
        }
        var key = full.ToLowerInvariant();
        lock (sync)
        {
            if (overrides.TryGetValue(key, out var o))
            {
                material = o;
                return true;
            }
        }
        var e = LoadEntry(key, full);
        material = e.Material;
        error = e.Error;
        return material is not null;
    }

    public string? GetError(string? path)
    {
        TryGet(path, out _, out var error);
        return error;
    }

    /// <summary>The file version (cached), ignoring overrides; null when missing/unreadable.</summary>
    public Rvmat? LoadFromDisk(string? path)
    {
        var full = Resolve(path);
        return full is null ? null : LoadEntry(full.ToLowerInvariant(), full).Material;
    }

    /// <summary>True when the referenced file exists (cached until <see cref="CheckForChanges"/>/<see cref="Invalidate"/>).</summary>
    public bool FileExists(string? path)
    {
        var full = Resolve(path);
        return full is not null && LoadEntry(full.ToLowerInvariant(), full).Exists;
    }

    /// <summary>Increments whenever the material's override or file changes (renderers cache by key + version).</summary>
    public int Version(string? path)
    {
        var key = Key(path);
        if (key is null) return 0;
        lock (sync) return versions.GetValueOrDefault(key);
    }

    /// <summary>Installs an unsaved edit (takes ownership: do not modify <paramref name="material"/> afterwards). Raises <see cref="Changed"/>.</summary>
    public void SetOverride(string? path, Rvmat material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var key = Key(path);
        if (key is null) return;
        lock (sync)
        {
            overrides[key] = material;
            Bump(key);
        }
        Changed?.Invoke(key);
    }

    /// <summary>Drops the unsaved edit (the file version is used again). Raises <see cref="Changed"/> when there was one.</summary>
    public bool ClearOverride(string? path)
    {
        var key = Key(path);
        if (key is null) return false;
        lock (sync)
        {
            if (!overrides.Remove(key)) return false;
            Bump(key);
        }
        Changed?.Invoke(key);
        return true;
    }

    public bool HasOverride(string? path)
    {
        var key = Key(path);
        if (key is null) return false;
        lock (sync) return overrides.ContainsKey(key);
    }

    /// <summary>Keys of every material with unsaved edits.</summary>
    public IReadOnlyList<string> OverrideKeys
    {
        get
        {
            lock (sync) return [.. overrides.Keys];
        }
    }

    /// <summary>Forgets the cached file (null = every file) so the next <see cref="Get"/> reloads it. Raises <see cref="Changed"/> per key.</summary>
    public void Invalidate(string? path = null)
    {
        List<string> keys;
        lock (sync)
        {
            if (path is null) keys = [.. cache.Keys];
            else
            {
                var key = Key(path);
                keys = key is null ? [] : [key];
            }
            foreach (var k in keys)
            {
                cache.Remove(k);
                Bump(k);
            }
        }
        foreach (var k in keys) Changed?.Invoke(k);
    }

    /// <summary>Re-checks the cached files (size + mtime); changed ones are reloaded on the next <see cref="Get"/>.
    /// Raises <see cref="Changed"/> per changed key and returns how many changed.</summary>
    public int CheckForChanges()
    {
        KeyValuePair<string, Entry>[] snapshot;
        lock (sync) snapshot = [.. cache];
        var changed = new List<string>();
        foreach (var (key, e) in snapshot)
        {
            bool exists;
            DateTime mtime = default;
            long length = 0;
            try
            {
                var fi = new FileInfo(key);
                exists = fi.Exists;
                if (exists)
                {
                    mtime = fi.LastWriteTimeUtc;
                    length = fi.Length;
                }
            }
            catch
            {
                exists = false;
            }
            if (exists == e.Exists && (!exists || (mtime == e.Mtime && length == e.Length))) continue;
            lock (sync)
            {
                if (cache.TryGetValue(key, out var cur) && ReferenceEquals(cur, e))
                {
                    cache.Remove(key);
                    Bump(key);
                    changed.Add(key);
                }
            }
        }
        foreach (var k in changed) Changed?.Invoke(k);
        return changed.Count;
    }

    void Bump(string key) => versions[key] = versions.GetValueOrDefault(key) + 1;

    Entry LoadEntry(string key, string full)
    {
        lock (sync)
        {
            if (cache.TryGetValue(key, out var e)) return e;
        }
        var entry = ReadEntry(full);
        lock (sync)
        {
            if (cache.TryGetValue(key, out var e2)) return e2;
            cache[key] = entry;
            return entry;
        }
    }

    static Entry ReadEntry(string file)
    {
        var e = new Entry();
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists)
            {
                e.Error = "file not found";
                return e;
            }
            e.Exists = true;
            e.Mtime = fi.LastWriteTimeUtc;
            e.Length = fi.Length;
            e.Material = Rvmat.FromBytes(File.ReadAllBytes(file), file);
        }
        catch (Exception ex)
        {
            e.Error = ex.Message;
        }
        return e;
    }
}
