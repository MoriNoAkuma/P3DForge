using System.Diagnostics;
using System.IO;
using System.Windows;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Materials;
using P3DForge.Core.Ops;

namespace P3DForge.App.Panels.Materials;

/// <summary>Material actions shared by the material editor, the material library and the commands.</summary>
public static class MaterialActions
{
    public const string EditorPanelId = "materialEditor";
    public const string LibraryPanelId = "materialLibrary";

    /// <summary>UI flag: model path of the material shown in the material editor (string, null when none).</summary>
    public const string CurrentFlag = "materials.current";

    /// <summary>UI flag (automation / other panels): set to a material path to open it in the material editor.</summary>
    public const string OpenFlag = "materials.open";

    static EditorState Ed => AppServices.Editor;
    public static MaterialStore Store => MaterialStore.Shared;

    /// <summary>Faces an assignment applies to: selected faces, or faces whose points are all selected.</summary>
    public static byte[] SelectedFaces()
    {
        if (Ed.Model.Lods.Length == 0) return [];
        var sel = Ed.GetSelection();
        return Attributes.EffectiveFaces(Ed.Lod, sel.Points, sel.Faces);
    }

    /// <summary>The most used material among the selected faces of the active LOD (null when none / no material).</summary>
    public static string? SelectedFacesMaterial()
    {
        if (Ed.Model.Lods.Length == 0) return null;
        var lod = Ed.Lod;
        var faces = SelectedFaces();
        if (faces.Length != lod.FaceCount) return null;
        var counts = new int[lod.Strings.Length];
        for (int f = 0; f < faces.Length; f++)
            if (faces[f] != 0) counts[lod.FaceMaterial[f]]++;
        int best = -1;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > 0 && lod.Strings[i].Trim().Length > 0 && (best < 0 || counts[i] > counts[best])) best = i;
        return best < 0 ? null : lod.Strings[best];
    }

    /// <summary>Materials used by the model (path, total faces), most used first.</summary>
    public static List<(string Path, int Faces)> UsedMaterials()
    {
        try
        {
            return Attributes.Usage(Ed.Model)
                .Where(u => u.Kind == ResourceKind.Material)
                .Select(u => (u.Path, u.TotalFaces))
                .OrderByDescending(u => u.TotalFaces)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Model path for a material file: relative to the project root when inside it, backslashes (P3D style).</summary>
    public static string ToModelPath(string pathOrFile)
    {
        var p = pathOrFile.Replace('/', '\\');
        return Path.IsPathRooted(p) && !p.StartsWith('\\') ? AppServices.ToProjectPath(p) : p;
    }

    /// <summary>Assigns the material to the selected faces of the active LOD (one undo step).</summary>
    public static bool AssignToSelectedFaces(string material)
    {
        if (Ed.Model.Lods.Length == 0) return false;
        var faces = SelectedFaces();
        int n = 0;
        foreach (var b in faces)
            if (b != 0) n++;
        if (n == 0)
        {
            Ed.Log("Assign material: select faces first", LogLevel.Warn);
            return false;
        }
        var mp = ToModelPath(material);
        var ok = Ed.EditLod($"Assign material {Path.GetFileName(mp)}", lod => Attributes.SetFaceTextureMaterial(lod, faces, null, mp));
        Ed.Log(ok ? $"Material {mp} assigned to {n:N0} face(s)" : $"{n:N0} face(s) already use {mp}");
        return ok;
    }

    /// <summary>Selects the faces of the active LOD that use the material; returns their count.</summary>
    public static int SelectFacesUsing(string material)
    {
        if (Ed.Model.Lods.Length == 0) return 0;
        var mask = Attributes.FacesWithMaterial(Ed.Lod, ToModelPath(material));
        int n = 0;
        foreach (var b in mask)
            if (b != 0) n++;
        SelectionOps.SelectFaces(mask, null, SelectOp.Set, "Select faces using material");
        Ed.Log(n == 0 ? $"No face of this LOD uses {material}" : $"{n:N0} face(s) use {material}");
        return n;
    }

    /// <summary>Opens the material in the material editor panel.</summary>
    /// <summary>
    /// rvmat stages, colours and flags are only drawn in the Final view mode: switch to it when the material editor
    /// opens, so every edit shows up live (Alt+3 goes back to Textured).
    /// </summary>
    public static void EnsureFinalView()
    {
        if (Ed.Flag("view.shading", "textured") == "final") return;
        Ed.SetFlag("view.shading", "final");
        Ed.Log("Viewport switched to Final (Alt+4) — material (rvmat) edits show there live; Alt+3 = back to Textured");
    }

    public static void OpenInEditor(string material)
    {
        EnsureFinalView();
        PanelRegistry.Focus(EditorPanelId);
        if (MaterialEditorPanel.Current is { } p) p.Open(ToModelPath(material));
        else Ed.SetFlag(OpenFlag, ToModelPath(material));
    }

    public static void RevealInExplorer(string material)
    {
        var full = Store.Resolve(material);
        if (full is null) return;
        try
        {
            if (File.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(full) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else Ed.Log($"Not found: {full}", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            Ed.Log($"Explorer: {ex.Message}", LogLevel.Warn);
        }
    }

    public static void CopyPath(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Ed.Log($"Copied {text}");
        }
        catch (Exception ex)
        {
            Ed.Log($"Clipboard: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Under automation material files are written only into this folder (flag "materials.saveDir", else the
    /// folder of the automation script); null when not automated.</summary>
    public static string? AutomationSaveDir()
    {
        if (!Dlg.Automated) return null;
        var dir = Ed.Flag<string?>("materials.saveDir", null) ?? Path.GetDirectoryName(Path.GetFullPath(Automation.ScriptPath!));
        return dir;
    }

    /// <summary>Writes a material: the real file normally (with .bak when Settings.BackupOnSave), a copy in
    /// <see cref="AutomationSaveDir"/> under automation. Returns the written file or null.</summary>
    public static string? Write(Rvmat material, string targetFile)
    {
        try
        {
            var dir = AutomationSaveDir();
            var file = dir is null ? targetFile : Path.Combine(dir, Path.GetFileName(targetFile));
            material.Clone().Save(file, keepBackup: AppServices.Settings.BackupOnSave);
            Ed.Log(dir is null ? $"Saved {file}" : $"Automation: saved a copy as {file} (original untouched)");
            return file;
        }
        catch (Exception ex)
        {
            Ed.Log($"Save material failed: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
}
