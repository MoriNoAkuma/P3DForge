using System.IO;
using System.Windows;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Resources;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// The one obvious way to put a texture or material on geometry: Surfaces ▸ Set Texture… (Ctrl+T) / Set Material…, the
/// same items in the viewport context menu and on the toolbar, and dropping .paa / .tga / .png / .rvmat files onto the
/// window. Target: the selected faces (or the faces whose points are all selected); with nothing selected it offers the
/// whole LOD instead of silently doing nothing.
/// </summary>
public sealed class SurfaceFeature : IFeature
{
    public const string SetTextureId = "surface.setTexture";
    public const string SetMaterialId = "surface.setMaterial";
    public int Order => 44;
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = SetTextureId,
                Label = "Set _Texture…",
                Hotkeys = ["Ctrl+T"],
                Description = "Choose a texture (.paa) for the selected faces — or for the whole LOD when nothing is selected. You can also drop a .paa file onto the window.",
                CanExecute = HasFaces,
                Execute = () => Pick(ResourceKind.Texture),
            },
            new Command
            {
                Id = SetMaterialId,
                Label = "Set _Material (.rvmat)…",
                Description = "Choose a material (.rvmat) for the selected faces — or for the whole LOD when nothing is selected. You can also drop an .rvmat file onto the window.",
                CanExecute = HasFaces,
                Execute = () => Pick(ResourceKind.Material),
            },
            new Command
            {
                Id = "surface.fixPaths",
                Label = "_Fix Missing Paths (textures, materials, proxies)…",
                Description = "Finds texture / material paths that do not exist on the work drive (e.g. the first folder is missing), looks for the files in the folders around the model and fixes the paths in every LOD",
                CanExecute = () => Ed.Model.Lods.Length > 0,
                Execute = FixPaths,
            });
        Toolbar.Add(new ToolbarItem { Command = SetTextureId, Glyph = "", Group = "surface", Order = 60, Tooltip = "Set texture on the selected faces (Ctrl+T)" });
        Toolbar.Add(new ToolbarItem { Command = SetMaterialId, Glyph = "", Group = "surface", Order = 61, Tooltip = "Set material (.rvmat) on the selected faces" });
        Toolbar.Add(new ToolbarItem { Command = "material.editSelected", Glyph = "", Group = "surface", Order = 62, Tooltip = "Edit the material (rvmat) of the selected faces — live preview in Final mode" });
    }

    static bool HasFaces() => Ed.Lod.FaceCount > 0;

    public static bool IsTextureFile(string path) =>
        ResourcePicker.TextureExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsMaterialFile(string path) =>
        ResourcePicker.MaterialExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Faces to change; null when cancelled. Nothing selected → asks whether to use every face of the LOD.</summary>
    static byte[]? Target(string what)
    {
        var lod = Ed.Lod;
        if (lod.FaceCount == 0)
        {
            Dlg.Fail($"This LOD has no faces to put a {what} on.");
            return null;
        }
        var faces = OpsUtil.TargetFaces(lod);
        if (MeshMath.CountSet(faces) > 0) return faces;
        if (!Dlg.Confirm($"Set {what}",
                $"No faces are selected.\n\nPut the {what} on all {lod.FaceCount:N0} faces of this LOD?\n\n" +
                "To change only a part of the model, select it first: click it, drag a rectangle, or click a named selection.",
                "All faces"))
            return null;
        return MeshMath.Ones(lod.FaceCount);
    }

    static void Pick(ResourceKind kind)
    {
        bool tex = kind == ResourceKind.Texture;
        if (Target(tex ? "texture" : "material") is not { } faces) return;
        var lod = Ed.Lod;
        int first = Array.IndexOf(faces, (byte)1);
        string current = first < 0 ? "" : lod.Strings[(int)(tex ? lod.FaceTexture[first] : lod.FaceMaterial[first])];
        if (ResourcePicker.Pick(Application.Current.MainWindow, kind, current) is not { Length: > 0 } path) return;
        Apply(kind, path, faces);
    }

    /// <summary>A texture / material file dropped onto the window.</summary>
    public static void Drop(string file)
    {
        var kind = IsMaterialFile(file) ? ResourceKind.Material : ResourceKind.Texture;
        if (Target(kind == ResourceKind.Texture ? "texture" : "material") is not { } faces) return;
        Apply(kind, AppServices.ToProjectPath(Path.GetFullPath(file)), faces);
    }

    /// <summary>The file exists, or (for .tga / .png references) its .paa, which is what the game loads.</summary>
    static bool Exists(string abs)
    {
        if (File.Exists(abs)) return true;
        var ext = Path.GetExtension(abs);
        return (ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) || ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
               && File.Exists(Path.ChangeExtension(abs, ".paa"));
    }

    /// <summary>
    /// Looks for a missing path under every folder that contains the model, deepest first: "rifles\data\a_co.paa"
    /// referenced by P:\MyMod\rifles\gun\gun.p3d is found as P:\MyMod\rifles\data\a_co.paa, which
    /// becomes the game path "MyMod\rifles\data\a_co.paa". Only files on the work drive count.
    /// </summary>
    static string? FindNearModel(string path)
    {
        if (Ed.Path is not { } file) return null;
        var rel = path.Replace('/', '\\').TrimStart('\\');
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(file)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, rel);
            if (!Exists(candidate)) continue;
            var game = AppServices.NormalizeGamePath(candidate);
            if (!Path.IsPathRooted(game)) return game;
        }
        return null;
    }

    static void FixPaths()
    {
        var model = Ed.Model;
        // textures / materials
        var missing = new List<ResourceUse>();
        foreach (var u in Attributes.Usage(model))
        {
            if (u.Path.Length == 0 || u.Path.StartsWith('#')) continue;
            if (AppServices.ResolveProjectPath(u.Path) is { } abs && Exists(abs)) continue;
            missing.Add(u);
        }
        var fixes = new List<(ResourceUse Use, string New)>();
        foreach (var u in missing)
            if (FindNearModel(u.Path) is { } found && !string.Equals(found, u.Path, StringComparison.OrdinalIgnoreCase))
                fixes.Add((u, found));
        // proxies: "proxy:\rifles\gun\mag.001" whose .p3d only exists under another first folder
        int proxyMissing = 0;
        var proxyFixes = new List<(string Old, string New)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lod in model.Lods)
            foreach (var tag in lod.Tags)
            {
                if (tag is not SelectionTag s || !Proxies.IsProxyName(s.Name) || !seen.Add(s.Name)) continue;
                var (proxyPath, index) = Proxies.Parse(s.Name);
                var file = proxyPath.Replace('/', '\\').TrimStart('\\') + ".p3d";
                if (AppServices.ResolveProjectPath(file) is { } abs && File.Exists(abs)) continue;
                proxyMissing++;
                if (FindNearModel(file) is not { } found) continue;
                var newPath = "\\" + Path.ChangeExtension(found, null);
                proxyFixes.Add((s.Name, index >= 0 ? Proxies.SelectionName(newPath, index) : Proxies.Prefix + newPath));
            }
        int total = missing.Count + proxyMissing, fixable = fixes.Count + proxyFixes.Count, lost = total - fixable;
        if (total == 0)
        {
            Ed.Log("Every texture, material and proxy path exists on the work drive — nothing to fix");
            return;
        }
        if (fixable == 0)
        {
            Ed.Log($"{OpsUtil.Plural(total, "path")} do not exist on the work drive and no matching file was found around the model (Tools ▸ Validate lists them)", LogLevel.Warn);
            return;
        }
        var lines = fixes.Select(f => $"{f.Use.Path}\n      → {f.New}").Concat(proxyFixes.Select(p => $"{p.Old}\n      → {p.New}")).ToList();
        var list = string.Join("\n", lines.Take(12));
        if (lines.Count > 12) list += $"\n… and {lines.Count - 12} more";
        if (lost > 0) list += $"\n\n{OpsUtil.Plural(lost, "other missing path")} could not be found anywhere around the model.";
        if (!Dlg.Confirm("Fix Missing Paths", $"These files exist under a different folder of the work drive:\n\n{list}\n\nFix the paths in every LOD?", "Fix paths"))
            return;
        Dlg.Try(() => Ed.Edit("Fix missing paths", m =>
        {
            foreach (var (use, fixedPath) in fixes)
                m = Attributes.ReplacePaths(m, use.Path, fixedPath, new ReplaceOptions
                {
                    Match = PathMatch.Exact,
                    Textures = use.Kind == ResourceKind.Texture,
                    Materials = use.Kind == ResourceKind.Material,
                }).Model;
            foreach (var (oldName, newName) in proxyFixes) m = NamedSelections.RenameAll(m, oldName, newName);
            return m;
        }));
        foreach (var line in lines) Ed.Log("Path fixed: " + line.Replace("\n      ", " "));
        Ed.Log($"Fixed {OpsUtil.Plural(fixable, "path")}" + (lost > 0 ? $"; {OpsUtil.Plural(lost, "path")} still missing (Tools ▸ Validate)" : ""), lost > 0 ? LogLevel.Warn : LogLevel.Info);
    }

    static void Apply(ResourceKind kind, string path, byte[] faces)
    {
        path = AppServices.NormalizeGamePath(path);
        bool tex = kind == ResourceKind.Texture;
        int n = MeshMath.CountSet(faces);
        if (!OpsUtil.Apply(tex ? "Set Texture" : "Set Material",
                lod => Attributes.SetFaceTextureMaterial(lod, faces, tex ? path : null, tex ? null : path)))
            return;
        Ed.Log($"{(tex ? "Texture" : "Material")} {path} set on {OpsUtil.Plural(n, "face")}");
        if (Path.IsPathRooted(path))
            Ed.Log($"{path} is outside the work drive ({AppServices.Settings.ProjectRoot}) — the game will not find it. Copy it into your mod folder on the work drive and set it again.", LogLevel.Warn);
        else if (tex && !path.StartsWith('#') && !Path.GetExtension(path).Equals(".paa", StringComparison.OrdinalIgnoreCase))
            Ed.Log($"{Path.GetFileName(path)} is not a .paa — the game needs .paa textures (Surfaces ▸ Convert to PAA…).", LogLevel.Warn);
        // Make the result visible.
        if (Ed.Flag("view.shading", "textured") is "wire" or "solid") Ed.SetFlag("view.shading", "textured");
    }
}
