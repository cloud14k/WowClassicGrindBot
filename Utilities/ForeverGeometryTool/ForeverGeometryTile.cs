using System.Numerics;
using System.Text.Json.Serialization;

using PPather.Navmesh;

namespace ForeverGeometryTool;

/// <summary>
/// Auditable per-navmesh-tile export. The three source classes remain separate
/// in the file; <see cref="ToTileGeometry"/> combines terrain and collision
/// into the ground soup expected by the existing baker.
/// </summary>
public sealed class ForeverGeometryTile
{
    [JsonPropertyName("format")] public string Format { get; init; } = "wow-forever-tilegeometry-v1";
    [JsonPropertyName("client_product")] public required string ClientProduct { get; init; }
    [JsonPropertyName("client_build")] public required int ClientBuild { get; init; }
    [JsonPropertyName("source_sha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("map_id")] public required int MapId { get; init; }
    [JsonPropertyName("adt_xy")] public required int[] AdtXY { get; init; }
    [JsonPropertyName("source_coordinates")] public string SourceCoordinates { get; init; } = "[WoW_X, WoW_Z, WoW_Y], yards";
    [JsonPropertyName("coordinates")] public string Coordinates { get; init; } = "Recast [WoW_Y, WoW_Z, WoW_X], yards";
    [JsonPropertyName("dt_tile_x")] public required int DtTileX { get; init; }
    [JsonPropertyName("dt_tile_z")] public required int DtTileZ { get; init; }
    [JsonPropertyName("bmin_rc")] public required float[] BMinRc { get; init; }
    [JsonPropertyName("bmax_rc")] public required float[] BMaxRc { get; init; }

    [JsonPropertyName("source_counts")] public required GeometryCounts SourceCounts { get; init; }
    [JsonPropertyName("tile_counts")] public required GeometryCounts TileCounts { get; init; }

    [JsonPropertyName("terrain_vertices_rc")] public required float[] TerrainVerticesRc { get; init; }
    [JsonPropertyName("terrain_triangles")] public required int[] TerrainTriangles { get; init; }
    [JsonPropertyName("collision_vertices_rc")] public required float[] CollisionVerticesRc { get; init; }
    [JsonPropertyName("collision_triangles")] public required int[] CollisionTriangles { get; init; }
    [JsonPropertyName("liquid_vertices_rc")] public required float[] LiquidVerticesRc { get; init; }
    [JsonPropertyName("liquid_triangles")] public required int[] LiquidTriangles { get; init; }
    [JsonPropertyName("liquid_areas")] public required int[] LiquidAreas { get; init; }

    public TileGeometry ToTileGeometry()
    {
        int terrainIndexCount = TerrainTriangles.Length;
        float[] groundVerts = new float[TerrainVerticesRc.Length + CollisionVerticesRc.Length];
        Array.Copy(TerrainVerticesRc, 0, groundVerts, 0, TerrainVerticesRc.Length);
        Array.Copy(CollisionVerticesRc, 0, groundVerts, TerrainVerticesRc.Length, CollisionVerticesRc.Length);

        int[] groundTris = new int[terrainIndexCount + CollisionTriangles.Length];
        Array.Copy(TerrainTriangles, 0, groundTris, 0, terrainIndexCount);
        int collisionVertexOffset = TerrainVerticesRc.Length / 3;
        for (int i = 0; i < CollisionTriangles.Length; i++)
        {
            groundTris[terrainIndexCount + i] = CollisionTriangles[i] + collisionVertexOffset;
        }

        return new TileGeometry
        {
            GroundVerts = groundVerts,
            GroundTris = groundTris,
            LiquidVerts = LiquidVerticesRc,
            LiquidTris = LiquidTriangles,
            LiquidAreas = LiquidAreas,
            BMin = new Vector3(BMinRc[0], BMinRc[1], BMinRc[2]),
            BMax = new Vector3(BMaxRc[0], BMaxRc[1], BMaxRc[2]),
        };
    }
}

public sealed class GeometryCounts
{
    [JsonPropertyName("terrain_triangles")] public required int TerrainTriangles { get; init; }
    [JsonPropertyName("collision_triangles")] public required int CollisionTriangles { get; init; }
    [JsonPropertyName("wmo_collision_triangles")] public required int? WmoCollisionTriangles { get; init; }
    [JsonPropertyName("m2_collision_triangles")] public required int? M2CollisionTriangles { get; init; }
    [JsonPropertyName("liquid_triangles")] public required int LiquidTriangles { get; init; }
    [JsonPropertyName("water_triangles")] public required int WaterTriangles { get; init; }
    [JsonPropertyName("magma_slime_triangles")] public required int MagmaSlimeTriangles { get; init; }
}
