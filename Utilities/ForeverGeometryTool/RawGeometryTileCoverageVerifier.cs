using PPather.Navmesh;

namespace ForeverGeometryTool;

/// <summary>
/// Independently derives tile coverage from raw WoW coordinates. It uses direct
/// double-precision interval searches over canonical world bounds; it does not
/// call NavmeshCoords.ToRc or the exporter's float candidate-range helpers.
/// </summary>
internal static class RawGeometryTileCoverageVerifier
{
    // One centimetre is larger than accumulated float rounding at WoW world
    // coordinates (~17,000 yards), yet only 0.0075% of a Detour tile edge.
    // The exporter uses this same explicit boundary policy for its final AABB test.
    internal const double BoundaryToleranceYards = 0.01d;
    private const int MaximumTilesPerSource = 256;

    public static HashSet<(int X, int Z)> Calculate(RawGeometryDocument raw,
        GeometryExportRequest request)
    {
        double border = TileGeometryExtractor.BorderCells(request.AgentRadius) *
                        (double)NavmeshSettings.CellSize + request.PaddingYards;
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

    private static void Add(float[][] vertices, int[][] triangles, double border, float? minWorldZ,
        HashSet<(int X, int Z)> result)
    {
        double origin = NavmeshCoords.Origin;
        double tileSize = NavmeshSettings.TileWorldSize;
        int tilesPerSide = NavmeshSettings.TilesPerSide;
        foreach (int[] triangle in triangles)
        {
            // Raw source axes are [WoW_X, WoW_Z, WoW_Y]. Tile X follows WoW Y,
            // and tile Z follows WoW X; raw[1] is world height for MinWorldZ.
            float[] a = vertices[triangle[0]];
            float[] b = vertices[triangle[1]];
            float[] c = vertices[triangle[2]];
            if (minWorldZ is float floor && Math.Max(a[1], Math.Max(b[1], c[1])) < floor) continue;

            double minWowY = Math.Min(a[2], Math.Min(b[2], c[2]));
            double maxWowY = Math.Max(a[2], Math.Max(b[2], c[2]));
            double minWowX = Math.Min(a[0], Math.Min(b[0], c[0]));
            double maxWowX = Math.Max(a[0], Math.Max(b[0], c[0]));

            int[] xTiles = FindIntersectingTiles(minWowY, maxWowY, border, origin, tileSize, tilesPerSide);
            int[] zTiles = FindIntersectingTiles(minWowX, maxWowX, border, origin, tileSize, tilesPerSide);
            long candidateCount = (long)xTiles.Length * zTiles.Length;
            if (candidateCount > MaximumTilesPerSource)
            {
                throw new InvalidDataException($"Independent coverage rejected a triangle spanning {candidateCount} valid candidate tiles.");
            }

            foreach (int tileX in xTiles)
            {
                foreach (int tileZ in zTiles)
                {
                    result.Add((tileX, tileZ));
                    if (result.Count > MaximumTilesPerSource)
                    {
                        throw new InvalidDataException($"Independent coverage exceeded the {MaximumTilesPerSource}-tile per-source limit.");
                    }
                }
            }
        }
    }

    private static int[] FindIntersectingTiles(double minimum, double maximum, double border,
        double origin, double tileSize, int tilesPerSide)
    {
        // Binary-search the first tile whose upper edge reaches the padded
        // triangle interval, then walk forward until the lower edge passes it.
        // This is independent of the exporter's floor-based candidate bounds.
        int low = 0;
        int high = tilesPerSide;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            double tileMaximum = (origin + middle * tileSize) + tileSize;
            if (tileMaximum + border + BoundaryToleranceYards < minimum)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        List<int> result = [];
        for (int tile = low; tile < tilesPerSide; tile++)
        {
            double tileMinimum = origin + tile * tileSize;
            if (tileMinimum - border - BoundaryToleranceYards > maximum) break;
            result.Add(tile);
        }
        return [.. result];
    }
}
