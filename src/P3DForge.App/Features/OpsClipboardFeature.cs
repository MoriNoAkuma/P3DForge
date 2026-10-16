using System.IO;
using System.Windows;
using System.Windows.Controls;
using P3DForge.App.Dialogs;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Cut / Copy / Paste / Paste to LODs. The selection travels as a one-LOD MLOD file in the Windows clipboard format
/// "P3DForge.Fragment" (so it works between P3D Forge windows) plus a plain-text summary; an in-process copy is kept as
/// a fallback when the clipboard is busy.
/// </summary>
public sealed class OpsClipboardFeature : IFeature
{
    public const string Format = "P3DForge.Fragment";
    public int Order => 43;
    static EditorState Ed => AppServices.Editor;

    static byte[]? local;

    static bool CanCopy() => OpsUtil.HasSelection();

    static bool CanPaste()
    {
        if (local is not null) return true;
        try
        {
            return Clipboard.ContainsData(Format);
        }
        catch
        {
            return false;
        }
    }

    static Fragment? Copy()
    {
        var lod = Ed.Lod;
        var sel = Selection.GetSelected(lod);
        var points = Selection.PointsFromFaces(lod, sel.Faces, sel.Points);
        if (!points.AsSpan().ContainsAnyExcept((byte)0)) return null;
        var frag = Structure.Extract(lod, points, sel.Faces);
        var bytes = MeshMath.FragmentToMlod(frag, lod.Resolution);
        local = bytes;
        try
        {
            var data = new DataObject();
            data.SetData(Format, new MemoryStream(bytes));
            data.SetText(MeshMath.FragmentSummary(frag));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex)
        {
            Ed.Log($"Windows clipboard unavailable ({ex.Message}); the copy is kept inside this window", LogLevel.Warn);
        }
        return frag;
    }

    static Fragment? Read()
    {
        byte[]? bytes = null;
        try
        {
            if (Clipboard.ContainsData(Format) && Clipboard.GetData(Format) is MemoryStream ms) bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            Ed.Log($"Windows clipboard unavailable ({ex.Message}); using this window's last copy", LogLevel.Warn);
        }
        bytes ??= local;
        if (bytes is null) return null;
        try
        {
            return MeshMath.MlodToFragment(bytes).Fragment;
        }
        catch (Exception ex)
        {
            Ed.Log($"Clipboard data is not valid P3D geometry: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "edit.copy",
                Label = "_Copy",
                Hotkeys = ["Ctrl+C", "Ctrl+Insert"],
                CanExecute = CanCopy,
                Execute = () =>
                {
                    if (Copy() is { } f) Ed.Log($"Copied {OpsUtil.Plural(f.PointCount, "point")}, {OpsUtil.Plural(f.FaceCount, "face")}");
                },
            },
            new Command
            {
                Id = "edit.cut",
                Label = "Cu_t",
                Hotkeys = ["Ctrl+X", "Shift+Delete"],
                CanExecute = CanCopy,
                Execute = () =>
                {
                    if (Copy() is not { } f) return;
                    var sel = Ed.GetSelection();
                    OpsUtil.Apply("Cut", lod => Structure.DeleteSelection(lod, sel.Points, sel.Faces, Ed.SelectMode == SelectMode.Faces));
                    Ed.Log($"Cut {OpsUtil.Plural(f.PointCount, "point")}, {OpsUtil.Plural(f.FaceCount, "face")}");
                },
            },
            new Command
            {
                Id = "edit.paste",
                Label = "_Paste",
                Hotkeys = ["Ctrl+V", "Shift+Insert"],
                CanExecute = CanPaste,
                Execute = () =>
                {
                    if (Read() is not { } f) return;
                    if (OpsUtil.Apply("Paste", lod => MeshMath.AppendSafe(lod, f, selectNew: true)))
                        Ed.Log($"Pasted {OpsUtil.Plural(f.PointCount, "point")}, {OpsUtil.Plural(f.FaceCount, "face")}");
                },
            },
            new Command
            {
                Id = "edit.pasteToLods",
                Label = "Paste to _LODs…",
                CanExecute = CanPaste,
                Execute = () =>
                {
                    if (Read() is not { } f) return;
                    if (AskLods(Ed.Model, f) is not { Length: > 0 } targets) return;
                    Dlg.Try(() => Ed.Edit("Paste to LODs", m =>
                    {
                        foreach (int i in targets) m = m.WithLod(i, MeshMath.AppendSafe(m.Lods[i], f, selectNew: true));
                        return m;
                    }));
                    Ed.Log($"Pasted {OpsUtil.Plural(f.PointCount, "point")}, {OpsUtil.Plural(f.FaceCount, "face")} into {OpsUtil.Plural(targets.Length, "LOD")}");
                },
            });
    }

    /// <summary>LOD check list; automation answer "0,2". Null when cancelled.</summary>
    static int[]? AskLods(P3DModel model, Fragment f)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Dlg.Label($"Paste {OpsUtil.Plural(f.PointCount, "point")} and {OpsUtil.Plural(f.FaceCount, "face")} into:"));
        var boxes = new List<(CheckBox Box, int Index)>();
        var stack = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
        for (int i = 0; i < model.Lods.Length; i++)
        {
            var cb = new CheckBox { Content = LodNames.Name(model.Lods[i].Resolution), IsChecked = i == Ed.ActiveLod, FontSize = 12, MinHeight = 0, Margin = new Thickness(0, 1, 0, 1) };
            boxes.Add((cb, i));
            stack.Children.Add(cb);
        }
        panel.Children.Add(new ScrollViewer { Content = stack, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var w = Dlg.MakeWindow("Paste to LODs", panel, 320);
        var (row, _) = Dlg.OkCancel(w, () => boxes.Any(b => b.Box.IsChecked == true), "Paste");
        panel.Children.Add(row);
        if (Dlg.Automated)
        {
            Dlg.Capture(w, "paste-to-lods");
            var a = Dlg.TakeAnswer();
            if (a is null) return null;
            return a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i >= 0 && i < model.Lods.Length).Distinct().ToArray();
        }
        return w.ShowDialog() == true ? boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Index).ToArray() : null;
    }
}
