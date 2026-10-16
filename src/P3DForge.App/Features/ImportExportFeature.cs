using System.IO;
using Microsoft.Win32;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Ops;
using P3DForge.App.Editor;
using P3DForge.App.Services;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// File ▸ Merge / Merge as Selection (another .p3d into this model, LOD by LOD), File ▸ Import ▸ OBJ / FBX (into the
/// active LOD or a new LOD, axes as the Arma 3 Blender toolbox, textures mapped to work-drive paths) and File ▸
/// Export ▸ OBJ (the selected faces or the whole LOD, with an .mtl pointing at the textures on the work drive).
/// .obj / .fbx files dropped on the window are imported. Each import / merge is one undo step and becomes the selection.
/// </summary>
public sealed class ImportExportFeature : IFeature
{
    public int Order => 12;
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "file.merge",
                Label = "_Merge…",
                Description = "Adds every LOD of another .p3d to this model: into the LOD with the same resolution, or as a new LOD",
                Execute = () => MergeFile(asSelection: false),
            },
            new Command
            {
                Id = "file.mergeAsSelection",
                Label = "Merge as _Selection…",
                Description = "Like Merge, and the merged geometry also gets one named selection (the file name by default)",
                Execute = () => MergeFile(asSelection: true),
            },
            new Command
            {
                Id = "file.import.obj",
                Label = "_OBJ (Wavefront)…",
                Description = "Imports an .obj (Blender: File ▸ Export ▸ Wavefront) into the active LOD or a new LOD",
                Execute = () => Import(PickFile("Import OBJ", "Wavefront OBJ (*.obj)|*.obj")),
            },
            new Command
            {
                Id = "file.import.fbx",
                Label = "_FBX (binary)…",
                Description = "Imports the meshes of a binary .fbx (Blender, Maya, 3ds Max) with their UV sets and textures",
                Execute = () => Import(PickFile("Import FBX", "FBX (*.fbx)|*.fbx")),
            },
            new Command
            {
                Id = "file.export.obj",
                Label = "_OBJ (Wavefront)…",
                Description = "Exports the selected faces (or the whole active LOD) as .obj + .mtl, e.g. for Blender",
                CanExecute = () => Ed.Lod.FaceCount > 0,
                Execute = ExportObj,
            });
    }

    public static bool IsImportable(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".obj" or ".fbx";

    /// <summary>File chooser; under automation the path comes from flag "automation.file".</summary>
    static string? PickFile(string title, string filter, bool save = false, string? fileName = null)
    {
        if (Dlg.Automated)
        {
            var f = Ed.Flag<string?>("automation.file", null);
            Ed.SetFlag("automation.file", null);
            return f;
        }
        var dir = Ed.Path is null ? AppServices.Settings.ProjectRoot : Path.GetDirectoryName(Ed.Path);
        FileDialog dlg = save
            ? new SaveFileDialog { Title = title, Filter = filter, InitialDirectory = dir, FileName = fileName ?? "" }
            : new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = dir };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ---- merge ----------------------------------------------------------------------------------

    static void MergeFile(bool asSelection)
    {
        if (PickFile(asSelection ? "Merge as Selection" : "Merge", "P3D models (*.p3d)|*.p3d") is not { } file) return;
        P3DModel source;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (Odol.IsOdol(bytes))
            {
                Dlg.Fail($"{Path.GetFileName(file)} is binarized (ODOL) — only source (MLOD) models can be merged");
                return;
            }
            source = Mlod.Read(bytes);
        }
        catch (Exception ex)
        {
            Dlg.Fail($"Cannot read {file}: {ex.Message}");
            return;
        }
        string? name = null;
        if (asSelection)
        {
            name = Dlg.Prompt("Merge as Selection", "Named selection for the merged geometry:", Path.GetFileNameWithoutExtension(file),
                s => string.IsNullOrWhiteSpace(s) ? "Enter a name" : null);
            if (name is null) return;
        }
        ModelMergeResult? result = null;
        Dlg.Try(() => Ed.Edit($"Merge {Path.GetFileName(file)}", m => (result = Merge.Models(m, source, name)).Model));
        if (result is null) return;
        Ed.Log($"Merged {Path.GetFileName(file)}: {OpsUtil.Plural(result.Appended, "LOD")} added to existing LODs, " +
               $"{OpsUtil.Plural(result.Added, "new LOD")}" + (result.RenamedProxies > 0 ? $", {OpsUtil.Plural(result.RenamedProxies, "proxy")} renumbered" : "")
               + (name is null ? "" : $", selection \"{name}\""));
    }

    // ---- import ---------------------------------------------------------------------------------

    /// <summary>Asks how to import <paramref name="file"/> (scale, axes, target LOD) and appends it as one undo step.</summary>
    public static void Import(string? file)
    {
        if (file is null) return;
        bool fbx = Path.GetExtension(file).Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        var settings = AppServices.Settings;
        var form = new Form($"Import {(fbx ? "FBX" : "OBJ")}",
                $"{Path.GetFileName(file)}\nAxes as the Arma 3 Blender toolbox: Blender X, Y, Z → P3D X, Z, Y. The imported geometry becomes the selection.")
            .Number("scale", "Scale", settings.Get("import.scale", 1.0), 1e-6, 1e6);
        if (!fbx) form.Choice("up", "Up axis in the file", ["Y (Blender's default OBJ export)", "Z"], settings.Get("import.zUp", false) ? 1 : 0);
        form.Choice("target", "Into", [$"Active LOD ({LodNames.Name(Ed.Lod.Resolution)})", "New LOD"], 0)
            .Number("resolution", "New LOD resolution", NextFreeResolution(), 0, float.MaxValue);
        if (!form.Show("Import")) return;
        double scale = form.Num("scale");
        bool zUp = !fbx && form.Index("up") == 1;
        settings.Set("import.scale", scale);
        if (!fbx) settings.Set("import.zUp", zUp);
        var outside = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new ImportOptions
        {
            Scale = scale,
            ZUp = zUp,
            Texture = path => GamePath(path, Path.GetDirectoryName(file)!, outside),
        };
        ImportResult r;
        try
        {
            r = fbx ? Fbx.Read(File.ReadAllBytes(file), options) : Obj.Read(File.ReadAllText(file), Path.GetDirectoryName(file), options);
        }
        catch (Exception ex)
        {
            Dlg.Fail($"Cannot import {Path.GetFileName(file)}: {ex.Message}");
            return;
        }
        if (r.Fragment.FaceCount == 0)
        {
            Dlg.Fail($"{Path.GetFileName(file)}: nothing to import ({string.Join("; ", r.Warnings)})");
            return;
        }
        bool newLod = form.Index("target") == 1;
        float resolution = (float)form.Num("resolution");
        int target = -1;
        Dlg.Try(() => Ed.Edit($"Import {Path.GetFileName(file)}", m =>
        {
            if (!newLod)
            {
                target = Ed.ActiveLod;
                return m.WithLod(target, MeshMath.AppendSafe(m.Lods[target], r.Fragment, selectNew: true));
            }
            target = LodList.Find(m, resolution);
            if (target >= 0) return m.WithLod(target, MeshMath.AppendSafe(m.Lods[target], r.Fragment, selectNew: true));
            var lods = m.Lods.ToList();
            target = LodList.SortedInsertIndex(lods, resolution);
            lods.Insert(target, MeshMath.AppendSafe(Lod.Empty(resolution), r.Fragment, selectNew: true));
            return m with { Lods = [.. lods] };
        }));
        if (target < 0) return;
        if (target != Ed.ActiveLod) Ed.SetActiveLod(target);
        Ed.Log($"Imported {Path.GetFileName(file)}: {r.Fragment.PointCount:N0} points, {r.Fragment.FaceCount:N0} faces from " +
               $"{OpsUtil.Plural(r.Objects, "object")}" + (r.Triangulated > 0 ? $" ({OpsUtil.Plural(r.Triangulated, "polygon")} with more than 4 corners split into triangles)" : "")
               + (r.Textures.Count > 0 ? $", {OpsUtil.Plural(r.Textures.Count, "texture")}" : ""));
        foreach (var w in r.Warnings) Ed.Log($"Import: {w}", LogLevel.Warn);
        if (outside.Count > 0)
            Ed.Log($"{OpsUtil.Plural(outside.Count, "texture")} outside the work drive ({settings.ProjectRoot}), the game will not find them: " +
                   string.Join(", ", outside.Take(5).Select(Path.GetFileName)) + " — copy them into your mod folder, convert to .paa and use Surfaces ▸ Set Texture", LogLevel.Warn);
        if (Ed.Flag("view.shading", "textured") is "wire" or "solid" && r.Textures.Count > 0) Ed.SetFlag("view.shading", "textured");
    }

    /// <summary>A texture file of the imported model as a model path: on the work drive → "Mod\data\x.paa" (the .paa next to a
    /// .png / .tga when there is one); elsewhere the absolute path, reported as outside.</summary>
    static string GamePath(string path, string folder, ISet<string> outside)
    {
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(folder, path));
        if (!File.Exists(full) && Path.Combine(folder, Path.GetFileName(path)) is var local && File.Exists(local)) full = local;
        var paa = Path.ChangeExtension(full, ".paa");
        if (!full.EndsWith(".paa", StringComparison.OrdinalIgnoreCase) && File.Exists(paa)) full = paa;
        var game = AppServices.NormalizeGamePath(full);
        if (Path.IsPathRooted(game)) outside.Add(game);
        return game;
    }

    static double NextFreeResolution()
    {
        float r = 0;
        while (LodList.Find(Ed.Model, r) >= 0 && r < 100) r += 1;
        return r;
    }

    // ---- export ---------------------------------------------------------------------------------

    static void ExportObj()
    {
        var lod = Ed.Lod;
        var faces = OpsUtil.TargetFaces(lod);
        bool selection = MeshMath.CountSet(faces) > 0;
        var name = Path.GetFileNameWithoutExtension(Ed.FileName);
        if (PickFile("Export OBJ", "Wavefront OBJ (*.obj)|*.obj", save: true, $"{name}_{LodNames.Name(lod.Resolution).Replace(' ', '_')}.obj") is not { } file) return;
        if (!file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) file += ".obj";
        var mtl = Path.ChangeExtension(file, ".mtl");
        var (objText, mtlText) = Obj.Write(lod, selection ? faces : null, name, Path.GetFileName(mtl),
            tex => AppServices.ResolveProjectPath(tex) is { } abs && !File.Exists(abs) && Path.ChangeExtension(abs, ".png") is var png && File.Exists(png) ? png
                : AppServices.ResolveProjectPath(tex) ?? tex);
        try
        {
            FileService.WriteAtomic(file, System.Text.Encoding.UTF8.GetBytes(objText), backup: false);
            FileService.WriteAtomic(mtl, System.Text.Encoding.UTF8.GetBytes(mtlText), backup: false);
        }
        catch (Exception ex)
        {
            Dlg.Fail($"Cannot write {file}: {ex.Message}");
            return;
        }
        Ed.Log($"Exported {(selection ? $"{MeshMath.CountSet(faces):N0} selected faces" : $"{lod.FaceCount:N0} faces")} of LOD {LodNames.Name(lod.Resolution)} to {file}");
    }
}
