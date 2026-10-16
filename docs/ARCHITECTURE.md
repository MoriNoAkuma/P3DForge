# P3D Forge — architecture & conventions (C# / .NET 10 / WPF)

P3D Forge is a Windows desktop editor for DayZ/Arma MLOD `.p3d` models (an Object Builder replacement).
Open `P3DForge.sln` in Visual Studio 2026 and press F5.

```
P3DForge.sln
src/P3DForge.Core/            class library (net10.0) — NO UI. Model, IO, operations, algorithms. Unit-tested.
  Model/Lod.cs Tags.cs        Lod (structure-of-arrays record), P3DModel, Tag records
  IO/Mlod.cs                  Mlod.Read / Mlod.Write — lossless, byte-exact (verified on 162 real files + oracle)
  LodNames.cs Flags.cs        LOD resolution names; point/face flag bits; selection weight encoding (Weights)
  Ops/Selection.cs            SelectionMask, #Selected#/#Hidden#/#Lock# helpers
  Ops/Structure.cs            Compact, Extract/Append (Fragment), DeleteSelection, TransformPoints, RecalcNormals, SetFaceTextures
  Ops/*.cs, Topology/, Paa/, ModelCfg/ …  more pure code (ported from legacy-ts/)
src/P3DForge.App/             WPF app (net10.0-windows), assembly P3DForge.exe
  AppServices.cs              singletons: Editor (EditorState), Settings; ResolveProjectPath/ToProjectPath
  Editor/EditorState.cs       document, active LOD, undo/redo (snapshots), events, UI flags, log
  Editor/Commands.cs          command registry + accelerator parsing
  Editor/SelectionOps.cs      selection changes respecting hidden/locked + select mode
  Editor/Settings.cs          %APPDATA%\P3DForge\settings.json (Extra dictionary for feature settings)
  Shell/Registry.cs           IFeature (auto-discovered), PanelRegistry, Toolbar, DynamicMenus
  Shell/MenuModel.cs          main menu (Object Builder structure) by command id
  Shell/Automation.cs         P3DFORGE_AUTOMATION test hook (scripted steps, screenshots, state dump)
  Services/FileService.cs     open/save (atomic write + .bak), confirm discard
  Viewport/ViewportContract.cs  IViewportContext / IViewHandle / ViewportPlugins (contract for plugins)
  Viewport/ViewportView.xaml(.cs) + Rendering/   OpenGL 4.1 renderer in a GLWpfControl
  Features/*.cs Panels/*.cs   feature modules
tests/P3DForge.Core.Tests/    xUnit v3 (Test Explorer): round-trip corpus, oracle cross-check, ops
testdata/                     local only, not in the repository: synthetic MLODs, DayZ samples, local.json (tests skip without it)
docs/FORMAT.md                verified MLOD format reference; docs/COMMANDS.md command ids/hotkeys/owners
```

## 1. The model is immutable (critical)
`Lod` and `P3DModel` are persistent data structures. **Never write into an array that belongs to a Lod
which is (or was) part of `EditorState.Model`.** Copy, modify, and build a new record:

```csharp
var positions = (float[])lod.Positions.Clone();   // copy first
positions[i * 3] += 1;                            // modify the copy
return lod with { Positions = positions };        // new Lod sharing every other array
```
Undo snapshots keep references to old arrays, so an in-place write silently corrupts history.
`Selection.GetSelected(lod)` / `EditorState.GetSelection()` return fresh copies (safe to modify).
`Tags` and `Strings` are `ImmutableArray`s; replace tag records with `with` expressions.
Faces are SoA with 4 slots: `FaceVertCount[f]` (3|4), `FacePoints[f*4+k]`, `FaceNormals[f*4+k]`,
`FaceUV[f*8+k*2(+1)]`, `FaceFlags[f]`, `FaceTexture[f]`/`FaceMaterial[f]` → `Strings`.
Per-point/per-face tag arrays must stay sized to `PointCount`/`FaceCount`; `Mlod.AssertIntegrity(lod)`
checks everything (the writer runs it before saving). Use `Structure.Compact` (delete),
`Structure.Extract`/`Append` (copy/add) for topology changes — they remap named selections, state tags,
UV sets, #Mass#, animations and sharp edges. Write new operations as pure static methods
`(Lod, …) → Lod` in `src/P3DForge.Core/Ops/` with xUnit tests; commands call
`AppServices.Editor.EditLod("Label", lod => Op(lod, …))`.

Selection: the current selection is the LOD's `#Selected#` state tag (OB saves it too). Change it via
`SelectionOps` (App) or `EditorState.SetSelection(mask)`. Masks are `byte[]` (0 = unselected,
non-zero = selected / encoded weight). Hidden = point flag 0x01000000 or #Hidden#; locked = 0x02000000
or #Lock#; a face is hidden if any of its points is.

## 2. Coordinates
RAW = file coordinates (left-handed: +X right, +Y up, +Z forward). Everything outside the renderer is RAW.
RENDER = OpenGL space = (x, y, -z). Convert only with `Coords.RawToRender/RenderToRaw`. Stored winding:
outward closed meshes have NEGATIVE right-handed signed volume on RAW coords; after the z-mirror they are
CCW from outside (standard GL front faces). Generated faces must follow the P3D winding.

## 3. EditorState (AppServices.Editor) — UI thread only
`Model`, `Lod` (active), `ActiveLod`, `Path`, `Dirty`, `Edit(label, model => newModel, opts)`,
`EditLod(label, lod => newLod, opts)` (opts: Kind = Selection for selection-only edits, MergeKey to merge
rapid edits, KeepNormals, LodIndex). Geometry edits recalc normals automatically. `Changed` event with
`Change` flags (Document, Model, Selection, ActiveLod, Ui, Pin, Log). `SelectMode`, `Tool`, `Pin`,
`Flag<T>(key, fallback)` / `SetFlag(key, value)` for UI toggles (namespaced keys like "view.shading").
`Log(message, level)` → status bar + Log panel. Settings: `AppServices.Settings` (+ `Get<T>/Set<T>` extras).

## 4. UI extension points (no shared-file edits needed)
* A feature is any class implementing `IFeature` (`Install()`), discovered automatically at startup.
* Commands: `Commands.Register(new Command { Id, Label, Hotkeys, Execute, CanExecute, IsChecked })`.
  Ids/labels/hotkeys: docs/COMMANDS.md. Menus (MenuModel) reference ids; unregistered ids render disabled.
  Hotkeys are dispatched by MainWindow (text boxes keep their own editing keys).
* Panels: `PanelRegistry.Register(new PanelDescriptor { Id, Title, Location, Order, Create = () => new MyPanel() })`;
  `PanelRegistry.Focus(id)` for Window-menu commands.
* Toolbar: `Toolbar.Add(new ToolbarItem { Command, Glyph = "\uE74E" /* Segoe Fluent Icons */, Group, Order })`.
* Dynamic menus: `DynamicMenus.Register("recentFiles", () => items)`.
* Dialogs: WPF `Window`s with `Owner = Application.Current.MainWindow`, `ShowDialog()`; reuse the styles in
  Themes/Editor.xaml (CompactTextBox, CompactButton, IconButton, CompactListBoxItem, MonoFont, Editor.* brushes).
  The app uses WPF's Fluent theme (ThemeMode="Dark") + AvalonDock VS2013 dark.
* Viewport plugins: `ViewportPlugins.Register(ctx => { …; return () => dispose; })`, using only `IViewportContext`
  (views, picking, projection, preview positions, overlay lines, overlay canvas). See its input-ownership rules.

## 5. Performance
Real models: 30k points / 17k faces / 185 named selections per LOD (54 MB files); design for 200k faces.
Typed arrays + index loops in hot paths, no LINQ/allocations per element, no O(n²). Never block the UI
> 100 ms; render on demand (`Invalidate()`), never continuously while idle.

## 6. Build, test, verify
* Build: `dotnet build P3DForge.sln` (or VS). Tests: `dotnet test tests/P3DForge.Core.Tests` (filter with
  `--filter "FullyQualifiedName~MyTests"`). Corpus: `TestData.Corpus()` = the user's real MLOD files on P:\ (read-only!).
* Parallel agents must not fight over bin/obj: build with your own output folder, e.g.
  `dotnet build src/P3DForge.App -c Debug -o .build/<agent>` and run `.build/<agent>/P3DForge.exe`.
* UI verification: run the exe with `P3DFORGE_AUTOMATION=<script.json>` (see Shell/Automation.cs; steps: open,
  command, wait, lod, selectPoints, selectNamed, setFlag, screenshot, viewportShot, dump, save) and LOOK at the
  PNGs. Use forward slashes in JSON paths. Write scratch output to testdata/out/<agent>/. Set
  `P3DFORGE_SETTINGS=<temp>/settings.json` to isolate settings. Never write to P:\.
