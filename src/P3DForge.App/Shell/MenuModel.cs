namespace P3DForge.App.Shell;

/// <summary>A main-menu entry: a command id, a separator, a submenu or a dynamic list.</summary>
public abstract record MenuEntry;
public sealed record CommandEntry(string Command, string? Label = null) : MenuEntry;
public sealed record SeparatorEntry : MenuEntry;
public sealed record SubmenuEntry(string Title, MenuEntry[] Items) : MenuEntry;
/// <summary>Filled at open time by <see cref="DynamicMenus"/> (e.g. "recentFiles").</summary>
public sealed record DynamicEntry(string Id, string Title) : MenuEntry;

/// <summary>
/// The main menu, modelled on Object Builder's (docs/reference/raw/objectbuilder/ob_menu.txt).
/// Commands are implemented by feature modules (docs/COMMANDS.md); unregistered ids are hidden (<see cref="Visible"/>).
/// </summary>
public static class MenuModel
{
    static CommandEntry C(string id, string? label = null) => new(id, label);
    static readonly SeparatorEntry Sep = new();
    static SubmenuEntry Sub(string title, params MenuEntry[] items) => new(title, items);

    public static readonly (string Title, MenuEntry[] Items)[] Menus =
    [
        ("_File",
        [
            C("file.new"), C("file.newWindow"), Sep,
            C("file.open"), new DynamicEntry("recentFiles", "Open _Recent"), C("file.merge"), C("file.mergeAsSelection"), Sep,
            C("file.save"), C("file.saveAs"), C("file.revert"), C("file.reload"), C("objects.reloadProxies"), Sep,
            Sub("_Import", C("file.import.obj"), C("file.import.fbx"), C("file.import.gltf")),
            Sub("_Export", C("file.export.obj"), C("file.export.gltf")), Sep,
            C("file.reveal"), C("file.preferences"), C("help.hotkeys", "Shortcuts…"), Sep,
            C("file.exit"),
        ]),
        ("_Edit",
        [
            C("edit.undo"), C("edit.redo"), Sep,
            C("edit.cut"), C("edit.copy"), C("edit.paste"), C("edit.pasteToLods"), C("edit.delete"), C("edit.deleteFaces"), Sep,
            C("edit.selectAll"), C("edit.deselect"), C("edit.deselectFaces"), C("edit.deselectPoints"), Sep,
            C("edit.invertSelection"), C("edit.facesFromPoints"), C("edit.pointsFromFaces"), C("edit.selectConnected"), Sep,
            C("edit.growSelection"), C("edit.shrinkSelection"), C("edit.unselectTriangles"),
            Sub("Selection _Operators", C("edit.storeSelection"), C("edit.unionStored"), C("edit.complementStored"), C("edit.intersectStored"), Sep, C("edit.recallSelection")), Sep,
            C("transform.pinToSelection"), C("transform.usePin"), C("transform.pinToOrigin"), Sep,
            Sub("Edit _Modes", C("mode.selectObject"), C("mode.selectFaces"), C("mode.selectPoints"), C("mode.movePin"), Sep, C("mode.lasso"), Sep,
                C("tool.select"), C("tool.move"), C("tool.rotate"), C("tool.scale")), Sep,
            C("app.commandPalette"),
        ]),
        ("_View",
        [
            C("view.front"), C("view.top"), C("view.left"), C("view.projected"), C("view.right"), C("view.back"), C("view.bottom"), C("view.togglePerspective"), Sep,
            C("view.zoomIn"), C("view.zoomOut"), Sub("Center && _Zoom", C("view.frameAll"), C("view.frameSelection"), C("objects.frameSelected"), C("view.frameAllViews")), C("view.lookAtPin"), Sep,
            C("view.toggleQuad"), C("view.nextPane"), Sep,
            C("view.shadingWire"), C("view.shadingSolid"), C("view.shadingTextured"), C("view.shadingFinal"), C("view.backfaceCulling"), C("view.showBackfaces"), C("view.showGrid"), Sep,
            Sub("_Lock Coordinates", C("view.lockX"), C("view.lockY"), C("view.lockZ")),
            Sub("Lock/_Hide", C("edit.hideSelection"), C("edit.unhideAll"), C("edit.lockSelection"), C("edit.unlockAll")), Sep,
            C("view.showPoints"), C("view.showNormals"), C("view.showProxies"), C("objects.toggleProxies"), C("objects.hideSelected"), C("objects.showAll"), C("view.showMemoryLabels"), C("view.showHidden"), C("view.showWeights"), C("view.backgroundLod"), C("view.firstLodBackground"), Sep,
            C("view.gameFov"), C("view.stats"), C("view.saveScreenshot"),
        ]),
        ("_Create",
        [
            C("create.point"), Sep, C("create.plane"), C("create.box"), C("create.circle"), C("create.sphere"), C("create.cylinder"), Sep,
            C("create.face"), C("create.proxy"), Sep, C("create.extrude"),
        ]),
        ("Str_ucture",
        [
            C("structure.checkFaces"), C("tools.validate"), C("transform.centerAll"), Sep,
            Sub("_Topology", C("structure.findNonClosed"), C("structure.findComponents")),
            Sub("_Convexity", C("structure.findNonConvexities"), C("structure.convexHull"), C("structure.componentConvexHull")), Sep,
            C("faces.triangulate"), C("faces.triangulateAlt"), C("faces.squarize"), C("faces.squarizeAllLods"), Sep,
            C("structure.removeDuplicateFaces"), C("structure.repairDegenerate"), Sep,
            C("mass.set"), C("mass.clear"),
        ]),
        ("_Points",
        [
            C("points.selectFirst"), C("points.selectNext"), C("points.selectPrev"), C("points.selectIsolated"), Sep,
            Sub("Transform _3D", C("transform.move"), C("transform.rotate"), C("transform.scale")),
            Sub("_Mirror", C("transform.mirrorX"), C("transform.mirrorY"), C("transform.mirrorZ")), Sep,
            C("points.merge"), C("points.mergeNear"), Sep, C("points.flatten"), C("points.snapToGrid"), Sep,
            C("points.properties"),
        ]),
        ("F_aces",
        [
            C("faces.reverse"), C("faces.uncross"), C("edit.deleteFaces", "Remove"), Sep,
            C("faces.moveTop"), C("faces.moveBottom"), Sep,
            C("faces.recalcNormals"), C("faces.sharpEdge"), C("faces.smoothEdge"), Sep,
            C("faces.selectByTexture"), C("faces.properties"),
        ]),
        ("_Surfaces",
        [
            C("surface.setTexture"), C("surface.setMaterial"), C("faces.properties"), C("surface.fixPaths"), Sep,
            C("faces.selectByTexture"), C("material.selectFaces"), C("material.editSelected"), C("material.new"), Sep,
            C("window.resources"), C("window.material"), C("window.materialLibrary"), C("window.textures"), Sep,
            C("textures.convertToPaa"), C("textures.convertFolder"), C("textures.exportPaa"), Sep,
            C("uv.editor"),
            Sub("UV _Mapping", C("uv.planarView"), C("uv.planarX"), C("uv.planarY"), C("uv.planarZ"), Sep,
                C("uv.fit"), C("uv.flipU"), C("uv.flipV"), C("uv.rotate90")),
            Sub("UV _Sets", C("uv.activeSet"), C("uv.addSet"), C("uv.deleteSet")),
        ]),
        ("_LOD",
        [
            C("lod.new"), C("lod.duplicate"), C("lod.properties"), C("lod.delete"), Sep,
            C("lod.copySelectionTo"), C("lod.sort"), C("lod.generate"), Sep, C("lod.prev"), C("lod.next"),
        ]),
        ("_Tools", [C("tools.animations"), C("anim.animateSelection"), C("tools.weaponAnimations"), C("tools.variants"), C("tools.measure"), C("edit.findReplacePaths"), C("tools.batchRename"), Sep, C("tools.validate"), C("tools.binarize"), Sep, C("textures.convertToPaa"), C("textures.convertFolder"), C("textures.exportPaa")]),
        ("_Window",
        [
            C("window.lods"), C("window.namedSelections"), C("window.namedProperties"), C("window.mass"),
            C("window.currentPosition"), C("window.resources"), C("window.material"), C("window.materialLibrary"), C("window.objects"), C("window.history"), C("window.log"), C("window.textures"), C("uv.editor"), C("window.render"), Sep, C("window.resetLayout"),
        ]),
        ("_Help", [C("help.hotkeys"), C("app.commandPalette"), Sep, C("help.about")]),
    ];

    /// <summary>
    /// The entries to show: commands nobody registered (not implemented yet) are left out, then submenus that became empty,
    /// and leading, trailing and doubled separators.
    /// </summary>
    public static MenuEntry[] Visible(MenuEntry[] items, Func<string, bool> registered)
    {
        var kept = new List<MenuEntry>();
        foreach (var e in items)
        {
            var v = e switch
            {
                CommandEntry c => registered(c.Command) ? e : null,
                SubmenuEntry s => Visible(s.Items, registered) is { Length: > 0 } sub ? s with { Items = sub } : null,
                _ => e,
            };
            if (v is null || (v is SeparatorEntry && (kept.Count == 0 || kept[^1] is SeparatorEntry))) continue;
            kept.Add(v);
        }
        while (kept.Count > 0 && kept[^1] is SeparatorEntry) kept.RemoveAt(kept.Count - 1);
        return [.. kept];
    }
}
