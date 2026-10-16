using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Faces;
using P3DForge.App.Dialogs.Points;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Inspector feature (Object Builder parity): Face Properties (E), Point Properties (Shift+E), Select Faces with Same
/// Texture. The Resource Library / Mass Renaming and the Inspector panel / Measuring live in
/// InspectorPanelsFeature.
/// </summary>
public sealed class InspectorFeature : IFeature
{
    public int Order => 60;

    static EditorState Ed => AppServices.Editor;

    static bool HasPoints() => Ed.Lod.PointCount > 0 && Selection.PeekSelected(Ed.Lod) is { } s && !s.IsEmpty;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "faces.properties",
                Label = "Face _Properties…",
                Hotkeys = ["E"],
                Execute = FacePropertiesDialog.Show,
                CanExecute = () => Ed.Lod.FaceCount > 0 && HasPoints(),
                Description = "Texture, material, lighting, shadows, Z bias, texture merging and user value of the selected faces",
            },
            new Command
            {
                Id = "points.properties",
                Label = "Point _Properties…",
                Hotkeys = ["Shift+E"],
                Execute = PointPropertiesDialog.Show,
                CanExecute = HasPoints,
                Description = "Position and flags of the selected points",
            },
            new Command
            {
                Id = "faces.selectByTexture",
                Label = "Select Faces with Same _Texture",
                Execute = SelectByTexture,
                CanExecute = () => Ed.Lod.FaceCount > 0 && HasPoints(),
                Description = "Selects every face of the active LOD whose texture is used by a selected face",
            });
    }

    /// <summary>Selects all faces whose texture equals the texture of any selected face (faces of selected points when none).</summary>
    public static void SelectByTexture()
    {
        var lod = Ed.Lod;
        var sel = Ed.GetSelection();
        var faces = Attributes.EffectiveFaces(lod, sel.Points, sel.Faces);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var used = new bool[lod.Strings.Length];
        for (int f = 0; f < faces.Length; f++)
            if (faces[f] != 0) used[lod.FaceTexture[f]] = true;
        for (int i = 0; i < used.Length; i++)
            if (used[i]) keys.Add(Attributes.PathKey(lod.Strings[i]));
        if (keys.Count == 0)
        {
            Dlg.Fail("Select Faces with Same Texture: select a face first.");
            return;
        }
        var hit = new bool[lod.Strings.Length];
        for (int i = 0; i < hit.Length; i++) hit[i] = keys.Contains(Attributes.PathKey(lod.Strings[i]));
        var mask = new byte[lod.FaceCount];
        int n = 0;
        for (int f = 0; f < mask.Length; f++)
        {
            if (!hit[lod.FaceTexture[f]]) continue;
            mask[f] = 1;
            n++;
        }
        SelectionOps.SelectFaces(mask, null, SelectOp.Set, "Select faces with same texture");
        var label = keys.Count == 1 ? (keys.First().Length == 0 ? "(no texture)" : keys.First()) : $"{keys.Count} textures";
        Ed.Log($"Selected {n:N0} faces with {label}");
    }
}
