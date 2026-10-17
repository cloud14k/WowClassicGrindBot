using System.Text.Json.Serialization;

namespace ForeverGeometryTool;

/// <summary>
/// Input contract emitted by the P1 pre-Recast hook. The tool intentionally
/// accepts one map and one ADT per input file.
/// </summary>
internal sealed class RawGeometryDocument
{
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("map_id")] public int MapId { get; set; }
    [JsonPropertyName("adt_xy")] public int[] AdtXY { get; set; } = [];
    [JsonPropertyName("mmap_xy")] public int[] MmapXY { get; set; } = [];
    [JsonPropertyName("client_product")] public string ClientProduct { get; set; } = "";
    [JsonPropertyName("client_build")] public int ClientBuild { get; set; }
    [JsonPropertyName("units")] public string Units { get; set; } = "";
    [JsonPropertyName("coordinates")] public string Coordinates { get; set; } = "";
    [JsonPropertyName("target_rc_transform")] public string TargetRcTransform { get; set; } = "";

    [JsonPropertyName("terrain_vertex_count")] public int TerrainVertexCount { get; set; }
    [JsonPropertyName("terrain_triangle_count")] public int TerrainTriangleCount { get; set; }
    [JsonPropertyName("collision_vertex_count")] public int CollisionVertexCount { get; set; }
    [JsonPropertyName("collision_triangle_count")] public int CollisionTriangleCount { get; set; }
    [JsonPropertyName("wmo_triangle_count")] public int WmoTriangleCount { get; set; }
    [JsonPropertyName("m2_triangle_count")] public int M2TriangleCount { get; set; }
    [JsonPropertyName("liquid_vertex_count")] public int LiquidVertexCount { get; set; }
    [JsonPropertyName("liquid_triangle_count")] public int LiquidTriangleCount { get; set; }
    [JsonPropertyName("liquid_type_count")] public int LiquidTypeCount { get; set; }

    [JsonPropertyName("terrain_vertices")] public float[][] TerrainVertices { get; set; } = [];
    [JsonPropertyName("terrain_triangles")] public int[][] TerrainTriangles { get; set; } = [];
    [JsonPropertyName("collision_vertices")] public float[][] CollisionVertices { get; set; } = [];
    [JsonPropertyName("collision_triangles")] public int[][] CollisionTriangles { get; set; } = [];
    [JsonPropertyName("liquid_vertices")] public float[][] LiquidVertices { get; set; } = [];
    [JsonPropertyName("liquid_triangles")] public int[][] LiquidTriangles { get; set; } = [];
    [JsonPropertyName("liquid_types")] public int[] LiquidTypes { get; set; } = [];
}
