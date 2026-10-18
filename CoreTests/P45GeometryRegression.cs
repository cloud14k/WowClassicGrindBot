using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

using ForeverGeometryTool;
using PPather.Navmesh;

namespace CoreTests;

internal static class P45GeometryRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wowclassicgrindbot-p45-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        string leftPath = WriteFixture(root, "adt33_46.rawgeo.json", 33, 46, 70094, 100f, false);
        string rightPath = WriteFixture(root, "adt34_46.rawgeo.json", 34, 46, 70094, 100f, false);
        GeometryInputFile left = new(leftPath, 33, 46);
        GeometryInputFile right = new(rightPath, 34, 46);
        AdtCoordinate[] expected = [new(33, 46), new(34, 46)];

        GeometryAggregate forward = Export([left, right], expected);
        GeometryAggregate reverse = Export([right, left], expected);
        if (forward.Tiles.Count != reverse.Tiles.Count ||
            forward.DuplicateTrianglesRemoved.Total <= 0 ||
            !forward.Tiles.Any(static tile => tile.SourceAdtsXY.Length == 2 &&
                                               tile.DuplicateTrianglesRemoved.Collision > 0 &&
                                               tile.TileCounts.CollisionTriangles >= 2))
        {
            throw new InvalidOperationException("The multi-ADT merge did not combine and identify exact duplicate collision triangles.");
        }

        ForeverGeometryTile shared = forward.Tiles.First(static tile => tile.SourceAdtsXY.Length == 2 &&
            tile.DuplicateTrianglesRemoved.Collision > 0 && tile.TileCounts.CollisionTriangles >= 2);
        float[] collisionHeights = Enumerable.Range(0, shared.CollisionVerticesRc.Length / 3)
            .Select(index => shared.CollisionVerticesRc[index * 3 + 1]).Distinct().ToArray();
        if (!collisionHeights.Contains(100f) || !collisionHeights.Contains(105f))
        {
            throw new InvalidOperationException("Exact deduplication removed the valid overlapping collision floor at a different height.");
        }

        if (shared.CollisionVerticesRc[0] != -7800f || shared.CollisionVerticesRc[1] != 100f ||
            shared.CollisionVerticesRc[2] != -1067f)
        {
            throw new InvalidOperationException("Geometry aggregation changed source triangle order or coordinates.");
        }

        if (!shared.LiquidAreas.Contains(8) || !shared.LiquidAreas.Contains(9) ||
            shared.DuplicateTrianglesRemoved.Liquid <= 0)
        {
            throw new InvalidOperationException("Liquid deduplication did not preserve distinct water and magma/slime areas.");
        }

        string forwardDir = Path.Combine(root, "forward");
        string reverseDir = Path.Combine(root, "reverse");
        ForeverGeometryAggregator.WriteDirectory(forwardDir, forward);
        ForeverGeometryAggregator.WriteDirectory(reverseDir, reverse);
        string[] forwardFiles = Directory.EnumerateFiles(forwardDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        string[] reverseFiles = Directory.EnumerateFiles(reverseDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!forwardFiles.SequenceEqual(reverseFiles, StringComparer.Ordinal) ||
            forwardFiles.Any(name => !File.ReadAllBytes(Path.Combine(forwardDir, name!))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(reverseDir, name!)))) )
        {
            throw new InvalidOperationException("The aggregate output changed when input ADT order was reversed.");
        }

        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left], [new(33, 46), new(34, 46)])), "Missing expected ADT was not rejected.");
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, left], [new(33, 46)])), "Repeated ADT input was not rejected.");

        string mixedBuildPath = WriteFixture(root, "adt34_46_build70095.rawgeo.json", 34, 46, 70095, 100f, false);
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, new GeometryInputFile(mixedBuildPath, 34, 46)], expected)),
            "Mixed client Builds were not rejected.");

        string corruptPath = WriteFixture(root, "adt34_46_bad_index.rawgeo.json", 34, 46, 70094, 100f, true);
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, new GeometryInputFile(corruptPath, 34, 46)], expected)),
            "Out-of-range triangle indices were not rejected.");

        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [new GeometryInputFile(Path.Combine(root, "missing.rawgeo.json"), 33, 46)], [new(33, 46)])),
            "A missing raw ADT file was not rejected.");

        string cacheDir = Path.Combine(root, "cache-manifest");
        Directory.CreateDirectory(cacheDir);
        string cacheTilePath = Path.Combine(cacheDir, "tile_1_2.dnm");
        byte[] cacheTile = [1, 3, 3, 7];
        File.WriteAllBytes(cacheTilePath, cacheTile);
        string cacheTileHash = Convert.ToHexString(SHA256.HashData(cacheTile)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(cacheDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            format = ForeverNavmeshCacheManifest.Format,
            client_product = "wow_cn_beta",
            client_build = 70094,
            map_id = 0,
            settings_hash = "test-settings",
            scope = "explicit_adt_and_tile_set",
            complete_for_requested_sources_and_tiles = true,
            publishable_as_continent_cache = false,
            tiles = new[] { new { dt_tile_x = 1, dt_tile_z = 2, file = "tile_1_2.dnm", sha256 = cacheTileHash } },
        }));
        ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_cn_beta", 70094, "test-settings", 0);
        ExpectFailure(() => ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_cn_beta", 70094, "test-settings", 1),
            "A cache manifest for another MapID was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_classic", 70094, "test-settings", 0),
            "A cache manifest for another Product was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_cn_beta", 70095, "test-settings", 0),
            "A cache manifest for another Build was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_cn_beta", 70094, "different-settings", 0),
            "A cache manifest for another bake settings hash was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForPublication(
            cacheDir, "wow_cn_beta", 70094, "test-settings", 0),
            "A selected-scope test cache was accepted for continent publication.");
        File.WriteAllBytes(cacheTilePath, [1, 3, 3, 8]);
        ExpectFailure(() => ForeverNavmeshCacheManifest.Validate(cacheDir, "wow_cn_beta", 70094, "test-settings", 0),
            "A modified .dnm file was not rejected by its manifest hash.");
    }

    private static GeometryAggregate Export(IReadOnlyList<GeometryInputFile> inputs, IReadOnlyList<AdtCoordinate> expected) =>
        ForeverGeometryAggregator.Export(new GeometryAggregateRequest(0, inputs, expected));

    private static string WriteFixture(string root, string fileName, int adtX, int adtY,
        int build, float firstCollisionHeight, bool corruptIndex)
    {
        float[] terrain = adtX == 33
            ? [-1055f, 110f, -7800f, -1045f, 110f, -7800f, -1050f, 110f, -7790f]
            : [-1100f, 110f, -7800f, -1090f, 110f, -7800f, -1095f, 110f, -7790f];
        float[] sharedCollision =
        [
            -1067f, firstCollisionHeight, -7800f,
            -1066.9f, firstCollisionHeight, -7800f,
            -1066.8f, firstCollisionHeight, -7799.9f,
        ];
        float[] collision = sharedCollision;
        int[][] collisionTriangles = [[0, 1, corruptIndex ? 99 : 2]];
        int wmoTriangles = 1;
        int m2Triangles = 0;
        if (adtX == 34)
        {
            collision = [.. sharedCollision,
                -1067f, 105f, -7800f,
                -1066.9f, 105f, -7800f,
                -1066.8f, 105f, -7799.9f];
            collisionTriangles = [[0, 1, corruptIndex ? 99 : 2], [3, 4, 5]];
            wmoTriangles = 1;
            m2Triangles = 1;
        }

        Dictionary<string, object> raw = new()
        {
            ["format"] = "wow-raw-geometry-v1",
            ["map_id"] = 0,
            ["adt_xy"] = new[] { adtX, adtY },
            ["mmap_xy"] = new[] { adtY, adtX },
            ["client_product"] = "wow_cn_beta",
            ["client_build"] = build,
            ["units"] = "yard",
            ["coordinates"] = "[WoW_X,WoW_Z,WoW_Y]",
            ["target_rc_transform"] = "[raw_Z,raw_Y,raw_X]",
            ["terrain_vertex_count"] = terrain.Length / 3,
            ["terrain_triangle_count"] = 1,
            ["collision_vertex_count"] = collision.Length / 3,
            ["collision_triangle_count"] = collisionTriangles.Length,
            ["wmo_triangle_count"] = wmoTriangles,
            ["m2_triangle_count"] = m2Triangles,
            ["liquid_vertex_count"] = 3,
            ["liquid_triangle_count"] = adtX == 33 ? 1 : 2,
            ["liquid_type_count"] = adtX == 33 ? 1 : 2,
            ["terrain_vertices"] = ToVertices(terrain),
            ["terrain_triangles"] = new[] { new[] { 0, 1, 2 } },
            ["collision_vertices"] = ToVertices(collision),
            ["collision_triangles"] = collisionTriangles,
            ["liquid_vertices"] = new[]
            {
                new[] { -1067f, 99f, -7800f },
                new[] { -1066.9f, 99f, -7800f },
                new[] { -1066.8f, 99f, -7799.9f },
            },
            ["liquid_triangles"] = adtX == 33
                ? new[] { new[] { 0, 1, 2 } }
                : new[] { new[] { 0, 1, 2 }, new[] { 0, 1, 2 } },
            ["liquid_types"] = adtX == 33 ? new[] { 8 } : new[] { 8, 9 },
        };

        string path = Path.Combine(root, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(raw));
        return path;
    }

    private static float[][] ToVertices(float[] values)
    {
        float[][] result = new float[values.Length / 3][];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = [values[i * 3], values[i * 3 + 1], values[i * 3 + 2]];
        }

        return result;
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
