using System.Numerics;
using System.Windows;
using P3DForge.App.Editor;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Viewport.Interaction;

/// <summary>
/// Flag keys and shared helpers of the Object Builder mouse scheme (selection modes, axis locks, pin).
/// Modes live in <see cref="EditorState"/> flags so menus, toolbar and other features can read them:
/// Object mode = <see cref="SelectMode.Faces"/> + flag <see cref="ObjectMode"/>.
/// </summary>
public static class Interact
{
    /// <summary>bool: Select Object mode (click/rectangle selects whole connected parts). Implies SelectMode.Faces.</summary>
    public const string ObjectMode = "mode.object";
    /// <summary>bool: LMB drag draws a freehand lasso instead of a rectangle.</summary>
    public const string Lasso = "mode.lasso";
    /// <summary>bool: the next LMB click places the pin (one-shot).</summary>
    public const string MovePin = "mode.movePin";
    /// <summary>bool: OB axis locks — mouse transforms only move along the locked axes.</summary>
    public const string LockX = "view.lockX", LockY = "view.lockY", LockZ = "view.lockZ";
    /// <summary>bool: rotate/scale/mirror (and create.point) use the pin.</summary>
    public const string UsePin = "transform.usePin";
    /// <summary>bool (default false): rectangle/lasso/click also select points occluded by faces (X-ray).</summary>
    public const string SelectHidden = "interaction.selectHidden";
    /// <summary>bool (default false): encoded selection weights act as soft selection for mouse/numeric transforms.</summary>
    public const string UseWeights = "interaction.useWeights";

    static EditorState Ed => AppServices.Editor;

    public static bool IsObjectMode => Ed.Flag(ObjectMode, false);
    public static bool IsLasso => Ed.Flag(Lasso, false);
    public static bool IsMovePin => Ed.Flag(MovePin, false);
    public static (bool X, bool Y, bool Z) Locks => (Ed.Flag(LockX, false), Ed.Flag(LockY, false), Ed.Flag(LockZ, false));
    /// <summary>Use Pin is on and a pin exists.</summary>
    public static bool PinActive => Ed.Flag(UsePin, false) && Ed.Pin is not null;
    public static bool SoftSelection => Ed.Flag(UseWeights, false);
    /// <summary>Selection ignores occluded points unless X-ray selection is on or the view is wireframe.</summary>
    public static bool VisibleOnly => !Ed.Flag(SelectHidden, false) && Ed.Flag("view.shading", "textured") != "wire";

    public static Vec3 ToVec(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 ToVector(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
    public static Vec3 RenderToRaw(Vector3 r) => new(r.X, r.Y, -r.Z);
    public static Vector3 RawToRender(Vec3 r) => new((float)r.X, (float)r.Y, (float)-r.Z);

    /// <summary>Mouse ray of a view in RAW coordinates.</summary>
    public static (Vec3 O, Vec3 D) RawRay(IViewHandle v, Point p)
    {
        var (o, d) = v.RayAt(p);
        return (RenderToRaw(o), RenderToRaw(d));
    }

    /// <summary>View direction (camera forward) in RAW coordinates.</summary>
    public static Vec3 ViewDir(IViewHandle v) => RenderToRaw(v.Forward);

    /// <summary>Selected points that may be transformed (hidden/locked removed, weights kept); null when none.</summary>
    public static byte[]? TransformMask(Lod lod)
    {
        var sel = Selection.GetSelected(lod).Points;
        var blocked = SelectTools.BlockedPoints(lod);
        int n = 0;
        for (int i = 0; i < sel.Length; i++)
        {
            if (blocked[i] != 0) sel[i] = 0;
            else if (sel[i] != 0) n++;
        }
        return n > 0 ? sel : null;
    }

    /// <summary>Pivot for rotate/scale/mirror: the pin when Use Pin is on, else the bounding-box centre of <paramref name="mask"/>.</summary>
    public static Vec3 Pivot(Lod lod, byte[]? mask) => PinActive ? ToVec(Ed.Pin!.Value) : Transforms.Center(lod, mask) ?? Vec3.Zero;

    /// <summary>
    /// 3D cursor for a mouse position without a face under it: the y = 0 ground plane in perspective views (or the
    /// plane through the pin facing the camera when the ground is not hit), the view plane through the pin (or origin)
    /// in orthographic views.
    /// </summary>
    public static Vec3? PlanePoint(IViewHandle v, Point p)
    {
        var (o, d) = RawRay(v, p);
        var pin = Ed.Pin is { } q ? ToVec(q) : Vec3.Zero;
        var vd = ViewDir(v);
        if (!v.IsOrthographic)
        {
            var g = Transforms.RayPlane(o, d, Vec3.Zero, new Vec3(0, 1, 0));
            if (g is { } gp && Vec3.Dot(gp - o, d) > 0 && (gp - o).Length < 1e4) return gp;
        }
        return Transforms.RayPlane(o, d, pin, vd);
    }

    public static string Fmt(double v) => (Math.Abs(v) < 5e-4 ? 0 : v).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    public static string Fmt(Vec3 v) => $"{Fmt(v.X)}, {Fmt(v.Y)}, {Fmt(v.Z)}";
}
