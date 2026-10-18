# ForeverGeometryTool

`ForeverGeometryTool` converts explicitly selected pre-Recast raw geometry JSON
exports from the Forever client into aggregated per-navmesh-tile geometry JSON.
It does not read the client or scan directories: every input file, expected
MapID, source ADT and output tile is explicit.

The input contract is `wow-raw-geometry-v1`, with yard coordinates in
`[WoW_X, WoW_Z, WoW_Y]` order. Each output keeps terrain, combined WMO/M2
collision, and liquid triangle soup in separate arrays. It also records the
client Build, source hash, selected MapID/ADT, tile bounds, and liquid areas.
Terrain and collision are combined only by `ToTileGeometry()` at the boundary
to the existing `PPather.Navmesh.TileGeometry` type.

The exporter validates a one-to-one match between input files, their ADT
metadata, and the separately supplied expected ADT set. It rejects missing
files, mixed Products/Builds/maps/transforms, malformed arrays and unknown
liquid areas. It preserves whole triangles and assigns them to every
133.33333 yd tile whose padded bounds intersect their horizontal AABB, matching
`TileGeometryExtractor`. It converts coordinates to `[WoW_Y, WoW_Z, WoW_X]`,
maps liquid area 9 to water and area 8 to magma/slime, and applies the optional
runtime `MinWorldZ` floor before aggregation.

All source geometry is merged before baking. Duplicate triangles are removed
only when all three coordinates match exactly as float values, independent of
vertex order; the emitted triangle retains the first source winding. Height is
part of the identity, and liquid area is part of the liquid identity, so
bridges/layers and water versus magma/slime remain separate. No coordinate
rounding is used. Per-tile output records all source ADTs and source hashes.

Example (run from the repository root):

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- `
  --input tmp/p1-casc-sample/raw-geometry/map0000_adt34_46.rawgeo.json `
  --adt 34,46 --expect-adt 34,46 --output tmp/p2-geometry --map 0
```

The output directory must be empty. This prevents stale tiles from being
mistaken for results of the selected ADT.

For a limited bake proof, repeat `--bake-tile` for each requested tile. The
tool first aggregates all input ADTs, bakes each requested tile exactly once
with the project's existing `NavmeshTileBuilder`, writes the standard `.dnm`
format, reads each file back, adds the set to `DtNavMesh`, verifies tile links,
queries ground height, checks the WoW/Rc coordinate round-trip, and queries a
connected path:

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- `
  --input tmp/p1-casc-sample/raw-geometry/map0000_adt34_46.rawgeo.json `
  --adt 34,46 --expect-adt 34,46 --output tmp/p3-geometry --map 0 `
  --dnm-output tmp/p3-dnm --bake-tile 68,118
```

Both output directories must be empty. `--bake-tile` uses Detour coordinates
`X,Z`, and every requested tile must exist in the aggregate. Geometry and DNM
manifests record the source set, Build, bake settings, duplicate counts and file
hashes. A selected ADT/tile test pack is explicitly marked as not publishable as
a continent cache. Runtime Forever cache loading requires a matching manifest,
Product, Build, MapID, settings hash and exact DNM file set.

## Rebuild the restricted CASC tools

The raw geometry hook is based on [`advocaite/TrinityCore` commit
`440d365e791baff1cdf555ed2179c71bd6ebd014`](https://github.com/advocaite/TrinityCore/commit/440d365e791baff1cdf555ed2179c71bd6ebd014), whose `COPYING` file declares GPL-2.0. The compact patch is
[`forever-rawgeo-440d365.patch`](patches/forever-rawgeo-440d365.patch). It adds
MapID/ADT filters to the map and VMAP extractors, skips the global model sweep
for a selected VMAP tile, and exports validated raw arrays from
`TileBuilder::buildTile` after terrain and VMAP loading but before Recast. The
export keeps original triangle order, liquid areas, source coordinates and
client identity metadata. WMO collision uses unique triangle references from
each group's `MOBR` BSP list; groups without `MOBR` retain the legacy `MOPY`
material heuristic as a fallback. A failed requested write terminates the
generator.
Small Windows build compatibility edits cover Boost 1.86 module discovery,
static OpenSSL's zlib link, and CascLib/OpenSSL AES symbol collisions.

All downloaded source and build products stay under ignored root `tmp/`. The
script reuses cached Boost 1.86 and OpenSSL 3.3.2 packages; it does not download
or build those dependencies. Recreate the four required executables with:

```powershell
powershell -ExecutionPolicy Bypass -File Utilities/ForeverGeometryTool/scripts/rebuild-casc-tools.ps1
```

Then extract one explicit ADT (use a new output directory for each source ADT):

```powershell
powershell -ExecutionPolicy Bypass -File Utilities/ForeverGeometryTool/scripts/extract-single-adt.ps1 `
  -ClientRoot 'E:\games\World of Warcraft Forever' -MapId 0 -AdtX 34 -AdtY 46 `
  -OutputDirectory 'tmp/p45-one-adt' -ToolDirectory 'tmp/p45-trinitycore-build/bin/Release'
```

The extraction script validates active Product/Build from `.build.info`, checks
the selected tile in the MapID's WDT, checks that the generated `.tilelist`
contains exactly one ADT, and records separate logs under the selected output.
It rejects unexpected `.map` or raw geometry files and verifies that raw mode
did not produce `.mmtile`. To reproduce on a machine with an empty `tmp/`, first
provide Boost 1.86 headers/libraries and OpenSSL 3.3.2 headers/libraries at the
script's documented `tmp/` paths (or pass explicit paths to the build script),
then run the two commands above. The scripts intentionally do not fetch or
compile large dependencies.
