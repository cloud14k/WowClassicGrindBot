using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using PPather.Navmesh;

namespace ForeverGeometryTool;

public sealed record GeometryInputFile(string Path, int AdtX, int AdtY);
public readonly record struct AdtCoordinate(int X, int Y);

public sealed record GeometryAggregateRequest(int MapId, IReadOnlyList<GeometryInputFile> Inputs,
    IReadOnlyList<AdtCoordinate> ExpectedAdts,
    float AgentRadius = 0.5f, float AgentMaxClimb = 1.6f,
    float PaddingYards = TileGeometryExtractor.PaddingYd, float WalkableSlope = 48f,
    float? MinWorldZ = null, string? Continent = null);

public sealed record GeometrySourceRecord(
    [property: JsonPropertyName("adt_xy")] int[] AdtXY,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("counts")] GeometryCounts Counts);

public sealed class GeometryAggregate
{
    public required int MapId { get; init; }
    public required string ClientProduct { get; init; }
    public required int ClientBuild { get; init; }
    public required IReadOnlyList<GeometrySourceRecord> Sources { get; init; }
    public required IReadOnlyList<AdtCoordinate> ExpectedAdts { get; init; }
    public required IReadOnlyList<ForeverGeometryTile> Tiles { get; init; }
    public required GeometryDuplicateCounts DuplicateTrianglesRemoved { get; init; }
    public required float AgentRadius { get; init; }
    public required float AgentMaxClimb { get; init; }
    public required float PaddingYards { get; init; }
    public required float WalkableSlope { get; init; }
    public required float? MinWorldZ { get; init; }
    public string? Continent { get; init; }
    public string? SettingsHash { get; init; }
    public int SourceTerrainTriangles => Sources.Sum(static source => source.Counts.TerrainTriangles);
    public int SourceCollisionTriangles => Sources.Sum(static source => source.Counts.CollisionTriangles);
    public int SourceLiquidTriangles => Sources.Sum(static source => source.Counts.LiquidTriangles);
}

/// <summary>
/// Merges explicit, validated raw ADT inputs before any tile is baked. Triangle
/// identity is exact float-bit geometry; no coordinate rounding or winding
/// rewrite is performed. Only exact duplicates within the same geometry class
/// are removed, so vertically separated floors and liquid areas remain distinct.
/// </summary>
public static class ForeverGeometryAggregator
{
    public const int MaximumSourceAdts = 64;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GeometryAggregate Export(GeometryAggregateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MapId < 0 || request.Inputs.Count is < 1 or > MaximumSourceAdts ||
            request.ExpectedAdts.Count is < 1 or > MaximumSourceAdts)
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"Map ID must be non-negative and there must be 1..{MaximumSourceAdts} explicit ADT inputs.");
        }

        if (!float.IsFinite(request.AgentRadius) || request.AgentRadius < 0 ||
            !float.IsFinite(request.AgentMaxClimb) || request.AgentMaxClimb < 0 ||
            !float.IsFinite(request.PaddingYards) || request.PaddingYards < 0 ||
            request.MinWorldZ.HasValue && !float.IsFinite(request.MinWorldZ.Value) ||
            !float.IsFinite(request.WalkableSlope) || request.WalkableSlope is < 0 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Bake and padding settings must be finite and non-negative.");
        }

        GeometryInputFile[] inputs = request.Inputs
            .OrderBy(static input => input.AdtY)
            .ThenBy(static input => input.AdtX)
            .ToArray();
        if (inputs.Select(static input => (input.AdtX, input.AdtY)).Distinct().Count() != inputs.Length)
        {
            throw new InvalidDataException("The same ADT was supplied more than once.");
        }

        AdtCoordinate[] expectedAdts = request.ExpectedAdts.OrderBy(static adt => adt.Y)
            .ThenBy(static adt => adt.X).ToArray();
        if (expectedAdts.Any(static adt => adt.X is < 0 or >= 64 || adt.Y is < 0 or >= 64) ||
            expectedAdts.Distinct().Count() != expectedAdts.Length)
        {
            throw new InvalidDataException("Expected ADT coordinates must be unique values in the 0..63 grid.");
        }

        HashSet<(int X, int Y)> expectedSet = expectedAdts.Select(static adt => (adt.X, adt.Y)).ToHashSet();
        HashSet<(int X, int Y)> actualSet = inputs.Select(static input => (input.AdtX, input.AdtY)).ToHashSet();
        if (!expectedSet.SetEquals(actualSet))
        {
            string missing = string.Join(",", expectedSet.Except(actualSet).OrderBy(static adt => adt.Y).ThenBy(static adt => adt.X)
                .Select(static adt => $"{adt.X},{adt.Y}"));
            string unexpected = string.Join(",", actualSet.Except(expectedSet).OrderBy(static adt => adt.Y).ThenBy(static adt => adt.X)
                .Select(static adt => $"{adt.X},{adt.Y}"));
            throw new InvalidDataException($"Raw ADT inputs do not match the explicit expected set. Missing: [{missing}]. Unexpected: [{unexpected}].");
        }

        List<GeometrySourceRecord> sources = [];
        Dictionary<(int X, int Z), TileAccumulator> merged = [];
        string? expectedProduct = null;
        int? expectedBuild = null;

        foreach (GeometryInputFile input in inputs)
        {
            IReadOnlyList<ForeverGeometryTile> exported = RawGeometryTileExporter.Export(input.Path,
                new GeometryExportRequest(request.MapId, input.AdtX, input.AdtY,
                    request.AgentRadius, request.AgentMaxClimb, request.PaddingYards, request.MinWorldZ));
            ForeverGeometryTile first = exported[0];
            expectedProduct ??= first.ClientProduct;
            expectedBuild ??= first.ClientBuild;
            if (!string.Equals(expectedProduct, first.ClientProduct, StringComparison.Ordinal) || expectedBuild != first.ClientBuild)
            {
                throw new InvalidDataException(
                    $"Mixed CASC identities are not allowed: expected {expectedProduct} Build {expectedBuild}, " +
                    $"received {first.ClientProduct} Build {first.ClientBuild} at ADT ({input.AdtX},{input.AdtY}).");
            }

            GeometrySourceRecord source = new(
                [input.AdtX, input.AdtY], Path.GetFileName(input.Path), first.SourceSha256, first.SourceCounts);
            sources.Add(source);

            foreach (ForeverGeometryTile tile in exported.OrderBy(static tile => tile.DtTileX).ThenBy(static tile => tile.DtTileZ))
            {
                if (!merged.TryGetValue((tile.DtTileX, tile.DtTileZ), out TileAccumulator? accumulator))
                {
                    accumulator = new TileAccumulator(tile);
                    merged.Add((tile.DtTileX, tile.DtTileZ), accumulator);
                }

                accumulator.Add(tile, source);
            }
        }

        if (sources.Count != inputs.Length)
        {
            throw new InvalidDataException("Not every requested ADT produced a validated geometry source record.");
        }

        List<ForeverGeometryTile> tiles = [];
        foreach (KeyValuePair<(int X, int Z), TileAccumulator> entry in merged.OrderBy(static pair => pair.Key.X).ThenBy(static pair => pair.Key.Z))
        {
            tiles.Add(entry.Value.ToTile(request.MapId, expectedProduct!, expectedBuild!.Value));
        }

        GeometryDuplicateCounts duplicates = SumDuplicates(tiles.Select(static tile => tile.DuplicateTrianglesRemoved));
        string? settingsHash = null;
        if (!string.IsNullOrWhiteSpace(request.Continent))
        {
            ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create(request.Continent,
                expectedProduct!, expectedBuild!.Value, new SharedLib.NavmeshBakeOptions
                {
                    AgentRadius = request.AgentRadius,
                    AgentMaxClimb = request.AgentMaxClimb,
                    WalkableSlope = request.WalkableSlope,
                    MinWorldZ = request.MinWorldZ.HasValue
                        ? new Dictionary<string, float?> { [request.Continent] = request.MinWorldZ.Value }
                        : [],
                });
            settingsHash = profile.SettingsHash;
        }

        return new GeometryAggregate
        {
            MapId = request.MapId,
            ClientProduct = expectedProduct!,
            ClientBuild = expectedBuild!.Value,
            Sources = sources,
            ExpectedAdts = expectedAdts,
            Tiles = tiles,
            DuplicateTrianglesRemoved = duplicates,
            AgentRadius = request.AgentRadius,
            AgentMaxClimb = request.AgentMaxClimb,
            PaddingYards = request.PaddingYards,
            WalkableSlope = request.WalkableSlope,
            MinWorldZ = request.MinWorldZ,
            Continent = request.Continent,
            SettingsHash = settingsHash,
        };
    }

    public static void WriteDirectory(string outputDirectory, GeometryAggregate aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(aggregate);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException("Output directory must be empty to prevent mixing tile data from different source sets.");
        }

        foreach (ForeverGeometryTile tile in aggregate.Tiles)
        {
            string fileName = $"map{tile.MapId:D4}_tile_{tile.DtTileX}_{tile.DtTileZ}.geometry.json";
            using FileStream stream = File.Create(Path.Combine(outputDirectory, fileName));
            JsonSerializer.Serialize(stream, tile, JsonOptions);
        }

        var manifest = new
        {
            format = "wow-forever-geometry-manifest-v2",
            map_id = aggregate.MapId,
            client_product = aggregate.ClientProduct,
            client_build = aggregate.ClientBuild,
            continent = aggregate.Continent,
            settings_hash = aggregate.SettingsHash,
            scope = "explicit_adt_set",
            complete_for_requested_sources = true,
            publishable_as_continent_cache = false,
            settings = new
            {
                agent_radius = aggregate.AgentRadius,
                agent_max_climb = aggregate.AgentMaxClimb,
                walkable_slope = aggregate.WalkableSlope,
                min_world_z = aggregate.MinWorldZ,
                padding_yards = aggregate.PaddingYards,
                cell_size = NavmeshSettings.CellSize,
                cell_height = NavmeshSettings.CellHeight,
                tile_world_size = NavmeshSettings.TileWorldSize,
                border_cells = TileGeometryExtractor.BorderCells(aggregate.AgentRadius),
            },
            sources = aggregate.Sources,
            expected_adts_xy = aggregate.ExpectedAdts.Select(static adt => new[] { adt.X, adt.Y }).ToArray(),
            source_counts = new
            {
                terrain_triangles = aggregate.SourceTerrainTriangles,
                collision_triangles = aggregate.SourceCollisionTriangles,
                liquid_triangles = aggregate.SourceLiquidTriangles,
            },
            duplicate_triangles_removed = aggregate.DuplicateTrianglesRemoved,
            tile_count = aggregate.Tiles.Count,
            tiles = aggregate.Tiles.Select(static tile => new
            {
                dt_tile_x = tile.DtTileX,
                dt_tile_z = tile.DtTileZ,
                source_adts_xy = tile.SourceAdtsXY,
                terrain_triangles = tile.TileCounts.TerrainTriangles,
                collision_triangles = tile.TileCounts.CollisionTriangles,
                liquid_triangles = tile.TileCounts.LiquidTriangles,
                duplicate_triangles_removed = tile.DuplicateTrianglesRemoved,
            }).ToArray(),
        };
        using FileStream manifestStream = File.Create(Path.Combine(outputDirectory, "manifest.json"));
        JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
    }

    public static string ComputeSourceSetHash(IEnumerable<GeometrySourceRecord> sources)
    {
        string canonical = string.Join('\n', sources.OrderBy(static source => source.AdtXY[1])
            .ThenBy(static source => source.AdtXY[0])
            .Select(static source => $"{source.AdtXY[0]},{source.AdtXY[1]}:{source.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static GeometryDuplicateCounts SumDuplicates(IEnumerable<GeometryDuplicateCounts> counts)
    {
        GeometryDuplicateCounts[] all = counts.ToArray();
        return new GeometryDuplicateCounts
        {
            Terrain = all.Sum(static count => count.Terrain),
            Collision = all.Sum(static count => count.Collision),
            Liquid = all.Sum(static count => count.Liquid),
        };
    }

    private sealed class TileAccumulator
    {
        private readonly ForeverGeometryTile bounds;
        private readonly List<float> terrainVertices = [];
        private readonly List<int> terrainTriangles = [];
        private readonly List<float> collisionVertices = [];
        private readonly List<int> collisionTriangles = [];
        private readonly List<float> liquidVertices = [];
        private readonly List<int> liquidTriangles = [];
        private readonly List<int> liquidAreas = [];
        private readonly HashSet<TriangleKey> terrainKeys = [];
        private readonly HashSet<TriangleKey> collisionKeys = [];
        private readonly HashSet<TriangleKey> liquidKeys = [];
        private readonly SortedDictionary<(int X, int Y), GeometrySourceRecord> contributors = [];
        private float minRcHeight;
        private float maxRcHeight;
        private int duplicateTerrain;
        private int duplicateCollision;
        private int duplicateLiquid;

        public TileAccumulator(ForeverGeometryTile bounds)
        {
            this.bounds = bounds;
            minRcHeight = bounds.BMinRc[1];
            maxRcHeight = bounds.BMaxRc[1];
        }

        public void Add(ForeverGeometryTile source, GeometrySourceRecord sourceRecord)
        {
            if (!SameHorizontalBounds(bounds, source))
            {
                throw new InvalidDataException($"ADT inputs disagree on Detour tile ({source.DtTileX},{source.DtTileZ}) bounds.");
            }

            minRcHeight = MathF.Min(minRcHeight, source.BMinRc[1]);
            maxRcHeight = MathF.Max(maxRcHeight, source.BMaxRc[1]);
            contributors.TryAdd((sourceRecord.AdtXY[0], sourceRecord.AdtXY[1]), sourceRecord);
            AddTriangles(source.TerrainVerticesRc, source.TerrainTriangles, null,
                terrainVertices, terrainTriangles, null, terrainKeys, ref duplicateTerrain);
            AddTriangles(source.CollisionVerticesRc, source.CollisionTriangles, null,
                collisionVertices, collisionTriangles, null, collisionKeys, ref duplicateCollision);
            AddTriangles(source.LiquidVerticesRc, source.LiquidTriangles, source.LiquidAreas,
                liquidVertices, liquidTriangles, liquidAreas, liquidKeys, ref duplicateLiquid);
        }

        public ForeverGeometryTile ToTile(int mapId, string product, int build)
        {
            GeometrySourceRecord[] sourceList = contributors.Values.ToArray();
            if (sourceList.Length == 0 || terrainTriangles.Count == 0 && collisionTriangles.Count == 0 && liquidTriangles.Count == 0)
            {
                throw new InvalidDataException("An aggregate tile has no geometry sources or triangles.");
            }

            GeometryDuplicateCounts duplicateCounts = new()
            {
                Terrain = duplicateTerrain,
                Collision = duplicateCollision,
                Liquid = duplicateLiquid,
            };
            return new ForeverGeometryTile
            {
                ClientProduct = product,
                ClientBuild = build,
                SourceSha256 = sourceList.Length == 1 ? sourceList[0].Sha256 : ComputeSourceSetHash(sourceList),
                SourceSha256s = sourceList.Select(static source => source.Sha256).ToArray(),
                MapId = mapId,
                AdtXY = sourceList.Length == 1 ? [.. sourceList[0].AdtXY] : [],
                SourceAdtsXY = sourceList.Select(static source => source.AdtXY.ToArray()).ToArray(),
                DtTileX = bounds.DtTileX,
                DtTileZ = bounds.DtTileZ,
                BMinRc = [bounds.BMinRc[0], minRcHeight, bounds.BMinRc[2]],
                BMaxRc = [bounds.BMaxRc[0], maxRcHeight, bounds.BMaxRc[2]],
                SourceCounts = SumCounts(sourceList.Select(static source => source.Counts)),
                TileCounts = new GeometryCounts
                {
                    TerrainTriangles = terrainTriangles.Count / 3,
                    CollisionTriangles = collisionTriangles.Count / 3,
                    WmoCollisionTriangles = null,
                    M2CollisionTriangles = null,
                    LiquidTriangles = liquidTriangles.Count / 3,
                    WaterTriangles = liquidAreas.Count(static area => area == NavmeshSettings.AreaWater),
                    MagmaSlimeTriangles = liquidAreas.Count(static area => area == NavmeshSettings.AreaMagmaSlime),
                },
                DuplicateTrianglesRemoved = duplicateCounts,
                TerrainVerticesRc = [.. terrainVertices],
                TerrainTriangles = [.. terrainTriangles],
                CollisionVerticesRc = [.. collisionVertices],
                CollisionTriangles = [.. collisionTriangles],
                LiquidVerticesRc = [.. liquidVertices],
                LiquidTriangles = [.. liquidTriangles],
                LiquidAreas = [.. liquidAreas],
            };
        }

        private static GeometryCounts SumCounts(IEnumerable<GeometryCounts> source)
        {
            GeometryCounts[] counts = source.ToArray();
            return new GeometryCounts
            {
                TerrainTriangles = counts.Sum(static item => item.TerrainTriangles),
                CollisionTriangles = counts.Sum(static item => item.CollisionTriangles),
                WmoCollisionTriangles = counts.All(static item => item.WmoCollisionTriangles.HasValue)
                    ? counts.Sum(static item => item.WmoCollisionTriangles!.Value) : null,
                M2CollisionTriangles = counts.All(static item => item.M2CollisionTriangles.HasValue)
                    ? counts.Sum(static item => item.M2CollisionTriangles!.Value) : null,
                LiquidTriangles = counts.Sum(static item => item.LiquidTriangles),
                WaterTriangles = counts.Sum(static item => item.WaterTriangles),
                MagmaSlimeTriangles = counts.Sum(static item => item.MagmaSlimeTriangles),
            };
        }

        private static bool SameHorizontalBounds(ForeverGeometryTile left, ForeverGeometryTile right) =>
            left.DtTileX == right.DtTileX && left.DtTileZ == right.DtTileZ &&
            left.BMinRc[0] == right.BMinRc[0] && left.BMinRc[2] == right.BMinRc[2] &&
            left.BMaxRc[0] == right.BMaxRc[0] && left.BMaxRc[2] == right.BMaxRc[2];

        private static void AddTriangles(float[] vertices, int[] triangles, int[]? areas,
            List<float> mergedVertices, List<int> mergedTriangles, List<int>? mergedAreas,
            HashSet<TriangleKey> keys, ref int duplicates)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = ReadVertex(vertices, triangles[i]);
                Vector3 b = ReadVertex(vertices, triangles[i + 1]);
                Vector3 c = ReadVertex(vertices, triangles[i + 2]);
                int area = areas is null ? -1 : areas[i / 3];
                if (!keys.Add(TriangleKey.Create(a, b, c, area)))
                {
                    duplicates++;
                    continue;
                }

                int baseIndex = mergedVertices.Count / 3;
                AppendVertex(mergedVertices, a);
                AppendVertex(mergedVertices, b);
                AppendVertex(mergedVertices, c);
                mergedTriangles.Add(baseIndex);
                mergedTriangles.Add(baseIndex + 1);
                mergedTriangles.Add(baseIndex + 2);
                if (mergedAreas is not null)
                {
                    mergedAreas.Add(area);
                }
            }
        }

        private static Vector3 ReadVertex(float[] vertices, int index) =>
            new(vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2]);

        private static void AppendVertex(List<float> vertices, Vector3 value)
        {
            vertices.Add(value.X);
            vertices.Add(value.Y);
            vertices.Add(value.Z);
        }
    }

    private readonly record struct PointKey(int X, int Y, int Z) : IComparable<PointKey>
    {
        public static PointKey From(Vector3 value) => new(ToBits(value.X), ToBits(value.Y), ToBits(value.Z));

        public int CompareTo(PointKey other)
        {
            int compare = X.CompareTo(other.X);
            if (compare != 0) return compare;
            compare = Y.CompareTo(other.Y);
            return compare != 0 ? compare : Z.CompareTo(other.Z);
        }

        private static int ToBits(float value) => BitConverter.SingleToInt32Bits(value == 0f ? 0f : value);
    }

    private readonly record struct TriangleKey(PointKey A, PointKey B, PointKey C, int Area)
    {
        public static TriangleKey Create(Vector3 a, Vector3 b, Vector3 c, int area)
        {
            PointKey first = PointKey.From(a);
            PointKey second = PointKey.From(b);
            PointKey third = PointKey.From(c);

            // A cyclic rotation preserves the face normal; swapping any pair
            // reverses it. Recast uses that normal for walkable-slope marking,
            // so only compare the three cyclic forms and retain reverse winding.
            (PointKey A, PointKey B, PointKey C) canonical = (first, second, third);
            (PointKey A, PointKey B, PointKey C) rotateOnce = (second, third, first);
            (PointKey A, PointKey B, PointKey C) rotateTwice = (third, first, second);
            if (Compare(rotateOnce, canonical) < 0) canonical = rotateOnce;
            if (Compare(rotateTwice, canonical) < 0) canonical = rotateTwice;
            return new TriangleKey(canonical.A, canonical.B, canonical.C, area);
        }

        private static int Compare((PointKey A, PointKey B, PointKey C) left,
            (PointKey A, PointKey B, PointKey C) right)
        {
            int result = left.A.CompareTo(right.A);
            if (result != 0) return result;
            result = left.B.CompareTo(right.B);
            return result != 0 ? result : left.C.CompareTo(right.C);
        }
    }
}
