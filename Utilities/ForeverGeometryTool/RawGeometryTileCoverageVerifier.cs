using System.Numerics;

using PPather.Navmesh;

namespace ForeverGeometryTool;

/// <summary>
/// Computes expected tile coverage directly from raw triangle bounds. This is
/// deliberately separate from the exporter accumulator, so publication can
/// compare the geometry discovered by its normal export path with a second
/// traversal of the source vertices and indices.
/// </summary>
internal static class RawGeometryTileCoverageVerifier
{
    private const int MaximumTilesPerSource = 256;

    public static HashSet<(int X, int Z)> Calculate(RawGeometryDocument raw,
        GeometryExportRequest request)
    {
        float border = TileGeometryExtractor.BorderCells(request.AgentRadius) * NavmeshSettings.CellSize +
                       request.PaddingYards;
        HashSet<(int X, int Z)> result = [];
        Add(raw.TerrainVertices, raw.TerrainTriangles, border, request.MinWorldZ, result);
        Add(raw.CollisionVertices, raw.CollisionTriangles, border, request.MinWorldZ, result);
        Add(raw.LiquidVertices, raw.LiquidTriangles, border, request.MinWorldZ, result);
        if (result.Count == 0)
        {
            throw new InvalidDataException("Independent coverage found no ground geometry in valid Detour tiles.");
        }

        return result;
    }

    private static void Add(float[][] vertices, int[][] triangles, float border, float? minWorldZ,
        HashSet<(int X, int Z)> result)
    {
        foreach (int[] triangle in triangles)
        {
            // Raw input is [WoW_X, WoW_Z, WoW_Y]. The shared coordinate bridge
            // maps WoW Y/Z/X to Recast X/Y/Z, preserving the project's Detour
            // axis convention instead of deriving ADT or tile indices anew.
            Vector3 a = ToRc(vertices[triangle[0]]);
            Vector3 b = ToRc(vertices[triangle[1]]);
            Vector3 c = ToRc(vertices[triangle[2]]);
            if (minWorldZ is float floor && MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < floor) continue;

            float minWowY = MathF.Min(a.X, MathF.Min(b.X, c.X));
            float maxWowY = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float minWowX = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            float maxWowX = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

            // Enumerate a deliberately loose index interval, then independently
            // test the exact padded AABB intersection against canonical tile
            // bounds. The one-tile slack also covers floating-point boundaries.
            int firstX = CandidateMin(minWowY, border);
            int lastX = CandidateMax(maxWowY, border);
            int firstZ = CandidateMin(minWowX, border);
            int lastZ = CandidateMax(maxWowX, border);
            long candidateCount = ((long)lastX - firstX + 1) * ((long)lastZ - firstZ + 1);
            if (candidateCount > MaximumTilesPerSource)
            {
                throw new InvalidDataException($"Independent coverage rejected a triangle spanning {candidateCount} candidate tiles.");
            }

            for (int tileX = Math.Max(firstX, 0); tileX <= Math.Min(lastX, NavmeshSettings.TilesPerSide - 1); tileX++)
            {
                NavmeshCoords.GetTileWowBounds(tileX, 0, out _, out float tileMinY, out _, out float tileMaxY);
                if (minWowY > tileMaxY + border || maxWowY < tileMinY - border) continue;

                for (int tileZ = Math.Max(firstZ, 0); tileZ <= Math.Min(lastZ, NavmeshSettings.TilesPerSide - 1); tileZ++)
                {
                    NavmeshCoords.GetTileWowBounds(0, tileZ, out float tileMinX, out _, out float tileMaxX, out _);
                    if (minWowX > tileMaxX + border || maxWowX < tileMinX - border) continue;
                    result.Add((tileX, tileZ));
                    if (result.Count > MaximumTilesPerSource)
                    {
                        throw new InvalidDataException($"Independent coverage exceeded the {MaximumTilesPerSource}-tile per-source limit.");
                    }
                }
            }
        }
    }

    private static Vector3 ToRc(float[] raw) => NavmeshCoords.ToRc(new Vector3(raw[0], raw[2], raw[1]));

    private static int CandidateMin(float minimum, float border) =>
        (int)MathF.Floor((minimum - border - NavmeshCoords.Origin) / NavmeshSettings.TileWorldSize) - 1;

    private static int CandidateMax(float maximum, float border) =>
        (int)MathF.Floor((maximum + border - NavmeshCoords.Origin) / NavmeshSettings.TileWorldSize) + 1;
}
