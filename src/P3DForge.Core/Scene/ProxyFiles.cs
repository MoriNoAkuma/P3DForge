using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.Core.Scene;

/// <summary>Result of loading a proxy model file (never throws).</summary>
/// <param name="File">The file that was looked up.</param>
/// <param name="Model">The model, or null when it could not be loaded.</param>
/// <param name="LodIndex">LOD drawn for the proxy (<see cref="ProxyPlacement.DisplayLodIndex"/>), -1 without a model.</param>
/// <param name="Error">Why the model could not be loaded; null on success.</param>
/// <param name="Missing">True when the file does not exist.</param>
/// <param name="WriteTimeUtc">Last write time of the file (default when missing).</param>
/// <param name="Length">File size in bytes (-1 when missing).</param>
/// <param name="Binarized">The model was read from a binarized (ODOL) game file: drawn, but view only.</param>
public sealed record ProxyFileResult(string File, P3DModel? Model, int LodIndex, string? Error, bool Missing, DateTime WriteTimeUtc, long Length,
    bool Binarized = false);

/// <summary>Proxy path → file resolution and loading (pure IO, no UI).</summary>
public static class ProxyFiles
{
    public const string OdolError = "binarized (ODOL) — could not be read";

    /// <summary>
    /// File of a proxy path under the project root: "\dz\x\box" / "dz/x/box" / "\dz\x\box.p3d" → "P:\dz\x\box.p3d".
    /// Absolute paths (with a drive letter) are kept. Windows file lookups are case-insensitive.
    /// </summary>
    public static string Resolve(string proxyPath, string projectRoot)
    {
        var s = proxyPath.Trim().Replace('/', '\\');
        string full;
        if (s.Length > 2 && char.IsAsciiLetter(s[0]) && s[1] == ':') full = s;
        else
        {
            var root = projectRoot.Trim().Replace('/', '\\').TrimEnd('\\');
            full = (root.Length > 0 ? root + "\\" : "") + s.TrimStart('\\');
        }
        while (full.Contains(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\", StringComparison.Ordinal))
            full = full.Replace(@"\\", @"\", StringComparison.Ordinal);
        if (!full.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase)) full += ".p3d";
        return full;
    }

    /// <summary>Case-insensitive cache key of a resolved file.</summary>
    public static string Key(string file)
    {
        try
        {
            return Path.GetFullPath(file).ToLowerInvariant();
        }
        catch (Exception)
        {
            return file.ToLowerInvariant();
        }
    }

    /// <summary>(write time, length) of a file, or (default, -1) when it does not exist / cannot be read.</summary>
    public static (DateTime WriteTimeUtc, long Length) Stamp(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (default, -1);
        }
        catch (Exception)
        {
            return (default, -1);
        }
    }

    /// <summary>
    /// MLOD source copies of a proxy model near the edited model, tried when the project-root file is missing or
    /// binarized: a mod's sources kept outside its P:\ prefix (proxy "\mycar\proxy\door" with the edited model in
    /// P:\MyCar_test → P:\MyCar_test\proxy\door.p3d). Closest folder first (the host's folder, then up to
    /// <paramref name="levels"/> - 1 parents); each folder tries the proxy path without 0..n-1 leading folders (the bare
    /// file name only in the host's own folder). Absolute proxy paths have no alternatives.
    /// </summary>
    public static List<string> Alternatives(string proxyPath, string hostFile, int levels = 3)
    {
        var list = new List<string>();
        var parts = proxyPath.Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].EndsWith(':')) return list;
        if (!parts[^1].EndsWith(".p3d", StringComparison.OrdinalIgnoreCase)) parts[^1] += ".p3d";
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(hostFile));
        }
        catch (Exception)
        {
            return list;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int up = 0; up < levels && !string.IsNullOrEmpty(dir); up++, dir = Path.GetDirectoryName(dir))
        {
            int maxSkip = up == 0 ? parts.Length - 1 : parts.Length - 2;
            for (int skip = 0; skip <= maxSkip; skip++)
            {
                var full = Path.Combine(dir, string.Join('\\', parts, skip, parts.Length - skip));
                if (seen.Add(full)) list.Add(full);
            }
        }
        return list;
    }

    /// <summary>
    /// <see cref="Load"/> of the project-root file; when it is missing or binarized and the edited model's file is known,
    /// the first loadable MLOD of <see cref="Alternatives"/> instead (<paramref name="alternative"/> = true, the result's
    /// File names it). Otherwise the project-root result.
    /// </summary>
    public static ProxyFileResult LoadPreferSource(string file, string proxyPath, string? hostFile, out bool alternative)
    {
        alternative = false;
        var r = Load(file);
        // A source (MLOD) copy next to the edited model wins over a binarized game file.
        if ((r.Model is not null && !r.Binarized) || string.IsNullOrEmpty(hostFile) || !(r.Missing || r.Binarized || r.Error == OdolError)) return r;
        foreach (var alt in Alternatives(proxyPath, hostFile))
        {
            if (string.Equals(alt, file, StringComparison.OrdinalIgnoreCase) || !File.Exists(alt)) continue;
            var a = Load(alt);
            if (a.Model is null) continue;
            alternative = true;
            return a;
        }
        return r;
    }

    /// <summary>
    /// Loads a proxy model: MLOD, or the drawn LOD of a binarized (ODOL) game model. Missing files and parse errors
    /// become <see cref="ProxyFileResult.Error"/>.
    /// </summary>
    public static ProxyFileResult Load(string file)
    {
        var (time, length) = Stamp(file);
        if (length < 0) return new ProxyFileResult(file, null, -1, "file not found: " + file, true, default, -1);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            return new ProxyFileResult(file, null, -1, "cannot read: " + ex.Message, false, time, length);
        }
        if (Odol.IsOdol(bytes))
        {
            try
            {
                // Only the LOD that is drawn is converted.
                int lod = ProxyPlacement.DisplayLodIndex(new P3DModel([.. Odol.Resolutions(bytes).Select(Lod.Empty)]));
                if (lod < 0) return new ProxyFileResult(file, null, -1, "the model has no LOD", false, time, length);
                var model = Odol.Read(bytes, (i, _) => i == lod);
                return new ProxyFileResult(file, model, lod, null, false, time, length, Binarized: true);
            }
            catch (Exception)
            {
                return new ProxyFileResult(file, null, -1, OdolError, false, time, length);
            }
        }
        try
        {
            var model = Mlod.Read(bytes);
            int lod = ProxyPlacement.DisplayLodIndex(model);
            return lod < 0
                ? new ProxyFileResult(file, null, -1, "the model has no LOD", false, time, length)
                : new ProxyFileResult(file, model, lod, null, false, time, length);
        }
        catch (Exception ex)
        {
            return new ProxyFileResult(file, null, -1, "cannot read: " + ex.Message, false, time, length);
        }
    }
}
