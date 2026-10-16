using System.IO;
using System.Text;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.Core.IO;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Tools ▸ Rename Paths in Folder…: the mass texture / material renaming of one model, for every model (MLOD:
/// textures, rvmats, proxy paths), rvmat and config.cpp (quoted strings) of a folder — e.g. after moving a mod.
/// A dry run lists what would change; nothing is written before the confirmation; .bak copies are kept by default.
/// The game's files (P:\DZ) are left alone unless the folder is inside them.
/// </summary>
public sealed class BatchRenameFeature : IFeature
{
    static EditorState Ed => AppServices.Editor;
    static bool running;

    public int Order => 62;

    sealed record FileChange(string File, int Count, bool Model, byte[] NewBytes);

    public void Install()
    {
        Commands.Register(new Command
        {
            Id = "tools.batchRename",
            Label = "Rename Paths in _Folder…",
            Execute = Run,
            CanExecute = () => !running,
            Description = "Replaces texture / rvmat / proxy / model paths in every .p3d, .rvmat and config.cpp of a folder (preview first, .bak copies)",
        });
    }

    static async void Run()
    {
        var root = AppServices.Settings.ProjectRoot;
        var start = GameConfigs.ModRoot(Ed.Path, root) ?? root;
        var form = new Dialogs.Ops.Form("Rename paths in a folder",
                "Replaces paths in every model (textures, rvmats, proxies), rvmat and config.cpp of the folder. A preview lists the files first; nothing is written before you confirm.")
            .Text("folder", "Folder", start, t => Directory.Exists(t.Trim()) ? null : "The folder does not exist")
            .Text("find", "Find", "", t => t.Trim().Length == 0 ? "Type the path (part) to replace" : null)
            .Text("replace", "Replace with", "")
            .Choice("match", "Match", ["Contains (every occurrence)", "Whole path", "Starts with (folder prefix)", "Mask (* and ?)"], 0)
            .Check("sub", "Include subfolders", true)
            .Check("models", "Models (.p3d)", true)
            .Check("rvmats", "Materials (.rvmat)", true)
            .Check("configs", "Configs (config.cpp)", true)
            .Check("case", "Match case", false)
            .Check("backup", "Keep a .bak copy of every changed file", true);
        if (!form.Show("Preview")) return;
        var folder = Path.GetFullPath(form.Str("folder").Trim());
        var options = new ReplaceOptions
        {
            Match = form.Index("match") switch { 1 => PathMatch.Exact, 2 => PathMatch.Prefix, 3 => PathMatch.Wildcard, _ => PathMatch.Substring },
            CaseInsensitive = !form.Bool("case"),
        };
        var replacer = Attributes.Replacer(form.Str("find").Trim(), form.Str("replace").Trim(), options);
        bool sub = form.Bool("sub"), models = form.Bool("models"), rvmats = form.Bool("rvmats"), configs = form.Bool("configs"), backup = form.Bool("backup");
        var dz = Path.Combine(root, "DZ");
        bool insideDz = folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dz)), StringComparison.OrdinalIgnoreCase);

        running = true;
        Ed.Log($"Rename paths: scanning {folder}…");
        List<FileChange> changes;
        List<string> skipped;
        try
        {
            (changes, skipped) = await Task.Run(() => Scan(folder, sub, models, rvmats, configs, insideDz ? null : dz, replacer, options));
        }
        finally
        {
            running = false;
        }
        if (changes.Count == 0)
        {
            Dlg.Info("Rename paths", $"Nothing to rename in {folder}." + (skipped.Count > 0 ? $"\n\nSkipped: {string.Join("; ", skipped.Take(5))}" : ""));
            return;
        }
        // the open model: rewritten on disk only when it has no unsaved changes (then reloaded)
        bool openTouched = Ed.Path is { } open && changes.Any(c => string.Equals(c.File, Path.GetFullPath(open), StringComparison.OrdinalIgnoreCase));
        if (openTouched && Ed.Dirty)
        {
            changes.RemoveAll(c => string.Equals(c.File, Path.GetFullPath(Ed.Path!), StringComparison.OrdinalIgnoreCase));
            skipped.Insert(0, $"{Path.GetFileName(Ed.Path)} (open with unsaved changes — use Tools ▸ Mass Texture & Material Renaming in it)");
            openTouched = false;
        }
        int total = changes.Sum(c => c.Count);
        var list = string.Join("\n", changes.Take(25).Select(c => $"{Path.GetRelativePath(folder, c.File)} — {c.Count}"));
        if (!Dlg.Confirm("Rename paths",
                $"{changes.Count} file{(changes.Count == 1 ? "" : "s")}, {total:N0} reference{(total == 1 ? "" : "s")} in {folder}:\n\n{list}" +
                (changes.Count > 25 ? $"\n… and {changes.Count - 25} more" : "") +
                (skipped.Count > 0 ? $"\n\nSkipped: {string.Join("; ", skipped.Take(5))}{(skipped.Count > 5 ? " …" : "")}" : "") +
                $"\n\nWrite the changes{(backup ? " (with .bak copies)" : " (no .bak copies)")}?", "Rename"))
            return;
        int written = 0;
        foreach (var c in changes)
        {
            try
            {
                FileService.WriteAtomic(c.File, c.NewBytes, backup);
                written++;
            }
            catch (Exception ex)
            {
                Ed.Log($"Rename paths: cannot write {c.File}: {ex.Message}", LogLevel.Error);
            }
        }
        Ed.Log($"Rename paths: {written} file{(written == 1 ? "" : "s")} written, {total:N0} references renamed in {folder}");
        if (openTouched && Ed.Path is { } reopen) FileService.Open(reopen);
    }

    static (List<FileChange>, List<string>) Scan(string folder, bool sub, bool models, bool rvmats, bool configs, string? skipFolder,
        Func<string, string?> replacer, ReplaceOptions options)
    {
        var changes = new List<FileChange>();
        var skipped = new List<string>();
        var opt = sub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> Files(string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(folder, pattern, opt).ToList();
            }
            catch (Exception ex)
            {
                skipped.Add($"{pattern}: {ex.Message}");
                return [];
            }
        }
        var files = new List<string>();
        if (models) files.AddRange(Files("*.p3d"));
        if (rvmats) files.AddRange(Files("*.rvmat"));
        if (configs) files.AddRange(Files("config.cpp"));
        int dzSkipped = 0;
        foreach (var f in files)
        {
            try
            {
                if (skipFolder is not null && BatchRename.SkipReason(f, [], skipFolder) is not null)
                {
                    dzSkipped++;
                    continue;
                }
                var bytes = File.ReadAllBytes(f);
                if (BatchRename.SkipReason(f, bytes, null) is { } why)
                {
                    skipped.Add($"{Path.GetFileName(f)} ({why})");
                    continue;
                }
                if (f.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase))
                {
                    var (next, n) = BatchRename.Model(bytes, replacer, options);
                    if (next is not null) changes.Add(new FileChange(Path.GetFullPath(f), n, true, next));
                    continue;
                }
                // Latin-1 keeps every byte as it is (paths are ASCII)
                var (text, count) = BatchRename.Text(Encoding.Latin1.GetString(bytes), replacer);
                if (text is not null) changes.Add(new FileChange(Path.GetFullPath(f), count, false, Encoding.Latin1.GetBytes(text)));
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        if (dzSkipped > 0) skipped.Add($"{dzSkipped} file{(dzSkipped == 1 ? "" : "s")} of the game in {skipFolder}");
        return (changes, skipped);
    }
}
