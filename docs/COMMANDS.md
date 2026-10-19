# Commands

Every user action is a registered command (`Commands.Register`, src/P3DForge.App/Editor/Commands.cs). Use exactly
these ids, labels and default hotkeys. Hotkeys follow Object Builder (see
docs/reference/raw/objectbuilder/ob_accel.txt) unless marked *(new)*. Accelerator names:
`Ctrl+Shift+X`, `Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `Space`, `Tab`, `F1`…`F12`,
numpad keys `Num0`…`Num9`, `NumMultiply`, `NumAdd`, `NumSubtract`, `NumDivide`, `NumDecimal`,
punctuation by character (`/`, `\`, `[`, `]`).

Owner = the agent/area responsible; it implements the command in its own feature class under src/P3DForge.App/Features/ (C#).

## Object Builder mouse scheme (viewport, owner: interaction)
| Input | Action |
|---|---|
| LMB click / drag | select (rectangle; lasso when Lasso mode is on) — replaces selection |
| Ctrl+LMB | add to selection |
| Ctrl+Shift+LMB | subtract from selection |
| Shift+LMB *(new)* | add to selection (modern habit) |
| RMB drag on viewport | move selection (in the view plane, X/Y/Z axis lock applies) |
| Shift+RMB drag | copy the selection and move the copy |
| Ctrl+RMB drag | rotate selection around the pin (or selection centre) |
| Ctrl+Shift+RMB drag | scale selection from the pin (or selection centre) |
| RMB click (no drag) *(new)* | context menu |
| Alt+RMB drag | orbit camera (perspective view) |
| Alt+LMB drag | pan |
| Alt+LMB+RMB drag / wheel | zoom (wheel zooms towards the cursor) |
| MMB drag *(new)* | orbit (perspective) / pan (ortho); Shift+MMB pan |
| X / Y / Z | toggle axis lock for mouse transforms (shown in the view label) |
| Arrow keys / Ctrl+arrows | scroll the active view (slow / fast) |
| Gizmo (Move/Rotate/Scale tools) *(new)* | drag handles; Ctrl = snap |

## File — owner: core (done) unless noted
| id | label | hotkey |
|---|---|---|
| file.new | New | Ctrl+N |
| file.open | Open… | Ctrl+O |
| file.save | Save | Ctrl+S |
| file.saveAs | Save As… | Ctrl+Shift+S |
| file.revert | Revert to Saved | |
| file.merge | Merge… (io) | |
| file.mergeAsSelection | Merge As Selection… (io) | |
| file.import.obj / file.import.fbx / file.import.gltf | OBJ… / FBX… / glTF/GLB… (io) | |
| file.export.obj / file.export.gltf | OBJ… / glTF/GLB… (io) | |
| file.newWindow | New Window (chrome) | Ctrl+Shift+N |
| file.reveal | Show in Explorer | |
| file.preferences | Options… (chrome) | |
| file.exit | Exit | Alt+F4 |

## Edit — owners: core (done) / ops
| id | label | hotkey | owner |
|---|---|---|---|
| edit.undo | Undo | Ctrl+Z, Alt+Backspace | core |
| edit.redo | Redo | Ctrl+Y, Ctrl+Shift+Z | core |
| edit.cut | Cut | Ctrl+X, Shift+Delete | ops |
| edit.copy | Copy | Ctrl+C, Ctrl+Insert | ops |
| edit.paste | Paste | Ctrl+V, Shift+Insert | ops |
| edit.pasteToLods | Paste to LODs… | | ops |
| edit.delete | Delete | Delete | core |
| edit.deleteFaces | Remove Faces (keep points) | D | ops |
| edit.selectAll | Select All | Ctrl+A | core |
| edit.deselect | Deselect | Ctrl+D | core |
| edit.deselectFaces | Deselect Faces | | ops |
| edit.deselectPoints | Deselect Vertices | | ops |
| edit.invertSelection | Invert Selection | Ctrl+I | core |
| edit.facesFromPoints | Select Faces from Points | | ops |
| edit.pointsFromFaces | Select Points from Faces | | ops |
| edit.growSelection | Grow Selected | | ops |
| edit.shrinkSelection | Shrink Selected | | ops |
| edit.selectConnected | Select Connected | | ops |
| edit.unselectTriangles | Unselect Triangles | | ops |
| edit.storeSelection | Selection Operators: Copy (store) | | ops |
| edit.recallSelection | Selection Operators: Recall | | ops |
| edit.unionStored / edit.complementStored / edit.intersectStored | Union / Complement / Intersection with stored | | ops |
| edit.hideSelection | Hide Selection | Ctrl+H | ops |
| edit.unhideAll | Unhide | Ctrl+Shift+H | ops |
| edit.lockSelection | Lock Selection | Ctrl+L | ops |
| edit.unlockAll | Unlock | Ctrl+Shift+L | ops |
| edit.findReplacePaths | Mass Texture & Material Renaming… | | inspector |

## Modes & pin — owners: interaction / transform
| id | label | hotkey | owner |
|---|---|---|---|
| mode.selectObject | Select Object (connected parts) | F2, O | interaction |
| mode.selectFaces | Select Faces | F3 | interaction |
| mode.selectPoints | Select Vertices | F4, V | interaction |
| mode.movePin | Move Pin (click sets pin) | R | interaction |
| mode.lasso | Lasso | L | interaction |
| tool.select / tool.move / tool.rotate / tool.scale | Select / Move / Rotate / Scale (gizmo) *(new)* | | interaction |
| view.lockX / view.lockY / view.lockZ | Lock X / Y / Z | X / Y / Z | interaction |
| transform.pinToSelection | Center Pin | C | transform |
| transform.usePin | Use Pin (toggle) | Shift+C | transform |
| transform.pinToOrigin | Pin to Origin *(new)* | | transform |
| transform.move / rotate / scale | Move… / Rotate… / Scale… (numeric dialogs, "apply to all LODs") | | transform |
| transform.mirrorX / mirrorY / mirrorZ | Mirror X / Y / Z (around pin; faces reversed) | | transform |
| transform.centerAll | Center All | | transform |

## View — owner: viewport
| id | label | hotkey |
|---|---|---|
| view.front / back / left / right / top / bottom | Front … Bottom | Num1 / Ctrl+Num1 / Ctrl+Num3 / Num3 / Num7 / Ctrl+Num7 *(new)* |
| view.projected | Projected (3D) | Num0 *(new)* |
| view.togglePerspective | Perspective/Orthographic | Num5 *(new)* |
| view.frameSelection | Center & Zoom: Selection | NumMultiply, Shift+F *(new)* |
| view.frameAll | Center & Zoom: Drawing | Ctrl+NumMultiply, Home |
| view.frameAllViews | Center & Zoom: All Views | Alt+NumMultiply |
| view.zoomIn / view.zoomOut | Zoom In / Out | NumAdd / NumSubtract |
| view.lookAtPin | Look At Pin | Space |
| view.toggleQuad | Four Views (F, L, T, 3D) | F9 |
| view.nextPane / view.prevPane | Next / Previous View | Tab / Shift+Tab |
| view.shadingWire / view.shadingSolid / view.shadingTextured | Wireframe / Solid / Textured | Alt+1 / Alt+2 / Alt+3 *(new)* |
| view.backfaceCulling | Face Culling | F |
| view.showGrid | Grid | |
| view.showPoints / showNormals / showProxies / showMemoryLabels / showHidden / showWeights / showBackfaces | toggles | |
| view.backgroundLod | Show LOD at Background… | |

## Create — owner: ops
| id | label | hotkey |
|---|---|---|
| create.point | Point (at cursor, or at pin when Use Pin is on) | Insert |
| create.face | New Face (from 3–4 selected points) | F6 |
| create.plane / create.circle / create.sphere | Plane… / Circle… / Sphere… | |
| create.box | Box… (at pin) | F7 |
| create.cylinder | Cylinder… (at pin) | F8 |
| create.proxy | Proxy… | |
| create.extrude | Extrude (selected faces) | G |

## Points — owners: ops, inspector
| id | label | hotkey | owner |
|---|---|---|---|
| points.selectFirst / selectNext / selectPrev | Select First / Next / Previous point | Ctrl+Home / Ctrl+PageDown / Ctrl+PageUp | ops |
| points.selectIsolated | Select Isolated | | ops |
| points.merge | Merge (into one point) | Shift+D, Shift+M | ops |
| points.mergeNear | Merge Near… | | ops |
| points.flatten | Flatten Points… | P | ops |
| points.snapToGrid | Snap to Grid *(new)* | | ops |
| points.properties | Point Properties… | Shift+E | inspector |

## Faces — owners: ops, inspector
| id | label | hotkey | owner |
|---|---|---|---|
| faces.reverse | Reverse | W | ops |
| faces.uncross | Uncross | Q | ops |
| faces.triangulate | Triangulate \\ | `\` | ops |
| faces.triangulateAlt | Triangulate / | `/` | ops |
| faces.squarize | Squarize (tris → quads) | | ops |
| faces.squarizeAllLods | Squarize All LODs | | ops |
| faces.moveTop / faces.moveBottom | Move Top / Bottom (face order) | Ctrl+Shift+Home / Ctrl+Shift+End | ops |
| faces.recalcNormals | Recalculate Normals | F5 | ops |
| faces.sharpEdge / faces.smoothEdge | Sharp Edge / Smooth Edge | U / I | ops |
| faces.selectByTexture | Select Faces with Same Texture | | inspector |
| faces.properties | Face Properties… | E | inspector |

## Surfaces / UV — owner: uv
| id | label | hotkey |
|---|---|---|
| uv.setBackgroundTexture | Set Background Texture… | A |
| uv.backgroundMapping | Background Mapping (planar from active view) | B |
| uv.editor | UV Editor… | |
| uv.activeSet / uv.addSet / uv.deleteSet | UV set management | |

## Structure — owner: structure
| id | label |
|---|---|
| structure.checkFaces | Check Faces (degenerate / non-planar / duplicate) |
| structure.findNonClosed | Topology: Find Non-Closed |
| structure.findComponents | Topology: Find Components (ComponentXX selections) |
| structure.findNonConvexities | Convexity: Find Non-Convexities |
| structure.convexHull | Convexity: Convex Hull (of selection) |
| structure.componentConvexHull | Convexity: Component Convex Hull |
| structure.removeDuplicateFaces | Remove Duplicate Faces |
| structure.repairDegenerate | Degenerated Faces: Repair |
| mass.set | Set Mass… |
| mass.clear | Clear Mass |
| tools.validate | Validate Model (DayZ rules) — Validation panel |

## LOD — owner: panels
| id | label | hotkey |
|---|---|---|
| lod.new | New LOD… | |
| lod.duplicate | Duplicate LOD | |
| lod.delete | Delete LOD | |
| lod.properties | LOD Properties (resolution)… | |
| lod.copySelectionTo | Copy Selection to LODs… | |
| lod.sort | Sort LODs | |
| lod.prev / lod.next | Previous / Next LOD | `[` / `]` *(new)* |

## Windows / tools / help
| id | label | hotkey | owner |
|---|---|---|---|
| window.namedProperties | Named Properties | Ctrl+1, Shift+F8 | panels |
| window.namedSelections | Named Selections | Shift+F5 | panels |
| window.lods | LODs | Shift+F6 | panels |
| window.mass | Mass | Shift+F7 | structure |
| window.currentPosition | Current Position / Inspector | Shift+F3 | inspector |
| window.history | History (undo stack) | Shift+F4 | chrome |
| window.resources | Resource Library (textures/materials/proxies) | Shift+F10 | inspector |
| tools.measure | Measuring | Shift+F11 | inspector |
| tools.animations | Animations (model.cfg preview) | Shift+F2 | animations |
| app.commandPalette | Command Palette *(new)* | Ctrl+Shift+P, F1 | chrome |
| window.log | Log | | chrome |
| window.resetLayout | Reset Window Layout | | chrome |
| help.hotkeys | Shortcuts | | chrome |
| help.about | About P3D Forge | | chrome |

## Named selections / properties — owner: panels (done)
| id | label |
|---|---|
| namedSelection.new / delete / deleteAllLods / rename / renameAllLods / deleteEmpty | New, Delete, Delete (All LODs), Rename…, Rename (All LODs)…, Delete Empty |
| namedSelection.redefine / addSelected / removeSelected / weights / showWeights / copyName | Redefine, Add Selected, Remove Selected, Weights…, Show Weights, Copy Name |
| namedProperty.set / namedProperty.delete | Set Named Property…, Delete Named Property |
| lod.showAtBackground | Show LOD at Background (toggle) |

## Implementation status
* ops commands (Edit selection tools, hide/lock, clipboard, Create, Points, Faces except properties) are implemented in
  src/P3DForge.App/Features/Ops{Edit,Clipboard,Create,PointsFaces}Feature.cs on top of Core/Ops/{PointOps,FaceOps,SelectTools,
  Primitives,Proxies,Extrude}.cs. Parameter dialogs use Dialogs/Ops/Form.cs (automation answer "key=value;key=value").
* Clipboard format: "P3DForge.Fragment" = a one-LOD MLOD file (MeshMath.FragmentToMlod) + a plain-text summary.

## Editor flags (EditorState.Flag / SetFlag)
| flag | values |
|---|---|
| view.shading | "textured" (default) / "solid" / "wire" |
| view.quad | bool — four views |
| view.backgroundLod | int LOD index or null |
| view.showWeights | selection name (string) / true (current selection) / null |
| view.showProxies, view.showMemoryLabels | bool, default true |
| view.showNormals, view.showHidden, view.showPoints, view.showGrid, view.backfaceCulling, view.showBackfaces | bool |
| transform.usePin | bool — Use Pin (Shift+C); primitives / merge / flatten / snap use the pin when on |
| selections.focus | name — Named Selections panel reveals that row, then clears the flag |
| automation.answer / automation.confirm | dialog answers under P3DFORGE_AUTOMATION |

## Wave 3 — rendering, proxies, materials, textures (done)

Right-hand tool panels open as tabs next to Objects (PanelDescriptor.TabWith).

| Command | Hotkey | What |
|---|---|---|
| view.shadingFinal | Alt+4 | Final (game-like) shading: rvmat stages (_co, _nohq, _smdi, _as, _mc, _dt), sun, shadows, ACES + EV |
| window.render | Shift+F12 | Render panel: view mode, FOV (default / game), final toggles, stage view, light preset, sun, shadows, exposure |
| view.stats / view.gameFov / view.saveScreenshot | — | Stats overlay (FPS, CPU / GPU ms, draws, tris); DayZ FOV; save the viewport as PNG |
| view.light.&lt;preset&gt; | — | Light presets buldozer / ingame / inventory / headlight; Alt+Shift+LMB drag moves the sun |
| view.firstLodBackground | — | First LOD drawn as a yellow frame in the other LODs (on by default); Show LOD at Background… picks another |
| window.objects | Shift+F9 | Objects panel: proxy models (MLOD only; ODOL marked), visibility, selection box / tint, click to select |
| objects.toggleProxies / objects.reloadProxies | — | Show / hide all proxy models; reload proxy files from disk |
| file.reload | Ctrl+Shift+R | Reload the document from disk (+ changed proxy files) |
| window.material | Ctrl+Shift+M | Material editor: shaders, colours, render flags, stages, uvTransform; live preview; Save / Save as / Revert |
| material.assign / material.selectFaces | — | Assign the edited rvmat to the selected faces; select the faces using it |
| window.materialLibrary | Ctrl+Shift+F12 | Material library: scans a folder for .rvmat (cached index), search, sphere previews, double-click assigns |
| window.textures | Ctrl+Shift+T | Texture tools: channel view, brightness / contrast / gamma / invert per channel, Save .paa / Save as |
| textures.convertToPaa / textures.convertFolder / textures.exportPaa | — | PNG / JPG / BMP / TGA → PAA (DXT1 / DXT5 + mips, TexConvert suffix rules); folder batch; PAA → PNG |
| window.log / file.preferences | — | Log panel (filter, search, copy); Preferences (work drive, texture size, background, grid, undo, backup) |

Cross-feature UI flags: `textures.openPath` (open a texture in the tools panel), `textures.changed` (viewport reloads that
texture), `view.shading = "final"`, `view.light.*`, `view.final.*`, `view.shadow.*`, `view.post.exposure`, `view.fov`.
Known gaps: binarized (ODOL) proxy models are not drawn (no ODOL reader yet — every P:\DZ model is ODOL); no auto exposure;
automation has no mouse click / drag step.

## Wave 4 — menu clean-up, game (ODOL) proxies, UV editing

The main menu hides commands nobody registered (`MenuModel.Visible`): unfinished items (animations, import / export,
merge, history, about) no longer show as dead entries and appear by themselves once a feature registers them.

| Command | Hotkey | What |
|---|---|---|
| window.resetLayout | — | Puts every panel back where it was at startup (closed panels return, floating ones dock) |
| uv.editor | Ctrl+Shift+U | UV editor panel: UVs of the selected faces (all when none) over their texture; select (click / rectangle, Shift / Ctrl, double-click = island), drag to move / rotate / scale (W / E / R), rotate 90°, flip, fit, planar mapping; wheel zoom, middle / right drag pan, F / Home frame |
| uv.planarView / uv.planarX / uv.planarY / uv.planarZ | — | Planar mapping of the selected faces from the active view or along a model axis, fitted to 0–1 |
| uv.fit / uv.flipU / uv.flipV / uv.rotate90 | — | Fit to 0–1, mirror, rotate the UVs of the selected faces |
| uv.activeSet / uv.addSet / uv.deleteSet | — | Switch / add (copy of the active set) / delete UV sets (#UVSet# stage k; set 0 lives in the faces) |

Binarized proxies: `Core/IO/Odol.cs` reads ODOL (DayZ version 54, whose model header and material v20 differ from
Arma's; Arma 44–73 unencrypted). Only the drawn LOD is converted (one point per render vertex, faces reversed to the
MLOD winding, bounding centre added back, nested proxies become proxy triangles + selections). `Odol.Verify` checks the
layout against each file's LOD address table: all 8,191 ODOL files of P:\DZ pass. A source (MLOD) copy next to the edited
model still wins over the game file. Game models are view only (Objects panel: "game ODOL").

### Wave 4b — merge, import / export, speed

| Command | Hotkey | What |
|---|---|---|
| file.merge / file.mergeAsSelection | — | Every LOD of another MLOD into the LOD with the same resolution (appended, selected) or as a new LOD; colliding proxy names get the next free index; "as Selection" adds one named selection |
| file.import.obj / file.import.fbx | — | OBJ (Y-up, or Z-up) and binary FBX 7.x (axes / units from the file, model transforms, all UV layers → UV sets, diffuse textures) into the active LOD or a new LOD; objects → named selections; textures → work-drive paths (.paa next to .png / .tga preferred). Dropping .obj / .fbx on the window imports too |
| file.export.obj | — | Selected faces (or the LOD) as .obj + .mtl (map_Kd = the texture file on the work drive) |

Axes follow the Arma 3 Blender toolbox: Blender (x, y, z) = P3D (x, z, y); imported faces keep their outside (MLOD winding).
testdata/import holds a Blender 5.1 cube exported as OBJ (Y-up, Z-up) and FBX for the tests.
Automation: flag `automation.file` answers file choosers; command steps log their UI-thread time; step `perf` logs the
viewport timings. Dragging uses a positions-only buffer update (BTR, 31k points: 7.5 ms per frame instead of ~23);
the UV editor rasterises its wireframe in software (18k faces ≈ 7–10 ms per redraw).

### Wave 5 — model.cfg animations, DayZ weapon animation wiring

| Command | Hotkey | What |
|---|---|---|
| tools.animations | Ctrl+Shift+A | Animations panel: the model.cfg binarize would use (same folder or the nearest parent), the model class (= .p3d name), its animations (own + inherited, "class Animations: Animations"), live preview (a slider per source, Play, rest pose = 0 when in range), editing (type, bone, source, axis / begin / end from the Memory LOD, min / max value, angles in degrees, offsets, hide, source address), New axis… (two Memory LOD points through the bone along X / Y / Z or from 2 selected points), skeleton bones (parents, add / remove, "add missing bones"), problems (bone not in skeleton, no named selection, axis missing / far from the part), Save (rewrites only changed classes; comments and layout stay) |
| tools.weaponAnimations | — | Weapon Animations panel: weapon classes whose model= is this file (path mismatch warned), class chain, inherited weaponStateAnim / boneRemap (game configs on the work drive), the AddItemInHandsProfileIK registration in the mod's scripts, presets from the game's weapons (player_main_<w>.asi, ik\weapons\<w>.anm, reloads\<w>\w_<w>_states.anm), file search pickers, bone check (states .anm → boneRemap → skeleton bone → named selection), Write to script / Write weaponStateAnim (shows the change first) |
| anim.preview | — | Toggle the animation preview |
| weaponAnims.writeScript / weaponAnims.writeConfig | — | The panel's write buttons as commands |
| anim.setValues / weaponAnims.setFiles | — | Automation: source values ("name=v;*=max") / file fields ("asi=…;ik=…;states=…", "like=svd") |

Core: Anim/ConfigSpans (class / property spans in config text), Anim/ModelCfg (read with inheritance, values "1,74" / "rad 90" /
expressions, span edits), Anim/AnimRig (rotation / translation / hide / direct, bone hierarchy, phases clamp / loop / mirror),
Anim/Anm (bone names from the .anm HEAD chunk), Anim/WeaponLinks (ConfigLibrary across files, boneRemap check, script / config
edits). ODOL: DayZ versions 53 / 54 / 55 (DayZ Tools write 53 and 55): 12 / 13 / 13 special LOD indices, an extra header byte
in 55, material blocks found by layout (v16: 56 extra bytes, v20: 104), no extra hide float — every ODOL file on P:\ reads.
Automation: steps "click" (button by caption), "scrollPanels", "perf"; command steps log their UI-thread time.

### Wave 5b — easier animations, DeODOL

- Animations panel reorganised around "select the part → ＋ Animate selection… → Test slider → Save": a card per animation
  (Test slider for that animation alone, plain-language fields, range "from / to" with ⇄ Reverse, Advanced section),
  the selected animation's axis (yellow, with arrow) and moving part (cyan box) drawn in the viewport, problems / all
  sources / skeleton in collapsed sections, getting-started hint when the model has no animations.
- anim.animateSelection (Tools menu, viewport right-click): wizard — part (named selection made from the selection when
  new), kind (door / wheel / lever / slide / hide), axis direction and position (auto: door hinge on an edge, wheels and
  levers through the centre; an existing memory axis named after the part is reused), amount, source, parent bone;
  creates the named selection, bone, Memory LOD axis points and the animation in one go.
- Opening a binarized (ODOL) .p3d: choose "convert to editable MLOD with DeODOL55UNI.exe" (File ▸ Preferences ▸ Tools;
  default: the exe in the work drive's root) — runs on a copy in %LOCALAPPDATA%\P3DForge\deodol, opens the result as an
  unsaved model (Save As suggests the original folder, offers the recovered model.cfg) — or "view with the built-in
  reader". The built-in ODOL reader for proxies is unchanged.

### Wave 5c — cut-out textures, close-up zoom

- Textures cut out by their PAA alpha flags as well as by name: a _co texture saved with an alpha key (FLAG bit 1, e.g.
  ghillie_green_co.paa, DXT1 1-bit alpha) and hair / tree / grass shaders are alpha-tested. No red back-face hint on proxy
  models or two-sided / cut-out materials.
- Mouse wheel: over a face, proxy model or vertex the zoom closes in on that surface (the camera never goes inside the
  model); over empty space it zooms towards the view centre. Zooming out moves at least 0.5 % of the model per notch.
- Perspective far / near ≤ 1e6 and cursor rays computed from the camera: very close zoom no longer degenerated the
  projection (NaN rays put the camera target at NaN — the whole view disappeared for good).
- Automation step "wheel": [x, y, notches] (fractions of the viewport; + = in), logs the camera afterwards.

### Wave 6 — safety, config.cpp variants, DayZ checks, binarize, LOD generation, folder renaming

| Id | Hotkey | What |
|---|---|---|
| window.history | — | History panel: every undo step since the file was opened; click one to go back / forward to it |
| help.about | — | Version, work drive, autosave and cache folders |
| tools.variants | — | Camo & Damage panel: the config.cpp classes that show the model (own or inherited model=), preview of hiddenSelections textures / rvmats and of the healthLevels damage states (render-only copy of the LOD, the model is not edited); problems: hidden selection missing in a visual LOD, files not on the work drive (or only one folder deeper = PBO prefix), damage entries no face uses |
| variants.set | — | Automation: "class=…;level=n" |
| tools.binarize | Ctrl+Shift+B | Binarize Test: DayZ Tools binarize.exe on a temporary copy (unsaved edits included, model.cfg of the folder and its parents), run from the work drive like DayZ Tools; its errors / warnings grouped per LOD with the vertices (click = go there); result per LOD (faces, vertices after binarize); open the ODOL result |
| lod.generate | — | Generate LODs: resolution LODs by quadric edge collapse (texture / material / selection borders, UV seams, hard-edge splits and open edges stay; coincident points are welded only while simplifying so no gaps open), Shadow Volume / Geometry (mass by volume, ComponentXX) / Fire / View Geometry from convex hulls of the parts (thinned, at most N hulls, model.cfg bones get hulls of their own) |
| tools.batchRename | — | Rename Paths in Folder: textures / rvmats / proxies in every MLOD, quoted paths in .rvmat and config.cpp; preview, confirmation, .bak copies; P:\DZ untouched unless chosen; binarized files skipped |

- Autosave: every 2 min (File ▸ Preferences ▸ Editing, 0 = off) a copy of the unsaved document goes to %LOCALAPPDATA%\P3DForge\autosave
  (one per running editor); saving / closing normally removes it; after a crash the next start offers to recover it (opens with the
  model's path, unsaved). P3DFORGE_AUTOSAVE_DIR redirects it for tests; automation runs do not autosave otherwise.
- Validate (Tools ▸ Validate Model) knows the object kind from config.cpp (CfgWeapons → weapon, CfgMagazines, CfgVehicles by base:
  CarScript → vehicle, House… → building, Clothing, Inventory_Base → item) and adds: memory points of the kind (inventory: boundingbox_min/max,
  ce_center, ce_radius, invview; weapons: usti hlavne, konec hlavne, eye, nabojnicestart/end; vehicles: pos_driver(_dir)), Shadow
  Volume closed / sharp / simpler than LOD 0 / untextured, LODs ordered by face count, Fire / View Geometry for buildings, vehicles,
  weapons, model.cfg animated parts in every resolution LOD (and collision LODs of buildings / vehicles), hidden selections in every
  visual LOD, bone weights below 100 %, textured faces with collapsed UVs, sections (draw calls), 2nd UV set in special LODs. "Fix" /
  "Fix all" repair what is safe (sharp shadow edges, clear shadow textures, bone weights to 100 %, delete UV set 1) in one undo step.
- ODOL: version 56 (current DayZ Tools binarize) reads (4 more header bytes, no Arma-56 animation fields).
- Viewport: PAA alpha flags make _co textures cut out (ghillie), hair / tree / grass shaders cut by alpha, no red back-face hint on
  proxies and two-sided / cut-out materials; wheel zoom closes in on the surface under the cursor (never into the model), zooms to the
  view centre over empty space; perspective far / near ≤ 1e6 and rays from the camera (very close zoom no longer breaks the view).
- Automation steps: "wheel" [x, y, notches], "autosave" (write now), "crash" (exit without cleanup); the dump lists validation results.
- 6b fixes: clicking a proxy model whose triangle is hidden in the model (Edit ▸ Hide / Object Builder) shows it again and selects
  it (one undo step); Fix / Fix all compare the model's content (a selection change no longer makes the results stale; a real edit
  re-validates with a message); Camo & Damage finds worn clothing through ClothingTypes male / female and DeODOL copies
  "name_mlod.p3d" as name.p3d, ignores hidden selections a clothing model does not carry (camoGround / camoMale on a female
  model), switches to Final shading when the variant changes rvmats.

### 0.2.0 — .anm animations

- .anm files (Enfusion, compiled by Workbench from .txa) are read: SET5 (translation, rotation) and SET6 (+ scale) — per bone the
  key frames and 16-bit values scaled into the bone's range; every vanilla SET5 / SET6 file reads (the 10 old SET4 files do not).
  The .anm space is Z-up: model (x, y, z) = .anm (x, z, -y); a weapon bone's transform turns about the model's origin.
- Weapon Animations panel ▸ Preview in the viewport: the states .anm (frame 0 closed, 1 open, 2 jammed) or any animation of the
  weapon's own .asi (its #parent files are not read) played on the model — slider and Play; only model bones of the model.cfg
  skeleton move, through boneRemap, like in the game; display only. The status line says which parts it moves.
- weaponAnims.setFiles also takes "preview=<text of a list item>;frame=<n>" (automation).
