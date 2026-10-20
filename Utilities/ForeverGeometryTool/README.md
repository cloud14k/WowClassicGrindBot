# ForeverGeometryTool

`ForeverGeometryTool` converts explicitly selected pre-Recast raw geometry JSON
exports from the Forever client into aggregated per-navmesh-tile geometry JSON.
It does not read the client or scan directories: every input file, expected
MapID, source ADT and output tile is explicit.

The input contract is `wow-raw-geometry-v2`, with yard coordinates in
`[WoW_X, WoW_Z, WoW_Y]` order. Each output keeps terrain, combined WMO/M2
collision, and liquid triangle soup in separate arrays. It also records the
client Build, source hash, selected MapID/ADT, tile bounds, and liquid areas.
Terrain and collision are combined only by `ToTileGeometry()` at the boundary
to the existing `PPather.Navmesh.TileGeometry` type.

The native terrain/model builder stores `[WoW_Y,WoW_Z,WoW_X]`. The v2 hook
swaps its horizontal components when writing the declared raw contract; the
C# importer then restores Recast coordinates without reflecting the mesh or
reversing its faces. ADT X indexes world Y and ADT Y indexes world X. Version
v1 incorrectly labeled the native array and is rejected, including its old
DNM geometry identity. Re-extract into a new directory with the current hook;
do not relabel an old file or overwrite an earlier sample.

The exporter validates a one-to-one match between input files, their ADT
metadata, and the separately supplied expected ADT set. It rejects missing
files, mixed Products/Builds/maps/transforms, malformed arrays and unknown
liquid areas. It preserves whole triangles and assigns them to every
133.33333 yd tile whose padded bounds intersect their horizontal AABB, matching
`TileGeometryExtractor`. It converts coordinates to `[WoW_Y, WoW_Z, WoW_X]`,
maps liquid area 9 to water and area 8 to magma/slime, and applies the optional
runtime `MinWorldZ` floor before aggregation.

All source geometry is merged before baking. Duplicate triangles are removed
only when all three coordinates match exactly as float values, allowing cyclic
rotations that preserve the normal but keeping reversed winding as a distinct
face. Height is part of the identity, and liquid area is part of the liquid identity, so
bridges/layers and water versus magma/slime remain separate. No coordinate
rounding is used. Per-tile output records all source ADTs and source hashes.

Example (run from the repository root):

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- `
  --input tmp/Stormwind/input/adt30_48/raw-geometry/map0000_adt30_48.rawgeo.json `
  --adt 30,48 --expect-adt 30,48 --output tmp/city-one-adt-geometry --map 0
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
  --input tmp/Stormwind/input/adt30_48/raw-geometry/map0000_adt30_48.rawgeo.json `
  --adt 30,48 --expect-adt 30,48 --output tmp/city-bake-geometry --map 0 `
  --continent Azeroth --dnm-output tmp/city-bake-dnm --bake-tile 132,61
```

## Resumable explicit ADT batch preparation

`batch` consumes **existing** rawgeo JSON files from an explicit input root; it
does not open CASC or discover a WDT. `--adt-range` accepts an inclusive
rectangle bounded to the client 0..63 grid for partial/test runs. `--adt-list`
accepts a sparse requested set, one `X,Y` coordinate per line; it also accepts
`tile MapID X Y` lines from a matching `mapextractor --list-tiles` log. Use the
explicit list form for a complete continent candidate. Files may be directly in
the input root or in the `adtXX_YY/raw-geometry/` folders created by
`extract-single-adt.ps1`. The runner
records source and tile task states in `batch-state.json`, hashes every rawgeo,
and writes one geometry/DNM tile at a time using only that tile's contributing
ADTs. Re-running with `--resume` reuses unchanged validated tasks and retries
tasks invalidated by source SHA or bake configuration changes. Tasks retain
Product/Build, source and settings hashes, contributors, output paths, query
results, elapsed milliseconds, sampled working-set size, total file count and
disk bytes. Working-set samples are taken at task boundaries; they are not a
continuous peak-memory measurement.

For a deliberately small run, provide one explicit ADT and a new output pair:

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- batch `
  --map 0 --adt-list tmp/selected-adts.txt `
  --input-dir tmp/p45-casc-3x3-clean `
  --output-dir tmp/p5-small-batch `
  --dnm-output tmp/p5-small-batch-dnm `
  --continent Azeroth
```

If `--tile X,Z` is omitted, the runner uses the union of tiles reported by the
selected rawgeo files. Repeat `--tile X,Z` to restrict the target set. After a
successful run, independently check the saved state, current source hashes,
DNM hashes/readback, exact file set, tile links, height query, and path query:

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- batch-verify `
  --state tmp/p5-small-batch/batch-state.json
```

The batch manifest always starts with `scope=explicit_adt_and_tile_set` and
`publishable_as_continent_cache=false`. It does not infer continent coverage
from an ADT rectangle. A separate sealing command can change those fields only
after it runs the verified `mapextractor.exe` against the matching Forever
client for exactly one MapID. It checks the process arguments, Product, Locale,
CASC Data root, Build, exit code, fatal output, final WDT completion record,
all 4096 MAIN slots, stdout ADT rows and the native binary `.tilelist`. Those
WDT ADTs must exactly match the requested and successfully processed ADTs.
Every source and tile task is checked again by SHA/configuration, every
contributed Detour tile must be present, and independent DNM read-back, height,
path and cross-tile checks must pass:

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- batch-seal `
  --state tmp/p5-small-batch/batch-state.json `
  --client-root 'E:\games\World of Warcraft Forever' `
  --tool-directory tmp/p1-trinitycore-build/bin/Release `
  --locale zhCN
```

The command starts the tool with `-i <client-root> -o <isolated-output>
-p <Product> -l <Locale> -e 1 -m <MapID> --list-tiles`. This reads the
selected map's WDT metadata only; it does not extract ADTs or bake tiles. The
build script writes `forever-casc-tools.json` beside the executable with the
pinned source commit, checked-in patch hash and executable hash. A caller
supplied text log cannot seal a cache. Text listings remain useful to provide
an explicit `--adt-list`, but they are not publication proof.

On success, the cache contains content-addressed WDT process output, native
`.tilelist`, and a v2 proof. The runtime verifies their paths and SHA-256 hashes,
tool source identity, process arguments and completion record, native tile-list
Build and ADT set, plus the exact expected/processed ADT and target/completed
tile sets. Existing manifest v2 identity and DNM hashes remain required. A
failed seal writes no manifest pointing to new evidence; evidence files are
immutable and an earlier manifest remains recoverable. The old external-log
proof format is rejected and must be resealed by the process-driven command.

Both output directories must be empty. `--bake-tile` uses Detour coordinates
`X,Z`, and every requested tile must exist in the aggregate. Geometry and DNM
manifests record the source set, Build, bake settings, duplicate counts and file
hashes. A selected ADT/tile test pack is explicitly marked as not publishable as
a continent cache. Runtime Forever cache loading requires a matching manifest,
Product, Build, MapID, settings hash and exact DNM file set.

The bake identity includes the raw geometry format and exporter algorithm
version. Vertical geometry padding is fixed at 4 yards because runtime has no
padding setting; a custom value is rejected. The Forever DNM manifest is now
format v2 and requires the package-contained WDT coverage proof, so v1 sealed
cache packages must be rebuilt and sealed again. Only corrected v2 rawgeo
files can be reused when their source and geometry validations still pass.

## Explicit local city pilot

A city pack keeps `scope=explicit_adt_and_tile_set` and remains ineligible for
continent publication. The default runtime gate still requires the full WDT
proof. An operator can explicitly select a local pilot through
`Navmesh:Query:ForeverRegion`:

```json
{
  "MapId": 0,
  "Continent": "Azeroth",
  "ManifestSha256": "<SHA-256 of the independently verified manifest.json>",
  "SourceAdts": [[30, 48]],
  "Tiles": [[132, 61]]
}
```

This example selects one interior tile only. Supply the actual independently
chosen source and target sets, including every ADT touched by each target's
padded footprint. The runtime rejects missing padding sources, set changes,
duplicate coordinates, identity/hash failures and queries outside the selected
tiles. Put the pack under the normal `forever/Azeroth/<settings_hash>` directory
in an isolated data root. Pinning is an explicit operator trust decision after
`batch-verify` and real-coordinate checks; it is not proof that arbitrary
caller-supplied geometry is complete, or a substitute for a continent WDT seal.

Use `extract-single-adt.ps1 -ExportMapMetadata` to write the selected MapID's
primary `WorldMapArea.json` entries from the same client's UiMap,
UiMapAssignment and Map DB2 tables. This does not export area hierarchy or
exploration levels, which stay zero. Unknown DB2 layouts and copied/ambiguous
assignments fail instead of using an older era's bounds. Preserve the metadata
hash and client identity alongside the pilot configuration.

For an existing prepared pilot directory containing `appsettings.json`,
`data_config.json` and explicit world XYZ `routes.json`, run:

```powershell
dotnet run --project CoreTests -c Release -- forever-region <pilot-directory>
```

This read-only suite loads the pack through PPatherService, checks arrival and
endpoint displacement, raycasts every output segment and checks its floor
height. Rays on shared vertices are additionally checked through dense
sub-voxel surface samples, using a 0.004 yd (two world float ULP) boundary
tolerance. A diagonal through a missing upper floor is explicitly rejected by
regression. It writes `route-verification.json` and `navmesh-polygons.json`. These
checks exercise the real DNM/service; landmark coordinates and actual character
movement still require separate confirmation in the game.

The real suite requires `Pathing:ForeverClientRoot` and
`Pathing:ForeverToolDirectory` (with the current pinned mapextractor and its
provenance). It runs WDT metadata enumeration only, verifies the process/native
tile list through the existing enumerator, and requires every regional source
ADT to belong to that WDT. It never extracts terrain or bakes a tile.

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
