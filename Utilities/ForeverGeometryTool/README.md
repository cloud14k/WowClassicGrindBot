# ForeverGeometryTool

`ForeverGeometryTool` converts one pre-Recast raw geometry JSON export from the
Forever client into per-navmesh-tile geometry JSON. It does not read the client
or scan directories: the input file, MapID and ADT coordinates are explicit.

The input contract is `wow-raw-geometry-v1`, with yard coordinates in
`[WoW_X, WoW_Z, WoW_Y]` order. Each output keeps terrain, combined WMO/M2
collision, and liquid triangle soup in separate arrays. It also records the
client Build, source hash, selected MapID/ADT, tile bounds, and liquid areas.
Terrain and collision are combined only by `ToTileGeometry()` at the boundary
to the existing `PPather.Navmesh.TileGeometry` type.

The exporter preserves whole triangles and assigns them to every 133.33333 yd
tile whose padded bounds intersect their horizontal AABB, matching
`TileGeometryExtractor`. It converts coordinates to `[WoW_Y, WoW_Z, WoW_X]`,
maps liquid area 9 to water and area 8 to magma/slime, and rejects unknown areas
or malformed indices.

Example (run from the repository root):

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- `
  --input tmp/p1-casc-sample/raw-geometry/map0000_adt34_46.rawgeo.json `
  --output tmp/p2-geometry --map 0 --adt 34,46
```

The output directory must be empty. This prevents stale tiles from being
mistaken for results of the selected ADT.

For the staged P3 proof, the tool can also bake exactly one exported Detour
tile with the project's existing `NavmeshTileBuilder`, write the standard
`.dnm` format, read it back, add it to `DtNavMesh`, query its ground height,
check the WoW/Rc coordinate round-trip, and find a path across two connected
polygons:

```powershell
dotnet run --project Utilities/ForeverGeometryTool/ForeverGeometryTool.csproj -c Release -- `
  --input tmp/p1-casc-sample/raw-geometry/map0000_adt34_46.rawgeo.json `
  --output tmp/p3-geometry --map 0 --adt 34,46 `
  --dnm-output tmp/p3-dnm --bake-tile 68,118
```

Both output directories must be empty. `--bake-tile` uses Detour coordinates
`X,Z`, and the tile must be present in the selected one-ADT geometry export.
