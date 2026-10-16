using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using P3DForge.App.Editor;
using P3DForge.Core.IO;
using P3DForge.Core.Model;

namespace P3DForge.App.Services;

/// <summary>Open / save / save as with safety: atomic writes (temp + rename) and an optional .bak.</summary>
public static class FileService
{
    const string Filter = "P3D models (*.p3d)|*.p3d|All files (*.*)|*.*";
    static EditorState Ed => AppServices.Editor;

    /// <summary>The binarized model the current (unsaved) document was made from, or null.</summary>
    public static ConvertedModel? Converted { get; private set; }

    /// <summary>The document is no longer the converted one (new model, another file).</summary>
    public static void ForgetConverted() => Converted = null;

    /// <summary>Asks what to do with unsaved changes; false = the user cancelled the pending action.</summary>
    public static bool ConfirmDiscard(string action)
    {
        if (!Ed.Dirty) return true;
        if (Dialogs.Dlg.Automated)
        {
            Ed.Log($"[automation] unsaved changes discarded before you {action}");
            return true;
        }
        var r = MessageBox.Show(Application.Current.MainWindow, $"Save changes to {Ed.FileName} before you {action}?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        return r switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Error box. Under automation only the log line (written by the caller) remains: a modal box would block the script.</summary>
    static void ShowError(string text, string title)
    {
        if (!Dialogs.Dlg.Automated) MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Opens a .p3d (no unsaved-changes check). Returns false on failure (already reported).</summary>
    public static bool Open(string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot read {path}: {ex.Message}", LogLevel.Error);
            ShowError($"Cannot read {path}\n\n{ex.Message}", "Open failed");
            return false;
        }
        Converted = null;
        if (Odol.IsOdol(bytes)) return OpenBinarized(path, bytes);
        var warnings = new List<string>();
        P3DModel model;
        try
        {
            model = Mlod.Read(bytes, warnings);
        }
        catch (MlodException ex)
        {
            Ed.Log($"Cannot open {path}: {ex.Message}", LogLevel.Error);
            ShowError($"{path}\n\n{ex.Message}", "Open failed");
            return false;
        }
        Ed.Load(model, path);
        AppServices.Settings.AddRecent(path);
        foreach (var w in warnings) Ed.Log(w, LogLevel.Warn);
        int faces = model.Lods.Sum(l => l.FaceCount);
        Ed.Log($"Opened {path} — {model.Lods.Length} LODs, {faces:N0} faces in {sw.ElapsedMilliseconds} ms");
        return true;
    }

    /// <summary>
    /// A binarized (ODOL) model: converted to an editable MLOD by the external converter (DeODOL, on a copy), or shown by
    /// the built-in reader (read only: geometry, textures, materials, proxies). Either way it opens as an unsaved document;
    /// the original file is not changed.
    /// </summary>
    static bool OpenBinarized(string path, byte[] bytes)
    {
        var tool = Deodol.ToolPath();
        int version = BitConverter.ToInt32(bytes, 4);
        var name = Path.GetFileNameWithoutExtension(path);
        var form = new Dialogs.Ops.Form("Binarized model",
                $"{Path.GetFileName(path)} is binarized (ODOL v{version}) — the game's packed format, not an editable source model.\n" +
                "The file itself is not changed; the result opens as a new, unsaved model.")
            .Choice("how", "Open", [
                tool is null ? $"Convert to an editable MLOD with {Deodol.DefaultName} (not found — set it in File ▸ Preferences)"
                    : $"Convert to an editable MLOD with {Path.GetFileName(tool)} (named selections, proxies, model.cfg)",
                "View with the built-in reader (read only: geometry, textures, materials, proxies)",
            ], tool is null ? 1 : 0);
        if (!form.Show("Open")) return false;
        var sw = Stopwatch.StartNew();
        if (form.Index("how") == 0)
        {
            if (tool is null)
            {
                ShowError($"{Deodol.DefaultName} was not found. Set its path in File ▸ Preferences ▸ Tools.", "Open failed");
                Ed.Log($"{Deodol.DefaultName} not found — set it in File ▸ Preferences", LogLevel.Warn);
                return false;
            }
            try
            {
                var (mlod, cfg, log) = Deodol.Convert(tool, path);
                var warnings = new List<string>();
                var model = Mlod.Read(File.ReadAllBytes(mlod), warnings);
                Ed.Load(model, null);
                Ed.UntitledName = name + "_mlod.p3d";
                Converted = new ConvertedModel(path, cfg, name, Editable: true);
                AppServices.Settings.AddRecent(path);
                foreach (var w in warnings) Ed.Log(w, LogLevel.Warn);
                Ed.Log($"Converted {path} with {Path.GetFileName(tool)} — {model.Lods.Length} LODs in {sw.ElapsedMilliseconds} ms. " +
                       $"Not saved yet: File ▸ Save asks where to put {Ed.UntitledName}" + (cfg is null ? "" : " (the recovered model.cfg can go next to it)"));
                foreach (var line in log.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("arning", StringComparison.Ordinal)).Take(5))
                    Ed.Log($"{Path.GetFileName(tool)}: {line}", LogLevel.Warn);
                return true;
            }
            catch (Exception ex)
            {
                Ed.Log($"{Path.GetFileName(tool)} could not convert {path}: {ex.Message} — opening it with the built-in reader instead", LogLevel.Warn);
            }
        }
        try
        {
            var model = Odol.Read(bytes);
            Ed.Load(model, null);
            Ed.UntitledName = name + " (binarized, view)";
            Converted = new ConvertedModel(path, null, name, Editable: false);
            AppServices.Settings.AddRecent(path);
            Ed.Log($"Opened {path} with the built-in reader — {model.Lods.Length} LODs, view only (no named selections or properties; " +
                   "Save writes a plain MLOD of what you see)");
            return true;
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot open {path}: {ex.Message}", LogLevel.Error);
            ShowError($"{path}\n\n{ex.Message}", "Open failed");
            return false;
        }
    }

    /// <summary>Saves to <paramref name="path"/> (default: current path; asks when untitled).</summary>
    public static bool Save(string? path = null)
    {
        path ??= Ed.Path;
        if (path is null) return SaveAs();
        byte[] bytes;
        try
        {
            bytes = Mlod.Write(Ed.Model);
        }
        catch (Exception ex)
        {
            Ed.Log($"Save aborted, the model is inconsistent: {ex.Message}", LogLevel.Error);
            ShowError($"The model could not be serialized:\n{ex.Message}\n\nNothing was written.", "Save failed");
            return false;
        }
        try
        {
            WriteAtomic(path, bytes, AppServices.Settings.BackupOnSave);
        }
        catch (Exception ex)
        {
            Ed.Log($"Cannot write {path}: {ex.Message}", LogLevel.Error);
            ShowError($"Cannot write {path}\n\n{ex.Message}", "Save failed");
            return false;
        }
        Ed.MarkSaved(path);
        AppServices.Settings.AddRecent(path);
        Ed.Log($"Saved {path} ({bytes.Length / 1024:N0} KB)");
        if (Converted is { ModelCfg: { } cfg } conv && File.Exists(cfg))
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, "model.cfg");
            if (!File.Exists(target) && Dialogs.Dlg.Confirm("model.cfg",
                    $"The converter recovered a model.cfg (skeleton, sections, animations of \"{conv.ModelName}\").\n\nSave it next to the model as\n{target}?" +
                    (string.Equals(Path.GetFileNameWithoutExtension(path), conv.ModelName, StringComparison.OrdinalIgnoreCase) ? ""
                        : $"\n\nIts model class is \"{conv.ModelName}\" — it applies to {conv.ModelName}.p3d, not to {Path.GetFileName(path)}."), "Save model.cfg"))
            {
                File.Copy(cfg, target);
                Ed.Log($"Saved the recovered model.cfg as {target}");
            }
            else if (File.Exists(target)) Ed.Log($"The recovered model.cfg was not copied: {target} already exists (it is in {Path.GetDirectoryName(cfg)})");
        }
        Converted = null;
        return true;
    }

    public static bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = Filter,
            FileName = Ed.FileName.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase) ? Ed.FileName : Path.GetFileNameWithoutExtension(Ed.FileName.Split(" (")[0]) + ".p3d",
            InitialDirectory = Ed.Path is not null ? Path.GetDirectoryName(Ed.Path)
                : Converted is { } c ? Path.GetDirectoryName(c.Original) : AppServices.Settings.ProjectRoot,
        };
        return dlg.ShowDialog() == true && Save(dlg.FileName);
    }

    public static void OpenWithDialog()
    {
        if (!ConfirmDiscard("open another file")) return;
        var dlg = new OpenFileDialog
        {
            Filter = Filter,
            InitialDirectory = Ed.Path is null ? AppServices.Settings.ProjectRoot : Path.GetDirectoryName(Ed.Path),
        };
        if (dlg.ShowDialog() == true) Open(dlg.FileName);
    }

    /// <summary>Writes via a temp file + rename so a crash never leaves a half-written model.</summary>
    public static void WriteAtomic(string path, byte[] bytes, bool backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".forge-tmp";
        File.WriteAllBytes(tmp, bytes);
        if (backup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }
}
