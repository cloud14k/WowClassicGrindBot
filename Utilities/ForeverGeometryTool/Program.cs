using System.Globalization;
using System.Numerics;
using System.Text.Json;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;

using PPather.Navmesh;
using SharedLib;

namespace ForeverGeometryTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            Dictionary<string, string> values = ParseArgs(args);
            string input = Required(values, "--input");
            string output = Required(values, "--output");
            int map = int.Parse(Required(values, "--map"), CultureInfo.InvariantCulture);
            (int adtX, int adtY) = ParseAdt(Required(values, "--adt"));
            bool hasDnmOutput = values.TryGetValue("--dnm-output", out string? dnmOutput);
            bool hasBakeTile = values.TryGetValue("--bake-tile", out string? bakeTileValue);
            if (hasDnmOutput != hasBakeTile)
            {
                throw new ArgumentException("--dnm-output and --bake-tile must be supplied together; P3 currently limits baking to one tile.");
            }

            IReadOnlyList<ForeverGeometryTile> tiles = RawGeometryTileExporter.Export(input,
                new GeometryExportRequest(map, adtX, adtY));
            RawGeometryTileExporter.WriteDirectory(output, tiles);

            BakeVerification? bakeVerification = null;
            if (hasDnmOutput)
            {
                (int tileX, int tileZ) = ParseTile(bakeTileValue!);
                ForeverGeometryTile tile = tiles.SingleOrDefault(t => t.DtTileX == tileX && t.DtTileZ == tileZ)
                    ?? throw new InvalidDataException($"Requested bake tile ({tileX},{tileZ}) is not present in the selected MapID/ADT geometry.");
                bakeVerification = BakeWriteReadAndQuery(tile, dnmOutput!);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "PASS",
                map_id = map,
                adt_xy = new[] { adtX, adtY },
                input_file = Path.GetFullPath(input),
                output_directory = Path.GetFullPath(output),
                tile_count = tiles.Count,
                terrain_triangles = tiles.Sum(t => t.TileCounts.TerrainTriangles),
                collision_triangles = tiles.Sum(t => t.TileCounts.CollisionTriangles),
                liquid_triangles = tiles.Sum(t => t.TileCounts.LiquidTriangles),
                client_product = tiles[0].ClientProduct,
                client_build = tiles[0].ClientBuild,
                baked_tile = bakeVerification,
            }, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ForeverGeometryTool: {ex.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name is not ("--input" or "--output" or "--map" or "--adt" or "--dnm-output" or "--bake-tile") || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown or incomplete option '{name}'.");
            }

            if (!result.TryAdd(name, args[++i]))
            {
                throw new ArgumentException($"Option '{name}' was specified more than once.");
            }
        }

        return result;
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '{name}' is missing.");

    private static (int X, int Y) ParseAdt(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("--adt must be X,Y (for example 34,46).");
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static (int X, int Z) ParseTile(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("--bake-tile must be Detour X,Z (for example 68,118).");
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static BakeVerification BakeWriteReadAndQuery(ForeverGeometryTile source, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException(".dnm output directory must be empty to prevent mixing tiles from different runs.");
        }

        TileGeometry geometry = source.ToTileGeometry();
        DtMeshData data = NavmeshTileBuilder.Bake(geometry, source.DtTileX, source.DtTileZ)
            ?? throw new InvalidDataException("NavmeshTileBuilder returned no polygons for the selected geometry tile.");

        string dnmPath = Path.Combine(outputDirectory, $"tile_{source.DtTileX}_{source.DtTileZ}.dnm");
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
            throw new InvalidDataException("Read-back .dnm tile header does not match the requested tile or has no mesh data.");
        }

        DtNavMesh navMesh = new();
        DtNavMeshParams navParams = new()
        {
            orig = new Vector3(NavmeshCoords.Origin, 0, NavmeshCoords.Origin),
            tileWidth = NavmeshSettings.TileWorldSize,
            tileHeight = NavmeshSettings.TileWorldSize,
            maxTiles = NavmeshSettings.MaxTiles,
            maxPolys = NavmeshSettings.MaxPolysPerTile,
        };
        DtStatus initStatus = navMesh.Init(navParams, NavmeshSettings.VertsPerPoly);
        if (!initStatus.Succeeded())
        {
            throw new InvalidOperationException($"DtNavMesh.Init failed for the .dnm read-back: {initStatus}");
        }

        DtStatus addStatus = navMesh.AddTile(loaded, 0, 0, out long tileRef);
        if (!addStatus.Succeeded() || tileRef == 0)
        {
            throw new InvalidOperationException($"DtNavMesh.AddTile failed for the .dnm read-back: {addStatus}");
        }

        DtMeshTile meshTile = navMesh.GetTileByRef(tileRef)
            ?? throw new InvalidOperationException("DtNavMesh.AddTile succeeded but the tile reference could not be read back.");
        DtNavMeshQuery query = new(navMesh);
        WowQueryFilter filter = new();
        filter.BeginQuery();
        long baseRef = navMesh.GetPolyRefBase(meshTile);

        for (int polyIndex = 0; polyIndex < meshTile.data.header.polyCount; polyIndex++)
        {
            DtPoly poly = meshTile.data.polys[polyIndex];
            if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND || (poly.flags & WowQueryFilter.IncludeFlags) == 0)
            {
                continue;
            }

            long startRef = baseRef | (long)poly.index;
            Vector3 center = navMesh.GetPolyCenter(startRef);
            DtStatus nearestStatus = query.FindNearestPoly(center, new Vector3(2, 8, 2), filter,
                out long resolvedStartRef, out var resolvedStart, out _);
            if (!nearestStatus.Succeeded() || resolvedStartRef != startRef)
            {
                continue;
            }

            DtStatus heightStatus = query.ClosestPointOnPoly(startRef, center, out var surface, out _);
            if (!heightStatus.Succeeded())
            {
                continue;
            }

            Vector3 wowPosition = NavmeshCoords.ToWow(surface);
            Vector3 roundTrip = NavmeshCoords.ToRc(wowPosition);
            if (Vector3.Distance(roundTrip, surface) > 0.01f)
            {
                throw new InvalidDataException("Rc/WoW coordinate round-trip failed on the loaded .dnm tile.");
            }

            for (int linkIndex = poly.firstLink; linkIndex != DtDetour.DT_NULL_LINK; linkIndex = meshTile.links[linkIndex].next)
            {
                long endRef = meshTile.links[linkIndex].refs;
                if (endRef == 0 || endRef == startRef || !navMesh.IsValidPolyRef(endRef))
                {
                    continue;
                }

                Vector3 endCenter = navMesh.GetPolyCenter(endRef);
                long[] path = new long[32];
                DtStatus pathStatus = query.FindPath(startRef, endRef, resolvedStart, endCenter,
                    filter, path, out int pathCount, path.Length);
                if (pathStatus.Succeeded() && pathCount >= 2 && path[pathCount - 1] == endRef)
                {
                    return new BakeVerification
                    {
                        File = Path.GetFullPath(dnmPath),
                        PolyCount = loaded.header.polyCount,
                        VertexCount = loaded.header.vertCount,
                        AddTileSucceeded = true,
                        HeightQuerySucceeded = true,
                        PathSucceeded = true,
                        PathPolyCount = pathCount,
                        StartWow = [wowPosition.X, wowPosition.Y, wowPosition.Z],
                        HeightWow = wowPosition.Z,
                    };
                }
            }
        }

        throw new InvalidDataException("The .dnm tile loaded, but no connected two-polygon path and ground-height query could be found.");
    }

    private sealed class BakeVerification
    {
        public required string File { get; init; }
        public required int PolyCount { get; init; }
        public required int VertexCount { get; init; }
        public required bool AddTileSucceeded { get; init; }
        public required bool HeightQuerySucceeded { get; init; }
        public required bool PathSucceeded { get; init; }
        public required int PathPolyCount { get; init; }
        public required float[] StartWow { get; init; }
        public required float HeightWow { get; init; }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ForeverGeometryTool --input <one rawgeo.json> --output <empty directory> --map <MapID> --adt <X,Y> [--dnm-output <empty directory> --bake-tile <X,Z>]");
        Console.WriteLine("Exports only the selected MapID/ADT file into per-navmesh-tile geometry JSON.");
        Console.WriteLine("Optional P3 mode bakes and verifies exactly one selected tile as a standard .dnm file.");
    }
}
