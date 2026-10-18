using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;

using PPather.Navmesh;
using SharedLib;

namespace ForeverGeometryTool;

public sealed record BakedTileRecord(
    [property: System.Text.Json.Serialization.JsonPropertyName("dt_tile_x")] int DtTileX,
    [property: System.Text.Json.Serialization.JsonPropertyName("dt_tile_z")] int DtTileZ,
    [property: System.Text.Json.Serialization.JsonPropertyName("file")] string File,
    [property: System.Text.Json.Serialization.JsonPropertyName("sha256")] string Sha256,
    [property: System.Text.Json.Serialization.JsonPropertyName("polygon_count")] int PolygonCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("vertex_count")] int VertexCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("terrain_triangles")] int TerrainTriangles,
    [property: System.Text.Json.Serialization.JsonPropertyName("collision_triangles")] int CollisionTriangles,
    [property: System.Text.Json.Serialization.JsonPropertyName("liquid_triangles")] int LiquidTriangles);

public sealed record BakeVerification(int TileCount, int AddTileCount, int CrossTileLinkCount,
    bool HeightQuerySucceeded, bool ConnectedPathSucceeded, int PathPolyCount,
    float[] StartWow, float[] EndWow, string SettingsHash, IReadOnlyList<BakedTileRecord> Tiles);

public sealed record DnmSetVerification(int TileCount, int AddTileCount, int CrossTileLinkCount,
    bool HeightQuerySucceeded, bool ConnectedPathSucceeded, int PathPolyCount,
    IReadOnlyList<BakedTileRecord> Tiles);

public static class ForeverGeometryBakeVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Independently validates an already-baked explicit DNM set. Used by the
    /// resumable batch tool after per-tile writes and by its separate verify
    /// command; it does not read or bake geometry.
    /// </summary>
    public static DnmSetVerification VerifyDnmFiles(string dnmDirectory,
        IReadOnlyCollection<(int X, int Z)> expectedTiles, bool requireExactFileSet = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dnmDirectory);
        ArgumentNullException.ThrowIfNull(expectedTiles);
        if (!Directory.Exists(dnmDirectory) || expectedTiles.Count == 0 ||
            expectedTiles.Distinct().Count() != expectedTiles.Count)
        {
            throw new InvalidDataException("DNM verification requires an existing directory and a non-empty unique explicit tile set.");
        }

        HashSet<string> expectedFiles = expectedTiles
            .Select(static tile => $"tile_{tile.X}_{tile.Z}.dnm")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actualFiles = Directory.EnumerateFiles(dnmDirectory, "*.dnm")
            .Select(Path.GetFileName).Where(static name => name is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requireExactFileSet && !actualFiles.SetEquals(expectedFiles) ||
            !requireExactFileSet && expectedFiles.Any(file => !actualFiles.Contains(file)))
        {
            throw new InvalidDataException("The DNM output file set does not match the explicit expected tile set.");
        }

        List<(int X, int Z, DtMeshData Data, string File, string Hash)> loaded = [];
        foreach ((int x, int z) in expectedTiles.OrderBy(static item => item.X).ThenBy(static item => item.Z))
        {
            if (!NavmeshCoords.IsValidTile(x, z))
            {
                throw new InvalidDataException($"Invalid Detour tile coordinate ({x},{z}).");
            }

            string file = $"tile_{x}_{z}.dnm";
            string path = Path.Combine(dnmDirectory, file);
            DtMeshData data;
            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new(stream);
                data = new DtMeshDataReader().Read(reader, NavmeshSettings.VertsPerPoly);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new InvalidDataException($"DNM tile is unreadable or malformed: {path}", ex);
            }

            if (data.header.x != x || data.header.y != z || data.header.polyCount <= 0 || data.header.vertCount <= 0)
            {
                throw new InvalidDataException($"DNM header is empty or has the wrong tile identity: {path}");
            }

            loaded.Add((x, z, data, file,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()));
        }

        DtNavMesh navMesh = new();
        DtStatus initStatus = navMesh.Init(new DtNavMeshParams
        {
            orig = new Vector3(NavmeshCoords.Origin, 0, NavmeshCoords.Origin),
            tileWidth = NavmeshSettings.TileWorldSize,
            tileHeight = NavmeshSettings.TileWorldSize,
            maxTiles = NavmeshSettings.MaxTiles,
            maxPolys = NavmeshSettings.MaxPolysPerTile,
        }, NavmeshSettings.VertsPerPoly);
        if (!initStatus.Succeeded())
        {
            throw new InvalidOperationException($"DtNavMesh.Init failed for the explicit DNM set: {initStatus}");
        }

        Dictionary<(int X, int Z), DtMeshTile> meshTiles = [];
        foreach ((int x, int z, DtMeshData data, _, _) in loaded)
        {
            DtStatus addStatus = navMesh.AddTile(data, 0, 0, out long tileRef);
            if (!addStatus.Succeeded() || tileRef == 0)
            {
                throw new InvalidOperationException($"DtNavMesh.AddTile failed for ({x},{z}): {addStatus}");
            }

            meshTiles.Add((x, z), navMesh.GetTileByRef(tileRef)
                ?? throw new InvalidOperationException("DtNavMesh.AddTile succeeded but the tile could not be read back."));
        }

        DtNavMeshQuery query = new(navMesh);
        WowQueryFilter filter = new();
        filter.BeginQuery();
        HashSet<(int X, int Z)> locallyVerifiedTiles = [];
        HashSet<(int Ax, int Az, int Bx, int Bz)> checkedCrossTilePairs = [];
        HashSet<(int Ax, int Az, int Bx, int Bz)> actualPortalPairs = [];
        bool invalidCrossTilePath = false;
        int pathPolyCount = 0;
        int crossTileLinks = 0;
        foreach (DtMeshTile tile in meshTiles.Values)
        {
            long baseRef = navMesh.GetPolyRefBase(tile);
            bool tileHeightSucceeded = false;
            bool tilePathSucceeded = false;
            for (int polyIndex = 0; polyIndex < tile.data.header.polyCount; polyIndex++)
            {
                DtPoly poly = tile.data.polys[polyIndex];
                if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND ||
                    (poly.flags & WowQueryFilter.IncludeFlags) == 0)
                {
                    continue;
                }

                long startRef = baseRef | (long)poly.index;
                Vector3 center = navMesh.GetPolyCenter(startRef);
                if (!tileHeightSucceeded && query.ClosestPointOnPoly(startRef, center, out _, out _).Succeeded())
                {
                    tileHeightSucceeded = true;
                }
                if (!tilePathSucceeded)
                {
                    long[] localPath = new long[4];
                    DtStatus localStatus = query.FindPath(startRef, startRef, center, center, filter,
                        localPath, out int localCount, localPath.Length);
                    if (localStatus.Succeeded() && localCount == 1 && localPath[0] == startRef)
                    {
                        tilePathSucceeded = true;
                        pathPolyCount = Math.Max(pathPolyCount, localCount);
                    }
                }

                for (int linkIndex = poly.firstLink; linkIndex != DtDetour.DT_NULL_LINK; linkIndex = tile.links[linkIndex].next)
                {
                    DtLink link = tile.links[linkIndex];
                    long endRef = link.refs;
                    if (endRef == 0 || endRef == startRef || !navMesh.IsValidPolyRef(endRef) ||
                        !navMesh.GetTileAndPolyByRef(endRef, out DtMeshTile endTile, out _).Succeeded())
                    {
                        continue;
                    }

                    bool crossTile = endTile.data.header.x != tile.data.header.x || endTile.data.header.y != tile.data.header.y;
                    if (crossTile)
                    {
                        crossTileLinks++;
                        if (link.edge < poly.vertCount && link.side is 0 or 2 or 4 or 6)
                        {
                            actualPortalPairs.Add(NormalizeTilePair(tile.data.header.x, tile.data.header.y,
                                endTile.data.header.x, endTile.data.header.y));
                        }
                    }

                    if (!crossTile)
                    {
                        continue;
                    }

                    (int Ax, int Az, int Bx, int Bz) pair = tile.data.header.x < endTile.data.header.x ||
                        tile.data.header.x == endTile.data.header.x && tile.data.header.y < endTile.data.header.y
                        ? (tile.data.header.x, tile.data.header.y, endTile.data.header.x, endTile.data.header.y)
                        : (endTile.data.header.x, endTile.data.header.y, tile.data.header.x, tile.data.header.y);
                    if (!checkedCrossTilePairs.Add(pair)) continue;

                    Vector3 end = navMesh.GetPolyCenter(endRef);
                    long[] path = new long[64];
                    DtStatus status = query.FindPath(startRef, endRef, center, end, filter,
                        path, out int count, path.Length);
                    if (status.Succeeded() && count >= 2 && path[count - 1] == endRef)
                    {
                        pathPolyCount = Math.Max(pathPolyCount, count);
                    }
                    else
                    {
                        invalidCrossTilePath = true;
                    }
                }
            }
            if (tileHeightSucceeded && tilePathSucceeded)
            {
                locallyVerifiedTiles.Add((tile.data.header.x, tile.data.header.y));
            }
        }

        (int Ax, int Az, int Bx, int Bz)[] missingBoundaryLinks = FindExpectedBoundaryLinks(meshTiles)
            .Where(pair => !actualPortalPairs.Contains(pair)).ToArray();
        if (missingBoundaryLinks.Length > 0)
        {
            throw new InvalidDataException("Expected adjacent walkable tile boundaries have no Detour portal link: " +
                string.Join(", ", missingBoundaryLinks.Select(static pair => $"({pair.Ax},{pair.Az})-({pair.Bx},{pair.Bz})")));
        }

        bool heightSucceeded = locallyVerifiedTiles.Count == expectedTiles.Count;
        bool pathSucceeded = heightSucceeded && !invalidCrossTilePath;
        if (!heightSucceeded || !pathSucceeded)
        {
            throw new InvalidDataException(
                $"The DNM set failed local height/path verification for {expectedTiles.Count - locallyVerifiedTiles.Count} tile(s), " +
                $"or an established cross-tile link failed path verification. Disconnected terrain components are permitted.");
        }

        BakedTileRecord[] records = loaded.Select(static item => new BakedTileRecord(
            item.X, item.Z, item.File, item.Hash, item.Data.header.polyCount, item.Data.header.vertCount,
            0, 0, 0)).ToArray();
        return new DnmSetVerification(loaded.Count, loaded.Count, crossTileLinks,
            heightSucceeded, pathSucceeded, pathPolyCount, records);
    }

    public static BakeVerification BakeAndVerify(GeometryAggregate aggregate,
        IReadOnlyCollection<(int X, int Z)> requestedTiles, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (requestedTiles.Count == 0)
        {
            throw new ArgumentException("At least one explicit Detour tile must be selected for baking.", nameof(requestedTiles));
        }

        if (requestedTiles.Distinct().Count() != requestedTiles.Count)
        {
            throw new InvalidDataException("A Detour tile was selected for baking more than once.");
        }

        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException(".dnm output directory must be empty to prevent mixed or overwritten tile sets.");
        }

        Dictionary<(int X, int Z), ForeverGeometryTile> available = aggregate.Tiles
            .ToDictionary(static tile => (tile.DtTileX, tile.DtTileZ));
        List<ForeverGeometryTile> selected = [];
        foreach ((int x, int z) in requestedTiles.OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z))
        {
            if (!available.TryGetValue((x, z), out ForeverGeometryTile? tile))
            {
                throw new InvalidDataException($"Requested tile ({x},{z}) is not present in the aggregated geometry.");
            }

            selected.Add(tile);
        }

        NavmeshBakeOptions bake = new()
        {
            AgentRadius = aggregate.AgentRadius,
            AgentMaxClimb = aggregate.AgentMaxClimb,
            WalkableSlope = aggregate.WalkableSlope,
            MinWorldZ = aggregate.MinWorldZ.HasValue && aggregate.Continent is not null
                ? new Dictionary<string, float?> { [aggregate.Continent] = aggregate.MinWorldZ.Value }
                : [],
        };
        if (string.IsNullOrWhiteSpace(aggregate.Continent))
        {
            throw new InvalidDataException("Baking DNM tiles requires an explicit --continent so the shared Forever bake profile can resolve MinWorldZ and cache identity.");
        }

        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create(aggregate.Continent,
            aggregate.ClientProduct, aggregate.ClientBuild, bake, aggregate.PaddingYards);
        if (!string.Equals(aggregate.SettingsHash, profile.SettingsHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The aggregate settings hash does not match the DNM bake profile; regenerate geometry with the same continent and bake settings.");
        }

        List<(ForeverGeometryTile Source, DtMeshData Data, string Path, string Sha256)> loadedTiles = [];
        foreach (ForeverGeometryTile source in selected)
        {
            TileGeometry geometry = source.ToTileGeometry();
            DtMeshData data = NavmeshTileBuilder.Bake(geometry, source.DtTileX, source.DtTileZ, bake)
                ?? throw new InvalidDataException($"NavmeshTileBuilder returned no polygons for tile ({source.DtTileX},{source.DtTileZ}).");

            string fileName = $"tile_{source.DtTileX}_{source.DtTileZ}.dnm";
            string dnmPath = Path.Combine(outputDirectory, fileName);
            using (FileStream stream = File.Create(dnmPath))
            using (BinaryWriter writer = new(stream))
            {
                new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
            }

            DtMeshData loaded;
            using (FileStream stream = File.OpenRead(dnmPath))
            using (BinaryReader reader = new(stream))
            {
                loaded = new DtMeshDataReader().Read(reader, NavmeshSettings.VertsPerPoly);
            }

            if (loaded.header.x != source.DtTileX || loaded.header.y != source.DtTileZ ||
                loaded.header.polyCount <= 0 || loaded.header.vertCount <= 0)
            {
                throw new InvalidDataException($"Read-back .dnm header mismatch or empty tile at ({source.DtTileX},{source.DtTileZ}).");
            }

            loadedTiles.Add((source, loaded, dnmPath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dnmPath))).ToLowerInvariant()));
        }

        DtNavMesh navMesh = new();
        DtStatus initStatus = navMesh.Init(new DtNavMeshParams
        {
            orig = new Vector3(NavmeshCoords.Origin, 0, NavmeshCoords.Origin),
            tileWidth = NavmeshSettings.TileWorldSize,
            tileHeight = NavmeshSettings.TileWorldSize,
            maxTiles = NavmeshSettings.MaxTiles,
            maxPolys = NavmeshSettings.MaxPolysPerTile,
        }, NavmeshSettings.VertsPerPoly);
        if (!initStatus.Succeeded())
        {
            throw new InvalidOperationException($"DtNavMesh.Init failed after .dnm read-back: {initStatus}");
        }

        Dictionary<(int X, int Z), DtMeshTile> meshTiles = [];
        foreach ((ForeverGeometryTile source, DtMeshData data, _, _) in loadedTiles)
        {
            DtStatus addStatus = navMesh.AddTile(data, 0, 0, out long tileRef);
            if (!addStatus.Succeeded() || tileRef == 0)
            {
                throw new InvalidOperationException($"DtNavMesh.AddTile failed for ({source.DtTileX},{source.DtTileZ}): {addStatus}");
            }

            meshTiles.Add((source.DtTileX, source.DtTileZ), navMesh.GetTileByRef(tileRef)
                ?? throw new InvalidOperationException("DtNavMesh.AddTile succeeded but the tile could not be read back."));
        }

        DtNavMeshQuery query = new(navMesh);
        WowQueryFilter filter = new();
        filter.BeginQuery();
        HashSet<(int X, int Z)> locallyVerifiedTiles = [];
        HashSet<(int Ax, int Az, int Bx, int Bz)> checkedCrossTilePairs = [];
        HashSet<(int Ax, int Az, int Bx, int Bz)> actualPortalPairs = [];
        bool invalidCrossTilePath = false;
        int pathPolyCount = 0;
        int crossTileLinks = 0;
        Vector3 startWow = Vector3.Zero;
        Vector3 endWow = Vector3.Zero;

        foreach (KeyValuePair<(int X, int Z), DtMeshTile> entry in meshTiles)
        {
            DtMeshTile tile = entry.Value;
            long baseRef = navMesh.GetPolyRefBase(tile);
            bool tileHeightSucceeded = false;
            bool tilePathSucceeded = false;
            for (int polyIndex = 0; polyIndex < tile.data.header.polyCount; polyIndex++)
            {
                DtPoly poly = tile.data.polys[polyIndex];
                if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND ||
                    (poly.flags & WowQueryFilter.IncludeFlags) == 0)
                {
                    continue;
                }

                long startRef = baseRef | (long)poly.index;
                Vector3 center = navMesh.GetPolyCenter(startRef);
                if (!tileHeightSucceeded && query.ClosestPointOnPoly(startRef, center, out Vector3 surface, out _).Succeeded())
                {
                    Vector3 wow = NavmeshCoords.ToWow(surface);
                    if (Vector3.Distance(NavmeshCoords.ToRc(wow), surface) > 0.01f)
                    {
                        throw new InvalidDataException("Rc/WoW coordinate round-trip failed on a loaded .dnm tile.");
                    }

                    startWow = wow;
                    tileHeightSucceeded = true;
                }
                if (!tilePathSucceeded)
                {
                    long[] localPath = new long[4];
                    DtStatus localStatus = query.FindPath(startRef, startRef, center, center,
                        filter, localPath, out int localCount, localPath.Length);
                    if (localStatus.Succeeded() && localCount == 1 && localPath[0] == startRef)
                    {
                        tilePathSucceeded = true;
                        pathPolyCount = Math.Max(pathPolyCount, localCount);
                    }
                }

                for (int linkIndex = poly.firstLink; linkIndex != DtDetour.DT_NULL_LINK; linkIndex = tile.links[linkIndex].next)
                {
                    DtLink link = tile.links[linkIndex];
                    long endRef = link.refs;
                    if (endRef == 0 || endRef == startRef || !navMesh.IsValidPolyRef(endRef))
                    {
                        continue;
                    }

                    if (!navMesh.GetTileAndPolyByRef(endRef, out DtMeshTile endTile, out _).Succeeded())
                    {
                        continue;
                    }

                    bool crossTile = endTile.data.header.x != tile.data.header.x || endTile.data.header.y != tile.data.header.y;
                    if (crossTile)
                    {
                        crossTileLinks++;
                        if (link.edge < poly.vertCount && link.side is 0 or 2 or 4 or 6)
                        {
                            actualPortalPairs.Add(NormalizeTilePair(tile.data.header.x, tile.data.header.y,
                                endTile.data.header.x, endTile.data.header.y));
                        }
                    }

                    if (!crossTile)
                    {
                        continue;
                    }

                    (int Ax, int Az, int Bx, int Bz) pair = tile.data.header.x < endTile.data.header.x ||
                        tile.data.header.x == endTile.data.header.x && tile.data.header.y < endTile.data.header.y
                        ? (tile.data.header.x, tile.data.header.y, endTile.data.header.x, endTile.data.header.y)
                        : (endTile.data.header.x, endTile.data.header.y, tile.data.header.x, tile.data.header.y);
                    if (!checkedCrossTilePairs.Add(pair)) continue;

                    Vector3 endCenter = navMesh.GetPolyCenter(endRef);
                    long[] path = new long[64];
                    DtStatus pathStatus = query.FindPath(startRef, endRef, center, endCenter,
                        filter, path, out int count, path.Length);
                    if (pathStatus.Succeeded() && count >= 2 && path[count - 1] == endRef)
                    {
                        Vector3 rcStart = navMesh.GetPolyCenter(startRef);
                        Vector3 rcEnd = navMesh.GetPolyCenter(endRef);
                        startWow = NavmeshCoords.ToWow(rcStart);
                        endWow = NavmeshCoords.ToWow(rcEnd);
                        pathPolyCount = Math.Max(pathPolyCount, count);
                    }
                    else
                    {
                        invalidCrossTilePath = true;
                    }
                }
            }
            if (tileHeightSucceeded && tilePathSucceeded)
            {
                locallyVerifiedTiles.Add(entry.Key);
            }
        }

        (int Ax, int Az, int Bx, int Bz)[] missingBoundaryLinks = FindExpectedBoundaryLinks(meshTiles)
            .Where(pair => !actualPortalPairs.Contains(pair)).ToArray();
        if (missingBoundaryLinks.Length > 0)
        {
            throw new InvalidDataException("Expected adjacent walkable tile boundaries have no Detour portal link: " +
                string.Join(", ", missingBoundaryLinks.Select(static pair => $"({pair.Ax},{pair.Az})-({pair.Bx},{pair.Bz})")));
        }

        bool heightQuerySucceeded = locallyVerifiedTiles.Count == loadedTiles.Count;
        bool pathSucceeded = heightQuerySucceeded && !invalidCrossTilePath;
        if (!heightQuerySucceeded || !pathSucceeded)
        {
            throw new InvalidDataException(
                $"The loaded .dnm set failed local height/path verification for {loadedTiles.Count - locallyVerifiedTiles.Count} tile(s), " +
                "or an established cross-tile link failed path verification. Disconnected terrain components are permitted.");
        }

        string settingsHash = profile.SettingsHash;
        BakedTileRecord[] records = loadedTiles.Select(static item => new BakedTileRecord(
            item.Source.DtTileX, item.Source.DtTileZ, Path.GetFileName(item.Path), item.Sha256,
            item.Data.header.polyCount, item.Data.header.vertCount,
            item.Source.TileCounts.TerrainTriangles, item.Source.TileCounts.CollisionTriangles,
            item.Source.TileCounts.LiquidTriangles)).ToArray();
        var manifest = new
        {
            format = ForeverNavmeshCacheManifest.Format,
            client_product = aggregate.ClientProduct,
            client_build = aggregate.ClientBuild,
            data_set_id = profile.DataSetIdentity,
            settings_hash = settingsHash,
            map_id = aggregate.MapId,
            continent = profile.Continent,
            scope = "explicit_adt_and_tile_set",
            complete_for_requested_sources_and_tiles = true,
            publishable_as_continent_cache = false,
            source_adts = aggregate.Sources,
            duplicate_triangles_removed = aggregate.DuplicateTrianglesRemoved,
            bake_settings = new
            {
                agent_radius = bake.AgentRadius,
                agent_max_climb = bake.AgentMaxClimb,
                walkable_slope = bake.WalkableSlope,
                min_world_z = profile.MinWorldZ,
                cell_size = NavmeshSettings.CellSize,
                cell_height = NavmeshSettings.CellHeight,
                tile_world_size = NavmeshSettings.TileWorldSize,
                verts_per_poly = NavmeshSettings.VertsPerPoly,
                format_version = NavmeshSettings.FormatVersion,
                dot_recast_version = profile.DotRecastVersion,
                padding_yards = profile.PaddingYards,
                geometry_export_version = profile.GeometryExportVersion,
                raw_geometry_format = profile.RawGeometryFormat,
            },
            query_verification = new
            {
                add_tile_count = loadedTiles.Count,
                cross_tile_link_count = crossTileLinks,
                height_query_succeeded = heightQuerySucceeded,
                path_succeeded = pathSucceeded,
                path_polygon_count = pathPolyCount,
                start_wow = new[] { startWow.X, startWow.Y, startWow.Z },
                end_wow = new[] { endWow.X, endWow.Y, endWow.Z },
            },
            tiles = records,
        };
        using FileStream manifestStream = File.Create(Path.Combine(outputDirectory, "manifest.json"));
        JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);

        return new BakeVerification(loadedTiles.Count, loadedTiles.Count, crossTileLinks,
            heightQuerySucceeded, pathSucceeded, pathPolyCount,
            [startWow.X, startWow.Y, startWow.Z], [endWow.X, endWow.Y, endWow.Z], settingsHash, records);
    }

    private static IEnumerable<(int Ax, int Az, int Bx, int Bz)> FindExpectedBoundaryLinks(
        Dictionary<(int X, int Z), DtMeshTile> tiles)
    {
        foreach (((int x, int z), DtMeshTile tile) in tiles.OrderBy(static item => item.Key.X).ThenBy(static item => item.Key.Z))
        {
            if (tiles.TryGetValue((x + 1, z), out DtMeshTile? xNeighbor) &&
                HasMatchingPortalEdges(tile, xNeighbor, sideA: 0, sideB: 4))
            {
                yield return (x, z, x + 1, z);
            }

            if (tiles.TryGetValue((x, z + 1), out DtMeshTile? zNeighbor) &&
                HasMatchingPortalEdges(tile, zNeighbor, sideA: 2, sideB: 6))
            {
                yield return (x, z, x, z + 1);
            }
        }
    }

    private static (int Ax, int Az, int Bx, int Bz) NormalizeTilePair(int ax, int az, int bx, int bz) =>
        ax < bx || ax == bx && az < bz ? (ax, az, bx, bz) : (bx, bz, ax, az);

    private static bool HasMatchingPortalEdges(DtMeshTile first, DtMeshTile second, int sideA, int sideB)
    {
        List<BoundarySlab> firstSlabs = ReadPortalSlabs(first, sideA);
        List<BoundarySlab> secondSlabs = ReadPortalSlabs(second, sideB);
        float walkableClimb = Math.Max(first.data.header.walkableClimb, second.data.header.walkableClimb);
        foreach (BoundarySlab a in firstSlabs)
        foreach (BoundarySlab b in secondSlabs)
        {
            if (SlabsOverlap(a, b, walkableClimb)) return true;
        }
        return false;
    }

    private static List<BoundarySlab> ReadPortalSlabs(DtMeshTile tile, int side)
    {
        List<BoundarySlab> slabs = [];
        foreach (DtPoly poly in tile.data.polys)
        {
            if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND ||
                (poly.flags & WowQueryFilter.IncludeFlags) == 0) continue;
            for (int edge = 0; edge < poly.vertCount; edge++)
            {
                if ((poly.neis[edge] & DtDetour.DT_EXT_LINK) == 0 ||
                    (poly.neis[edge] & 0xff) != side ||
                    !TryReadBoundarySlab(tile.data.verts, poly, edge, side, out BoundarySlab slab)) continue;
                slabs.Add(slab);
            }
        }
        return slabs;
    }

    private static bool TryReadBoundarySlab(float[] vertices, DtPoly poly, int edge, int side,
        out BoundarySlab slab)
    {
        int start = poly.verts[edge] * 3;
        int end = poly.verts[(edge + 1) % poly.vertCount] * 3;
        bool xPlane = side is 0 or 4;
        float planeA = vertices[start + (xPlane ? 0 : 2)];
        float planeB = vertices[end + (xPlane ? 0 : 2)];
        float tangentA = vertices[start + (xPlane ? 2 : 0)];
        float tangentB = vertices[end + (xPlane ? 2 : 0)];
        float heightA = vertices[start + 1];
        float heightB = vertices[end + 1];
        if (!float.IsFinite(planeA) || !float.IsFinite(planeB) || !float.IsFinite(tangentA) ||
            !float.IsFinite(tangentB) || !float.IsFinite(heightA) || !float.IsFinite(heightB) ||
            MathF.Abs(planeA - planeB) > 0.01f || MathF.Abs(tangentA - tangentB) < 1e-6f)
        {
            slab = default;
            return false;
        }

        slab = new BoundarySlab(planeA, tangentA, tangentB, heightA, heightB);
        return true;
    }

    private static bool SlabsOverlap(BoundarySlab a, BoundarySlab b, float walkableClimb)
    {
        if (MathF.Abs(a.Plane - b.Plane) > 0.01f) return false;
        float minT = Math.Max(Math.Min(a.T0, a.T1) + 0.01f, Math.Min(b.T0, b.T1) + 0.01f);
        float maxT = Math.Min(Math.Max(a.T0, a.T1) - 0.01f, Math.Max(b.T0, b.T1) - 0.01f);
        if (minT > maxT) return false;

        float deltaMin = Interpolate(b, minT) - Interpolate(a, minT);
        float deltaMax = Interpolate(b, maxT) - Interpolate(a, maxT);
        return deltaMin * deltaMax < 0 || MathF.Abs(deltaMin) <= walkableClimb * 2f ||
               MathF.Abs(deltaMax) <= walkableClimb * 2f;
    }

    private static float Interpolate(BoundarySlab slab, float tangent)
    {
        float t = (tangent - slab.T0) / (slab.T1 - slab.T0);
        return slab.Y0 + (slab.Y1 - slab.Y0) * t;
    }

    private readonly record struct BoundarySlab(float Plane, float T0, float T1, float Y0, float Y1);
}
