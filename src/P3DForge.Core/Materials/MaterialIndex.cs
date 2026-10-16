using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P3DForge.Core.Materials;

/// <summary>One indexed .rvmat (absolute path, file stamp and a summary for searching / previews).</summary>
public sealed record MaterialIndexEntry
{
    public required string Path { get; init; }
    public long MtimeTicks { get; init; }
    public long Size { get; init; }
    public string Shader { get; init; } = "";
    /// <summary>Stage textures (as written in the rvmat) by role; null when absent.</summary>
    public string? Diffuse { get; init; }
    public string? Normal { get; init; }
    public string? Specular { get; init; }
    public float[]? DiffuseColor { get; init; }
    public bool Binarized { get; init; }
    public string? Error { get; init; }
    /// <summary>Placeholder published by a running <see cref="MaterialIndex.Scan"/> before the file is parsed (never saved).</summary>
    [JsonIgnore] public bool Pending { get; init; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Summary of a parsed material.</summary>
    public static MaterialIndexEntry FromMaterial(string path, long mtimeTicks, long size, Rvmat m)
    {
        var d = m.Diffuse;
        return new MaterialIndexEntry
        {
            Path = path,
            MtimeTicks = mtimeTicks,
            Size = size,
            Shader = m.PixelShaderID,
            Diffuse = m.FindStage(StageRole.Diffuse)?.Texture,
            Normal = m.FindStage(StageRole.Normal)?.Texture,
            Specular = m.FindStage(StageRole.Specular)?.Texture,
            DiffuseColor = [d.R, d.G, d.B, d.A],
            Binarized = m.WasBinarized,
        };
    }
}

/// <summary>Progress of a <see cref="MaterialIndex.Scan"/>.</summary>
public readonly record struct MaterialScanProgress(int Found, int Parsed, bool Done, string? Folder);

/// <summary>
/// Background index of *.rvmat files: enumerates a folder tree (no extra stat per file), re-parses only files whose
/// size / mtime changed since the cached index (JSON, absolute paths; entries outside the scanned folder are kept).
/// Pure Core: the caller picks the cache file and runs <see cref="Scan"/> on a worker thread.
/// </summary>
public sealed class MaterialIndex
{
    readonly Dictionary<string, MaterialIndexEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    readonly object sync = new();

    public MaterialIndex(string? cacheFile = null) => CacheFile = cacheFile;

    /// <summary>JSON cache file (null = no persistence).</summary>
    public string? CacheFile { get; }

    public int Count
    {
        get
        {
            lock (sync) return entries.Count;
        }
    }

    /// <summary>Snapshot of the entries under <paramref name="folder"/> (all when null), sorted by path.</summary>
    public List<MaterialIndexEntry> Entries(string? folder = null)
    {
        List<MaterialIndexEntry> r;
        var prefix = folder is null ? null : Prefix(folder);
        lock (sync)
            r = prefix is null ? [.. entries.Values] : entries.Values.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        r.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return r;
    }

    static string Prefix(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";

    /// <summary>Loads the cache file (missing / corrupt cache = empty index).</summary>
    public void Load()
    {
        if (CacheFile is null || !File.Exists(CacheFile)) return;
        try
        {
            using var fs = File.OpenRead(CacheFile);
            var list = JsonSerializer.Deserialize<CacheDoc>(fs, JsonOpts)?.Entries ?? [];
            lock (sync)
            {
                entries.Clear();
                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.Path)) entries[e.Path] = e;
            }
        }
        catch
        {
            lock (sync) entries.Clear();
        }
    }

    /// <summary>Writes the cache file atomically.</summary>
    public void Save()
    {
        if (CacheFile is null) return;
        List<MaterialIndexEntry> list;
        lock (sync) list = entries.Values.Where(e => !e.Pending).ToList();
        var dir = Path.GetDirectoryName(Path.GetFullPath(CacheFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = CacheFile + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, new CacheDoc { Version = 1, Entries = list }, JsonOpts);
        File.Move(tmp, CacheFile, overwrite: true);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    sealed class CacheDoc
    {
        public int Version { get; set; }
        public List<MaterialIndexEntry> Entries { get; set; } = [];
    }

    /// <summary>
    /// Scans <paramref name="folder"/> recursively (inaccessible folders skipped). Unchanged files keep their cached
    /// summary; new / changed ones are parsed; vanished ones are dropped. Thread-safe against concurrent reads.
    /// Returns the number of entries under the folder. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public int Scan(string folder, IProgress<MaterialScanProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) return 0;
        var prefix = Prefix(root);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        var found = new List<(string Path, long Mtime, long Size)>();
        var files = new FileSystemEnumerable<(string, long, long)>(root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc.UtcTicks, e.Length), opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".rvmat", StringComparison.OrdinalIgnoreCase),
        };
        var lastReport = Environment.TickCount64;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            found.Add(f);
            if (Environment.TickCount64 - lastReport > 200)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(new MaterialScanProgress(found.Count, 0, false, Path.GetDirectoryName(f.Item1)));
            }
        }

        // publish every file at once (new / changed ones as Pending placeholders), then parse them in parallel
        // (IO-bound on large trees) and replace the placeholders as they complete
        var fresh = new MaterialIndexEntry[found.Count];
        var todo = new List<int>();
        lock (sync)
        {
            for (int i = 0; i < found.Count; i++)
            {
                var (path, mtime, size) = found[i];
                if (entries.TryGetValue(path, out var cached) && !cached.Pending && cached.MtimeTicks == mtime && cached.Size == size)
                    fresh[i] = cached;
                else
                {
                    fresh[i] = new MaterialIndexEntry { Path = path, MtimeTicks = mtime, Size = size, Pending = true };
                    todo.Add(i);
                }
            }
            var stale = entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in stale) entries.Remove(k);
            foreach (var e in fresh) entries[e.Path] = e;
        }
        progress?.Report(new MaterialScanProgress(found.Count, 0, false, null));

        var parsed = 0;
        if (todo.Count > 0)
        {
            var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, todo.Count, 16), po, range =>
            {
                for (int k = range.Item1; k < range.Item2; k++)
                {
                    if (ct.IsCancellationRequested) return;
                    var (path, mtime, size) = found[todo[k]];
                    var e = Summarize(path, mtime, size);
                    lock (sync)
                        if (entries.TryGetValue(path, out var cur) && cur.Pending)
                            entries[path] = e;
                    var n = Interlocked.Increment(ref parsed);
                    var now = Environment.TickCount64;
                    var last = Interlocked.Read(ref lastReport);
                    if (now - last > 200 && Interlocked.CompareExchange(ref lastReport, now, last) == last)
                        progress?.Report(new MaterialScanProgress(found.Count, n, false, null));
                }
            });
            ct.ThrowIfCancellationRequested();
        }
        progress?.Report(new MaterialScanProgress(found.Count, parsed, true, null));
        return found.Count;
    }

    /// <summary>Parses one file into an index entry (errors are recorded, never thrown).</summary>
    public static MaterialIndexEntry Summarize(string path, long mtimeTicks, long size)
    {
        try
        {
            return MaterialIndexEntry.FromMaterial(path, mtimeTicks, size, Rvmat.FromBytes(File.ReadAllBytes(path), path));
        }
        catch (Exception ex)
        {
            return new MaterialIndexEntry { Path = path, MtimeTicks = mtimeTicks, Size = size, Error = ex.Message };
        }
    }

    /// <summary>Entries whose file name or path contains every space-separated term of <paramref name="query"/> (case-insensitive).</summary>
    public static List<MaterialIndexEntry> Search(IReadOnlyList<MaterialIndexEntry> source, string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [.. source];
        var r = new List<MaterialIndexEntry>();
        for (int i = 0; i < source.Count; i++)
        {
            var e = source[i];
            var ok = true;
            foreach (var t in terms)
                if (e.Path.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0 && e.Shader.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    ok = false;
                    break;
                }
            if (ok) r.Add(e);
        }
        // names that match go first
        if (terms.Length > 0)
        {
            var t0 = terms[0];
            r = [.. r.OrderBy(e => e.Name.Contains(t0, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        }
        return r;
    }
}
