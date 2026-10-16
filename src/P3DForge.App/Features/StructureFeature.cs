using System.Diagnostics;
using System.Numerics;
using P3DForge.App.Dialogs;
using P3DForge.App.Dialogs.Structure;
using P3DForge.App.Editor;
using P3DForge.App.Panels;
using P3DForge.App.Shell;
using P3DForge.App.Viewport;
using P3DForge.Core;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;
using P3DForge.Core.Topology;

namespace P3DForge.App.Features;

/// <summary>
/// Structure menu (Object Builder "Structure" + checks): Topology (Find Non-Closed, Find Components), Convexity (Find
/// Non-Convexities, Convex Hull, Component Convex Hull), Check Faces, Remove Duplicate Faces, Repair Degenerated Faces.
/// Heavy work runs on a background thread on an immutable snapshot of the active LOD; the result is applied (one undo
/// step) only if the model did not change meanwhile.
/// </summary>
public sealed class StructureFeature : IFeature
{
    public int Order => 60;
    static EditorState Ed => AppServices.Editor;

    public void Install()
    {
        Commands.Register(
            new Command
            {
                Id = "structure.findComponents",
                Label = "Find _Components",
                Description = "Deletes the ComponentXX selections and creates Component01..NN, one per closed piece (Geometry LODs)",
                CanExecute = HasFaces,
                Execute = FindComponents,
            },
            new Command
            {
                Id = "structure.findNonClosed",
                Label = "Find _Non-Closed",
                Description = "Selects the points of open and non-manifold edges",
                CanExecute = HasFaces,
                Execute = FindNonClosed,
            },
            new Command
            {
                Id = "structure.findNonConvexities",
                Label = "Find Non-_Convexities",
                Description = "Selects the faces that are not on the convex hull of their component",
                CanExecute = HasFaces,
                Execute = FindNonConvexities,
            },
            new Command
            {
                Id = "structure.convexHull",
                Label = "Convex _Hull",
                Description = "Replaces the selection by its convex hull",
                CanExecute = () => Selection.PeekSelected(Ed.Lod) is { } s && s.PointCount >= 4,
                Execute = ConvexHullOfSelection,
            },
            new Command
            {
                Id = "structure.componentConvexHull",
                Label = "C_omponent Convex Hull",
                Description = "Replaces every component (of the selected faces, or of the whole LOD) by its convex hull",
                CanExecute = HasFaces,
                Execute = ComponentConvexHull,
            },
            new Command
            {
                Id = "structure.checkFaces",
                Label = "Check _Faces",
                Description = "Selects degenerate faces, non-planar quads and duplicate faces",
                CanExecute = HasFaces,
                Execute = CheckFaces,
            },
            new Command
            {
                Id = "structure.removeDuplicateFaces",
                Label = "Remove _Duplicate Faces",
                Description = "Deletes faces that repeat an earlier face with the same points and orientation",
                CanExecute = HasFaces,
                Execute = RemoveDuplicates,
            },
            new Command
            {
                Id = "structure.repairDegenerate",
                Label = "_Repair Degenerated Faces",
                Description = "Collapses repeated points of faces; deletes faces without area",
                CanExecute = HasFaces,
                Execute = RepairDegenerate,
            });
        InstallMass();
    }

    static bool HasFaces() => Ed.Lod.FaceCount > 0;

    // ------------------------------------------------------------------ commands

    static void FindComponents() => StructureWork.Run("Find Components",
        lod =>
        {
            var r = HullOps.CreateComponentSelections(lod, null, out int count, out int ignored);
            return (Lod: r, Count: count, Ignored: ignored);
        },
        (lod, r, ms) =>
        {
            Ed.EditLod("Find Components", cur => ReferenceEquals(cur, lod) ? r.Lod : HullOps.CreateComponentSelections(cur, null, out _, out _));
            string range = r.Count switch { 0 => "", 1 => " (Component01)", _ => $" (Component01–{ComponentAnalysis.ComponentName(r.Count - 1)})" };
            Ed.Log($"Find Components: {Plural(r.Count, "component")}{range}" +
                   (r.Ignored > 0 ? $"; {Plural(r.Ignored, "open piece")} ignored — see Find Non-Closed" : "") + $" [{ms} ms]",
                r.Ignored > 0 ? LogLevel.Warn : LogLevel.Info);
        });

    static void FindNonClosed() => StructureWork.Run("Find Non-Closed",
        lod => MeshTopology.CheckClosed(lod, ComponentAnalysis.ExcludeProxies(lod, null)),
        (lod, c, ms) =>
        {
            var pts = new byte[lod.PointCount];
            foreach (int p in c.OpenEdges) pts[p] = 1;
            foreach (int p in c.NonManifoldEdges) pts[p] = 1;
            int n = StructureWork.SelectPoints(pts, "Find Non-Closed");
            string inconsistent = c.InconsistentEdgeCount > 0
                ? $"; {Plural(c.InconsistentEdgeCount, "edge")} between faces of opposite winding ({Plural(c.InconsistentFaces.Length, "face")}, fix with Faces › Reverse)"
                : "";
            if (c.Closed)
                Ed.Log($"Find Non-Closed: closed — no open or non-manifold edges{inconsistent} [{ms} ms]", c.InconsistentEdgeCount > 0 ? LogLevel.Warn : LogLevel.Info);
            else
                Ed.Log($"Find Non-Closed: {Plural(c.OpenEdgeCount, "open edge")}, {Plural(c.NonManifoldEdgeCount, "non-manifold edge")} " +
                       $"({Plural(c.ProblemFaces.Length, "face")}); {Plural(n, "point")} selected{inconsistent} [{ms} ms]", LogLevel.Warn);
        });

    static void FindNonConvexities() => StructureWork.Run("Find Non-Convexities",
        lod =>
        {
            var reps = ComponentAnalysis.Analyze(lod);
            var faces = new byte[lod.FaceCount];
            var pts = new byte[lod.PointCount];
            int bad = 0, flat = 0;
            double worst = 0;
            foreach (var r in reps)
            {
                if (r.Flat) flat++;
                if (r.Convexity is not { Convex: false } cv) continue;
                bad++;
                worst = Math.Max(worst, cv.MaxViolation);
                foreach (int f in cv.NonConvexFaces) faces[f] = 1;
                foreach (int p in cv.ConcaveEdges) pts[p] = 1;
                foreach (int p in cv.InteriorPoints) pts[p] = 1;
            }
            return (Faces: faces, Points: pts, Components: reps.Length, NonConvex: bad, Flat: flat, Worst: worst);
        },
        (lod, r, ms) =>
        {
            int nf = MeshMath.CountSet(r.Faces);
            StructureWork.Select(lod, r.Points, r.Faces, "Find Non-Convexities");
            string flat = r.Flat > 0 ? $"; {Plural(r.Flat, "flat component")} not checked" : "";
            if (r.NonConvex == 0)
                Ed.Log($"Find Non-Convexities: all {Plural(r.Components, "component")} are convex{flat} [{ms} ms]");
            else
                Ed.Log($"Find Non-Convexities: {r.NonConvex} of {Plural(r.Components, "component")} not convex, {Plural(nf, "face")} selected " +
                       $"(deepest point {r.Worst * 1000:0.#} mm inside the hull){flat} [{ms} ms]", LogLevel.Warn);
        });

    static void ConvexHullOfSelection() => StructureWork.Run("Convex Hull",
        lod =>
        {
            var next = HullOps.ReplaceSelectionWithHull(lod, Selection.GetSelected(lod), out var hull, out var stats);
            return (Lod: next, Hull: hull, Stats: stats);
        },
        (lod, r, ms) =>
        {
            if (!r.Hull.Ok)
            {
                Ed.Log($"Convex Hull: {r.Hull.Describe()} — nothing changed", LogLevel.Warn);
                return;
            }
            Ed.EditLod("Convex Hull", cur => ReferenceEquals(cur, lod) ? r.Lod : HullOps.ReplaceSelectionWithHull(cur, Selection.GetSelected(cur), out _, out _));
            Ed.Log($"Convex Hull: {Plural(r.Hull.HullPoints.Length, "point")}, {Plural(r.Hull.FaceCount, "face")} " +
                   $"(faces {r.Stats.FacesBefore:N0} → {r.Stats.FacesAfter:N0}, {Plural(r.Stats.PointsRemoved, "interior point")} removed) [{ms} ms]");
        });

    static void ComponentConvexHull() => StructureWork.Run("Component Convex Hull",
        lod =>
        {
            byte[]? mask = Selection.PeekSelected(lod) is { FaceCount: > 0 } sel ? sel.Faces : null;
            var next = HullOps.ComponentHulls(lod, mask, out var stats);
            return (Lod: next, Stats: stats, Masked: mask is not null);
        },
        (lod, r, ms) =>
        {
            var s = r.Stats;
            if (s.Hulls == 0)
            {
                Ed.Log($"Component Convex Hull: no component could be hulled{(s.FirstFailure is null ? "" : $" ({s.FirstFailure})")}", LogLevel.Warn);
                return;
            }
            Ed.EditLod("Component Convex Hull", cur => ReferenceEquals(cur, lod) ? r.Lod
                : HullOps.ComponentHulls(cur, Selection.PeekSelected(cur) is { FaceCount: > 0 } ss ? ss.Faces : null, out _));
            Ed.Log($"Component Convex Hull: {Plural(s.Hulls, "hull")}{(r.Masked ? " (selected faces)" : "")}, faces {s.FacesBefore:N0} → {s.FacesAfter:N0}, " +
                   $"{Plural(s.PointsRemoved, "point")} removed, Component01–{ComponentAnalysis.ComponentName(Math.Max(0, s.Components - 1))}" +
                   (s.Failed > 0 ? $"; {Plural(s.Failed, "component")} kept ({s.FirstFailure})" : "") + $" [{ms} ms]",
                s.Failed > 0 ? LogLevel.Warn : LogLevel.Info);
        });

    static void CheckFaces() => StructureWork.Run("Check Faces",
        lod => FaceChecks.Check(lod),
        (lod, r, ms) =>
        {
            var faces = new byte[lod.FaceCount];
            foreach (var list in new[] { r.Degenerate, r.NonPlanar, r.Duplicate, r.Reversed, r.Invalid })
                foreach (int f in list) faces[f] = 1;
            int n = MeshMath.CountSet(faces);
            StructureWork.Select(lod, null, faces, "Check Faces");
            if (n == 0)
            {
                Ed.Log($"Check Faces: {Plural(lod.FaceCount, "face")} OK [{ms} ms]");
                return;
            }
            var parts = new List<string>();
            if (r.Degenerate.Length > 0) parts.Add($"{r.Degenerate.Length:N0} degenerate");
            if (r.NonPlanar.Length > 0) parts.Add($"{r.NonPlanar.Length:N0} non-planar quad{(r.NonPlanar.Length == 1 ? "" : "s")} (max {r.MaxNonPlanarity * 1000:0.##} mm)");
            if (r.Duplicate.Length > 0) parts.Add($"{r.Duplicate.Length:N0} duplicate");
            if (r.Reversed.Length > 0) parts.Add($"{r.Reversed.Length:N0} back-to-back");
            if (r.Invalid.Length > 0) parts.Add($"{r.Invalid.Length:N0} invalid");
            Ed.Log($"Check Faces: {string.Join(", ", parts)}; {Plural(n, "face")} selected [{ms} ms]", LogLevel.Warn);
        });

    static void RemoveDuplicates() => StructureWork.Run("Remove Duplicate Faces",
        lod =>
        {
            var next = FaceChecks.RemoveDuplicateFaces(lod, null, includeReversed: false, out int removed);
            int backToBack = removed == 0 ? FaceChecks.Duplicates(lod).Reversed.Length : FaceChecks.Duplicates(next).Reversed.Length;
            return (Lod: next, Removed: removed, BackToBack: backToBack);
        },
        (lod, r, ms) =>
        {
            if (r.Removed > 0)
                Ed.EditLod("Remove Duplicate Faces", cur => ReferenceEquals(cur, lod) ? r.Lod : FaceChecks.RemoveDuplicateFaces(cur, null, false, out _));
            Ed.Log((r.Removed > 0 ? $"Removed {Plural(r.Removed, "duplicate face")}" : "No duplicate faces") +
                   (r.BackToBack > 0 ? $"; {Plural(r.BackToBack, "back-to-back face")} kept (double-sided?) — see Check Faces" : "") + $" [{ms} ms]");
        });

    static void RepairDegenerate() => StructureWork.Run("Repair Degenerated Faces",
        lod =>
        {
            var next = FaceChecks.RepairDegenerate(lod, null, out int fixedCount, out int deleted);
            return (Lod: next, Fixed: fixedCount, Deleted: deleted);
        },
        (lod, r, ms) =>
        {
            if (r.Fixed + r.Deleted > 0)
                Ed.EditLod("Repair Degenerated Faces", cur => ReferenceEquals(cur, lod) ? r.Lod : FaceChecks.RepairDegenerate(cur, null, out _, out _));
            Ed.Log(r.Fixed + r.Deleted == 0
                ? $"No degenerate faces [{ms} ms]"
                : $"Repaired degenerate faces: {Plural(r.Fixed, "face")} collapsed to triangles, {Plural(r.Deleted, "face")} without area deleted [{ms} ms]");
        });

    static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    // ------------------------------------------------------------------ mass

    public const string MassPanelId = "mass";

    static void InstallMass()
    {
        PanelRegistry.Register(new PanelDescriptor { Id = MassPanelId, Title = "Mass", Location = PanelLocation.Right, TabWith = "objects", Order = 30, StartHidden = true, Create = () => new MassPanel() });
        Commands.Register(
            new Command { Id = "window.mass", Label = "_Mass", Hotkeys = ["Shift+F7"], Execute = () => PanelRegistry.Focus(MassPanelId) },
            new Command
            {
                Id = "mass.set",
                Label = "Set _Mass…",
                Description = "Sets the mass of the selected points (all points when nothing is selected) in the Geometry LOD",
                CanExecute = () => IsMassLod(Ed.Lod) ? Ed.Lod.PointCount > 0 : Mass.GeometryLodIndex(Ed.Model) >= 0,
                Execute = SetMass,
            },
            new Command
            {
                Id = "mass.clear",
                Label = "_Clear Mass",
                Description = "Removes the mass of the selected points (the whole #Mass# when nothing is selected)",
                CanExecute = () => Ed.Lod.MassTag is not null || Mass.GeometryLodIndex(Ed.Model) is int g && g >= 0 && Ed.Model.Lods[g].MassTag is not null,
                Execute = ClearMass,
            });
        ViewportPlugins.Register(CenterOfMassOverlay);

        PanelRegistry.Register(new PanelDescriptor { Id = ValidationPanel.PanelId, Title = "Validation", Location = PanelLocation.Right, TabWith = "objects", Order = 40, StartHidden = true, Create = () => new ValidationPanel() });
        Commands.Register(new Command
        {
            Id = "tools.validate",
            Label = "_Validate Model",
            Description = "Checks the model against DayZ rules (geometry, components, mass, faces, paths) in the Validation window",
            Execute = () =>
            {
                PanelRegistry.Focus(ValidationPanel.PanelId);
                ValidationPanel.Validate();
            },
        });
    }

    /// <summary>LODs whose #Mass# the engine reads (Geometry, Geometry PhysX, Geometry Buoyancy).</summary>
    static bool IsMassLod(Lod lod) => LodNames.TypeOf(lod.Resolution).Type.Id is "geometry" or "geometryPhysX" or "geometryBuoyancy";

    /// <summary>Makes a mass LOD active (switching to the Geometry LOD when needed); false when the model has none.</summary>
    static bool EnsureMassLod(string what)
    {
        if (IsMassLod(Ed.Lod)) return true;
        int g = Mass.GeometryLodIndex(Ed.Model);
        if (g < 0)
        {
            Ed.Log($"{what}: the model has no Geometry LOD; mass is stored there (LOD > New LOD > Geometry)", LogLevel.Warn);
            return false;
        }
        Ed.SetActiveLod(g);
        Ed.Log($"{what}: switched to the Geometry LOD (the engine reads mass from it)");
        return true;
    }

    /// <summary>Selected points (non-zero = selected) or every point when nothing is selected.</summary>
    static (byte[] Mask, bool All) MassTarget(Lod lod)
    {
        if (Selection.PeekSelected(lod) is { PointCount: > 0 } s)
        {
            var m = new byte[lod.PointCount];
            for (int i = 0; i < m.Length; i++) m[i] = s.Points[i] != 0 ? (byte)1 : (byte)0;
            return (m, false);
        }
        return (MeshMath.Ones(lod.PointCount), true);
    }

    static void SetMass()
    {
        if (!EnsureMassLod("Set Mass")) return;
        var lod = Ed.Lod;
        if (lod.PointCount == 0)
        {
            Ed.Log("Set Mass: the Geometry LOD has no points", LogLevel.Warn);
            return;
        }
        var (mask, all) = MassTarget(lod);
        int n = MeshMath.CountSet(mask);
        string target = $"{LodNames.Name(lod.Resolution)}: " + (all ? $"all {Plural(n, "point")} (nothing selected)" : $"{Plural(n, "selected point")} of {lod.PointCount:N0}");
        var req = SetMassDialog.Show(target, Mass.Total(lod, mask));
        if (req is null) return;
        string? note = null;
        bool warn = false;
        Dlg.Try(() =>
        {
            bool done = Ed.EditLod("Set Mass", l =>
            {
                var (m, _) = MassTarget(l);
                switch (req.Mode)
                {
                    case SetMassMode.Each:
                        return Mass.SetEach(l, m, req.Value);
                    case SetMassMode.Density:
                    {
                        var r = Mass.FromDensity(l, m, req.Value, out double total, out int comps);
                        warn = comps == 0;
                        note = comps == 0
                            ? "no closed component is fully selected, nothing set (see Find Non-Closed)"
                            : $"{Plural(comps, "closed component")}, {total:0.###} kg";
                        return r;
                    }
                    default:
                    {
                        var r = Mass.Distribute(l, m, req.Value, req.Distribution, out bool fallback);
                        warn = fallback;
                        if (fallback) note = req.Distribution == MassDistribution.Volume
                            ? "no closed component is fully selected, distributed equally"
                            : "no face area, distributed equally";
                        return r;
                    }
                }
            });
            var after = Ed.Lod;
            Ed.Log("Set Mass: " + (done ? $"{Mass.Total(after, mask):0.###} kg on {Plural(n, "point")}, LOD total {Mass.Total(after):0.###} kg" : "nothing changed") +
                   (note is null ? "" : $" ({note})"), warn ? LogLevel.Warn : LogLevel.Info);
        });
    }

    static void ClearMass()
    {
        if (Ed.Lod.MassTag is null && !EnsureMassLod("Clear Mass")) return;
        var lod = Ed.Lod;
        if (lod.MassTag is null)
        {
            Ed.Log("Clear Mass: this LOD has no mass");
            return;
        }
        var (mask, all) = MassTarget(lod);
        double before = Mass.Total(lod);
        double selected = Mass.Total(lod, mask);
        bool done = Ed.EditLod("Clear Mass", l => Mass.Clear(l, all ? null : MassTarget(l).Mask));
        Ed.Log(!done ? "Clear Mass: the selected points have no mass"
            : all ? $"Clear Mass: #Mass# removed ({before:0.###} kg)"
            : $"Clear Mass: {selected:0.###} kg removed from {Plural(MeshMath.CountSet(mask), "point")}, {Mass.Total(Ed.Lod):0.###} kg left");
    }

    /// <summary>Viewport plugin: a cross at the centre of mass of the active LOD while flag mass.showCenter is on.</summary>
    static Action CenterOfMassOverlay(IViewportContext ctx)
    {
        const string owner = "mass.center";
        P3DModel? shownModel = null;
        int shownLod = -1;
        bool shown = false;
        void Update(Change c)
        {
            bool on = Ed.Flag("mass.showCenter", false);
            if (on == shown && ReferenceEquals(shownModel, Ed.Model) && shownLod == Ed.ActiveLod) return;
            shownModel = Ed.Model;
            shownLod = Ed.ActiveLod;
            shown = on;
            var lod = Ed.Lod;
            var props = on ? Mass.Properties(lod) : MassProperties.Empty;
            if (!props.HasMass)
            {
                ctx.SetOverlayLines(owner, null);
                ctx.Invalidate();
                return;
            }
            if (MeshMath.Bounds(lod.Positions, null) is not var (min, max)) return;
            double diag = Math.Sqrt((max.X - min.X) * (max.X - min.X) + (max.Y - min.Y) * (max.Y - min.Y) + (max.Z - min.Z) * (max.Z - min.Z));
            float h = (float)Math.Max(0.05, 0.06 * diag);
            var c0 = Coords.RawToRender(new Vector3((float)props.Center.X, (float)props.Center.Y, (float)props.Center.Z));
            const uint red = 0xFF4040FF, green = 0x40FF40FF, blue = 0x4080FFFF, yellow = 0xFFD040FF;
            ctx.SetOverlayLines(owner,
            [
                new OverlayLine(c0 - new Vector3(h, 0, 0), c0 + new Vector3(h, 0, 0), red),
                new OverlayLine(c0 - new Vector3(0, h, 0), c0 + new Vector3(0, h, 0), green),
                new OverlayLine(c0 - new Vector3(0, 0, h), c0 + new Vector3(0, 0, h), blue),
                new OverlayLine(c0 + new Vector3(-h, -h, 0) * 0.35f, c0 + new Vector3(h, h, 0) * 0.35f, yellow),
                new OverlayLine(c0 + new Vector3(-h, h, 0) * 0.35f, c0 + new Vector3(h, -h, 0) * 0.35f, yellow),
            ]);
            ctx.Invalidate();
        }
        Ed.Changed += Update;
        Update(Change.Ui);
        return () =>
        {
            Ed.Changed -= Update;
            ctx.SetOverlayLines(owner, null);
        };
    }
}

/// <summary>Runs structure computations off the UI thread on an immutable LOD snapshot (sync for small LODs).</summary>
internal static class StructureWork
{
    static EditorState Ed => AppServices.Editor;

    /// <summary>LODs smaller than this (points + faces) are processed synchronously.</summary>
    const int AsyncThreshold = 6000;

    static bool busy;

    public static bool Busy => busy;

    /// <summary>
    /// Computes <paramref name="compute"/> on the active LOD (background thread when it is large), then calls
    /// <paramref name="apply"/>(snapshot, result, milliseconds) on the UI thread if the model is unchanged.
    /// </summary>
    public static async void Run<T>(string what, Func<Lod, T> compute, Action<Lod, T, long> apply)
    {
        if (busy)
        {
            Ed.Log($"{what}: still working on the previous structure command", LogLevel.Warn);
            return;
        }
        var model = Ed.Model;
        int index = Ed.ActiveLod;
        var lod = Ed.Lod;
        var sw = Stopwatch.StartNew();
        T result;
        busy = true;
        try
        {
            if (lod.PointCount + lod.FaceCount < AsyncThreshold || Dlg.Automated && Environment.GetEnvironmentVariable("P3DFORGE_SYNC") == "1")
                result = compute(lod);
            else
            {
                Ed.Log($"{what}: working on {lod.FaceCount:N0} faces…");
                result = await Task.Run(() => compute(lod));
            }
        }
        catch (Exception ex)
        {
            Ed.Log($"{what} failed: {ex.Message}", LogLevel.Error);
            return;
        }
        finally
        {
            busy = false;
        }
        if (!ReferenceEquals(Ed.Model, model) || Ed.ActiveLod != index)
        {
            Ed.Log($"{what}: the model changed while computing — run it again", LogLevel.Warn);
            return;
        }
        try
        {
            apply(lod, result, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Ed.Log($"{what} failed: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>Selects <paramref name="points"/> (and the faces they complete), skipping hidden/locked points. Returns the point count.</summary>
    public static int SelectPoints(byte[] points, string label)
    {
        var lod = Ed.Lod;
        var ok = Selection.SelectablePoints(lod);
        var pts = (byte[])points.Clone();
        for (int i = 0; i < pts.Length; i++)
            if (ok[i] == 0) pts[i] = 0;
        Ed.SetSelection(new SelectionMask(pts, Selection.FacesFromPoints(lod, pts)), label);
        return MeshMath.CountSet(pts);
    }

    /// <summary>Selects <paramref name="faces"/> with their points plus extra <paramref name="points"/>, skipping hidden/locked ones.</summary>
    public static void Select(Lod lod, byte[]? points, byte[] faces, string label)
    {
        var ok = Selection.SelectablePoints(lod);
        var blocked = new byte[ok.Length];
        for (int i = 0; i < ok.Length; i++) blocked[i] = ok[i] == 0 ? (byte)1 : (byte)0;
        var blockedFaces = Selection.FacesTouching(lod, blocked);
        var f = (byte[])faces.Clone();
        for (int i = 0; i < f.Length; i++)
            if (blockedFaces[i] != 0) f[i] = 0;
        var pts = points is null ? new byte[lod.PointCount] : (byte[])points.Clone();
        Selection.PointsFromFaces(lod, f, pts);
        for (int i = 0; i < pts.Length; i++)
            if (ok[i] == 0) pts[i] = 0;
        Ed.SetSelection(new SelectionMask(pts, f), label);
    }
}
