using System.IO;
using P3DForge.App.Editor;
using P3DForge.App.Scene;
using P3DForge.App.Services;
using P3DForge.App.Viewport;
using P3DForge.Core;
using P3DForge.Core.Anim;
using P3DForge.Core.Model;

namespace P3DForge.App.Anim;

/// <summary>
/// The model.cfg of the open model: which file binarize would use (same folder or the nearest parent), the model's
/// CfgModels class (= the .p3d name), a working copy of its own animations and skeleton bones that the Animations
/// panel edits, and Save (only the changed classes are rewritten). Also drives the viewport preview: source values →
/// <see cref="AnimRig"/> → preview positions of the active LOD (and the proxy models on it).
/// </summary>
public static class AnimSession
{
    static EditorState Ed => AppServices.Editor;

    public static string? CfgPath { get; private set; }
    public static ModelCfg? Cfg { get; private set; }
    public static string Model { get; private set; } = "";
    public static string Skeleton { get; private set; } = "";
    /// <summary>Animations inherited from base models (read only here).</summary>
    public static List<AnimDef> Inherited { get; } = [];
    /// <summary>The model's own animations (edited).</summary>
    public static List<AnimDef> Own { get; } = [];
    /// <summary>Bones inherited through skeletonInherit (read only here).</summary>
    public static List<Bone> InheritedBones { get; } = [];
    /// <summary>The skeleton's own bones (edited).</summary>
    public static List<Bone> OwnBones { get; } = [];
    public static bool Dirty { get; private set; }
    public static string? LoadError { get; private set; }
    static string? loadedFor;
    static DateTime loadedStamp;

    /// <summary>Something in the session changed (load, edit, save, preview values).</summary>
    public static event Action? Changed;

    public static IEnumerable<AnimDef> All => Own.Concat(Inherited.Where(i => !Own.Any(o => string.Equals(o.Name, i.Name, StringComparison.OrdinalIgnoreCase))));
    public static IEnumerable<Bone> AllBones => InheritedBones.Concat(OwnBones);

    /// <summary>Loads (or reloads when the file changed on disk) the model.cfg of the current document.</summary>
    public static void Refresh(bool force = false)
    {
        var p3d = Ed.Path;
        var converted = p3d is null ? FileService.Converted : null;
        var cfgPath = p3d is null ? converted?.ModelCfg : ModelCfg.FindFor(p3d);
        var stamp = cfgPath is null ? default : File.GetLastWriteTimeUtc(cfgPath);
        string key = (p3d ?? "") + "|" + (cfgPath ?? "");
        if (!force && key == loadedFor && stamp == loadedStamp) return;
        if (Dirty && key == loadedFor && !force) return; // keep unsaved edits; the panel offers Reload
        loadedFor = key;
        loadedStamp = stamp;
        CfgPath = cfgPath;
        Model = p3d is not null ? ModelCfg.ModelName(p3d) : converted?.ModelName ?? "";
        LoadError = null;
        Own.Clear();
        Inherited.Clear();
        OwnBones.Clear();
        InheritedBones.Clear();
        Dirty = false;
        try
        {
            Cfg = cfgPath is null ? ModelCfg.Parse("") : ModelCfg.Load(cfgPath);
        }
        catch (Exception ex)
        {
            Cfg = ModelCfg.Parse("");
            LoadError = $"cannot read {cfgPath}: {ex.Message}";
        }
        Skeleton = Cfg.SkeletonName(Model);
        if (Skeleton.Length == 0) Skeleton = Model + "_skeleton";
        var own = new HashSet<string>(Cfg.OwnAnimationNames(Model), StringComparer.OrdinalIgnoreCase);
        foreach (var a in Cfg.Animations(Model))
            (own.Contains(a.Name) && string.Equals(a.Owner, Model, StringComparison.OrdinalIgnoreCase) ? Own : Inherited).Add(a);
        var ownBones = Cfg.OwnBones(Skeleton);
        OwnBones.AddRange(ownBones);
        foreach (var b in Cfg.Bones(Skeleton))
            if (!ownBones.Any(o => string.Equals(o.Name, b.Name, StringComparison.OrdinalIgnoreCase))) InheritedBones.Add(b);
        rig = null;
        Changed?.Invoke();
        ApplyPreview();
    }

    // ---- edits ------------------------------------------------------------------------------------

    public static void Touch()
    {
        Dirty = true;
        rig = null;
        Changed?.Invoke();
        ApplyPreview();
    }

    public static string UniqueName(string stem)
    {
        var names = new HashSet<string>(All.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        stem = new string(stem.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (stem.Length == 0 || char.IsDigit(stem[0])) stem = "Anim_" + stem;
        if (!names.Contains(stem)) return stem;
        for (int i = 2; ; i++)
            if (!names.Contains(stem + "_" + i)) return stem + "_" + i;
    }

    public static bool IsBone(string name) => AllBones.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The text Save would write (null when nothing would change).</summary>
    public static string? PendingText()
    {
        if (Cfg is null || Ed.Path is null) return null;
        var text = Cfg.Text;
        if (!OwnBones.SequenceEqual(Cfg.OwnBones(Skeleton)) || Cfg.SkeletonName(Model).Length == 0)
            text = ModelCfg.Parse(text).WithBones(Model, OwnBones);
        var cfg = ModelCfg.Parse(text);
        var current = cfg.OwnAnimations(Model);
        if (current.Count != Own.Count || current.Zip(Own).Any(p => !p.First.SameAs(p.Second with { Owner = p.First.Owner })) || !cfg.HasModel(Model))
            text = cfg.WithOwnAnimations(Model, Own);
        return text == Cfg.Text ? null : text;
    }

    /// <summary>Writes model.cfg (next to the model when it does not exist yet). Returns the file written, or null.</summary>
    public static string? Save()
    {
        if (Ed.Path is null) throw new InvalidOperationException("Save the model first — model.cfg goes next to it");
        if (PendingText() is not { } text)
        {
            Dirty = false;
            Changed?.Invoke();
            return null;
        }
        var path = CfgPath ?? Path.Combine(Path.GetDirectoryName(Ed.Path)!, "model.cfg");
        FileService.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text), AppServices.Settings.BackupOnSave);
        Dirty = false;
        loadedFor = null;
        Refresh(force: true);
        return path;
    }

    // ---- preview ----------------------------------------------------------------------------------

    /// <summary>Source values of the preview (missing = the source's minimum).</summary>
    public static Dictionary<string, double> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static bool PreviewOn { get; private set; }
    static AnimRig? rig;
    static Lod? rigLod;
    static Lod? rigMemory;
    static int previewLod = -1;

    /// <summary>The rig for the active LOD (rebuilt after edits or LOD / geometry changes).</summary>
    public static AnimRig? Rig
    {
        get
        {
            var model = Ed.Model;
            if (Ed.ActiveLod < 0 || Ed.ActiveLod >= model.Lods.Length) return null;
            var lod = model.Lods[Ed.ActiveLod];
            var memory = model.Lods.FirstOrDefault(l => LodNames.TypeOf(l.Resolution).Type.Id == "memory");
            if (rig is null || !ReferenceEquals(lod, rigLod) || !ReferenceEquals(memory, rigMemory))
            {
                rig = new AnimRig(lod, memory, [.. AllBones], [.. All]);
                rigLod = lod;
                rigMemory = memory;
            }
            return rig;
        }
    }

    public static void SetPreview(bool on)
    {
        PreviewOn = on;
        ApplyPreview();
        Changed?.Invoke();
    }

    public static void SetValue(string source, double value)
    {
        Values[source] = value;
        ApplyPreview();
    }

    public static void ResetValues()
    {
        Values.Clear();
        ApplyPreview();
        Changed?.Invoke();
    }

    /// <summary>Puts the animated positions on the viewport (or takes them off).</summary>
    public static void ApplyPreview()
    {
        var vp = ViewportPlugins.Current;
        if (previewLod >= 0 && (!PreviewOn || previewLod != Ed.ActiveLod))
        {
            vp?.SetPreviewPositions(previewLod, null);
            ProxySceneHost.SetPreview(-1, null);
            previewLod = -1;
        }
        if (!PreviewOn || vp is null || Rig is not { ActiveAnimations: > 0 } r) return;
        var pos = r.Evaluate(Values);
        previewLod = Ed.ActiveLod;
        vp.SetPreviewPositions(previewLod, pos);
        ProxySceneHost.SetPreview(previewLod, pos);
    }

    /// <summary>Called on document / model changes: reload when the file is another one, keep the preview on the new geometry.</summary>
    public static void OnEditorChanged(Change c)
    {
        if ((c & (Change.Document | Change.Load)) != 0) Refresh();
        if ((c & (Change.Model | Change.ActiveLod | Change.Load)) != 0) ApplyPreview();
    }
}
