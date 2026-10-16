using System.IO;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Ops;
using P3DForge.App.Editor;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.App.Viewport.Interaction;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.App.Features;

/// <summary>
/// Create menu: point (Insert), face from 3–4 points (F6), primitives (plane, box F7, circle, sphere, cylinder F8)
/// placed at the pin, proxy, extrude (G). New geometry becomes the selection.
/// </summary>
public sealed class OpsCreateFeature : IFeature
{
    public int Order => 42;
    static EditorState Ed => AppServices.Editor;

    static readonly string[] Aligns = ["Center to pin", "Bottom at pin", "Top at pin"];

    // last used parameters (per session)
    static PlaneParams plane = new(1, 1, 1, 1);
    static BoxParams box = new(1, 1, 1, 1, 1, 1);
    static CylinderParams cylinder = new(0.5, 1, 16, 1);
    static SphereParams sphere = new(0.5, 16, 8);
    static CircleParams circle = new(0.5, 16, true);
    static int align;
    static string lastProxy = "";

    /// <summary>Viewing direction of the active view (RAW), or null when there is no viewport.</summary>
    static Vec3? ViewDirection() =>
        ViewportPlugins.Current?.ActiveView is { } v ? OpsUtil.V(Coords.RenderToRaw(v.Forward)) : null;

    static string PlacementText() => OpsUtil.UsedPin() is not null ? "the pin" : Ed.Pin is not null ? "the pin" : "the origin";

    static Form Common(Form f) => f
        .Choice("align", "Placement", Aligns, align)
        .Text("selection", "Named selection (optional)", "", s => s.Length > 0 && NamedSelections.Validate(s) is { } e ? e : null);

    static T Placed<T>(T p, Form f) where T : PrimitiveOptions
    {
        align = f.Index("align");
        var name = f.Str("selection");
        return p with { At = OpsUtil.Placement(), Align = (PrimitiveAlign)align, Selection = name.Length > 0 ? name : null };
    }

    static void Create(string label, Func<Lod, Lod> op)
    {
        int before = Ed.Lod.PointCount, faces = Ed.Lod.FaceCount;
        if (OpsUtil.Apply(label, op))
            Ed.Log($"{label}: {OpsUtil.Plural(Ed.Lod.PointCount - before, "point")}, {OpsUtil.Plural(Ed.Lod.FaceCount - faces, "face")} at {PlacementText()}");
    }

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "create.point",
                Label = "_Point",
                Hotkeys = ["Insert"],
                Execute = () =>
                {
                    var at = OpsUtil.UsedPin() ?? (CursorState.Raw is { } c ? OpsUtil.V(c) : OpsUtil.Placement());
                    if (OpsUtil.Apply("New point", lod => Primitives.CreatePoint(lod, at))) Ed.Log($"Point {Ed.Lod.PointCount - 1} at {at}");
                },
            },
            new Command
            {
                Id = "create.face",
                Label = "New _Face",
                Hotkeys = ["F6"],
                CanExecute = () => OpsUtil.SelectedPointCount() is 3 or 4,
                Execute = () =>
                {
                    CreateFaceResult? r = null;
                    var view = ViewDirection();
                    if (!OpsUtil.Apply("New face", lod =>
                        {
                            r = Primitives.CreateFace(lod, Primitives.SelectedPointList(lod), view);
                            return r.Lod;
                        }))
                        return;
                    var note = r!.OrientedBy switch
                    {
                        FaceOrientationSource.Neighbours => "oriented like its neighbours",
                        FaceOrientationSource.View => "facing the view",
                        _ => "",
                    };
                    Ed.Log($"New face {r.Face} {note}" + (r.Convex ? "" : " — the quad is not convex") + (r.Planarity > 0.01 ? $" — not planar ({r.Planarity:P1})" : ""),
                        r.Convex && r.Planarity <= 0.01 ? LogLevel.Info : LogLevel.Warn);
                },
            },
            new Command
            {
                Id = "create.plane",
                Label = "P_lane…",
                Execute = () =>
                {
                    var f = Common(new Form("Create Plane", $"Horizontal plane at {PlacementText()}.")
                        .Number("sizeX", "Size X (m)", plane.SizeX, 1e-6, 1e5).Number("sizeZ", "Size Z (m)", plane.SizeZ, 1e-6, 1e5)
                        .Integer("segX", "Segments X", plane.SegmentsX, 1, Primitives.MaxSegments).Integer("segZ", "Segments Z", plane.SegmentsZ, 1, Primitives.MaxSegments));
                    if (!f.Show("Create")) return;
                    plane = Placed(new PlaneParams(f.Num("sizeX"), f.Num("sizeZ"), f.Int("segX"), f.Int("segZ")), f);
                    Create("Create plane", lod => Primitives.CreatePlane(lod, plane));
                },
            },
            new Command
            {
                Id = "create.box",
                Label = "_Box…",
                Hotkeys = ["F7"],
                Execute = () =>
                {
                    var f = Common(new Form("Create Box", $"Box at {PlacementText()}.")
                        .Number("sizeX", "Size X (m)", box.SizeX, 1e-6, 1e5).Number("sizeY", "Size Y (m)", box.SizeY, 1e-6, 1e5).Number("sizeZ", "Size Z (m)", box.SizeZ, 1e-6, 1e5)
                        .Integer("segX", "Segments X", box.SegmentsX, 1, Primitives.MaxSegments).Integer("segY", "Segments Y", box.SegmentsY, 1, Primitives.MaxSegments)
                        .Integer("segZ", "Segments Z", box.SegmentsZ, 1, Primitives.MaxSegments));
                    if (!f.Show("Create")) return;
                    box = Placed(new BoxParams(f.Num("sizeX"), f.Num("sizeY"), f.Num("sizeZ"), f.Int("segX"), f.Int("segY"), f.Int("segZ")), f);
                    Create("Create box", lod => Primitives.CreateBox(lod, box));
                },
            },
            new Command
            {
                Id = "create.cylinder",
                Label = "C_ylinder…",
                Hotkeys = ["F8"],
                Execute = () =>
                {
                    var f = Common(new Form("Create Cylinder", $"Cylinder around the Y axis at {PlacementText()}.")
                        .Number("radius", "Radius (m)", cylinder.Radius, 1e-6, 1e5).Number("height", "Height (m)", cylinder.Height, 1e-6, 1e5)
                        .Integer("sides", "Segments radius", cylinder.Sides, 3, Primitives.MaxSegments).Integer("segY", "Segments Y", cylinder.SegmentsY, 1, Primitives.MaxSegments)
                        .Check("caps", "Close top and bottom", cylinder.Caps));
                    if (!f.Show("Create")) return;
                    cylinder = Placed(new CylinderParams(f.Num("radius"), f.Num("height"), f.Int("sides"), f.Int("segY"), f.Bool("caps")), f);
                    Create("Create cylinder", lod => Primitives.CreateCylinder(lod, cylinder));
                },
            },
            new Command
            {
                Id = "create.sphere",
                Label = "_Sphere…",
                Execute = () =>
                {
                    var f = Common(new Form("Create Sphere", $"UV sphere at {PlacementText()}.")
                        .Number("radius", "Radius (m)", sphere.Radius, 1e-6, 1e5)
                        .Integer("sides", "Segments radius", sphere.Sides, 3, Primitives.MaxSegments).Integer("segY", "Segments Y", sphere.SegmentsY, 2, Primitives.MaxSegments));
                    if (!f.Show("Create")) return;
                    sphere = Placed(new SphereParams(f.Num("radius"), f.Int("sides"), f.Int("segY")), f);
                    Create("Create sphere", lod => Primitives.CreateSphere(lod, sphere));
                },
            },
            new Command
            {
                Id = "create.circle",
                Label = "C_ircle…",
                Execute = () =>
                {
                    var f = Common(new Form("Create Circle", $"Flat disc (facing up) at {PlacementText()}.")
                        .Number("radius", "Radius (m)", circle.Radius, 1e-6, 1e5)
                        .Integer("sides", "Segments", circle.Sides, 3, Primitives.MaxSegments)
                        .Check("center", "Centre point (triangle fan)", circle.Center));
                    if (!f.Show("Create")) return;
                    circle = Placed(new CircleParams(f.Num("radius"), f.Int("sides"), f.Bool("center")), f);
                    Create("Create circle", lod => Primitives.CreateCircle(lod, circle));
                },
            },
            new Command { Id = "create.proxy", Label = "P_roxy…", Execute = CreateProxy },
            new Command
            {
                Id = "create.extrude",
                Label = "E_xtrude",
                Hotkeys = ["G"],
                CanExecute = OpsUtil.HasTargetFaces,
                Execute = () =>
                {
                    ExtrudeResult? r = null;
                    if (!OpsUtil.Apply("Extrude", lod =>
                        {
                            r = Extrude.Faces(lod, OpsUtil.TargetFaces(lod), 0);
                            return r.SideFaces > 0 ? r.Lod : lod;
                        }))
                    {
                        if (r is { SideFaces: 0 }) Ed.Log("Extrude: the selected faces have no open boundary (closed region) — nothing to extrude", LogLevel.Warn);
                        return;
                    }
                    Ed.Log($"Extruded {OpsUtil.Plural(r!.FaceCount, "face")}: {OpsUtil.Plural(r.SideFaces, "side face")}, {OpsUtil.Plural(r.NewPoints, "new point")} — move the selection to pull it out");
                },
            });
    }

    static void CreateProxy()
    {
        var root = AppServices.Settings.ProjectRoot;
        var f = new Form("Create Proxy", $"Adds an Object Builder proxy triangle at {PlacementText()}.")
            .Text("path", "Model (.p3d)", lastProxy, s => s.Length == 0 ? "Enter the proxy model path" : null)
            .Text("index", "Index (empty = next free)", "", s => s.Length == 0 || (int.TryParse(s, out int i) && i >= 0) ? null : "Enter a whole number ≥ 0")
            .Action("Browse…", form =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Proxy model",
                    Filter = "P3D models (*.p3d)|*.p3d|All files|*.*",
                    InitialDirectory = Directory.Exists(root) ? root : null,
                };
                if (dlg.ShowDialog() == true) form.Set("path", Proxies.NormalizePath(dlg.FileName, root));
            });
        if (!f.Show("Create")) return;
        var path = Proxies.NormalizePath(f.Str("path"), root);
        lastProxy = path;
        int? index = int.TryParse(f.Str("index"), out int n) ? n : null;
        var at = OpsUtil.Placement();
        string? name = null;
        if (OpsUtil.Apply("Create proxy", lod =>
            {
                var r = Proxies.Create(lod, path, index, at);
                name = r.Name;
                return r.Lod;
            }))
        {
            Ed.Log($"Proxy {name} created at {at}");
            if (AppServices.ResolveProjectPath(path.TrimStart('\\') + ".p3d", root) is { } file && !File.Exists(file))
                Ed.Log($"Proxy model not found: {file}", LogLevel.Warn);
        }
    }
}
