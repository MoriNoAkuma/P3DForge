# MLOD P3D format — P3D Forge reference

Verified against the 162 real MLOD files on `P:\` (all parse with zero trailing bytes).
All integers little-endian. `asciiz` = bytes up to a NUL terminator. Strings are treated
as **latin1** (1 byte ↔ 1 char) everywhere so that round-trips are byte-exact.

## File

```
char[4]  "MLOD"
uint32   version          // 257 (0x101) in every known DayZ file
uint32   lodCount
LOD      lods[lodCount]
// nothing follows the last LOD
```

## LOD (`P3DM`, v28/256)

```
char[4]  "P3DM"
uint32   major            // 28 (0x1C)
uint32   minor            // 256 (0x100)
uint32   nPoints
uint32   nNormals
uint32   nFaces
uint32   headerFlags      // 0 in every known file; preserve verbatim
Point    points[nPoints]  // 16 bytes each
float32  normals[nNormals][3]
Face     faces[nFaces]
char[4]  "TAGG"
Tag      tags[...]        // until the tag named "#EndOfFile#" (inclusive)
float32  resolution       // the LOD "resolution" / type value
```

### Point (16 bytes)
`float32 x, y, z; uint32 flags;`

### Face (72 bytes + 2 strings)
```
uint32  nVerts            // 3 or 4
Vertex  v[4]              // always 4 slots on disk; slot 3 unused for triangles (preserve its bytes!)
uint32  faceFlags
asciiz  texture           // e.g. "dz\weapons\...\x_co.paa", "#(argb,8,8,3)color(1,0,0,1,co)" or ""
asciiz  material          // e.g. "dz\...\x.rvmat" or ""
```
`Vertex = { uint32 pointIndex; uint32 normalIndex; float32 u; float32 v; }` (16 bytes)

### Tag
```
uint8   active            // 1 in every known file; preserve
asciiz  name
uint32  dataSize
uint8   data[dataSize]
```

| Tag name | data |
|---|---|
| `#EndOfFile#` | size 0; terminates the tag list (always last) |
| `#Property#` | 128 bytes: `char key[64]` + `char value[64]` (NUL-padded). One tag per property |
| `#UVSet#` | `uint32 stage` then, for every face in order, for every *used* vertex (nVerts): `float32 u, v`. size = 4 + Σ nVerts·8. Stage 0 duplicates the face-vertex UVs (keep both in sync when editing, but preserve each verbatim on load→save). Stage 1+ exist (2nd UV set) |
| `#Mass#` | `float32 mass[nPoints]` (Geometry LOD) |
| `#SharpEdges#` | pairs `uint32 pointA, pointB` (size % 8 == 0) |
| `#Selected#`, `#Hidden#`, `#Lock#` | like a named selection: `uint8 pointBytes[nPoints]` + `uint8 faceBytes[nFaces]` |
| `#Animation#` | `float32 time` + `float32 xyz[nPoints][3]` (vertex animation frame) |
| any name not starting with `#` | **named selection**: `uint8 pointWeights[nPoints]` + `uint8 faceWeights[nFaces]` |

Tag order differs between exporters (Object Builder, Blender add-ons, …) — a reader must
keep the original order so that unmodified files re-serialize byte-for-byte.
Observed orders include `#UVSet#, #Property#…, <named>…, #EndOfFile#`,
`<named>…, #UVSet#, #EndOfFile#`, `#Mass#, #UVSet#, #Property#…, <named>…, #EndOfFile#`,
`#Selected#, <named>…, #UVSet#, #EndOfFile#`.

### Selection weight bytes
`0` = not in selection, `1` = full weight (1.0), any other byte `b` → weight `(256 - b) / 255`.
Encoding weight `w` (0<w<1): `b = round(256 - 255·w)`; `b == 256 → 0` (too small), `b == 1`/`w==1 → 1`.

## Coordinate system & winding (verified on corpus)

* P3D is **left-handed**: +X right, +Y up, +Z forward (model front faces +Z).
* For an outward-facing closed mesh, the RH signed volume
  `Σ dot(A, cross(B, C)) / 6` over triangles (quad split `(0,1,2),(0,2,3)`) computed on
  **raw** coordinates in **stored** vertex order is **negative** (29/29 Geometry LODs).
* Stored normals mostly equal `normalize(cross(P1-P0, P2-P0))` on raw coords/stored order,
  i.e. they point *inward* (317 of 380 LODs; other exporters differ). The engine does not
  rely on MLOD normals; P3D Forge renders with its own computed normals.
* **To display in Three.js (right-handed)**: map `(x, y, z) → (x, y, -z)`. The mirror flips
  handedness, so stored winding becomes CCW-from-outside and `FrontSide` is correct.
  Outward render normal = `(-nx, -ny, nz)` from a stored (inward) normal.
* UVs use the DirectX convention (v = 0 at the top of the image). Upload texture rows top-first
  with `flipY = false` and sample `(u, v)` directly.
* `nNormals` varies: = nPoints (smooth), = nFaces (flat) or anything else. Preserve verbatim.

## Point flags (uint32)
| mask | meaning |
|---|---|
| `0x0000000F` | land contact: 1 ON_LAND, 2 UNDER_LAND, 4 ABOVE_LAND, 8 KEEP_LAND |
| `0x000000F0` | lighting: 0x10 NOLIGHT (shining), 0x20 AMBIENT (always in shadow), 0x40 FULLLIGHT, 0x80 HALFLIGHT |
| `0x00000300` | decal: 0x100 DECAL, 0x200 VDECAL |
| `0x00003000` | fog: 0x1000 NOFOG, 0x2000 SKYFOG |
| `0x00FF0000` | user value (byte, step 0x10000) |
| `0x0F000000` | special: 0x1000000 HIDDEN (seen in corpus: 780 points) |

## Face flags (uint32)
| mask | meaning |
|---|---|
| `0x000000A7` + `0x00300000` | lighting: 0x1 NOLIGHT, 0x2 AMBIENT, 0x4 FULLLIGHT, 0x20 BOTHSIDESLIGHT, 0x80 SKYLIGHT, 0x100000 REVERSELIGHT, 0x200000 FLATLIGHT |
| `0x00000018` | shadow: 0x8 ISSHADOW, 0x10 NOSHADOW |
| `0x00000300` | z-bias (step 0x100: 0 none, 1 low, 2 mid, 3 high) |
| `0x01000000` | DISABLE_TEXMERGE |
| `0xFE000000` | user value (shift 25) |

All 580 995 faces in the corpus have flags 0.

## LOD resolution values (float32)

| value | name |
|---|---|
| `< 1000` | Visual LOD (shown as e.g. `0.000`, `1.000`, `2.000`) |
| `1000` | View - Gunner |
| `1100` | View - Pilot |
| `1200 + n` | View - Cargo (n = index) |
| `10000 + n` (10000–10999) | ShadowVolume n |
| `11000 + n` | ShadowBuffer n |
| `20000 + n` | Edit n |
| `1e13` | Geometry |
| `2e13` | Geometry Buoyancy |
| `4e13` | Geometry PhysX |
| `1e15` | Memory |
| `2e15` | LandContact |
| `3e15` | Roadway |
| `4e15` | Paths |
| `5e15` | Hit-points |
| `6e15` | View Geometry |
| `7e15` | Fire Geometry |
| `8e15` | View Cargo Geometry |
| `9e15` | View Cargo Fire Geometry |
| `1e16` | View Commander |
| `1.1e16` | View Commander Geometry |
| `1.2e16` | View Commander Fire Geometry |
| `1.3e16` | View Pilot Geometry |
| `1.4e16` | View Pilot Fire Geometry |
| `1.5e16` | View Gunner Geometry |
| `1.6e16` | View Gunner Fire Geometry |
| `1.7e16` | Sub Parts |
| `1.8e16` | ShadowVolume - View Cargo |
| `1.9e16` | ShadowVolume - View Pilot |
| `2e16` | ShadowVolume - View Gunner |
| `2.1e16` | Wreck |

Compare large values after float32 rounding (`Math.fround`), e.g. `1e13` is stored as `9999999827968`.

## Corpus facts (P:\ , 162 MLOD files, 442 LODs)
* resolutions seen: 0,1,2,3,4,5, 1100, 1200, 1201, 1e13, 1e15, 2e15, 6e15, 7e15
* named properties seen: autocenter, lodnoshadow, canocclude, canbeoccluded, prefershadowvolume,
  sbsource, reversed, damage, placement, generateoffmeshconnections, drawimportance, canclimb, ignorecomponents
* 326 138 triangles + 254 857 quads; point flags only 0 or 0x1000000
* sizes 82 B … 54 MB (`P:\MyCar\car.p3d`) — the large BTR files are the performance benchmark
