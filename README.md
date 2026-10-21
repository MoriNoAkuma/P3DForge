# P3D Forge

A model editor for **DayZ** (and Arma-style) **MLOD `.p3d`** files — an open alternative to Object Builder for Windows,
with the things modders usually do by hand built in: game-like rendering with real rvmats and proxies, model.cfg
animations, config.cpp camo / damage previews, DayZ-specific checks, a binarize test and LOD generation.

> Not affiliated with or endorsed by Bohemia Interactive. DayZ, Arma and Object Builder are trademarks of their owners.
> P3D Forge reads and writes the open MLOD format; it ships no game files.

Status: **early (0.1)** — usable for everyday editing; expect rough edges. `.anm` playback and an in-hands preview are planned.

## Features

**Editing**
- Open / save MLOD `.p3d` byte-exact (round-trip tested), every LOD type, named selections, properties, mass, UV sets, sharp edges
- Object Builder style editing: points / faces / objects, move / rotate / scale with pin, mirror, extrude, primitives, merge, Undo history with a History panel
- UV editor, texture / material assignment and mass rename (one model or a whole folder: models, rvmats, config.cpp)
- Import OBJ and binary FBX, export OBJ
- Autosave with crash recovery

**Viewing**
- OpenGL viewport with Solid / Textured / Final shading (rvmat stages, alpha test / blend, shadows)
- Proxies drawn as their real models — including the game's binarized (ODOL) models from `P:\DZ`
- Binarized models open read-only with the built-in ODOL reader (versions 53–56), or are converted to MLOD with an external tool of your choice

**DayZ**
- model.cfg animations: edit, preview with sliders, an "Animate selection" wizard (doors, wheels, levers, bolts), axis checks
- Weapon wiring: `.asi` / `.anm` hands animations and `weaponStateAnim` / `boneRemap` checks against the skeleton; finds the registration in script mods anywhere on the work drive, or creates the script and its `CfgMods` entry
- Weapon animations preview: plays the weapon's `.anm` files (bolt states, fire, reloads) on the model through `boneRemap` — also before the weapon has a config, with a game weapon's
- Camo & Damage: preview `hiddenSelectionsTextures` / `hiddenSelectionsMaterials` variants and `healthLevels` damage states from config.cpp
- Validate: geometry and components, mass, memory points by object kind (weapon, item, vehicle, building, clothing), shadow volume, LOD order, bone weights, UVs, hidden selections — with one-click fixes where safe
- Binarize test: runs DayZ Tools `binarize.exe` on a temporary copy and lists its errors / warnings with links to the LOD and vertices
- Generate LODs: simplified resolution LODs (texture borders and UV seams stay put) plus Shadow Volume, Geometry (with mass), Fire and View Geometry from convex hulls

## Download

Get `P3DForge.exe` from [Releases](https://github.com/MoriNoAkuma/P3DForge/releases) — one file, nothing to install
(the .NET runtime is inside). Put it anywhere and run it.

The exe is not code-signed, so on the first start Windows SmartScreen may say "Windows protected your PC" (unknown
publisher): click **More info ▸ Run anyway**. Every release is built from this repository by GitHub Actions
([workflow](.github/workflows/release.yml)), so you can check what went into it or build it yourself.

## Requirements

- Windows 10 / 11 x64, a GPU with OpenGL 4.1
- [.NET 10 SDK](https://dotnet.microsoft.com/download) only to build it yourself
- A DayZ work drive (`P:\`) for game paths (textures, rvmats, proxies); set it in **File ▸ Preferences**
- Optional: [DayZ Tools](https://store.steampowered.com/app/830640/DayZ_Tools/) for the binarize test (found automatically)

## Build and run

```powershell
git clone https://github.com/MoriNoAkuma/P3DForge.git
cd P3DForge
dotnet run --project src/P3DForge.App -c Release
```

The same single `P3DForge.exe` as in the releases:

```powershell
dotnet publish src/P3DForge.App/P3DForge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

### Making a release

Publish a release on GitHub with a tag like `v0.2.0`: the **Build release** workflow builds `P3DForge.exe` (version from
the tag) and attaches it, plus a zip, to the release a few minutes later.

## Tests

```powershell
dotnet test tests/P3DForge.Core.Tests -c Release
```

Many tests read model files that are not in the repository (a local `testdata/` folder and the unpacked game data in
`P:\DZ`); without them they are skipped:
- `testdata/dzsamples/` — the DayZ Tools sample models (`Test_Building_sample_building.p3d`, …)
- `testdata/local.json` — your own models, e.g. `{ "weapon": "P:\\MyMod\\gun\\gun.p3d", "btr": "P:\\MyCar\\car.p3d" }`

GitHub Actions builds the solution and runs the tests on every push and pull request ([workflow](.github/workflows/tests.yml)).

## Documentation

- [docs/COMMANDS.md](docs/COMMANDS.md) — every command, hotkey and what it does
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how the code is organized
- [docs/FORMAT.md](docs/FORMAT.md) — the MLOD format as verified against real files

## License

[MIT](LICENSE)
