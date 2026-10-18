using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;

using PPather.Navmesh;

namespace ForeverGeometryTool;

public sealed record GeometryExportRequest(int MapId, int AdtX, int AdtY, float AgentRadius = 0.5f,
    float AgentMaxClimb = 1.6f,
    float PaddingYards = TileGeometryExtractor.PaddingYd,
    float? MinWorldZ = null);

/// <summary>
/// Converts one raw Forever ADT export into per-tile, rc-space triangle soup.
/// It follows TileGeometryExtractor's horizontal AABB padding and tile bounds.
/// </summary>
public static class RawGeometryTileExporter
{
    private const string ExpectedInputFormat = "wow-raw-geometry-v1";
    private const string ExpectedCoordinates = "[WoW_X,WoW_Z,WoW_Y]";
    private const string ExpectedTransform = "[raw_Z,raw_Y,raw_X]";
    private const int MaximumTilesPerInput = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    public static IReadOnlyList<ForeverGeometryTile> Export(string inputPath, GeometryExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Raw geometry input was not found.", inputPath);
        }

        if (request.MapId < 0 || request.AdtX is < 0 or >= 64 || request.AdtY is < 0 or >= 64)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Map ID and ADT coordinates must be valid non-negative client coordinates.");
        }

        if (!float.IsFinite(request.AgentRadius) || request.AgentRadius < 0 ||
            !float.IsFinite(request.AgentMaxClimb) || request.AgentMaxClimb < 0 ||
            !float.IsFinite(request.PaddingYards) || request.PaddingYards < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Bake radius, climb and geometry padding must be finite and non-negative.");
        }

        RawGeometryDocument raw;
        using (FileStream stream = File.OpenRead(inputPath))
        {
            raw = JsonSerializer.Deserialize<RawGeometryDocument>(stream)
                  ?? throw new InvalidDataException("Raw geometry JSON was empty.");
        }

        ValidateInput(raw, request);
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))).ToLowerInvariant();
        GeometryCounts sourceCounts = new()
        {
            TerrainTriangles = raw.TerrainTriangleCount,
            CollisionTriangles = raw.CollisionTriangleCount,
            WmoCollisionTriangles = raw.WmoTriangleCount,
            M2CollisionTriangles = raw.M2TriangleCount,
            LiquidTriangles = raw.LiquidTriangleCount,
            WaterTriangles = raw.LiquidTypes.Count(static type => type == NavmeshSettings.AreaWater),
            MagmaSlimeTriangles = raw.LiquidTypes.Count(static type => type == NavmeshSettings.AreaMagmaSlime),
        };

        float border = (TileGeometryExtractor.BorderCells(request.AgentRadius) * NavmeshSettings.CellSize) + request.PaddingYards;
        SortedDictionary<(int X, int Z), TileAccumulator> tiles = [];

        AddTriangles(raw.TerrainVertices, raw.TerrainTriangles, null, GeometryKind.Terrain, border, request.MinWorldZ, tiles);
        AddTriangles(raw.CollisionVertices, raw.CollisionTriangles, null, GeometryKind.Collision, border, request.MinWorldZ, tiles);
        AddTriangles(raw.LiquidVertices, raw.LiquidTriangles, raw.LiquidTypes, GeometryKind.Liquid, border, request.MinWorldZ, tiles);

        if (tiles.Count > MaximumTilesPerInput)
        {
            throw new InvalidDataException($"One ADT input expanded to {tiles.Count} navmesh tiles; refusing an unexpectedly broad export (limit {MaximumTilesPerInput}).");
        }

        List<ForeverGeometryTile> result = [];
        foreach (KeyValuePair<(int X, int Z), TileAccumulator> entry in tiles)
        {
            int tileX = entry.Key.X;
            int tileZ = entry.Key.Z;
            TileAccumulator tile = entry.Value;
            if (tile.TerrainTris.Count == 0 && tile.CollisionTris.Count == 0 && tile.LiquidTris.Count == 0)
            {
                continue;
            }

            NavmeshCoords.GetTileWowBounds(tileX, tileZ,
                out float tileMinX, out float tileMinY, out float tileMaxX, out float tileMaxY);

            float minHeight = tile.MinHeight == float.MaxValue ? 0 : tile.MinHeight;
            float maxHeight = tile.MaxHeight == float.MinValue ? 1 : tile.MaxHeight;
            var bminRc = NavmeshCoords.ToRc(new System.Numerics.Vector3(
                tileMinX, tileMinY, minHeight - request.AgentMaxClimb));
            var bmaxRc = NavmeshCoords.ToRc(new System.Numerics.Vector3(
                tileMaxX, tileMaxY, maxHeight + NavmeshSettings.AgentHeight));

            result.Add(new ForeverGeometryTile
            {
                ClientProduct = raw.ClientProduct,
                ClientBuild = raw.ClientBuild,
                SourceSha256 = sourceHash,
                MapId = raw.MapId,
                AdtXY = [raw.AdtXY[0], raw.AdtXY[1]],
                DtTileX = tileX,
                DtTileZ = tileZ,
                BMinRc = [bminRc.X, bminRc.Y, bminRc.Z],
                BMaxRc = [bmaxRc.X, bmaxRc.Y, bmaxRc.Z],
                SourceCounts = sourceCounts,
                TileCounts = tile.GetCounts(),
                TerrainVerticesRc = [.. tile.TerrainVerts],
                TerrainTriangles = [.. tile.TerrainTris],
                CollisionVerticesRc = [.. tile.CollisionVerts],
                CollisionTriangles = [.. tile.CollisionTris],
                LiquidVerticesRc = [.. tile.LiquidVerts],
                LiquidTriangles = [.. tile.LiquidTris],
                LiquidAreas = [.. tile.LiquidAreas],
            });
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("The selected raw geometry contained no ground triangles in valid navmesh tiles.");
        }

        return result;
    }

    private static void ValidateInput(RawGeometryDocument raw, GeometryExportRequest request)
    {
        if (raw.Format != ExpectedInputFormat || raw.Units != "yard" ||
            raw.Coordinates != ExpectedCoordinates || raw.TargetRcTransform != ExpectedTransform)
        {
            throw new InvalidDataException("Input format, units, or coordinate transform does not match the P1 raw-geometry contract.");
        }

        if (raw.ClientProduct != "wow_cn_beta" || raw.ClientBuild <= 0)
        {
            throw new InvalidDataException($"Expected an identified Forever CASC client; received '{raw.ClientProduct}' build {raw.ClientBuild}.");
        }

        if (raw.MapId != request.MapId || raw.AdtXY.Length != 2 || raw.MmapXY.Length != 2 ||
            raw.AdtXY[0] != request.AdtX || raw.AdtXY[1] != request.AdtY ||
            raw.MmapXY[0] != request.AdtY || raw.MmapXY[1] != request.AdtX)
        {
            throw new InvalidDataException($"Selected filter MapID {request.MapId} / ADT ({request.AdtX},{request.AdtY}) does not match the input document.");
        }

        if (raw.TerrainVertexCount != raw.TerrainVertices.Length ||
            raw.TerrainTriangleCount != raw.TerrainTriangles.Length ||
            raw.CollisionVertexCount != raw.CollisionVertices.Length ||
            raw.CollisionTriangleCount != raw.CollisionTriangles.Length ||
            raw.WmoTriangleCount + raw.M2TriangleCount != raw.CollisionTriangleCount ||
            raw.LiquidVertexCount != raw.LiquidVertices.Length ||
            raw.LiquidTriangleCount != raw.LiquidTriangles.Length ||
            raw.LiquidTypeCount != raw.LiquidTypes.Length ||
            raw.LiquidTypes.Length != raw.LiquidTriangles.Length)
        {
            throw new InvalidDataException("Input count metadata does not match the geometry arrays.");
        }

        ValidateGeometry(raw.TerrainVertices, raw.TerrainTriangles, "terrain");
        ValidateGeometry(raw.CollisionVertices, raw.CollisionTriangles, "collision");
        ValidateGeometry(raw.LiquidVertices, raw.LiquidTriangles, "liquid");
        ValidateTerrainBounds(raw, request);

        for (int i = 0; i < raw.LiquidTypes.Length; i++)
        {
            if (raw.LiquidTypes[i] is not (NavmeshSettings.AreaWater or NavmeshSettings.AreaMagmaSlime))
            {
                throw new InvalidDataException($"Liquid triangle {i} has unsupported area {raw.LiquidTypes[i]}; refusing to classify it silently.");
            }
        }
    }

    private static void ValidateTerrainBounds(RawGeometryDocument raw, GeometryExportRequest request)
    {
        const float toleranceYards = 0.05f;
        float adtSize = NavmeshSettings.TileWorldSize * 4f;
        float minX = (31 - request.AdtX) * adtSize;
        float maxX = (32 - request.AdtX) * adtSize;
        float minY = (31 - request.AdtY) * adtSize;
        float maxY = (32 - request.AdtY) * adtSize;
        foreach (float[] vertex in raw.TerrainVertices)
        {
            if (vertex[0] < minX - toleranceYards || vertex[0] > maxX + toleranceYards ||
                vertex[2] < minY - toleranceYards || vertex[2] > maxY + toleranceYards)
            {
                throw new InvalidDataException(
                    $"Terrain vertex ({vertex[0]:F3},{vertex[2]:F3}) lies outside requested ADT ({request.AdtX},{request.AdtY}) " +
                    $"world bounds X[{minX:F3},{maxX:F3}] Y[{minY:F3},{maxY:F3}].");
            }
        }
    }

    private static void ValidateGeometry(float[][] vertices, int[][] triangles, string name)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i] is not { Length: 3 } || vertices[i].Any(static value => !float.IsFinite(value)))
            {
                throw new InvalidDataException($"{name} vertex {i} is malformed or non-finite.");
            }

            if (vertices[i].Any(static value => MathF.Abs(value) > 30000f))
            {
                throw new InvalidDataException($"{name} vertex {i} is outside the supported WoW world coordinate range.");
            }
        }

        for (int i = 0; i < triangles.Length; i++)
        {
            int[] triangle = triangles[i];
            if (triangle is not { Length: 3 } || triangle.Any(index => index < 0 || index >= vertices.Length))
            {
                throw new InvalidDataException($"{name} triangle {i} has malformed or out-of-range indices.");
            }
        }
    }

    private static void AddTriangles(float[][] sourceVertices, int[][] sourceTriangles, int[]? liquidTypes,
        GeometryKind kind, float border, float? minWorldZ, SortedDictionary<(int X, int Z), TileAccumulator> tiles)
    {
        for (int triIndex = 0; triIndex < sourceTriangles.Length; triIndex++)
        {
            int[] tri = sourceTriangles[triIndex];
            // Source order is [WoW_X, WoW_Z, WoW_Y]; target rc is [WoW_Y, WoW_Z, WoW_X].
            Vector3 v0 = ToRc(sourceVertices[tri[0]]);
            Vector3 v1 = ToRc(sourceVertices[tri[1]]);
            Vector3 v2 = ToRc(sourceVertices[tri[2]]);

            if (minWorldZ is float floorZ && MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < floorZ)
            {
                continue;
            }

            float minWowX = MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z));
            float maxWowX = MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z));
            float minWowY = MathF.Min(v0.X, MathF.Min(v1.X, v2.X));
            float maxWowY = MathF.Max(v0.X, MathF.Max(v1.X, v2.X));
            int minTileX = CandidateMin(minWowY, border);
            int maxTileX = CandidateMax(maxWowY, border);
            int minTileZ = CandidateMin(minWowX, border);
            int maxTileZ = CandidateMax(maxWowX, border);

            long candidateCount = ((long)maxTileX - minTileX + 1) * ((long)maxTileZ - minTileZ + 1);
            if (candidateCount > MaximumTilesPerInput)
            {
                throw new InvalidDataException($"A {kind} triangle spans {candidateCount} candidate tiles; refusing unexpectedly broad geometry.");
            }

            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                if (tileX < 0 || tileX >= NavmeshSettings.TilesPerSide)
                {
                    continue;
                }

                NavmeshCoords.GetTileWowBounds(tileX, 0, out _, out float tileMinY, out _, out float tileMaxY);
                if (minWowY > tileMaxY + border || maxWowY < tileMinY - border)
                {
                    continue;
                }

                for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
                {
                    if (tileZ < 0 || tileZ >= NavmeshSettings.TilesPerSide)
                    {
                        continue;
                    }

                    NavmeshCoords.GetTileWowBounds(0, tileZ, out float tileMinX, out _, out float tileMaxX, out _);
                    if (minWowX > tileMaxX + border || maxWowX < tileMinX - border)
                    {
                        continue;
                    }

                    if (!tiles.TryGetValue((tileX, tileZ), out TileAccumulator? tile))
                    {
                        tile = new TileAccumulator();
                        tiles.Add((tileX, tileZ), tile);
                    }

                    tile.Add(kind, v0, v1, v2, liquidTypes is null ? null : liquidTypes[triIndex]);
                    if (tiles.Count > MaximumTilesPerInput)
                    {
                        throw new InvalidDataException($"One ADT input expanded beyond {MaximumTilesPerInput} navmesh tiles; refusing an unexpectedly broad export.");
                    }
                }
            }
        }
    }

    private static int CandidateMin(float minWorld, float border)
    {
        return (int)MathF.Floor((minWorld - border - NavmeshCoords.Origin) / NavmeshSettings.TileWorldSize) - 1;
    }

    private static int CandidateMax(float maxWorld, float border)
    {
        return (int)MathF.Floor((maxWorld + border - NavmeshCoords.Origin) / NavmeshSettings.TileWorldSize) + 1;
    }

    private static Vector3 ToRc(float[] source) => new(source[2], source[1], source[0]);

    private enum GeometryKind { Terrain, Collision, Liquid }

    private sealed class TileAccumulator
    {
        public List<float> TerrainVerts { get; } = [];
        public List<int> TerrainTris { get; } = [];
        public List<float> CollisionVerts { get; } = [];
        public List<int> CollisionTris { get; } = [];
        public List<float> LiquidVerts { get; } = [];
        public List<int> LiquidTris { get; } = [];
        public List<int> LiquidAreas { get; } = [];
        public float MinHeight { get; private set; } = float.MaxValue;
        public float MaxHeight { get; private set; } = float.MinValue;

        public void Add(GeometryKind kind, Vector3 v0, Vector3 v1, Vector3 v2, int? liquidArea)
        {
            MinHeight = MathF.Min(MinHeight, MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)));
            MaxHeight = MathF.Max(MaxHeight, MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)));

            List<float> vertices = kind switch
            {
                GeometryKind.Terrain => TerrainVerts,
                GeometryKind.Collision => CollisionVerts,
                _ => LiquidVerts,
            };
            List<int> indices = kind switch
            {
                GeometryKind.Terrain => TerrainTris,
                GeometryKind.Collision => CollisionTris,
                _ => LiquidTris,
            };

            int baseIndex = vertices.Count / 3;
            Append(vertices, v0);
            Append(vertices, v1);
            Append(vertices, v2);
            indices.Add(baseIndex);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
            if (kind == GeometryKind.Liquid)
            {
                LiquidAreas.Add(liquidArea ?? throw new InvalidDataException("A liquid area was missing."));
            }
        }

        public GeometryCounts GetCounts()
        {
            int collision = CollisionTris.Count / 3;
            return new GeometryCounts
            {
                TerrainTriangles = TerrainTris.Count / 3,
                CollisionTriangles = collision,
                WmoCollisionTriangles = null,
                M2CollisionTriangles = null,
                LiquidTriangles = LiquidTris.Count / 3,
                WaterTriangles = LiquidAreas.Count(static area => area == NavmeshSettings.AreaWater),
                MagmaSlimeTriangles = LiquidAreas.Count(static area => area == NavmeshSettings.AreaMagmaSlime),
            };
        }

        private static void Append(List<float> values, Vector3 point)
        {
            values.Add(point.X);
            values.Add(point.Y);
            values.Add(point.Z);
        }
    }
}
