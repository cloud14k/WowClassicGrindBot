using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using ForeverGeometryTool;
using PPather.Navmesh;
using SharedLib;

namespace CoreTests;

internal static class P45GeometryRegression
{
    public static void Run()
    {
        string testOutputRoot = Environment.GetEnvironmentVariable("WOW_TEST_OUTPUT_ROOT")
            ?? Path.Combine(Environment.CurrentDirectory, "tmp");
        Directory.CreateDirectory(testOutputRoot);
        string root = Path.Combine(testOutputRoot, $"wowclassicgrindbot-p45-{Guid.NewGuid():N}");
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
                                               tile.TileCounts.CollisionTriangles >= 3))
        {
            throw new InvalidOperationException("The multi-ADT merge did not combine and identify exact duplicate collision triangles.");
        }

        ForeverGeometryTile shared = forward.Tiles.First(static tile => tile.SourceAdtsXY.Length == 2 &&
            tile.DuplicateTrianglesRemoved.Collision > 0 && tile.TileCounts.CollisionTriangles >= 3);
        if (shared.TileCounts.CollisionTriangles != 3)
        {
            throw new InvalidOperationException("The opposite-winding duplicate was removed or an exact same-winding duplicate was retained.");
        }
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

        GeometryAggregate filtered = ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, right], expected, MinWorldZ: 101f, Continent: "Azeroth"));
        ForeverGeometryTile filteredTile = filtered.Tiles.First(static tile => tile.SourceAdtsXY.Length == 2);
        string runtimeEquivalentHash = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions { MinWorldZ = new() { ["Azeroth"] = 101f } }).SettingsHash;
        if (filtered.SettingsHash != runtimeEquivalentHash || filteredTile.TileCounts.CollisionTriangles != 1 ||
            filteredTile.TileCounts.LiquidTriangles != 0)
        {
            throw new InvalidOperationException("MinWorldZ did not filter WoW world-Z before tile aggregation or resolve the same profile hash used by runtime.");
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

        RunWindingClassificationRegression();
        RunCacheManifestRegression(root);
        RunBatchRunnerRegression(root);
    }

    private static void RunCacheManifestRegression(string root)
    {
        const string product = "wow_cn_beta";
        const int build = 70094;
        const int mapId = 0;
        const string settingsHash = "test-settings";
        const string continent = "Azeroth";
        (int X, int Y)[] expectedAdts = [(0, 0)];
        (int X, int Z)[] expectedTiles = [(1, 2)];

        string selectedDir = Path.Combine(root, "cache-selected");
        WriteCache(selectedDir, product, build, mapId, settingsHash, "explicit_adt_and_tile_set",
            completeForRequested: true, publishable: false, continent: null);
        ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(selectedDir, product, build, settingsHash, mapId);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                selectedDir, product, build, settingsHash, mapId, continent),
            "A selected-scope test cache was accepted by the official runtime entry.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForPublication(
                selectedDir, product, build, settingsHash, mapId, continent, expectedAdts, expectedTiles),
            "A selected-scope test cache was accepted for continent publication.");

        string incompleteDir = Path.Combine(root, "cache-incomplete-continent");
        WriteCache(incompleteDir, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: false, continent);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                incompleteDir, product, build, settingsHash, mapId, continent),
            "complete_for_requested_sources_and_tiles was treated as complete continent coverage.");

        string missingRequestedCompletenessDir = Path.Combine(root, "cache-missing-request-completeness");
        WriteCache(missingRequestedCompletenessDir, product, build, mapId, settingsHash, "continent",
            completeForRequested: false, publishable: true, continent);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                missingRequestedCompletenessDir, product, build, settingsHash, mapId, continent),
            "A continent marker missing complete_for_requested_sources_and_tiles was accepted.");

        string validDir = Path.Combine(root, "cache-continent");
        WriteCache(validDir, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent);
        ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build, settingsHash, mapId, continent);
        ForeverNavmeshCacheManifest.ValidateForPublication(validDir, product, build, settingsHash, mapId,
            continent, expectedAdts, expectedTiles);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForPublication(validDir, product, build,
                settingsHash, mapId, continent, [(0, 1)], expectedTiles),
            "Publication accepted a WDT ADT set different from the manifest coverage proof.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForPublication(validDir, product, build,
                settingsHash, mapId, continent, expectedAdts, [(1, 3)]),
            "Publication accepted a target tile set different from the manifest coverage proof.");

        string continentManifestPath = Path.Combine(validDir, "manifest.json");
        string completeCoverageManifest = File.ReadAllText(continentManifestPath);
        JsonObject incompleteCoverage = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        incompleteCoverage["coverage"]!.AsObject()["wdt_tile_list_sha256"] = null;
        File.WriteAllText(continentManifestPath, incompleteCoverage.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a continent marker without a hashed WDT tile-list proof.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        string evidencePath = Path.Combine(validDir, ForeverNavmeshCacheManifest.WdtCoverageEvidenceRelativePath
            .Replace('/', Path.DirectorySeparatorChar));
        byte[] completeEvidence = File.ReadAllBytes(evidencePath);
        File.Delete(evidencePath);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a cache after its WDT coverage evidence file was deleted.");
        File.WriteAllBytes(evidencePath, completeEvidence);

        File.WriteAllBytes(evidencePath, [.. completeEvidence, 0x20]);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted modified WDT evidence without a matching hash.");
        File.WriteAllBytes(evidencePath, completeEvidence);

        JsonObject changedEvidenceHashManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        changedEvidenceHashManifest["coverage"]!.AsObject()["wdt_tile_list_sha256"] = new string('c', 64);
        File.WriteAllText(continentManifestPath, changedEvidenceHashManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a manifest hash that does not match its WDT evidence file.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongBuildEvidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        wrongBuildEvidence["client_build"] = build + 1;
        File.WriteAllText(evidencePath, wrongBuildEvidence.ToJsonString());
        JsonObject wrongBuildManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        wrongBuildManifest["coverage"]!.AsObject()["wdt_tile_list_sha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))).ToLowerInvariant();
        File.WriteAllText(continentManifestPath, wrongBuildManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted WDT evidence from a different Build when its file hash was updated.");
        File.WriteAllBytes(evidencePath, completeEvidence);
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongMapEvidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        wrongMapEvidence["map_id"] = mapId + 1;
        File.WriteAllText(evidencePath, wrongMapEvidence.ToJsonString());
        JsonObject wrongMapManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        wrongMapManifest["coverage"]!.AsObject()["wdt_tile_list_sha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))).ToLowerInvariant();
        File.WriteAllText(continentManifestPath, wrongMapManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted WDT evidence from a different MapID when its file hash was updated.");
        File.WriteAllBytes(evidencePath, completeEvidence);
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        foreach (int[][] changedAdts in new[]
                 {
                     Array.Empty<int[]>(),
                     new[] { new[] { 0, 0 }, new[] { 0, 0 } },
                     new[] { new[] { 0, 0 }, new[] { 0, 1 } },
                 })
        {
            JsonObject changedSetEvidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            changedSetEvidence["adt_xy"] = JsonSerializer.SerializeToNode(changedAdts);
            File.WriteAllText(evidencePath, changedSetEvidence.ToJsonString());
            JsonObject changedSetManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
            changedSetManifest["coverage"]!.AsObject()["wdt_tile_list_sha256"] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))).ToLowerInvariant();
            File.WriteAllText(continentManifestPath, changedSetManifest.ToJsonString());
            ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                    settingsHash, mapId, continent),
                "The runtime accepted deleted, duplicated, or extra ADTs in WDT evidence.");
            File.WriteAllBytes(evidencePath, completeEvidence);
            File.WriteAllText(continentManifestPath, completeCoverageManifest);
        }

        JsonObject escapedEvidenceManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        escapedEvidenceManifest["coverage"]!.AsObject()["wdt_tile_list_file"] = "../outside.json";
        File.WriteAllText(continentManifestPath, escapedEvidenceManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a WDT evidence path that escapes the cache directory.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, "wow_classic", build, settingsHash, mapId, continent),
            "A cache manifest for another Product was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build + 1, settingsHash, mapId, continent),
            "A cache manifest for another Build was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId + 1, continent),
            "A cache manifest for another MapID was not rejected.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, "different-settings", mapId, continent),
            "A cache manifest for another bake settings hash was not rejected.");

        string manifestPath = Path.Combine(validDir, "manifest.json");
        string savedManifest = File.ReadAllText(manifestPath);
        File.Delete(manifestPath);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId, continent),
            "A cache with no manifest was accepted.");
        File.WriteAllText(manifestPath, "{ broken json");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId, continent),
            "A corrupt manifest was accepted.");
        File.WriteAllText(manifestPath, savedManifest);

        string tilePath = Path.Combine(validDir, "tile_1_2.dnm");
        byte[] validTile = File.ReadAllBytes(tilePath);
        File.WriteAllBytes(tilePath, [.. validTile, 0x42]);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId, continent),
            "A modified .dnm file was not rejected by its manifest hash.");
        File.WriteAllBytes(tilePath, validTile);

        File.Delete(tilePath);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId, continent),
            "A deleted .dnm file was accepted.");
        File.WriteAllBytes(tilePath, validTile);

        File.Copy(tilePath, Path.Combine(validDir, "tile_1_3.dnm"));
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                validDir, product, build, settingsHash, mapId, continent),
            "An unlisted extra .dnm tile was accepted.");
    }

    private static void WriteCache(string directory, string product, int build, int mapId,
        string settingsHash, string scope, bool completeForRequested, bool publishable, string? continent)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"Test cache directory is not empty: {directory}");
        }

        string tilePath = Path.Combine(directory, "tile_1_2.dnm");
        using (FileStream stream = File.Create(tilePath))
        using (BinaryWriter writer = new(stream))
        {
            DtMeshData data = CreateGroundTileData(1, 2);
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        }

        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tilePath))).ToLowerInvariant();
        string evidenceRelativePath = ForeverNavmeshCacheManifest.WdtCoverageEvidenceRelativePath;
        string evidencePath = Path.Combine(directory, evidenceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            format = ForeverNavmeshCacheManifest.WdtCoverageEvidenceFormat,
            client_product = product,
            client_build = build,
            map_id = mapId,
            source = "mapextractor --list-tiles",
            source_log_file = "test-list-tiles.log",
            source_log_sha256 = new string('b', 64),
            adt_xy = new[] { new[] { 0, 0 } },
        }));
        string evidenceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidencePath))).ToLowerInvariant();
        Dictionary<string, object?> manifest = new()
        {
            ["format"] = ForeverNavmeshCacheManifest.Format,
            ["client_product"] = product,
            ["client_build"] = build,
            ["data_set_id"] = $"{product}-{build}",
            ["map_id"] = mapId,
            ["settings_hash"] = settingsHash,
            ["scope"] = scope,
            ["complete_for_requested_sources_and_tiles"] = completeForRequested,
            ["complete_for_continent"] = publishable,
            ["publishable_as_continent_cache"] = publishable,
            ["bake_settings"] = new
            {
                padding_yards = TileGeometryExtractor.PaddingYd,
                geometry_export_version = NavmeshSettings.ForeverGeometryExportAlgorithmVersion,
                raw_geometry_format = NavmeshSettings.ForeverRawGeometryFormat,
            },
            ["source_adts"] = new[] { new { adt_xy = new[] { 0, 0 }, file_name = "adt00_00.rawgeo.json", sha256 = new string('a', 64) } },
            ["tiles"] = new[] { new { dt_tile_x = 1, dt_tile_z = 2, file = "tile_1_2.dnm", sha256 = hash } },
        };
        if (continent is not null)
        {
            manifest["continent"] = continent;
            manifest["coverage_provenance"] = "wdt_tile_list";
            manifest["coverage"] = new
            {
                wdt_tile_list_file = evidenceRelativePath,
                wdt_tile_list_sha256 = evidenceHash,
                wdt_client_product = product,
                wdt_client_build = build,
                wdt_map_id = mapId,
                expected_source_adts_xy = new[] { new[] { 0, 0 } },
                processed_source_adts_xy = new[] { new[] { 0, 0 } },
                expected_tile_indices = new[] { new[] { 1, 2 } },
                completed_tile_indices = new[] { new[] { 1, 2 } },
            };
        }

        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void RunBatchRunnerRegression(string root)
    {
        string inputRoot = Path.Combine(root, "batch-input");
        string adtFolder = Path.Combine(inputRoot, "adt33_46", "raw-geometry");
        Directory.CreateDirectory(adtFolder);
        string rawPath = Path.Combine(adtFolder, "map0000_adt33_46.rawgeo.json");
        WriteBatchFixture(rawPath);
        string outputRoot = Path.Combine(root, "batch-state");
        string dnmRoot = Path.Combine(root, "batch-dnm");
        ForeverGeometryBatchRequest request = new(0, 33, 46, 33, 46,
            inputRoot, outputRoot, dnmRoot, "Azeroth");
        ExpectFailure(() => ForeverGeometryBatchRunner.Run(request with
            { PaddingYards = TileGeometryExtractor.PaddingYd + 0.5f }),
            "The batch runner accepted custom padding that runtime cannot identify.");

        ForeverGeometryBatchResult first = ForeverGeometryBatchRunner.Run(request);
        if (first.SourceCount != 1 || first.TileCount != 1 || first.FailedTaskCount != 0 ||
            first.Verification.AddTileCount != 1 || !first.Verification.HeightQuerySucceeded ||
            !first.Verification.ConnectedPathSucceeded)
        {
            throw new InvalidOperationException("The explicit single-ADT batch did not bake and verify its one selected tile.");
        }

        string batchSettingsHash = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions()).SettingsHash;
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot,
                "wow_cn_beta", 70094, batchSettingsHash, 0, "Azeroth"),
            "The batch runner's selected-scope manifest was accepted by the official runtime validator.");

        _ = ForeverGeometryBatchRunner.VerifyState(first.StateFile);
        ForeverGeometryBatchResult resumed = ForeverGeometryBatchRunner.Run(request with { Resume = true });
        if (resumed.ReusedSourceCount != 1 || resumed.ReusedTileCount != 1)
        {
            throw new InvalidOperationException("The batch resume path repeated successful unchanged source/tile tasks.");
        }

        File.AppendAllText(rawPath, Environment.NewLine);
        ForeverGeometryBatchResult sourceChanged = ForeverGeometryBatchRunner.Run(request with { Resume = true });
        if (sourceChanged.ReusedSourceCount != 0 || sourceChanged.ReusedTileCount != 0)
        {
            throw new InvalidOperationException("A source SHA change did not invalidate its source and dependent tile tasks.");
        }

        _ = ForeverGeometryBatchRunner.VerifyState(sourceChanged.StateFile);
        ForeverGeometryBatchResult configChanged = ForeverGeometryBatchRunner.Run(request with
        {
            AgentRadius = 0.6f,
            Resume = true,
        });
        if (configChanged.ReusedSourceCount != 0 || configChanged.ReusedTileCount != 0)
        {
            throw new InvalidOperationException("A bake configuration change did not invalidate the batch's dependent results.");
        }

        _ = ForeverGeometryBatchRunner.VerifyState(configChanged.StateFile);

        string missingInputRoot = Path.Combine(root, "batch-missing-input");
        string failedOutputRoot = Path.Combine(root, "batch-failed-state");
        string failedDnmRoot = Path.Combine(root, "batch-failed-dnm");
        ForeverGeometryBatchRequest failedRequest = new(0, 12, 12, 12, 12,
            missingInputRoot, failedOutputRoot, failedDnmRoot, "Azeroth");
        ExpectFailure(() => ForeverGeometryBatchRunner.Run(failedRequest),
            "A batch with a missing source ADT did not fail.");
        using (JsonDocument failedState = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedOutputRoot, "batch-state.json"))))
        {
            JsonElement state = failedState.RootElement;
            JsonElement source = state.GetProperty("SourceTasks")[0];
            if (state.GetProperty("Status").GetString() != "failed" ||
                state.GetProperty("FailedTaskCount").GetInt32() != 1 ||
                string.IsNullOrWhiteSpace(source.GetProperty("FailureReason").GetString()) ||
                state.GetProperty("ElapsedMilliseconds").GetInt64() < 0 ||
                state.GetProperty("OutputFileCount").GetInt64() == 0)
            {
                throw new InvalidOperationException("A failed batch did not persist its status, reason, and resource metrics.");
            }
        }

        string clientRoot = Path.Combine(root, "batch-client");
        Directory.CreateDirectory(clientRoot);
        File.WriteAllText(Path.Combine(clientRoot, ".build.info"),
            "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70094|wow_cn_beta\n");
        string wdtListLog = Path.Combine(root, "full-wdt-tile-list.log");
        string cascDataPath = Path.Combine(clientRoot, "Data");
        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 33 45\ntile 0 33 46\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublication(configChanged.StateFile,
                clientRoot, wdtListLog),
            "A rectangle batch was accepted for continent publication.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot,
                "wow_cn_beta", 70094, ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
                    new NavmeshBakeOptions { AgentRadius = 0.6f }).SettingsHash, 0, "Azeroth"),
            "A failed publication gate changed the selected batch manifest.");

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 33 46\n");
        ForeverGeometryBatchRequest explicitRequest = request with
        {
            AgentRadius = 0.6f,
            Resume = true,
            AdtList = [new AdtCoordinate(33, 46)],
        };
        ForeverGeometryBatchResult explicitBatch = ForeverGeometryBatchRunner.Run(explicitRequest);
        using (JsonDocument explicitState = JsonDocument.Parse(File.ReadAllText(explicitBatch.StateFile)))
        {
            if (explicitState.RootElement.GetProperty("AdtSelectionMode").GetString() != "explicit_list" ||
                explicitState.RootElement.GetProperty("RequestedAdts").GetArrayLength() != 1)
            {
                throw new InvalidOperationException("The normalized explicit ADT list was not recorded in batch state.");
            }
        }

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 33 45\ntile 0 33 46\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublication(explicitBatch.StateFile,
                clientRoot, wdtListLog),
            "A requested ADT set different from the matching WDT list was published.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot,
                "wow_cn_beta", 70094, ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
                    new NavmeshBakeOptions { AgentRadius = 0.6f }).SettingsHash, 0, "Azeroth"),
            "A rejected WDT coverage mismatch changed the selected manifest.");

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 33 46\n");
        File.AppendAllText(wdtListLog, "Detected client build: 70095\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublication(explicitBatch.StateFile,
                clientRoot, wdtListLog),
            "The publication gate accepted contradictory Build markers in the WDT coverage log.");

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 33 46\n");
        ForeverGeometryPublicationResult sealedBatch = ForeverGeometryBatchRunner.SealForPublication(
            explicitBatch.StateFile, clientRoot, wdtListLog);
        if (sealedBatch.SourceCount != 1 || sealedBatch.TileCount != 1)
        {
            throw new InvalidOperationException("The complete fixture WDT coverage did not seal the verified batch.");
        }
        ForeverNavmeshBakeProfile sealedProfile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions { AgentRadius = 0.6f });
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            sealedProfile.SettingsHash, 0, "Azeroth");

        RunSparseAdtBatchRegression(root, clientRoot);
        RunCrossTileBoundaryRegression(root);
    }

    private static void RunSparseAdtBatchRegression(string root, string clientRoot)
    {
        string inputRoot = Path.Combine(root, "sparse-batch-input");
        foreach (AdtCoordinate adt in new[] { new AdtCoordinate(33, 46), new AdtCoordinate(35, 46) })
        {
            string folder = Path.Combine(inputRoot, $"adt{adt.X:D2}_{adt.Y:D2}", "raw-geometry");
            Directory.CreateDirectory(folder);
            WriteBatchFixture(Path.Combine(folder, $"map0000_adt{adt.X:D2}_{adt.Y:D2}.rawgeo.json"), adt.X, adt.Y);
        }

        string tileListPath = Path.Combine(root, "sparse-mapextractor-list.log");
        string cascDataPath = Path.Combine(clientRoot, "Data");
        File.WriteAllText(tileListPath,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 35 46\ntile 0 33 46\n");
        AdtCoordinate[] adts = ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0);
        if (!adts.SequenceEqual([new AdtCoordinate(33, 46), new AdtCoordinate(35, 46)]))
        {
            throw new InvalidOperationException("The mapextractor ADT list was not de-duplicated and normalized by Y/X.");
        }
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 1),
            "The ADT list parser accepted a mapextractor record for a different MapID.");
        File.WriteAllText(tileListPath, "33,46\n33,46\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0),
            "The ADT list parser accepted a duplicate ADT coordinate.");
        File.WriteAllText(tileListPath, "64,46\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0),
            "The ADT list parser accepted an out-of-range ADT coordinate.");
        File.WriteAllText(tileListPath,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 35 46\ntile 0 33 46\n");

        string outputRoot = Path.Combine(root, "sparse-batch-state");
        string dnmRoot = Path.Combine(root, "sparse-batch-dnm");
        ForeverGeometryBatchRequest request = new(0, 0, 0, 0, 0, inputRoot,
            outputRoot, dnmRoot, "Azeroth", AdtList: [new(35, 46), new(33, 46)]);
        ForeverGeometryBatchResult first = ForeverGeometryBatchRunner.Run(request);
        if (first.SourceCount != 2 || first.TileCount < 2 || first.FailedTaskCount != 0 ||
            !first.Verification.HeightQuerySucceeded || !first.Verification.ConnectedPathSucceeded ||
            first.Verification.CrossTileLinkCount != 0)
        {
            throw new InvalidOperationException("Sparse ADTs with a legal missing coordinate did not bake as separate valid regions.");
        }
        _ = ForeverGeometryBatchRunner.VerifyState(first.StateFile);

        ForeverGeometryBatchResult reorderedResume = ForeverGeometryBatchRunner.Run(request with
        {
            AdtList = [new(33, 46), new(35, 46)],
            Resume = true,
        });
        if (reorderedResume.ReusedSourceCount != 2 || reorderedResume.ReusedTileCount != first.TileCount)
        {
            throw new InvalidOperationException("A reordered but equivalent normalized ADT set did not resume its successful tasks.");
        }

        ForeverGeometryBatchResult changedSet = ForeverGeometryBatchRunner.Run(request with
        {
            AdtList = [new(33, 46)],
            Resume = true,
        });
        if (changedSet.SourceCount != 1 || changedSet.ReusedSourceCount != 0)
        {
            throw new InvalidOperationException("Changing the requested ADT list did not invalidate the prior batch signature.");
        }

        ForeverGeometryBatchResult restoredSet = ForeverGeometryBatchRunner.Run(request with { Resume = true });
        if (restoredSet.SourceCount != 2 || restoredSet.ReusedSourceCount != 0)
        {
            throw new InvalidOperationException("Restoring the sparse ADT set did not rebuild the changed batch state.");
        }

        ForeverGeometryPublicationResult publication = ForeverGeometryBatchRunner.SealForPublication(
            restoredSet.StateFile, clientRoot, tileListPath);
        if (publication.SourceCount != 2 || publication.TileCount != restoredSet.TileCount)
        {
            throw new InvalidOperationException("A matching sparse WDT list did not seal the complete sparse batch.");
        }
        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions());
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            profile.SettingsHash, 0, "Azeroth");
    }

    private static void WriteBatchFixture(string path, int adtX = 33, int adtY = 46)
    {
        float adtSize = NavmeshSettings.TileWorldSize * 4f;
        float offsetX = (33 - adtX) * adtSize;
        float offsetY = (46 - adtY) * adtSize;
        float[][] terrain =
        [
            [-1050f + offsetX, 100f, -7800f + offsetY],
            [-1050f + offsetX, 100f, -7760f + offsetY],
            [-1010f + offsetX, 100f, -7800f + offsetY],
            [-1010f + offsetX, 100f, -7760f + offsetY],
        ];
        var raw = new
        {
            format = "wow-raw-geometry-v1",
            map_id = 0,
            adt_xy = new[] { adtX, adtY },
            mmap_xy = new[] { adtY, adtX },
            client_product = "wow_cn_beta",
            client_build = 70094,
            units = "yard",
            coordinates = "[WoW_X,WoW_Z,WoW_Y]",
            target_rc_transform = "[raw_Z,raw_Y,raw_X]",
            terrain_vertex_count = terrain.Length,
            terrain_triangle_count = 2,
            collision_vertex_count = 0,
            collision_triangle_count = 0,
            wmo_triangle_count = 0,
            m2_triangle_count = 0,
            liquid_vertex_count = 0,
            liquid_triangle_count = 0,
            liquid_type_count = 0,
            terrain_vertices = terrain,
            terrain_triangles = new[] { new[] { 0, 2, 1 }, new[] { 2, 1, 3 } },
            collision_vertices = Array.Empty<float[]>(),
            collision_triangles = Array.Empty<int[]>(),
            liquid_vertices = Array.Empty<float[]>(),
            liquid_triangles = Array.Empty<int[]>(),
            liquid_types = Array.Empty<int>(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(raw));
    }

    private static void RunCrossTileBoundaryRegression(string root)
    {
        string directory = Path.Combine(root, "adjacent-dnm");
        Directory.CreateDirectory(directory);
        foreach (int tileX in new[] { 1, 2 })
        {
            const int tileZ = 2;
            TileGeometry geometry = CreateBoundaryCrossingGeometry(tileX, tileZ);
            DtMeshData data = NavmeshTileBuilder.Bake(geometry, tileX, tileZ, new NavmeshBakeOptions())
                ?? throw new InvalidOperationException($"Adjacent test tile ({tileX},{tileZ}) did not bake.");
            string path = Path.Combine(directory, $"tile_{tileX}_{tileZ}.dnm");
            using FileStream stream = File.Create(path);
            using BinaryWriter writer = new(stream);
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        }

        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(directory, [(1, 2), (2, 2)]);
        if (verification.TileCount != 2 || !verification.HeightQuerySucceeded ||
            !verification.ConnectedPathSucceeded || verification.CrossTileLinkCount == 0)
        {
            throw new InvalidOperationException("Matching walkable tile boundaries did not produce a verified cross-tile Detour connection.");
        }
    }

    private static TileGeometry CreateBoundaryCrossingGeometry(int tileX, int tileZ)
    {
        float minX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float minZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        float maxX = minX + NavmeshSettings.TileWorldSize;
        float maxZ = minZ + NavmeshSettings.TileWorldSize;
        float[] verts =
        [
            minX - 5, 10, minZ + 10,
            minX - 5, 10, maxZ - 10,
            maxX + 5, 10, minZ + 10,
            maxX + 5, 10, maxZ - 10,
        ];
        return new TileGeometry
        {
            GroundVerts = verts,
            GroundTris = [0, 1, 2, 2, 1, 3],
            LiquidVerts = [],
            LiquidTris = [],
            LiquidAreas = [],
            BMin = new Vector3(minX, 0, minZ),
            BMax = new Vector3(maxX, 100, maxZ),
        };
    }

    private static DtMeshData CreateGroundTileData(int tileX, int tileZ)
    {
        float tileMinX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float tileMinZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        float[] verts =
        [
            tileMinX + 20, 10, tileMinZ + 20,
            tileMinX + 20, 10, tileMinZ + 60,
            tileMinX + 60, 10, tileMinZ + 20,
            tileMinX + 60, 10, tileMinZ + 60,
        ];
        TileGeometry geometry = new()
        {
            GroundVerts = verts,
            GroundTris = [0, 1, 2, 2, 1, 3],
            LiquidVerts = [],
            LiquidTris = [],
            LiquidAreas = [],
            BMin = new Vector3(tileMinX, 0, tileMinZ),
            BMax = new Vector3(tileMinX + NavmeshSettings.TileWorldSize, 100, tileMinZ + NavmeshSettings.TileWorldSize),
        };
        return NavmeshTileBuilder.Bake(geometry, tileX, tileZ, new NavmeshBakeOptions())
            ?? throw new InvalidOperationException("Test ground geometry did not produce a real DNM tile.");
    }

    private static void RunWindingClassificationRegression()
    {
        float[] upward = [0, 10, 0, 0, 10, 40, 40, 10, 0];
        float[] reversed = [0, 10, 0, 40, 10, 0, 0, 10, 40];
        float[] wall = [0, 0, 0, 40, 0, 0, 0, 40, 0];
        float[] ramp30 = [0, 0, 0, 0, 20, 40, 40, 0, 0];
        float[] ramp60 = [0, 0, 0, 0, 40, 20, 40, 0, 0];
        float[] stairs =
        [
            0, 0, 0, 0, 0, 20, 20, 0, 0,
            0, 1, 20, 0, 1, 40, 20, 1, 20,
            20, 0, 0, 20, 0, 20, 20, 1, 20,
            0, 0, 20, 20, 1, 20, 20, 0, 20,
        ];
        int[] oneTriangle = [0, 1, 2];
        RcContext context = new();
        RcAreaModification ground = new(NavmeshSettings.AreaGround);
        int upArea = RcRecast.MarkWalkableTriangles(context, 48, upward, oneTriangle, 1, ground)[0];
        int reverseArea = RcRecast.MarkWalkableTriangles(context, 48, reversed, oneTriangle, 1, ground)[0];
        int wallArea = RcRecast.MarkWalkableTriangles(context, 48, wall, oneTriangle, 1, ground)[0];
        int ramp30Area = RcRecast.MarkWalkableTriangles(context, 48, ramp30, oneTriangle, 1, ground)[0];
        int ramp60Area = RcRecast.MarkWalkableTriangles(context, 48, ramp60, oneTriangle, 1, ground)[0];
        if (upArea != NavmeshSettings.AreaGround || reverseArea == upArea ||
            wallArea == NavmeshSettings.AreaGround || ramp30Area != NavmeshSettings.AreaGround ||
            ramp60Area == NavmeshSettings.AreaGround)
        {
            throw new InvalidOperationException("Recast winding or slope classification did not match the controlled ground/wall/ramp cases.");
        }

        int[] stairTriangles = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        int[] stairAreas = RcRecast.MarkWalkableTriangles(context, 48, stairs, stairTriangles, 4, ground);
        if (stairAreas[0] != NavmeshSettings.AreaGround || stairAreas[1] != NavmeshSettings.AreaGround ||
            stairAreas[2] == NavmeshSettings.AreaGround || stairAreas[3] == NavmeshSettings.AreaGround)
        {
            throw new InvalidOperationException("Recast stair treads and risers were not classified independently.");
        }

        // A two-level pair stays distinct in the aggregator's exact coordinate key;
        // it is also classified as two valid horizontal surfaces by Recast.
        float[] twoLevels = [.. upward, 0, 15, 0, 0, 15, 40, 40, 15, 0];
        int[] twoFloors = [0, 1, 2, 3, 4, 5];
        int[] levels = RcRecast.MarkWalkableTriangles(context, 48, twoLevels, twoFloors, 2, ground);
        if (levels.Length != 2 || levels.Any(area => area != NavmeshSettings.AreaGround))
        {
            throw new InvalidOperationException("Recast did not preserve both controlled stacked walkable surfaces.");
        }

        _ = CreateGroundTileData(1, 2);
        if (NavmeshTileBuilder.Bake(CreateReversedFloorGeometry(1, 2), 1, 2, new NavmeshBakeOptions()) is not null)
        {
            throw new InvalidOperationException("A reversed floor produced a walkable navmesh polygon.");
        }

        DtMeshData stacked = NavmeshTileBuilder.Bake(CreateStackedFloorGeometry(1, 2), 1, 2, new NavmeshBakeOptions())
            ?? throw new InvalidOperationException("Stacked floor geometry produced no navmesh tile.");
        float[] outputHeights = Enumerable.Range(0, stacked.verts.Length / 3)
            .Select(index => stacked.verts[index * 3 + 1]).Distinct().ToArray();
        if (outputHeights.Length < 2)
        {
            throw new InvalidOperationException("The final navmesh bake collapsed two separated walkable floors into one layer.");
        }
    }

    private static TileGeometry CreateReversedFloorGeometry(int tileX, int tileZ)
    {
        float minX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float minZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        float[] verts =
        [
            minX + 20, 10, minZ + 20,
            minX + 20, 10, minZ + 60,
            minX + 60, 10, minZ + 20,
            minX + 60, 10, minZ + 60,
        ];
        return new TileGeometry
        {
            GroundVerts = verts,
            GroundTris = [0, 2, 1, 2, 3, 1],
            LiquidVerts = [],
            LiquidTris = [],
            LiquidAreas = [],
            BMin = new Vector3(minX, 0, minZ),
            BMax = new Vector3(minX + NavmeshSettings.TileWorldSize, 100, minZ + NavmeshSettings.TileWorldSize),
        };
    }

    private static TileGeometry CreateStackedFloorGeometry(int tileX, int tileZ)
    {
        float minX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float minZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        float[] verts =
        [
            minX + 20, 10, minZ + 20, minX + 20, 10, minZ + 60,
            minX + 60, 10, minZ + 20, minX + 60, 10, minZ + 60,
            minX + 20, 15, minZ + 20, minX + 20, 15, minZ + 60,
            minX + 60, 15, minZ + 20, minX + 60, 15, minZ + 60,
        ];
        return new TileGeometry
        {
            GroundVerts = verts,
            GroundTris = [0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7],
            LiquidVerts = [],
            LiquidTris = [],
            LiquidAreas = [],
            BMin = new Vector3(minX, 0, minZ),
            BMax = new Vector3(minX + NavmeshSettings.TileWorldSize, 100, minZ + NavmeshSettings.TileWorldSize),
        };
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
                -1067f, firstCollisionHeight, -7800f,
                -1066.8f, firstCollisionHeight, -7799.9f,
                -1066.9f, firstCollisionHeight, -7800f,
                -1067f, 105f, -7800f,
                -1066.9f, 105f, -7800f,
                -1066.8f, 105f, -7799.9f];
            collisionTriangles = [[1, corruptIndex ? 99 : 2, 0], [3, 4, 5], [6, 7, 8]];
            wmoTriangles = 2;
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
