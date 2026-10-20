using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using ForeverGeometryTool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PPather;
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

        RunCoverageBoundaryRegression(root);
        RunNativeCoordinateContractRegression(root);
        RunExplicitRegionRegression(root);
        RunConnectedSurfaceRegression(root);

        string leftPath = WriteFixture(root, "adt46_33.rawgeo.json", 46, 33, 70094, 100f, false);
        string rightPath = WriteFixture(root, "adt46_34.rawgeo.json", 46, 34, 70094, 100f, false);
        GeometryInputFile left = new(leftPath, 46, 33);
        GeometryInputFile right = new(rightPath, 46, 34);
        AdtCoordinate[] expected = [new(46, 33), new(46, 34)];

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
            0, [left], [new(46, 33), new(46, 34)])), "Missing expected ADT was not rejected.");
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, left], [new(46, 33)])), "Repeated ADT input was not rejected.");

        string mixedBuildPath = WriteFixture(root, "adt46_34_build70095.rawgeo.json", 46, 34, 70095, 100f, false);
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, new GeometryInputFile(mixedBuildPath, 46, 34)], expected)),
            "Mixed client Builds were not rejected.");

        string corruptPath = WriteFixture(root, "adt46_34_bad_index.rawgeo.json", 46, 34, 70094, 100f, true);
        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [left, new GeometryInputFile(corruptPath, 46, 34)], expected)),
            "Out-of-range triangle indices were not rejected.");

        ExpectFailure(() => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, [new GeometryInputFile(Path.Combine(root, "missing.rawgeo.json"), 46, 33)], [new(46, 33)])),
            "A missing raw ADT file was not rejected.");

        RunWindingClassificationRegression();
        RunMultiAdtSourceIdentityRegression(root);
        RunCacheManifestRegression(root);
        RunBatchRunnerRegression(root);
        RunEmptyNavmeshBakeRegression(root);
    }

    private static void RunNativeCoordinateContractRegression(string root)
    {
        // Captured from a fresh Build 70094 native ADT (30,48) export. These
        // vertices and winding come from the native terrain grid, not from
        // the exporter/coverage implementations under test.
        string path = Path.Combine(root, "native-30-48.rawgeo.json");
        WriteRawGeoWithTerrain(path, 30, 48,
            [[-8535.41602f, 19.0400696f, 1064.58325f],
             [-8533.33301f, 19.0458317f, 1062.5f],
             [-8533.33301f, 18.970768f, 1066.66663f]], [[0, 1, 2]]);
        GeometryExportRequest request = new(0, 30, 48, AgentRadius: 0, PaddingYards: 0);
        IReadOnlyList<ForeverGeometryTile> tiles = RawGeometryTileExporter.Export(path, request);
        if (!tiles.Select(static tile => (tile.DtTileX, tile.DtTileZ)).ToHashSet()
                .SetEquals([(135, 63), (135, 64), (136, 63), (136, 64)]))
            throw new InvalidOperationException("Native Stormwind terrain did not occupy its independently known world-grid corner.");
        foreach (ForeverGeometryTile tile in tiles)
        {
            float[] v = tile.TerrainVerticesRc;
            Vector3 a = new(v[0], v[1], v[2]), b = new(v[3], v[4], v[5]), c = new(v[6], v[7], v[8]);
            if (a != new Vector3(1064.58325f, 19.0400696f, -8535.41602f) ||
                Vector3.Normalize(Vector3.Cross(b - a, c - a)).Y < 0.99f)
                throw new InvalidOperationException("The native coordinate contract reflected terrain or reversed its walkable normal.");
        }
        JsonObject raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        raw["format"] = "wow-raw-geometry-v1";
        File.WriteAllText(path, raw.ToJsonString());
        ExpectFailure(() => RawGeometryTileExporter.Export(path, request), "Reflected v1 geometry was silently accepted as corrected geometry.");
        raw["format"] = NavmeshSettings.ForeverRawGeometryFormat;
        foreach (JsonArray v in raw["terrain_vertices"]!.AsArray())
            (v[0], v[2]) = (v[2]!.DeepClone(), v[0]!.DeepClone());
        File.WriteAllText(path, raw.ToJsonString());
        ExpectFailure(() => RawGeometryTileExporter.Export(path, request), "A relabeled old native array bypassed the independent ADT world-bounds check.");
    }

    private static void RunExplicitRegionRegression(string root)
    {
        string input = Path.Combine(root, "region-input");
        Directory.CreateDirectory(input);
        string rawPath = Path.Combine(input, "map0000_adt46_33.rawgeo.json");
        WriteRawGeoWithTerrain(rawPath, 46, 33,
            [[-900, 100, -7800], [-900, 100, -7760], [-860, 100, -7800], [-860, 100, -7760]],
            [[0, 2, 1], [2, 3, 1]]);
        NavmeshBakeOptions bake = new();
        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, bake);
        DataConfig data = new() { Root = Path.Combine(root, "region-data"), Exp = "forever", ClientProduct = "wow_cn_beta", ClientBuild = 70094 };
        string cache = Path.Combine(data.Navmesh, "Azeroth", profile.SettingsHash);
        ForeverGeometryBatchResult result = ForeverGeometryBatchRunner.Run(new(0, 46, 33, 46, 33,
            input, Path.Combine(root, "region-state"), cache, "Azeroth"));
        _ = ForeverGeometryBatchRunner.VerifyState(result.StateFile);
        ForeverNavmeshRegionOptions region = new()
        {
            MapId = 0, Continent = "Azeroth", SourceAdts = [[46, 33]], Tiles = [[69, 121]],
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(cache, "manifest.json")))),
        };
        ForeverNavmeshRuntimeCoverage Read() => ForeverNavmeshCacheManifest.ReadCoverageForRegion(cache,
            "wow_cn_beta", 70094, profile.SettingsHash, 0, "Azeroth", region);
        if (!Read().ContainsBakedTile(69, 121) || Read().ContainsExpectedTile(70, 121))
            throw new InvalidOperationException("Explicit regional runtime did not enforce its exact target coverage.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(cache, "wow_cn_beta", 70094,
            profile.SettingsHash, 0, "Azeroth"), "A pinned city region became a complete-continent cache.");
        string originalPin = region.ManifestSha256;
        region.ManifestSha256 = new string('0', 64);
        ExpectFailure(() => Read(), "A regional manifest bypassed its operator pin.");
        region.ManifestSha256 = originalPin;
        region.SourceAdts = [[46, 34]];
        ExpectFailure(() => Read(), "A region with the wrong source ADT set was accepted.");
        region.SourceAdts = [[46, 33]];
        region.Tiles = [[70, 121]];
        ExpectFailure(() => Read(), "A region with a different target set was accepted.");
        region.Tiles = [[69, 121]];
        byte[] correctManifest = File.ReadAllBytes(Path.Combine(cache, "manifest.json"));
        JsonObject changedSettings = JsonNode.Parse(correctManifest)!.AsObject();
        changedSettings["bake_settings"]!["agent_radius"] = 0.6f;
        File.WriteAllText(Path.Combine(cache, "manifest.json"), changedSettings.ToJsonString());
        region.ManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(cache, "manifest.json"))));
        ExpectFailure(() => Read(), "Repinning a regional manifest allowed its recorded bake radius to disagree with runtime settings.");
        File.WriteAllBytes(Path.Combine(cache, "manifest.json"), correctManifest);
        region.ManifestSha256 = originalPin;
        region.SourceAdts = [[46, 33], [46, 33]];
        ExpectFailure(() => Read(), "Duplicate configured regional ADTs were accepted.");
        region.SourceAdts = [[46, 33]];
        Directory.CreateDirectory(data.ExpDbc);
        File.WriteAllText(Path.Combine(data.ExpDbc, "WorldMapArea.json"), JsonSerializer.Serialize(new[] {
            new WorldMapArea { UIMapId = 1453, MapID = 0, AreaID = 1519, AreaName = "synthetic region",
                Continent = "Azeroth", LocBottom = -1100, LocTop = -800, LocRight = -8000, LocLeft = -7400 } }));
        using (PPatherService service = new(NullLogger<PPatherService>.Instance, data, new WorldMapAreaDB(data),
            Options.Create(bake), Options.Create(new NavmeshQueryOptions { ForeverRegion = region })))
        {
            service.Initialise(0);
            if (!service.DiskOnly || service.ToWorldZ(1453, -890, -7790, 0).Z is < 99 or > 101)
                throw new InvalidOperationException("Forever regional height did not come from its own certified DNM.");
            PPather.Graph.Path route = service.DoSearchAtLocations(new(-890, -7790, 100, 0), new(-870, -7770, 100, 0), SearchStrategy.A_Star);
            if (route is null || service.LatestNavmeshQueryResult?.Diagnostic is not null)
                throw new InvalidOperationException("The production service did not load and query the explicitly pinned region.");
            if (service.DoSearchAtLocations(new(-890, -7790, 100, 0), new(-870, -7770, 120, 0), SearchStrategy.A_Star) is not null ||
                service.LatestNavmeshQueryResult?.Diagnostic?.Reason != NavmeshPathFailureReason.NoWalkableSurface)
                throw new InvalidOperationException("An explicit Forever destination was silently changed to another floor by the wide endpoint retry.");
        }
        File.AppendAllText(Path.Combine(cache, "manifest.json"), " ");
        ExpectFailure(() => Read(), "A regional manifest changed after pinning was accepted.");

        // The usual single-ADT fixture touches its ADT edge. Its tile cannot
        // be certified regionally without the adjacent padding source.
        string edgeInput = Path.Combine(root, "region-edge-input");
        Directory.CreateDirectory(edgeInput);
        WriteBatchFixture(Path.Combine(edgeInput, "map0000_adt46_33.rawgeo.json"));
        string edgeCache = Path.Combine(root, "region-edge-dnm");
        ForeverGeometryBatchRunner.Run(new(0, 46, 33, 46, 33, edgeInput, Path.Combine(root, "region-edge-state"), edgeCache, "Azeroth"));
        region.Tiles = [[69, 120]];
        region.ManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(edgeCache, "manifest.json"))));
        ExpectFailure(() => ForeverNavmeshCacheManifest.ReadCoverageForRegion(edgeCache, "wow_cn_beta", 70094,
            profile.SettingsHash, 0, "Azeroth", region), "A region's partial outer padding footprint was accepted as complete.");
    }

    private static void RunConnectedSurfaceRegression(string root)
    {
        string cache = Path.Combine(root, "connected-surface-cache");
        WriteCache(cache, "wow_cn_beta", 70094, 0, "surface-test", "continent", true, true, "Azeroth");
        TileGeometry bounds = CreateWalkableGeometry(1, 2);
        float x = bounds.BMin.X, z = bounds.BMin.Z;
        List<float> vertices = [];
        List<int> triangles = [];
        void Floor(float minX, float minZ, float maxX, float maxZ, float height)
        {
            int start = vertices.Count / 3;
            vertices.AddRange([x + minX, height, z + minZ, x + minX, height, z + maxZ,
                x + maxX, height, z + minZ, x + maxX, height, z + maxZ]);
            triangles.AddRange([start, start + 1, start + 2, start + 2, start + 1, start + 3]);
        }
        // A connected upper L-shaped walkway, with a distinct lower floor in
        // the empty inner corner. Global nearest-point smoothing may fall to
        // that lower floor or cut across the missing upper surface.
        Floor(10, 10, 110, 110, 10);
        Floor(20, 20, 100, 40, 14);
        Floor(80, 35, 100, 100, 14);
        TileGeometry geometry = new()
        {
            GroundVerts = vertices.ToArray(), GroundTris = triangles.ToArray(),
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [], BMin = bounds.BMin, BMax = bounds.BMax,
        };
        DtMeshData baked = NavmeshTileBuilder.Bake(geometry, 1, 2, new NavmeshBakeOptions())!;
        string file = Path.Combine(cache, "tile_1_2.dnm");
        using (BinaryWriter writer = new(File.Create(file)))
            new DtMeshDataWriter().Write(writer, baked, RcByteOrder.LITTLE_ENDIAN, true);
        string manifestPath = Path.Combine(cache, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["tiles"]![0]!["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        ForeverNavmeshRuntimeCoverage coverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(cache,
            "wow_cn_beta", 70094, "surface-test", 0, "Azeroth");
        using NavmeshPathfinder pathfinder = CreateDiagnosticPathfinder(cache, coverage);
        Vector3 from = NavmeshCoords.ToWow(new(x + 30, 14, z + 30));
        Vector3 to = NavmeshCoords.ToWow(new(x + 90, 14, z + 90));
        NavmeshPathQueryResult result = pathfinder.FindPathWithResult(from, to, null);
        if (result.Path is null || result.Diagnostic is not null || result.Path.locations.Any(static p => p.Z < 13.5f) ||
            ForeverRegionRuntimeRegression.CountBlockedSegments(pathfinder, result.Path.locations) != 0)
            throw new InvalidOperationException("A Forever route cut the upper L-shaped walkway's corner or jumped onto its lower floor.");
        if (ForeverRegionRuntimeRegression.CountBlockedSegments(pathfinder, [from, to]) == 0)
            throw new InvalidOperationException("The independent segment verifier accepted a diagonal across the missing upper corner and its disconnected lower floor.");
        Vector3 lowerStart = NavmeshCoords.ToWow(new(x + 30, 10, z + 80));
        NavmeshPathQueryResult unreachableFloor = pathfinder.FindPathWithResult(lowerStart, to, null);
        if (unreachableFloor.Diagnostic is null || unreachableFloor.Stats.Retargeted)
            throw new InvalidOperationException("An unreachable explicit upper floor was replaced with the reachable floor four yards below it.");
    }

    private static void RunMultiAdtSourceIdentityRegression(string root)
    {
        AdtCoordinate[] adts = [new(46, 34), new(46, 35), new(47, 34), new(47, 35)];
        GeometryInputFile[] normalInputs = adts.Select(adt => new GeometryInputFile(
            WriteFixture(root, $"source-order-{adt.X}-{adt.Y}.rawgeo.json", adt.X, adt.Y, 70094, 100f, false),
            adt.X, adt.Y)).ToArray();
        foreach (GeometryInputFile input in normalInputs)
            RewriteToAdtCorner(input.Path, input.AdtX, input.AdtY);
        GeometryInputFile[] emptyInputs = adts.Select(adt =>
        {
            string path = WriteFixture(root, $"source-order-empty-{adt.X}-{adt.Y}.rawgeo.json",
                adt.X, adt.Y, 70094, 100f, false);
            RewriteToAdtCorner(path, adt.X, adt.Y);
            ReverseGroundTriangleWinding(path);
            return new GeometryInputFile(path, adt.X, adt.Y);
        }).ToArray();

        GeometryAggregate normalForward = ExportWithContinent(normalInputs, adts);
        GeometryAggregate normalReverse = ExportWithContinent(normalInputs.Reverse().ToArray(), adts);
        GeometryAggregate normalRandom = ExportWithContinent(
            [normalInputs[2], normalInputs[0], normalInputs[3], normalInputs[1]], adts);
        if (normalForward.Sources.Select(static source => source.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
        {
            throw new InvalidOperationException("The 2x2 multi-ADT identity regression does not use four distinct source hashes.");
        }

        ForeverGeometryTile normalTile = FindFourSourceTile(normalForward);
        string canonicalOrder = string.Join(';', normalTile.SourceAdtsXY.Select(static pair => $"{pair[0]},{pair[1]}"));
        string xThenYOrder = string.Join(';', normalTile.SourceAdtsXY.OrderBy(static pair => pair[0])
            .ThenBy(static pair => pair[1]).Select(static pair => $"{pair[0]},{pair[1]}"));
        if (canonicalOrder == xThenYOrder)
        {
            throw new InvalidOperationException("The chosen 2x2 coordinates did not distinguish X/Y from Y/X source ordering.");
        }

        AssertAggregateSourceOrder(normalForward, normalTile, "forward");
        AssertAggregateSourceOrder(normalReverse, FindFourSourceTile(normalReverse), "reverse");
        AssertAggregateSourceOrder(normalRandom, FindFourSourceTile(normalRandom), "random");

        string[] aggregateDirs = new[] { "source-order-forward", "source-order-reverse", "source-order-random" }
            .Select(name => Path.Combine(root, name)).ToArray();
        ForeverGeometryAggregator.WriteDirectory(aggregateDirs[0], normalForward);
        ForeverGeometryAggregator.WriteDirectory(aggregateDirs[1], normalReverse);
        ForeverGeometryAggregator.WriteDirectory(aggregateDirs[2], normalRandom);
        AssertDirectoryBytesEqual(aggregateDirs[0], aggregateDirs[1], "forward/reverse aggregate output");
        AssertDirectoryBytesEqual(aggregateDirs[0], aggregateDirs[2], "forward/random aggregate output");

        string normalBakeDir = Path.Combine(root, "source-order-normal-bake");
        BakeVerification normalBake = ForeverGeometryBakeVerifier.BakeAndVerify(normalForward,
            normalForward.Tiles.Select(static tile => (tile.DtTileX, tile.DtTileZ)).ToArray(), normalBakeDir);
        if (normalBake.TileCount <= 0 || !Directory.EnumerateFiles(normalBakeDir, "*.dnm").Any())
        {
            throw new InvalidOperationException("Four distinct ADT sources did not produce a verified normal DNM bake.");
        }
        string normalReverseBake = Path.Combine(root, "source-order-normal-bake-reverse");
        string normalRandomBake = Path.Combine(root, "source-order-normal-bake-random");
        ForeverGeometryBakeVerifier.BakeAndVerify(normalReverse,
            normalForward.Tiles.Select(static tile => (tile.DtTileX, tile.DtTileZ)).ToArray(), normalReverseBake);
        ForeverGeometryBakeVerifier.BakeAndVerify(normalRandom,
            normalForward.Tiles.Select(static tile => (tile.DtTileX, tile.DtTileZ)).ToArray(), normalRandomBake);
        AssertDirectoryBytesEqual(normalBakeDir, normalReverseBake, "forward/reverse normal bake output");
        AssertDirectoryBytesEqual(normalBakeDir, normalRandomBake, "forward/random normal bake output");

        string[] mutations = ["swap-hashes", "missing-source", "extra-source", "duplicate-source", "wrong-source-hash"];
        for (int i = 0; i < mutations.Length; i++)
        {
            ForeverGeometryTile changedTile = MutateTileSourceIdentity(normalTile, mutations[i]);
            GeometryAggregate changedAggregate = ReplaceAggregateTile(normalForward, changedTile);
            ExpectFailure(() => ForeverGeometryBakeVerifier.BakeAndVerify(changedAggregate,
                    [(normalTile.DtTileX, normalTile.DtTileZ)], Path.Combine(root, $"source-identity-reject-{i}")),
                $"The source identity validation accepted the '{mutations[i]}' tampering case.");
        }

        GeometryAggregate emptyForward = ExportWithContinent(emptyInputs, adts);
        GeometryAggregate emptyReverse = ExportWithContinent(emptyInputs.Reverse().ToArray(), adts);
        GeometryAggregate emptyRandom = ExportWithContinent(
            [emptyInputs[2], emptyInputs[0], emptyInputs[3], emptyInputs[1]], adts);
        ForeverGeometryTile emptyTile = FindFourSourceTile(emptyForward);
        AssertAggregateSourceOrder(emptyForward, emptyTile, "empty forward");
        AssertAggregateSourceOrder(emptyReverse, FindFourSourceTile(emptyReverse), "empty reverse");
        AssertAggregateSourceOrder(emptyRandom, FindFourSourceTile(emptyRandom), "empty random");

        string emptyBakeDir = Path.Combine(root, "source-order-empty-bake");
        BakeVerification emptyBake = ForeverGeometryBakeVerifier.BakeAndVerify(emptyForward,
            new[] { (emptyTile.DtTileX, emptyTile.DtTileZ) }, emptyBakeDir);
        if (emptyBake.TileCount != 0 || emptyBake.EmptyTiles is not { Count: 1 } ||
            emptyBake.EmptyTiles[0] != (emptyTile.DtTileX, emptyTile.DtTileZ) ||
            Directory.EnumerateFiles(emptyBakeDir, "*.dnm").Any())
        {
            throw new InvalidOperationException("Four distinct ADT sources did not produce a verified legal empty-tile result.");
        }
        string emptyReverseBake = Path.Combine(root, "source-order-empty-bake-reverse");
        string emptyRandomBake = Path.Combine(root, "source-order-empty-bake-random");
        ForeverGeometryBakeVerifier.BakeAndVerify(emptyReverse,
            new[] { (emptyTile.DtTileX, emptyTile.DtTileZ) }, emptyReverseBake);
        ForeverGeometryBakeVerifier.BakeAndVerify(emptyRandom,
            new[] { (emptyTile.DtTileX, emptyTile.DtTileZ) }, emptyRandomBake);
        AssertDirectoryBytesEqual(emptyBakeDir, emptyReverseBake, "forward/reverse empty evidence output");
        AssertDirectoryBytesEqual(emptyBakeDir, emptyRandomBake, "forward/random empty evidence output");

        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions());
        ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(emptyBakeDir, "wow_cn_beta", 70094,
            profile.SettingsHash, 0);
        string evidencePath = Directory.EnumerateFiles(Path.Combine(emptyBakeDir, "coverage"), "empty-tile-*.json")
            .Single();
        byte[] savedEvidence = File.ReadAllBytes(evidencePath);
        JsonObject changedEvidence = JsonNode.Parse(savedEvidence)!.AsObject();
        JsonArray changedHashes = changedEvidence["source_sha256s"]!.AsArray();
        changedHashes[0] = new string('f', 64);
        File.WriteAllText(evidencePath, changedEvidence.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(emptyBakeDir,
                "wow_cn_beta", 70094, profile.SettingsHash, 0),
            "The selected-pack manifest accepted modified empty-tile source-hash evidence.");
        File.WriteAllBytes(evidencePath, savedEvidence);
        ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(emptyBakeDir, "wow_cn_beta", 70094,
            profile.SettingsHash, 0);
    }

    private static GeometryAggregate ExportWithContinent(IReadOnlyList<GeometryInputFile> inputs,
        IReadOnlyList<AdtCoordinate> expected) => ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
            0, inputs, expected, Continent: "Azeroth"));

    private static ForeverGeometryTile FindFourSourceTile(GeometryAggregate aggregate) => aggregate.Tiles
        .Where(static tile => tile.SourceAdtsXY.Length == 4)
        .OrderBy(static tile => tile.DtTileX).ThenBy(static tile => tile.DtTileZ).First();

    private static void AssertAggregateSourceOrder(GeometryAggregate aggregate, ForeverGeometryTile tile,
        string inputOrder)
    {
        GeometrySourceIdentity[] expected = aggregate.TileSourceIdentities[(tile.DtTileX, tile.DtTileZ)].ToArray();
        string actualPairs = string.Join(';', tile.SourceAdtsXY.Select(static pair => $"{pair[0]},{pair[1]}"));
        string expectedPairs = string.Join(';', expected.Select(static source => $"{source.AdtX},{source.AdtY}"));
        string actualHashes = string.Join(';', tile.SourceSha256s);
        string expectedHashes = string.Join(';', expected.Select(static source => source.Sha256));
        if (actualPairs != expectedPairs || actualHashes != expectedHashes ||
            tile.SourceSha256 != ForeverGeometryAggregator.ComputeSourceSetHash(aggregate.Sources.Where(source =>
                expected.Any(identity => identity.AdtX == source.AdtXY[0] && identity.AdtY == source.AdtXY[1]))))
        {
            throw new InvalidOperationException($"The {inputOrder} 2x2 aggregate did not retain canonical ADT-to-SHA source identities.");
        }
    }

    private static ForeverGeometryTile MutateTileSourceIdentity(ForeverGeometryTile tile, string mutation)
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(tile))!.AsObject();
        JsonArray coordinates = json["source_adts_xy"]!.AsArray();
        JsonArray hashes = json["source_sha256s"]!.AsArray();
        switch (mutation)
        {
            case "swap-hashes":
                (hashes[0], hashes[1]) = (hashes[1]!.DeepClone(), hashes[0]!.DeepClone());
                break;
            case "missing-source":
                coordinates.RemoveAt(coordinates.Count - 1);
                hashes.RemoveAt(hashes.Count - 1);
                break;
            case "extra-source":
                coordinates.Add(new JsonArray(JsonValue.Create(36), JsonValue.Create(47)));
                hashes.Add(JsonValue.Create(new string('0', 64)));
                break;
            case "duplicate-source":
                coordinates[coordinates.Count - 1] = coordinates[0]!.DeepClone();
                break;
            case "wrong-source-hash":
                hashes[0] = new string('f', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        return json.Deserialize<ForeverGeometryTile>()
            ?? throw new InvalidOperationException("Could not deserialize the source identity regression fixture.");
    }

    private static GeometryAggregate ReplaceAggregateTile(GeometryAggregate aggregate, ForeverGeometryTile replacement) => new()
    {
        MapId = aggregate.MapId,
        ClientProduct = aggregate.ClientProduct,
        ClientBuild = aggregate.ClientBuild,
        Sources = aggregate.Sources,
        ExpectedAdts = aggregate.ExpectedAdts,
        Tiles = aggregate.Tiles.Select(tile => tile.DtTileX == replacement.DtTileX && tile.DtTileZ == replacement.DtTileZ
            ? replacement : tile).ToArray(),
        TileSourceIdentities = aggregate.TileSourceIdentities,
        DuplicateTrianglesRemoved = aggregate.DuplicateTrianglesRemoved,
        AgentRadius = aggregate.AgentRadius,
        AgentMaxClimb = aggregate.AgentMaxClimb,
        PaddingYards = aggregate.PaddingYards,
        WalkableSlope = aggregate.WalkableSlope,
        MinWorldZ = aggregate.MinWorldZ,
        Continent = aggregate.Continent,
        SettingsHash = aggregate.SettingsHash,
    };

    private static void ReverseGroundTriangleWinding(string path)
    {
        JsonObject raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (string name in new[] { "terrain_triangles", "collision_triangles" })
        {
            JsonArray triangles = raw[name]!.AsArray();
            foreach (JsonArray triangle in triangles)
            {
                (triangle[0], triangle[1]) = (triangle[1]!.DeepClone(), triangle[0]!.DeepClone());
            }
        }
        File.WriteAllText(path, raw.ToJsonString());
    }

    private static void RewriteToAdtCorner(string path, int adtX, int adtY)
    {
        const float cornerX = -1600f;
        const float cornerY = -8000f;
        int sideX = adtY == 34 ? 1 : -1;
        int sideY = adtX == 46 ? 1 : -1;
        JsonObject raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (string name in new[] { "terrain_vertices", "collision_vertices", "liquid_vertices" })
        {
            JsonArray vertices = raw[name]!.AsArray();
            for (int i = 0; i < vertices.Count; i++)
            {
                JsonArray vertex = vertices[i]!.AsArray();
                float xOffset = i % 3 == 2 ? sideX * 40f : 0f;
                float yOffset = i % 3 == 1 ? sideY * 40f : 0f;
                vertex[0] = cornerX + sideX + xOffset;
                vertex[2] = cornerY + sideY + yOffset;
            }
        }

        if (sideX * sideY < 0)
        {
            foreach (string name in new[] { "terrain_triangles", "collision_triangles" })
            {
                foreach (JsonArray triangle in raw[name]!.AsArray())
                    (triangle[1], triangle[2]) = (triangle[2]!.DeepClone(), triangle[1]!.DeepClone());
            }
        }

        // The original P45 fixture ordering faces down in Recast coordinates.
        // Normalize the four quadrants to a walkable winding; the separate
        // empty fixture reverses it once more after this step.
        foreach (string name in new[] { "terrain_triangles", "collision_triangles" })
        {
            foreach (JsonArray triangle in raw[name]!.AsArray())
                (triangle[1], triangle[2]) = (triangle[2]!.DeepClone(), triangle[1]!.DeepClone());
        }
        File.WriteAllText(path, raw.ToJsonString());
    }

    private static void AssertDirectoryBytesEqual(string first, string second, string scenario)
    {
        string[] firstFiles = Directory.EnumerateFiles(first).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray()!;
        string[] secondFiles = Directory.EnumerateFiles(second).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray()!;
        if (!firstFiles.SequenceEqual(secondFiles, StringComparer.Ordinal) ||
            firstFiles.Any(name => !File.ReadAllBytes(Path.Combine(first, name!))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(second, name!)))) )
        {
            throw new InvalidOperationException($"The {scenario} changed when source ADT input order changed.");
        }
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
        RunRuntimeEmptyTileRegression(root, product, build, mapId, settingsHash, continent);
        RunRuntimeNavigationDiagnosticsRegression(root, product, build, mapId, settingsHash, continent);

        RunCacheRelocationRegression(root, product, build, mapId, settingsHash, continent,
            expectedAdts, expectedTiles);

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

        string evidencePath = ReadCoverageProofPath(validDir);
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

        JsonObject validProof = JsonNode.Parse(completeEvidence)!.AsObject();
        string nativeRelative = validProof["native_tile_list_file"]!.GetValue<string>();
        string nativePath = Path.Combine(validDir, nativeRelative.Replace('/', Path.DirectorySeparatorChar));
        byte[] validNativeList = File.ReadAllBytes(nativePath);
        File.WriteAllBytes(nativePath, [.. validNativeList, 0x20]);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a modified native WDT .tilelist.");
        File.WriteAllBytes(nativePath, validNativeList);

        byte[] mismatchedNativeList = [.. validNativeList];
        mismatchedNativeList[12 + ForeverWdtTileList.TileCount - 1] = (byte)'0';
        (string mismatchedNativePath, string mismatchedNativeHash) = WriteCoverageBlob(validDir,
            ForeverNavmeshCacheManifest.WdtNativeTileListPathPrefix, ".tilelist", mismatchedNativeList);
        JsonObject mismatchedNativeProof = JsonNode.Parse(completeEvidence)!.AsObject();
        mismatchedNativeProof["native_tile_list_file"] = mismatchedNativePath;
        mismatchedNativeProof["native_tile_list_sha256"] = mismatchedNativeHash;
        JsonObject mismatchedNativeManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, mismatchedNativeProof, mismatchedNativeManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a validly hashed native WDT list whose ADT set differs from the process proof.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject legacyProof = JsonNode.Parse(completeEvidence)!.AsObject();
        legacyProof["format"] = "wow-forever-wdt-coverage-v1";
        JsonObject legacyManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, legacyProof, legacyManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime upgraded an external-log v1 coverage proof to a trusted process proof.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject previousProcessProof = JsonNode.Parse(completeEvidence)!.AsObject();
        previousProcessProof["format"] = "wow-forever-wdt-coverage-v2";
        previousProcessProof.Remove("publication_cache_root");
        previousProcessProof.Remove("mapextractor_path");
        JsonObject previousProcessManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, previousProcessProof, previousProcessManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "A legacy v2 process proof without the relocation provenance fields was silently upgraded.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject changedEvidenceHashManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        changedEvidenceHashManifest["coverage"]!.AsObject()["wdt_tile_list_sha256"] = new string('c', 64);
        File.WriteAllText(continentManifestPath, changedEvidenceHashManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a manifest hash that does not match its WDT evidence file.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongBuildEvidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        wrongBuildEvidence["client_build"] = build + 1;
        JsonObject wrongBuildManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, wrongBuildEvidence, wrongBuildManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted WDT evidence from a different Build when its file hash was updated.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongMapEvidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        wrongMapEvidence["map_id"] = mapId + 1;
        JsonObject wrongMapManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, wrongMapEvidence, wrongMapManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted WDT evidence from a different MapID when its file hash was updated.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongProductArgument = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        JsonArray productArguments = wrongProductArgument["arguments"]!.AsArray();
        productArguments[5] = "wow_classic";
        JsonObject wrongProductArgumentManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, wrongProductArgument, wrongProductArgumentManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a historical process argument with a different Product.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject wrongMapArgument = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        JsonArray mapArguments = wrongMapArgument["arguments"]!.AsArray();
        mapArguments[11] = (mapId + 1).ToString(CultureInfo.InvariantCulture);
        JsonObject wrongMapArgumentManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, wrongMapArgument, wrongMapArgumentManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a historical process argument with a different MapID.");
        File.WriteAllText(continentManifestPath, completeCoverageManifest);

        JsonObject escapedHistoricalOutput = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        JsonArray outputArguments = escapedHistoricalOutput["arguments"]!.AsArray();
        outputArguments[3] = Path.Combine(Path.GetTempPath(), "not-isolated-wdt-output");
        JsonObject escapedHistoricalOutputManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
        WriteCoverageProofVariant(validDir, escapedHistoricalOutput, escapedHistoricalOutputManifest);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                settingsHash, mapId, continent),
            "The runtime accepted a historical WDT output path outside the isolated cache workspace.");
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
            JsonObject changedSetManifest = JsonNode.Parse(completeCoverageManifest)!.AsObject();
            WriteCoverageProofVariant(validDir, changedSetEvidence, changedSetManifest);
            ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(validDir, product, build,
                    settingsHash, mapId, continent),
                "The runtime accepted deleted, duplicated, or extra ADTs in WDT evidence.");
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
        string settingsHash, string scope, bool completeForRequested, bool publishable, string? continent,
        IReadOnlyCollection<(int X, int Z)>? bakedTiles = null)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"Test cache directory is not empty: {directory}");
        }

        (int X, int Z)[] tileIndices = (bakedTiles ?? new[] { (1, 2) })
            .Distinct().OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z).ToArray();
        if (tileIndices.Length == 0 || tileIndices.Any(static tile => !NavmeshCoords.IsValidTile(tile.X, tile.Z)))
            throw new ArgumentException("Cache fixtures require at least one valid baked tile.", nameof(bakedTiles));
        var tileRecords = tileIndices.Select(tile =>
        {
            string tilePath = Path.Combine(directory, $"tile_{tile.X}_{tile.Z}.dnm");
            using (FileStream stream = File.Create(tilePath))
            using (BinaryWriter writer = new(stream))
            {
                DtMeshData data = CreateGroundTileData(tile.X, tile.Z);
                new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
            }
            return new
            {
                dt_tile_x = tile.X,
                dt_tile_z = tile.Z,
                file = Path.GetFileName(tilePath),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tilePath))).ToLowerInvariant(),
            };
        }).ToArray();
        string clientRoot = Path.GetFullPath(Path.Combine(directory, "proof-client"));
        string dataRoot = Path.Combine(clientRoot, "Data");
        string outputRoot = Path.Combine(directory, "coverage", ".wdt-listing-" + new string('a', 32));
        string executablePath = Path.Combine(directory, "mapextractor-not-installed", "mapextractor.exe");
        string[] arguments = ["-i", clientRoot, "-o", outputRoot, "-p", product,
            "-l", "zhCN", "-e", "1", "-m", mapId.ToString(CultureInfo.InvariantCulture), "--list-tiles"];
        (string nativePath, string nativeHash) = WriteCoverageBlob(directory,
            ForeverNavmeshCacheManifest.WdtNativeTileListPathPrefix, ".tilelist",
            CreateNativeTileList(build, [(0, 0)]));
        string stdoutText = string.Join(Environment.NewLine,
        [
            $"Opened casc storage '{dataRoot}'",
            $"Opened casc storage '{dataRoot}'",
            $"Opened casc storage '{dataRoot}' product {product} locale zhCN",
            $"Detected client build: {build}",
            $"Opened casc storage '{dataRoot}'",
            $"Opened casc storage '{dataRoot}' product {product} locale zhCN",
            $"Restricted extraction to MapID {mapId}.",
            $"tile\t{mapId}\t0\t0",
            $"wdt-enumeration-complete map={mapId} build={build} visited=4096 tiles=1",
        ]) + Environment.NewLine;
        (string stdoutPath, string stdoutHash) = WriteCoverageBlob(directory,
            ForeverNavmeshCacheManifest.WdtStdoutPathPrefix, ".txt", System.Text.Encoding.UTF8.GetBytes(stdoutText));
        (string stderrPath, string stderrHash) = WriteCoverageBlob(directory,
            ForeverNavmeshCacheManifest.WdtStderrPathPrefix, ".txt", []);
        byte[] evidenceBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = ForeverNavmeshCacheManifest.WdtCoverageEvidenceFormat,
            client_product = product,
            client_build = build,
            map_id = mapId,
            source = "mapextractor --list-tiles process",
            tool_source_repository = ForeverNavmeshCacheManifest.WdtEnumeratorSourceRepository,
            tool_source_commit = ForeverNavmeshCacheManifest.WdtEnumeratorSourceCommit,
            tool_patch_sha256 = ForeverNavmeshCacheManifest.WdtEnumeratorPatchSha256,
            mapextractor_sha256 = new string('d', 64),
            locale = "zhCN",
            publication_cache_root = Path.GetFullPath(directory),
            client_root = clientRoot,
            casc_data_root = dataRoot,
            mapextractor_path = executablePath,
            arguments,
            process_exit_code = 0,
            process_timed_out = false,
            wdt_slots_visited = 4096,
            adt_count = 1,
            native_tile_list_file = nativePath,
            native_tile_list_sha256 = nativeHash,
            stdout_file = stdoutPath,
            stdout_sha256 = stdoutHash,
            stderr_file = stderrPath,
            stderr_sha256 = stderrHash,
            adt_xy = new[] { new[] { 0, 0 } },
        }));
        string evidenceHash = Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        string evidenceRelativePath = ForeverNavmeshCacheManifest.WdtCoverageEvidencePathPrefix + evidenceHash + ".json";
        string evidencePath = Path.Combine(directory, evidenceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllBytes(evidencePath, evidenceBytes);
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
            ["tiles"] = tileRecords,
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
                expected_tile_indices = tileIndices.Select(static tile => new[] { tile.X, tile.Z }).ToArray(),
                completed_tile_indices = tileIndices.Select(static tile => new[] { tile.X, tile.Z }).ToArray(),
            };
        }

        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static void RunCacheRelocationRegression(string root, string product, int build, int mapId,
        string settingsHash, string continent, IReadOnlyCollection<(int X, int Y)> expectedAdts,
        IReadOnlyCollection<(int X, int Z)> expectedTiles)
    {
        string source = Path.Combine(root, "cache-relocation-source");
        string sameVolumeCopy = Path.Combine(root, "cache-relocation-same-volume");
        string? otherVolumeCopy = null;
        WriteCache(source, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent);
        ForeverNavmeshCacheManifest.ValidateForRuntime(source, product, build, settingsHash, mapId, continent);

        try
        {
            CopyDirectory(source, sameVolumeCopy);
            ForeverNavmeshCacheManifest.ValidateForRuntime(sameVolumeCopy, product, build,
                settingsHash, mapId, continent);

            string sourceVolume = Path.GetPathRoot(Path.GetFullPath(source))!;
            DriveInfo otherDrive = DriveInfo.GetDrives().FirstOrDefault(drive => drive.IsReady &&
                !string.Equals(drive.RootDirectory.FullName, sourceVolume, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The cache relocation regression requires a writable second volume.");
            otherVolumeCopy = Path.Combine(otherDrive.RootDirectory.FullName,
                "wow-forever-cache-relocation-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, otherVolumeCopy);
            ForeverNavmeshCacheManifest.ValidateForRuntime(otherVolumeCopy, product, build,
                settingsHash, mapId, continent);

            string historyClientRoot;
            string historyExecutable;
            using (JsonDocument evidence = JsonDocument.Parse(File.ReadAllBytes(ReadCoverageProofPath(otherVolumeCopy))))
            {
                historyClientRoot = evidence.RootElement.GetProperty("client_root").GetString()!;
                historyExecutable = evidence.RootElement.GetProperty("mapextractor_path").GetString()!;
            }
            if (Directory.Exists(historyClientRoot) || File.Exists(historyExecutable))
            {
                throw new InvalidOperationException("The relocation fixture unexpectedly created historical client or mapextractor files.");
            }

            Directory.Delete(source, recursive: true);
            ForeverNavmeshCacheManifest.ValidateForRuntime(otherVolumeCopy, product, build,
                settingsHash, mapId, continent);
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(sameVolumeCopy)) Directory.Delete(sameVolumeCopy, recursive: true);
            if (otherVolumeCopy is not null && Directory.Exists(otherVolumeCopy))
                Directory.Delete(otherVolumeCopy, recursive: true);
        }

        if (Directory.Exists(source))
        {
            throw new InvalidOperationException("The original cache directory was not removed after relocation.");
        }
    }

    private static void RunRuntimeEmptyTileRegression(string root, string product, int build, int mapId,
        string settingsHash, string continent)
    {
        string cache = Path.Combine(root, "cache-continent-with-empty-tile");
        WriteCache(cache, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent);
        string manifestPath = Path.Combine(cache, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        JsonObject bakeSettings = manifest["bake_settings"]!.AsObject();
        bakeSettings["dot_recast_version"] = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";

        int[][] expectedTiles = [[1, 2], [2, 2]];
        manifest["coverage"]!["expected_tile_indices"] = JsonSerializer.SerializeToNode(expectedTiles);
        manifest["coverage"]!["completed_tile_indices"] = JsonSerializer.SerializeToNode(expectedTiles);
        string sourceHash = new('a', 64);
        string sourceSetHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"0,0:{sourceHash}"))).ToLowerInvariant();
        JsonObject payload = new()
        {
            ["format"] = ForeverNavmeshCacheManifest.EmptyNavmeshEvidenceFormat,
            ["result"] = "completed_no_walkable_polygons",
            ["client_product"] = product,
            ["client_build"] = build,
            ["map_id"] = mapId,
            ["dt_tile_x"] = 2,
            ["dt_tile_z"] = 2,
            ["settings_hash"] = settingsHash,
            ["dotrecast_version"] = bakeSettings["dot_recast_version"]!.GetValue<string>(),
            ["geometry_export_version"] = bakeSettings["geometry_export_version"]!.GetValue<string>(),
            ["raw_geometry_format"] = bakeSettings["raw_geometry_format"]!.GetValue<string>(),
            ["polygon_count"] = 0,
            ["geometry_sha256"] = new string('b', 64),
            ["source_set_sha256"] = sourceSetHash,
            ["source_adts_xy"] = JsonSerializer.SerializeToNode(new[] { new[] { 0, 0 } }),
            ["source_sha256s"] = JsonSerializer.SerializeToNode(new[] { sourceHash }),
            ["terrain_triangles"] = 1,
            ["collision_triangles"] = 0,
            ["liquid_triangles"] = 0,
        };
        byte[] evidenceBytes = System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString());
        string evidenceHash = Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        string evidenceFile = $"{ForeverNavmeshCacheManifest.EmptyNavmeshEvidencePathPrefix}2-2-{evidenceHash}.json";
        string evidencePath = Path.Combine(cache, evidenceFile.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(evidencePath, evidenceBytes);
        manifest["empty_navmesh_tiles"] = new JsonArray(new JsonObject
        {
            ["evidence_file"] = evidenceFile,
            ["evidence_sha256"] = evidenceHash,
            ["evidence"] = payload.DeepClone(),
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        IReadOnlyCollection<(int X, int Z)> emptyTiles = ForeverNavmeshCacheManifest.ReadEmptyTilesForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        if (emptyTiles.Count != 1 || !emptyTiles.Contains((2, 2)))
            throw new InvalidOperationException("Runtime did not return the independently certified empty tile.");

        JsonObject missingTileManifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        missingTileManifest["empty_navmesh_tiles"] = new JsonArray();
        File.WriteAllText(manifestPath, missingTileManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                cache, product, build, settingsHash, mapId, continent),
            "Runtime treated an expected tile with neither DNM nor verified-empty evidence as complete.");
    }

    private static void RunRuntimeNavigationDiagnosticsRegression(string root, string product, int build,
        int mapId, string settingsHash, string continent)
    {
        string wrongHeaderCache = Path.Combine(root, "runtime-diagnostic-wrong-header");
        WriteCache(wrongHeaderCache, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent,
            bakedTiles: new[] { (1, 2) });
        string wrongHeaderPath = Path.Combine(wrongHeaderCache, "tile_1_2.dnm");
        WriteDnmAtPath(wrongHeaderPath, 3, 2, groundHeight: 10f);
        JsonObject wrongHeaderManifest = JsonNode.Parse(File.ReadAllText(
            Path.Combine(wrongHeaderCache, "manifest.json")))!.AsObject();
        wrongHeaderManifest["tiles"]![0]!["sha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wrongHeaderPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(wrongHeaderCache, "manifest.json"), wrongHeaderManifest.ToJsonString());
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(
                wrongHeaderCache, product, build, settingsHash, mapId, continent),
            "Runtime accepted a DNM whose path/manifest tile coordinate disagreed with its DNM header.");

        string cache = Path.Combine(root, "runtime-diagnostic-continent");
        WriteCache(cache, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent,
            bakedTiles: new[] { (1, 2), (3, 2) });
        AddVerifiedEmptyTile(cache, product, build, mapId, settingsHash, (2, 2));
        ForeverNavmeshRuntimeCoverage coverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        if (!coverage.ContainsExpectedTile(1, 2) || !coverage.ContainsBakedTile(1, 2) ||
            !coverage.ContainsVerifiedEmptyTile(2, 2) || !coverage.ContainsBakedTile(3, 2) ||
            coverage.ContainsVerifiedEmptyTile(1, 2))
        {
            throw new InvalidOperationException("Runtime coverage did not preserve the validated baked/empty tile partition.");
        }

        Vector3 start = TileWorldPoint(1, 2);
        Vector3 empty = TileWorldPoint(2, 2);
        Vector3 end = TileWorldPoint(3, 2);
        Vector3 outside = TileWorldPoint(4, 2);

        using (NavmeshPathfinder pathfinder = CreateDiagnosticPathfinder(cache, coverage))
        {
            if (pathfinder.FindPath(start, start, null) is null || pathfinder.LastDiagnostic is not null)
                throw new InvalidOperationException("A valid DNM endpoint did not produce a path without a failure diagnostic.");

            AssertPathFailure(pathfinder, start, empty,
                NavmeshPathFailureReason.VerifiedEmptyNavmeshTile, new NavmeshTileCoordinate(2, 2),
                "valid start to verified-empty destination");
            AssertPathFailure(pathfinder, empty, start,
                NavmeshPathFailureReason.VerifiedEmptyNavmeshTile, new NavmeshTileCoordinate(2, 2),
                "verified-empty start to valid destination");
            AssertPathFailure(pathfinder, empty, empty,
                NavmeshPathFailureReason.VerifiedEmptyNavmeshTile, new NavmeshTileCoordinate(2, 2),
                "two verified-empty endpoints");
            AssertPathFailure(pathfinder, outside, start,
                NavmeshPathFailureReason.OutsideCertifiedCoverage, new NavmeshTileCoordinate(4, 2),
                "query outside certified coverage");

            var disconnectedPath = pathfinder.FindPath(start, end, null);
            // A partial route remains compatible with the existing Path
            // contract; a complete null result must retain ordinary
            // unreachable semantics, never be mislabeled as empty.
            if (pathfinder.LastDiagnostic?.Reason != NavmeshPathFailureReason.PathUnreachable)
            {
                throw new InvalidOperationException($"Two valid endpoints separated by an empty tile were not classified as ordinary unreachable (returnedPath={disconnectedPath is not null}, diagnostic={pathfinder.LastDiagnostic?.Reason}).");
            }
            if (pathfinder.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.Loaded ||
                pathfinder.Tiles.GetTileStatus(3, 2) != NavmeshRuntimeTileStatus.Loaded)
            {
                throw new InvalidOperationException("The unreachable-path regression did not load both valid endpoint DNM tiles.");
            }
        }

        string tileOne = Path.Combine(cache, "tile_1_2.dnm");
        byte[] validDnm = File.ReadAllBytes(tileOne);
        ForeverNavmeshRuntimeCoverage missingCoverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        File.Delete(tileOne);
        try
        {
            using NavmeshPathfinder missing = CreateDiagnosticPathfinder(cache, missingCoverage);
            AssertPathFailure(missing, start, start, NavmeshPathFailureReason.MissingOrInvalidNavmeshTile,
                new NavmeshTileCoordinate(1, 2), "validated DNM deleted after runtime validation");
            if (missing.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.MissingOrInvalidNavmeshTile)
                throw new InvalidOperationException("A missing expected DNM was stored as an empty tile.");
        }
        finally
        {
            File.WriteAllBytes(tileOne, validDnm);
        }

        ForeverNavmeshRuntimeCoverage corruptCoverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        File.WriteAllBytes(tileOne, [0x44, 0x4e, 0x4d, 0x00]);
        byte[] corruptBytes = File.ReadAllBytes(tileOne);
        try
        {
            using NavmeshPathfinder corrupt = CreateDiagnosticPathfinder(cache, corruptCoverage);
            AssertPathFailure(corrupt, start, start, NavmeshPathFailureReason.MissingOrInvalidNavmeshTile,
                new NavmeshTileCoordinate(1, 2), "validated DNM corrupted after runtime validation");
            if (corrupt.Tiles.GetTileStatus(1, 2) == NavmeshRuntimeTileStatus.VerifiedEmptyNavmeshTile)
                throw new InvalidOperationException("A corrupt DNM was misidentified as a verified empty tile.");
            if (!File.Exists(tileOne) || !File.ReadAllBytes(tileOne).SequenceEqual(corruptBytes))
                throw new InvalidOperationException("Runtime deleted or changed a corrupt published Forever DNM while rejecting it.");
        }
        finally
        {
            File.WriteAllBytes(tileOne, validDnm);
        }

        // Force LRU eviction with a small, explicit cache budget. The second
        // actual disk load after eviction must hash-check against the immutable
        // coverage snapshot and must not rebake or overwrite a valid-looking
        // replacement file.
        ForeverNavmeshRuntimeCoverage reloadCoverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        using (NavmeshPathfinder reload = CreateDiagnosticPathfinder(cache, reloadCoverage, maxResidentTiles: 1))
        {
            if (!reload.TryGetHeight(start.X, start.Y, out float height) || MathF.Abs(height - 10f) > NavmeshSettings.CellHeight)
                throw new InvalidOperationException($"TryGetHeight did not return the fixture ground height (actual={height}).");
            if (!reload.TrySnapWalkable(start, out Vector3 snapped, out long snappedRef) || snappedRef == 0 ||
                MathF.Abs(snapped.Z - 10f) > NavmeshSettings.CellHeight)
                throw new InvalidOperationException("TrySnapWalkable did not resolve the fixture ground polygon.");

            if (!RunQueryBehindDetourWriter(reload, () =>
                    reload.TryGetHeight(start.X, start.Y, out float lockedHeight) && MathF.Abs(lockedHeight - 10f) <= NavmeshSettings.CellHeight,
                    "TryGetHeight") ||
                !RunQueryBehindDetourWriter(reload, () =>
                    reload.TrySnapWalkable(start, out _, out long lockedRef) && lockedRef != 0,
                    "TrySnapWalkable"))
            {
                throw new InvalidOperationException("A Detour query changed its result while waiting for the tile-cache write lock.");
            }

            // A negative Detour query must also release its read lock on the
            // early return path, so a worker can acquire the write lock next.
            if (reload.TrySnapWalkable(new Vector3(start.X, start.Y, 1000f), out _, out _))
                throw new InvalidOperationException("TrySnapWalkable unexpectedly found a polygon outside its vertical search range.");
            if (!reload.Tiles.Lock.TryEnterWriteLock(0))
                throw new InvalidOperationException("A failed TrySnapWalkable call leaked the Detour read lock.");
            reload.Tiles.Lock.ExitWriteLock();

            NavmeshPathQueryResult firstLoad = reload.FindPathWithResult(start, start, null);
            NavmeshPathQueryResult secondLoad = reload.FindPathWithResult(end, end, null);
            if (firstLoad.Path is null || firstLoad.Diagnostic is not null ||
                secondLoad.Path is null || secondLoad.Diagnostic is not null ||
                reload.Tiles.ResidentTiles.Contains((1, 2)) ||
                reload.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.ExpectedDnmNotLoaded ||
                reload.Tiles.GetTileStatus(2, 2) != NavmeshRuntimeTileStatus.VerifiedEmptyNavmeshTile)
            {
                throw new InvalidOperationException("The DNM reload fixture did not load two tiles, evict the first, and report it as ExpectedDnmNotLoaded.");
            }

            NavmeshPathQueryResult validReload = reload.FindPathWithResult(start, start, null);
            if (validReload.Path is null || validReload.Diagnostic is not null ||
                reload.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.Loaded)
                throw new InvalidOperationException("A valid Forever DNM did not reload as Loaded after LRU eviction.");

            NavmeshPathQueryResult evictAgain = reload.FindPathWithResult(end, end, null);
            if (evictAgain.Path is null || evictAgain.Diagnostic is not null ||
                reload.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.ExpectedDnmNotLoaded)
                throw new InvalidOperationException("A reloaded Forever DNM did not return to ExpectedDnmNotLoaded after eviction.");

            byte[] substitutedDnm = CreateDnmBytes(1, 2, groundHeight: 12f);
            File.WriteAllBytes(tileOne, substitutedDnm);
            NavmeshPathQueryResult substituted = reload.FindPathWithResult(start, start, null);
            if (substituted.Path is not null ||
                substituted.Diagnostic?.Reason != NavmeshPathFailureReason.MissingOrInvalidNavmeshTile ||
                substituted.Diagnostic.AffectedTile != new NavmeshTileCoordinate(1, 2) ||
                !File.ReadAllBytes(tileOne).SequenceEqual(substitutedDnm))
            {
                throw new InvalidOperationException("An evicted Forever tile was reloaded without matching its manifest hash, or was modified during rejection.");
            }

            NavmeshPathQueryResult unaffected = reload.FindPathWithResult(end, end, null);
            if (unaffected.Path is null || unaffected.Diagnostic is not null)
            {
                throw new InvalidOperationException("A valid Forever tile stopped working after a neighboring published DNM failed its reload hash check.");
            }
        }

        File.WriteAllBytes(tileOne, validDnm);

        string mapOneCache = Path.Combine(root, "runtime-diagnostic-map1");
        WriteCache(mapOneCache, product, build, 1, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent,
            bakedTiles: new[] { (2, 2) });
        ForeverNavmeshRuntimeCoverage mapOneCoverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            mapOneCache, product, build, settingsHash, 1, continent);
        if (coverage.MapId == mapOneCoverage.MapId || coverage.SettingsHash != mapOneCoverage.SettingsHash ||
            coverage.ContainsVerifiedEmptyTile(2, 2) != true || mapOneCoverage.ContainsVerifiedEmptyTile(2, 2) ||
            !mapOneCoverage.ContainsBakedTile(2, 2))
        {
            throw new InvalidOperationException("MapID changes reused a previous map's verified-empty tile state.");
        }
        using (NavmeshPathfinder mapOne = CreateDiagnosticPathfinder(mapOneCache, mapOneCoverage))
        {
            Vector3 mapOnePoint = TileWorldPoint(2, 2);
            NavmeshPathQueryResult mapOneResult = mapOne.FindPathWithResult(mapOnePoint, mapOnePoint, null);
            if (mapOneResult.Path is null || mapOneResult.Diagnostic is not null ||
                mapOneResult.RequestedStart != mapOnePoint || mapOne.LastQueryResult?.QueryId != mapOneResult.QueryId)
                throw new InvalidOperationException("MapID 1 did not load its own DNM at a coordinate empty on MapID 0.");
        }

        ExpectFailure(() => ForeverNavmeshCacheManifest.ReadCoverageForRuntime(cache, product, build + 1,
                settingsHash, mapId, continent),
            "A runtime coverage snapshot was reused across a client Build change.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ReadCoverageForRuntime(cache, product, build,
                "changed-settings", mapId, continent),
            "A runtime coverage snapshot was reused across a bake configuration hash change.");

        string legacyCache = Path.Combine(root, "runtime-diagnostic-legacy-no-manifest");
        Directory.CreateDirectory(legacyCache);
        WriteDnm(legacyCache, 1, 2);
        WriteDnm(legacyCache, 3, 2);
        using NavmeshPathfinder legacy = CreateDiagnosticPathfinder(legacyCache, runtimeCoverage: null, maxResidentTiles: 1);
        if (legacy.FindPath(start, start, null) is null || legacy.LastDiagnostic is not null ||
            legacy.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.Loaded)
            throw new InvalidOperationException("A Classic-era-style cache without a Forever manifest changed its legacy path behavior.");
        if (legacy.FindPath(end, end, null) is null || legacy.LastDiagnostic is not null ||
            legacy.Tiles.GetTileStatus(1, 2) == NavmeshRuntimeTileStatus.Loaded)
            throw new InvalidOperationException("A Classic dynamic cache continued reporting an LRU-evicted tile as Loaded.");
        if (legacy.FindPath(start, start, null) is null || legacy.LastDiagnostic is not null ||
            legacy.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.Loaded)
            throw new InvalidOperationException("A Classic dynamic tile did not reload after LRU eviction.");

        RunBackgroundLoadDuringHeightRegression(root, product, build, mapId, settingsHash, continent, start);
        RunLruEvictionDuringSnapRegression(root, product, build, mapId, settingsHash, continent, start);

        using NavmeshPathfinder concurrent = CreateDiagnosticPathfinder(cache, coverage);
        using ManualResetEventSlim startTogether = new(false);
        Task<NavmeshPathQueryResult> validTask = Task.Run(() =>
        {
            startTogether.Wait();
            return concurrent.FindPathWithResult(start, start, null);
        });
        Task<NavmeshPathQueryResult> emptyTask = Task.Run(() =>
        {
            startTogether.Wait();
            return concurrent.FindPathWithResult(empty, empty, null);
        });
        startTogether.Set();
        Task.WaitAll(validTask, emptyTask);
        NavmeshPathQueryResult validResult = validTask.Result;
        NavmeshPathQueryResult emptyResult = emptyTask.Result;
        NavmeshPathQueryResult concurrentLatest = concurrent.LastQueryResult
            ?? throw new InvalidOperationException("Concurrent queries did not publish a latest-result snapshot.");
        if (validResult.QueryId == emptyResult.QueryId || validResult.Path is null || validResult.Diagnostic is not null ||
            emptyResult.Path is not null || emptyResult.Diagnostic?.Reason != NavmeshPathFailureReason.VerifiedEmptyNavmeshTile ||
            validResult.RequestedStart != start || validResult.RequestedEnd != start ||
            emptyResult.RequestedStart != empty || emptyResult.RequestedEnd != empty ||
            concurrentLatest.QueryId != validResult.QueryId && concurrentLatest.QueryId != emptyResult.QueryId ||
            concurrentLatest.Diagnostic != concurrent.LastDiagnostic)
        {
            throw new InvalidOperationException("Concurrent valid and verified-empty queries lost their per-call paths, endpoints, IDs, or diagnostics.");
        }

        using ManualResetEventSlim failuresTogether = new(false);
        Task<NavmeshPathQueryResult> emptyFailureTask = Task.Run(() =>
        {
            failuresTogether.Wait();
            return concurrent.FindPathWithResult(empty, empty, null);
        });
        Task<NavmeshPathQueryResult> coverageFailureTask = Task.Run(() =>
        {
            failuresTogether.Wait();
            return concurrent.FindPathWithResult(outside, start, null);
        });
        failuresTogether.Set();
        Task.WaitAll(emptyFailureTask, coverageFailureTask);
        NavmeshPathQueryResult emptyFailure = emptyFailureTask.Result;
        NavmeshPathQueryResult coverageFailure = coverageFailureTask.Result;
        if (emptyFailure.QueryId == coverageFailure.QueryId ||
            emptyFailure.Diagnostic?.Reason != NavmeshPathFailureReason.VerifiedEmptyNavmeshTile ||
            emptyFailure.Diagnostic.AffectedTile != new NavmeshTileCoordinate(2, 2) ||
            coverageFailure.Diagnostic?.Reason != NavmeshPathFailureReason.OutsideCertifiedCoverage ||
            coverageFailure.Diagnostic.AffectedTile != new NavmeshTileCoordinate(4, 2) ||
            emptyFailure.RequestedStart != empty || coverageFailure.RequestedStart != outside)
        {
            throw new InvalidOperationException("Concurrent failure queries cross-assigned their failure reasons, coordinates, or affected tiles.");
        }

        RunInterleavedHeightSnapPathRegression(concurrent, start, end);
    }

    private static bool RunQueryBehindDetourWriter(NavmeshPathfinder pathfinder, Func<bool> query,
        string scenario, Action? whileWriteLocked = null)
    {
        using ManualResetEventSlim started = new(false);
        bool succeeded = false;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            started.Set();
            try
            {
                succeeded = query();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        bool waitedForLock = false;
        pathfinder.Tiles.Lock.EnterWriteLock();
        try
        {
            whileWriteLocked?.Invoke();
            thread.Start();
            if (!started.Wait(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException($"{scenario} query thread did not start.");
            waitedForLock = SpinWait.SpinUntil(
                () => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            pathfinder.Tiles.Lock.ExitWriteLock();
        }

        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException($"{scenario} deadlocked after the Detour write lock was released.");
        if (!waitedForLock)
            throw new InvalidOperationException($"{scenario} accessed DtNavMesh without waiting for its write lock.");
        if (failure is not null)
            throw new InvalidOperationException($"{scenario} threw while waiting for the Detour write lock.", failure);
        return succeeded;
    }

    private static void RunBackgroundLoadDuringHeightRegression(string root, string product, int build,
        int mapId, string settingsHash, string continent, Vector3 point)
    {
        string cache = Path.Combine(root, "runtime-diagnostic-background-height-load");
        WriteCache(cache, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent,
            bakedTiles: new[] { (1, 2), (3, 2) });
        ForeverNavmeshRuntimeCoverage coverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        using NavmeshPathfinder pathfinder = CreateDiagnosticPathfinder(cache, coverage, maxResidentTiles: 2);
        using ManualResetEventSlim startTogether = new(false);
        using ManualResetEventSlim tileThreeAdded = new(false);
        pathfinder.Tiles.NotifyTileAdded += (x, z, _) =>
        {
            if (x == 3 && z == 2)
                tileThreeAdded.Set();
        };

        Task prefetch = Task.Run(() =>
        {
            startTogether.Wait();
            pathfinder.Tiles.Prefetch(3, 2);
        });
        Task heightQueries = Task.Run(() =>
        {
            startTogether.Wait();
            for (int i = 0; i < 8; i++)
            {
                if (!pathfinder.TryGetHeight(point.X, point.Y, out float height) || MathF.Abs(height - 10f) > NavmeshSettings.CellHeight)
                    throw new InvalidOperationException("TryGetHeight failed while a background worker loaded another DNM tile.");
            }
        });

        startTogether.Set();
        if (!Task.WaitAll([prefetch, heightQueries], TimeSpan.FromSeconds(10)) ||
            !tileThreeAdded.Wait(TimeSpan.FromSeconds(5)) ||
            pathfinder.Tiles.GetTileStatus(3, 2) != NavmeshRuntimeTileStatus.Loaded)
            throw new InvalidOperationException("The controlled background DNM load did not complete alongside height queries.");
    }

    private static void RunLruEvictionDuringSnapRegression(string root, string product, int build,
        int mapId, string settingsHash, string continent, Vector3 point)
    {
        string cache = Path.Combine(root, "runtime-diagnostic-background-lru-snap");
        WriteCache(cache, product, build, mapId, settingsHash, "continent",
            completeForRequested: true, publishable: true, continent,
            bakedTiles: new[] { (1, 2), (3, 2), (5, 2) });
        ForeverNavmeshRuntimeCoverage coverage = ForeverNavmeshCacheManifest.ReadCoverageForRuntime(
            cache, product, build, settingsHash, mapId, continent);
        using NavmeshPathfinder pathfinder = CreateDiagnosticPathfinder(cache, coverage, maxResidentTiles: 2);
        Vector3 olderTile = TileWorldPoint(3, 2);
        if (pathfinder.FindPath(olderTile, olderTile, null) is null ||
            pathfinder.FindPath(point, point, null) is null)
            throw new InvalidOperationException("Could not establish the deterministic LRU order for the snap query fixture.");

        using ManualResetEventSlim tileFiveAdded = new(false);
        using ManualResetEventSlim tileThreeRemoved = new(false);
        pathfinder.Tiles.NotifyTileAdded += (x, z, _) =>
        {
            if (x == 5 && z == 2)
                tileFiveAdded.Set();
        };
        pathfinder.Tiles.NotifyTileRemoved += (x, z) =>
        {
            if (x == 3 && z == 2)
                tileThreeRemoved.Set();
        };

        bool snapped = RunQueryBehindDetourWriter(pathfinder, () =>
            pathfinder.TrySnapWalkable(point, out _, out long polyRef) && polyRef != 0,
            "TrySnapWalkable during LRU eviction",
            whileWriteLocked: () => pathfinder.Tiles.Prefetch(5, 2));
        if (!snapped || !tileFiveAdded.Wait(TimeSpan.FromSeconds(5)) ||
            !tileThreeRemoved.Wait(TimeSpan.FromSeconds(5)) ||
            pathfinder.Tiles.GetTileStatus(1, 2) != NavmeshRuntimeTileStatus.Loaded ||
            pathfinder.Tiles.GetTileStatus(3, 2) != NavmeshRuntimeTileStatus.ExpectedDnmNotLoaded ||
            pathfinder.Tiles.GetTileStatus(5, 2) != NavmeshRuntimeTileStatus.Loaded)
        {
            throw new InvalidOperationException("TrySnapWalkable did not remain valid while a worker loaded a tile and LRU-evicted a different resident tile.");
        }
    }

    private static void RunInterleavedHeightSnapPathRegression(NavmeshPathfinder pathfinder,
        Vector3 first, Vector3 second)
    {
        using ManualResetEventSlim startTogether = new(false);
        Task[] queries = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            startTogether.Wait();
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = ((worker + i) & 1) == 0 ? first : second;
                if (!pathfinder.TryGetHeight(point.X, point.Y, out float height) || MathF.Abs(height - 10f) > NavmeshSettings.CellHeight ||
                    !pathfinder.TrySnapWalkable(point, out _, out long polyRef) || polyRef == 0)
                    throw new InvalidOperationException("Interleaved height/snap queries returned an invalid result.");

                NavmeshPathQueryResult result = pathfinder.FindPathWithResult(point, point, null);
                if (result.Path is null || result.Diagnostic is not null)
                    throw new InvalidOperationException("FindPath changed behavior while height and snap queries ran concurrently.");
            }
        })).ToArray();
        startTogether.Set();
        if (!Task.WaitAll(queries, TimeSpan.FromSeconds(15)))
            throw new InvalidOperationException("Concurrent height, snap, and FindPath queries deadlocked or exceeded the regression timeout.");
    }

    private static void AddVerifiedEmptyTile(string cacheDirectory, string product, int build, int mapId,
        string settingsHash, (int X, int Z) tile)
    {
        string manifestPath = Path.Combine(cacheDirectory, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        JsonObject bakeSettings = manifest["bake_settings"]!.AsObject();
        bakeSettings["dot_recast_version"] = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        JsonObject coverage = manifest["coverage"]!.AsObject();
        HashSet<(int X, int Z)> allTiles = coverage["expected_tile_indices"]!.AsArray()
            .Select(static pair => (pair![0]!.GetValue<int>(), pair[1]!.GetValue<int>())).ToHashSet();
        if (!allTiles.Add(tile))
            throw new InvalidOperationException($"Empty fixture tile ({tile.X},{tile.Z}) already exists in its DNM set.");
        int[][] tilePairs = allTiles.OrderBy(static pair => pair.X).ThenBy(static pair => pair.Z)
            .Select(static pair => new[] { pair.X, pair.Z }).ToArray();
        coverage["expected_tile_indices"] = JsonSerializer.SerializeToNode(tilePairs);
        coverage["completed_tile_indices"] = JsonSerializer.SerializeToNode(tilePairs);

        string sourceHash = manifest["source_adts"]![0]!["sha256"]!.GetValue<string>();
        string sourceSetHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"0,0:{sourceHash}"))).ToLowerInvariant();
        JsonObject payload = new()
        {
            ["format"] = ForeverNavmeshCacheManifest.EmptyNavmeshEvidenceFormat,
            ["result"] = "completed_no_walkable_polygons",
            ["client_product"] = product,
            ["client_build"] = build,
            ["map_id"] = mapId,
            ["dt_tile_x"] = tile.X,
            ["dt_tile_z"] = tile.Z,
            ["settings_hash"] = settingsHash,
            ["dotrecast_version"] = bakeSettings["dot_recast_version"]!.GetValue<string>(),
            ["geometry_export_version"] = bakeSettings["geometry_export_version"]!.GetValue<string>(),
            ["raw_geometry_format"] = bakeSettings["raw_geometry_format"]!.GetValue<string>(),
            ["polygon_count"] = 0,
            ["geometry_sha256"] = new string('b', 64),
            ["source_set_sha256"] = sourceSetHash,
            ["source_adts_xy"] = JsonSerializer.SerializeToNode(new[] { new[] { 0, 0 } }),
            ["source_sha256s"] = JsonSerializer.SerializeToNode(new[] { sourceHash }),
            ["terrain_triangles"] = 1,
            ["collision_triangles"] = 0,
            ["liquid_triangles"] = 0,
        };
        byte[] evidenceBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        string evidenceHash = Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        string evidenceFile = $"{ForeverNavmeshCacheManifest.EmptyNavmeshEvidencePathPrefix}{tile.X}-{tile.Z}-{evidenceHash}.json";
        File.WriteAllBytes(Path.Combine(cacheDirectory, evidenceFile.Replace('/', Path.DirectorySeparatorChar)), evidenceBytes);
        manifest["empty_navmesh_tiles"] = new JsonArray(new JsonObject
        {
            ["evidence_file"] = evidenceFile,
            ["evidence_sha256"] = evidenceHash,
            ["evidence"] = payload.DeepClone(),
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    private static void WriteDnm(string directory, int tileX, int tileZ, float groundHeight = 10f)
    {
        Directory.CreateDirectory(directory);
        WriteDnmAtPath(Path.Combine(directory, $"tile_{tileX}_{tileZ}.dnm"), tileX, tileZ, groundHeight);
    }

    private static void WriteDnmAtPath(string path, int headerTileX, int headerTileZ, float groundHeight)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        new DtMeshDataWriter().Write(writer,
            CreateGroundTileData(headerTileX, headerTileZ, groundHeight), RcByteOrder.LITTLE_ENDIAN, false);
    }

    private static byte[] CreateDnmBytes(int tileX, int tileZ, float groundHeight)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            new DtMeshDataWriter().Write(writer,
                CreateGroundTileData(tileX, tileZ, groundHeight), RcByteOrder.LITTLE_ENDIAN, false);
        }
        return stream.ToArray();
    }

    private static NavmeshPathfinder CreateDiagnosticPathfinder(string cacheDirectory,
        ForeverNavmeshRuntimeCoverage? runtimeCoverage, int maxResidentTiles = NavmeshTileCache.MaxResidentTiles) => new(
            NullLogger<NavmeshPathfinder>.Instance, null, cacheDirectory, new NavmeshBakeOptions(),
            new NavmeshQueryOptions(), runtimeCoverage: runtimeCoverage, maxResidentTiles: maxResidentTiles);

    private static Vector3 TileWorldPoint(int tileX, int tileZ)
    {
        NavmeshCoords.GetTileWowBounds(tileX, tileZ, out float minX, out float minY, out _, out _);
        return new Vector3(minX + 40f, minY + 40f, 10f);
    }

    private static void AssertPathFailure(NavmeshPathfinder pathfinder, Vector3 start, Vector3 end,
        NavmeshPathFailureReason expectedReason, NavmeshTileCoordinate expectedAffected, string scenario)
    {
        if (pathfinder.FindPath(start, end, null) is not null ||
            pathfinder.LastDiagnostic?.Reason != expectedReason ||
            pathfinder.LastDiagnostic.AffectedTile != expectedAffected)
        {
            throw new InvalidOperationException($"Runtime path diagnostic did not identify {scenario} as {expectedReason} at ({expectedAffected.X},{expectedAffected.Z}).");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static byte[] CreateNativeTileList(int build, IReadOnlyCollection<(int X, int Y)> adts)
    {
        byte[] data = new byte[ForeverWdtTileList.FileLength];
        "MAPS"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, sizeof(uint)), ForeverWdtTileList.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, sizeof(uint)), checked((uint)build));
        data.AsSpan(12).Fill((byte)'0');
        foreach ((int x, int y) in adts)
        {
            int bitIndex = y * 64 + x;
            data[12 + ForeverWdtTileList.TileCount - 1 - bitIndex] = (byte)'1';
        }
        return data;
    }

    private static (string RelativePath, string Sha256) WriteCoverageBlob(string cacheDirectory,
        string pathPrefix, string suffix, byte[] content)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string relativePath = pathPrefix + sha256 + suffix;
        string fullPath = Path.Combine(cacheDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return (relativePath, sha256);
    }

    private static string ReadCoverageProofPath(string cacheDirectory)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cacheDirectory, "manifest.json")));
        string relative = manifest.RootElement.GetProperty("coverage").GetProperty("wdt_tile_list_file").GetString()!;
        return Path.Combine(cacheDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteCoverageProofVariant(string cacheDirectory, JsonObject proof, JsonObject manifest)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(proof.ToJsonString());
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string relative = ForeverNavmeshCacheManifest.WdtCoverageEvidencePathPrefix + hash + ".json";
        string path = Path.Combine(cacheDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        JsonObject coverage = manifest["coverage"]!.AsObject();
        coverage["wdt_tile_list_file"] = relative;
        coverage["wdt_tile_list_sha256"] = hash;
        File.WriteAllText(Path.Combine(cacheDirectory, "manifest.json"), manifest.ToJsonString());
    }

    private static void RunCoverageBoundaryRegression(string root)
    {
        double origin = NavmeshCoords.Origin;
        double size = NavmeshSettings.TileWorldSize;
        const int tileX = 80;
        const int tileZ = 120;
        GeometryExportRequest noPadding = new(0, 46, 33, AgentRadius: 0f, PaddingYards: 0f);
        double worldY = origin + tileX * size;
        double worldX = origin + tileZ * size;

        AssertCoverage("triangle inside one tile",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(worldX + 45, worldX + 55, worldY + 45, worldY + 55)), noPadding),
            (80, 120));

        AssertCoverage("triangle crossing two adjacent tiles",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(worldX + 45, worldX + 55, origin + 81 * size - 4, origin + 81 * size + 4)), noPadding),
            (80, 120), (81, 120));

        AssertCoverage("triangle crossing a four-tile corner",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(origin + 121 * size - 4, origin + 121 * size + 4,
                    origin + 81 * size - 4, origin + 81 * size + 4)), noPadding),
            (80, 120), (80, 121), (81, 120), (81, 121));

        double exactYBoundary = origin + 81 * size;
        AssertCoverage("triangle exactly on a tile edge",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(worldX + 45, worldX + 55, exactYBoundary, exactYBoundary)), noPadding),
            (80, 120), (81, 120));
        AssertCoverage("sub-centimetre boundary tolerance includes the adjacent tile",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(worldX + 45, worldX + 55,
                    exactYBoundary - TileGeometryExtractor.BorderCells(0f) * NavmeshSettings.CellSize - 0.005,
                    exactYBoundary - TileGeometryExtractor.BorderCells(0f) * NavmeshSettings.CellSize - 0.005)), noPadding),
            (80, 120), (81, 120));
        AssertCoverage("geometry beyond boundary tolerance stays in its tile",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(worldX + 45, worldX + 55,
                    exactYBoundary - TileGeometryExtractor.BorderCells(0f) * NavmeshSettings.CellSize - 0.02,
                    exactYBoundary - TileGeometryExtractor.BorderCells(0f) * NavmeshSettings.CellSize - 0.02)), noPadding),
            (80, 120));

        double negativeWorldX = origin + 45 * size;
        double negativeWorldY = origin + 33 * size;
        if (negativeWorldX >= 0 || negativeWorldY >= 0)
            throw new InvalidOperationException("The negative-coordinate boundary fixture did not use negative world coordinates.");
        AssertCoverage("negative world coordinates",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(negativeWorldX + 45, negativeWorldX + 55,
                    negativeWorldY + 45, negativeWorldY + 55)), noPadding),
            (33, 45));

        AssertCoverage("lower map edge clamps to tile zero",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(origin + 7, origin + 9, origin + 7, origin + 9)), noPadding),
            (0, 0));
        int lastTile = NavmeshSettings.TilesPerSide - 1;
        double lastTileOrigin = origin + lastTile * size;
        AssertCoverage("upper map edge clamps to the last valid tile",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(
                CoverageTriangle(lastTileOrigin + size - 9, lastTileOrigin + size - 7,
                    lastTileOrigin + size - 9, lastTileOrigin + size - 7)), noPadding),
            (lastTile, lastTile));

        float[][] nearBoundary = CoverageTriangle(worldX + 45, worldX + 55,
            origin + 81 * size - 3, origin + 81 * size - 2);
        AssertCoverage("padding disabled",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(nearBoundary), noPadding),
            (80, 120));
        GeometryExportRequest padded = noPadding with { PaddingYards = 4f };
        AssertCoverage("padding adds the neighboring tile",
            RawGeometryTileCoverageVerifier.Calculate(CollisionDocument(nearBoundary), padded),
            (80, 120), (81, 120));

        double secondWorldX = origin + 123 * size;
        double secondWorldY = origin + 84 * size;
        RawGeometryDocument floorDocument = CollisionDocument(
            CoverageTriangle(worldX + 45, worldX + 55, worldY + 45, worldY + 55, height: 5),
            CoverageTriangle(secondWorldX + 45, secondWorldX + 55,
                secondWorldY + 45, secondWorldY + 55, height: 30));
        AssertCoverage("MinWorldZ before filtering",
            RawGeometryTileCoverageVerifier.Calculate(floorDocument, noPadding),
            (80, 120), (84, 123));
        AssertCoverage("MinWorldZ after filtering low geometry",
            RawGeometryTileCoverageVerifier.Calculate(floorDocument, noPadding with { MinWorldZ = 20f }),
            (84, 123));

        float[][] crossingTriangle = CoverageTriangle(worldX + 45, worldX + 55,
            origin + 81 * size - 4, origin + 81 * size + 4);
        string fixturePath = WriteCoverageRawGeoFixture(root, crossingTriangle);
        IReadOnlyList<ForeverGeometryTile> exported = RawGeometryTileExporter.Export(fixturePath, noPadding);
        AssertTileSet("rawgeo exporter crossing edge", exported.Select(static tile => (tile.DtTileX, tile.DtTileZ)),
            (80, 120), (81, 120));

        string missing = CaptureFailure(() => RawGeometryTileExporter.ExportWithCoverageOverridesForTest(
            fixturePath, noPadding, null, tiles => tiles.Take(1).ToArray()));
        if (!missing.Contains("missing=[(81,120)]", StringComparison.Ordinal))
            throw new InvalidOperationException($"Exporter omission did not report the exact missing tile: {missing}");

        string extra = CaptureFailure(() => RawGeometryTileExporter.ExportWithCoverageOverridesForTest(
            fixturePath, noPadding, null, tiles => [.. tiles, CreateCoverageTile(82, 120)]));
        if (!extra.Contains("extra=[(82,120)]", StringComparison.Ordinal))
            throw new InvalidOperationException($"Exporter extra tile did not report the exact extra tile: {extra}");

        HashSet<(int X, int Z)> inconsistentIndependentSet = [(81, 120)];
        string independentMismatch = CaptureFailure(() => RawGeometryTileExporter.ExportWithCoverageOverridesForTest(
            fixturePath, noPadding, inconsistentIndependentSet, null));
        if (!independentMismatch.Contains("extra=[(80,120)]", StringComparison.Ordinal))
            throw new InvalidOperationException($"Independent coverage mismatch did not reject the exact disagreement: {independentMismatch}");
    }

    private static void AssertCoverage(string name, HashSet<(int X, int Z)> actual,
        params (int X, int Z)[] expected) => AssertTileSet(name, actual, expected);

    private static void AssertTileSet(string name, IEnumerable<(int X, int Z)> actual,
        params (int X, int Z)[] expected)
    {
        HashSet<(int X, int Z)> actualSet = actual.ToHashSet();
        HashSet<(int X, int Z)> expectedSet = expected.ToHashSet();
        if (!actualSet.SetEquals(expectedSet))
        {
            string Format(IEnumerable<(int X, int Z)> tiles) => string.Join(",", tiles.OrderBy(t => t.X)
                .ThenBy(t => t.Z).Select(t => $"({t.X},{t.Z})"));
            throw new InvalidOperationException(
                $"{name}: expected [{Format(expectedSet)}], actual [{Format(actualSet)}].");
        }
    }

    private static float[][] CoverageTriangle(double minWowX, double maxWowX,
        double minWowY, double maxWowY, float height = 10f) =>
    [
        [(float)minWowX, height, (float)minWowY],
        [(float)maxWowX, height, (float)minWowY],
        [(float)minWowX, height + 1f, (float)maxWowY],
    ];

    private static RawGeometryDocument CollisionDocument(params float[][][] triangles)
    {
        float[][] vertices = triangles.SelectMany(static triangle => triangle).ToArray();
        int[][] indices = Enumerable.Range(0, triangles.Length)
            .Select(static triangle => new[] { triangle * 3, triangle * 3 + 1, triangle * 3 + 2 }).ToArray();
        return new RawGeometryDocument
        {
            CollisionVertices = vertices,
            CollisionTriangles = indices,
        };
    }

    private static string WriteCoverageRawGeoFixture(string root, float[][] collisionVertices)
    {
        string path = Path.Combine(root, "coverage-boundary.rawgeo.json");
        var raw = new
        {
            format = NavmeshSettings.ForeverRawGeometryFormat,
            map_id = 0,
            adt_xy = new[] { 46, 33 },
            mmap_xy = new[] { 33, 46 },
            client_product = "wow_cn_beta",
            client_build = 70094,
            units = "yard",
            coordinates = "[WoW_X,WoW_Z,WoW_Y]",
            target_rc_transform = "[raw_Z,raw_Y,raw_X]",
            terrain_vertex_count = 0,
            terrain_triangle_count = 0,
            collision_vertex_count = collisionVertices.Length,
            collision_triangle_count = 1,
            wmo_triangle_count = 1,
            m2_triangle_count = 0,
            liquid_vertex_count = 0,
            liquid_triangle_count = 0,
            liquid_type_count = 0,
            terrain_vertices = Array.Empty<float[]>(),
            terrain_triangles = Array.Empty<int[]>(),
            collision_vertices = collisionVertices,
            collision_triangles = new[] { new[] { 0, 1, 2 } },
            liquid_vertices = Array.Empty<float[]>(),
            liquid_triangles = Array.Empty<int[]>(),
            liquid_types = Array.Empty<int>(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(raw));
        return path;
    }

    private static ForeverGeometryTile CreateCoverageTile(int tileX, int tileZ)
    {
        GeometryCounts counts = new()
        {
            TerrainTriangles = 0,
            CollisionTriangles = 1,
            WmoCollisionTriangles = 1,
            M2CollisionTriangles = 0,
            LiquidTriangles = 0,
            WaterTriangles = 0,
            MagmaSlimeTriangles = 0,
        };
        return new ForeverGeometryTile
        {
            ClientProduct = "wow_cn_beta",
            ClientBuild = 70094,
            SourceSha256 = new string('0', 64),
            MapId = 0,
            AdtXY = [46, 33],
            DtTileX = tileX,
            DtTileZ = tileZ,
            BMinRc = [0, 0, 0],
            BMaxRc = [1, 1, 1],
            SourceCounts = counts,
            TileCounts = counts,
            TerrainVerticesRc = [],
            TerrainTriangles = [],
            CollisionVerticesRc = [],
            CollisionTriangles = [],
            LiquidVerticesRc = [],
            LiquidTriangles = [],
            LiquidAreas = [],
        };
    }

    private static string CaptureFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex.Message; }
        throw new InvalidOperationException("An expected tile coverage mismatch was accepted.");
    }

    private static void RunBatchRunnerRegression(string root)
    {
        string inputRoot = Path.Combine(root, "batch-input");
        string adtFolder = Path.Combine(inputRoot, "adt46_33", "raw-geometry");
        Directory.CreateDirectory(adtFolder);
        string rawPath = Path.Combine(adtFolder, "map0000_adt46_33.rawgeo.json");
        WriteBatchFixture(rawPath);
        string outputRoot = Path.Combine(root, "batch-state");
        string dnmRoot = Path.Combine(root, "batch-dnm");
        ForeverGeometryBatchRequest request = new(0, 46, 33, 46, 33,
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
        Directory.CreateDirectory(Path.Combine(clientRoot, "Data"));
        File.WriteAllText(Path.Combine(clientRoot, ".build.info"),
            "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70094|wow_cn_beta\n");
        string toolDirectory = CreateFakeMapExtractorTool(root);
        string wdtListLog = Path.Combine(root, "full-wdt-tile-list.log");
        string cascDataPath = Path.Combine(clientRoot, "Data");
        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 45 33\ntile 0 46 33\n");
        byte[] rectangleManifestBeforeSeal = File.ReadAllBytes(Path.Combine(dnmRoot, "manifest.json"));
        ExpectSealRejectedBeforeWdt(configChanged.StateFile, clientRoot, toolDirectory,
            "A rectangle batch was accepted for continent publication.");
        if (!rectangleManifestBeforeSeal.SequenceEqual(File.ReadAllBytes(Path.Combine(dnmRoot, "manifest.json"))))
            throw new InvalidOperationException("Rejecting rectangle publication changed the existing manifest.");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublication(configChanged.StateFile,
                clientRoot, wdtListLog),
            "An external text log was accepted as a verified mapextractor tool directory.");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot,
                "wow_cn_beta", 70094, ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
                    new NavmeshBakeOptions { AgentRadius = 0.6f }).SettingsHash, 0, "Azeroth"),
            "A failed publication gate changed the selected batch manifest.");

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 46 33\n");
        ForeverGeometryBatchRequest explicitRequest = request with
        {
            AgentRadius = 0.6f,
            Resume = true,
            AdtList = [new AdtCoordinate(46, 33)],
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

        ExpectSealRejectedBeforeWdt(Path.Combine(failedOutputRoot, "batch-state.json"), clientRoot, toolDirectory,
            "A failed ADT source task reached WDT enumeration or publication.");

        byte[] validRawGeometry = File.ReadAllBytes(rawPath);
        try
        {
            File.AppendAllText(rawPath, "tampered");
            ExpectSealRejectedBeforeWdt(explicitBatch.StateFile, clientRoot, toolDirectory,
                "Publication accepted rawgeo whose SHA-256 changed after processing.");
        }
        finally
        {
            File.WriteAllBytes(rawPath, validRawGeometry);
        }

        string explicitDnmPath;
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(explicitBatch.StateFile)))
        {
            explicitDnmPath = state.RootElement.GetProperty("TileTasks")[0].GetProperty("DnmOutputPath").GetString()!;
        }
        byte[] validDnm = File.ReadAllBytes(explicitDnmPath);
        try
        {
            File.Delete(explicitDnmPath);
            ExpectSealRejectedBeforeWdt(explicitBatch.StateFile, clientRoot, toolDirectory,
                "Publication accepted a missing DNM tile.");
            File.WriteAllBytes(explicitDnmPath, validDnm);
            using (FileStream stream = File.Open(explicitDnmPath, FileMode.Append, FileAccess.Write, FileShare.None))
                stream.WriteByte(0x7F);
            ExpectSealRejectedBeforeWdt(explicitBatch.StateFile, clientRoot, toolDirectory,
                "Publication accepted a DNM tile whose SHA-256 changed after baking.");
        }
        finally
        {
            File.WriteAllBytes(explicitDnmPath, validDnm);
        }
        _ = ForeverGeometryBatchRunner.VerifyState(explicitBatch.StateFile);

        string buildInfoPath = Path.Combine(clientRoot, ".build.info");
        string validBuildInfo = File.ReadAllText(buildInfoPath);
        byte[] selectedManifestBeforeIdentityMismatch = File.ReadAllBytes(Path.Combine(dnmRoot, "manifest.json"));
        File.WriteAllText(buildInfoPath,
            "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70094|wow_classic\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublicationForTest(explicitBatch.StateFile,
                clientRoot, toolDirectory, "zhCN", CreateWdtFixtureProcessFactory("success", "46,33")),
            "A client Product different from the batch identity was accepted.");
        File.WriteAllText(buildInfoPath,
            "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70095|wow_cn_beta\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublicationForTest(explicitBatch.StateFile,
                clientRoot, toolDirectory, "zhCN", CreateWdtFixtureProcessFactory("success", "46,33")),
            "A client Build different from the batch identity was accepted.");
        File.WriteAllText(buildInfoPath, validBuildInfo);
        if (!selectedManifestBeforeIdentityMismatch.SequenceEqual(File.ReadAllBytes(Path.Combine(dnmRoot, "manifest.json"))))
            throw new InvalidOperationException("Client Product/Build mismatch damaged the selected batch manifest.");

        string invalidToolDirectory = CreateFakeMapExtractorTool(root, validProvenance: false);
        bool invalidToolStartedProcess = false;
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublicationForTest(explicitBatch.StateFile,
                clientRoot, invalidToolDirectory, "zhCN", (executable, arguments) =>
                {
                    invalidToolStartedProcess = true;
                    return CreateWdtFixtureProcessFactory("success", "46,33")(executable, arguments);
                }),
            "A tool built from an unexpected TrinityCore patch was accepted.");
        if (invalidToolStartedProcess)
            throw new InvalidOperationException("An unverified mapextractor executable was started.");

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 45 33\ntile 0 46 33\n");
        ExpectSealFailure(explicitBatch.StateFile, clientRoot, toolDirectory, "success", "45,33",
            "A requested ADT set different from the matching WDT list was published.",
            expectedFailureContains: "WDT-only=[(45,33)]");
        ExpectSealFailure(explicitBatch.StateFile, clientRoot, toolDirectory, "success", "46,33;47,33",
            "A WDT list with an extra ADT was published.", expectedFailureContains: "WDT-only=[(47,33)]");
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot,
                "wow_cn_beta", 70094, ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
                    new NavmeshBakeOptions { AgentRadius = 0.6f }).SettingsHash, 0, "Azeroth"),
            "A rejected WDT coverage mismatch changed the selected manifest.");

        foreach (string failureMode in new[]
                 {
                     "nonzero", "truncated", "incomplete", "duplicate", "wrong-map", "wrong-build",
                     "wrong-product", "wrong-locale", "wrong-casc-root", "wrong-casc-root-unqualified",
                     "fatal", "stderr-fatal",
                     "missing-native", "native-mismatch", "native-wrong-build", "native-truncated",
                 })
        {
            ExpectSealFailure(explicitBatch.StateFile, clientRoot, toolDirectory, failureMode, "46,33",
                $"The WDT publication gate accepted controlled failure mode '{failureMode}'.");
        }
        ExpectSealFailure(explicitBatch.StateFile, clientRoot, toolDirectory, "timeout", "46,33",
            "The WDT publication gate accepted an interrupted enumeration.", TimeSpan.FromSeconds(2));

        File.WriteAllText(wdtListLog,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 46 33\n");
        ForeverGeometryPublicationResult sealedBatch = ForeverGeometryBatchRunner.SealForPublicationForTest(
            explicitBatch.StateFile, clientRoot, toolDirectory, "zhCN",
            CreateWdtFixtureProcessFactory("success", "46,33"));
        if (sealedBatch.SourceCount != 1 || sealedBatch.TileCount != 1)
        {
            throw new InvalidOperationException("The complete fixture WDT coverage did not seal the verified batch.");
        }
        ForeverNavmeshBakeProfile sealedProfile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions { AgentRadius = 0.6f });
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            sealedProfile.SettingsHash, 0, "Azeroth");

        byte[] publishedManifest = File.ReadAllBytes(sealedBatch.ManifestFile);
        ExpectFailure(() => ForeverGeometryBatchRunner.SealForPublicationForTest(
                explicitBatch.StateFile, clientRoot, toolDirectory, "zhCN",
                CreateWdtFixtureProcessFactory("success", "46,33"),
                afterManifestWriteForTest: candidateManifestPath =>
                {
                    string proofRelativePath;
                    using (JsonDocument candidate = JsonDocument.Parse(File.ReadAllBytes(candidateManifestPath)))
                    {
                        proofRelativePath = candidate.RootElement.GetProperty("coverage")
                            .GetProperty("wdt_tile_list_file").GetString()!;
                    }
                    string proofPath = Path.Combine(dnmRoot, proofRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    File.AppendAllText(proofPath, " ");
                }),
            "A proof changed after manifest write did not abort publication.");
        if (!publishedManifest.SequenceEqual(File.ReadAllBytes(sealedBatch.ManifestFile)))
            throw new InvalidOperationException("A post-write proof validation failure did not restore the prior manifest byte-for-byte.");
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            sealedProfile.SettingsHash, 0, "Azeroth");

        ExpectSealFailure(explicitBatch.StateFile, clientRoot, toolDirectory, "nonzero", "46,33",
            "A failed reseal did not leave the previous valid continent manifest untouched.");
        if (!publishedManifest.SequenceEqual(File.ReadAllBytes(sealedBatch.ManifestFile)))
        {
            throw new InvalidOperationException("A failed reseal replaced the previously valid continent manifest.");
        }
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            sealedProfile.SettingsHash, 0, "Azeroth");

        RunSparseAdtBatchRegression(root, clientRoot, toolDirectory);
        RunCrossTileBoundaryRegression(root);
        RunDisconnectedIslandRegression(root);
    }

    private static void RunSparseAdtBatchRegression(string root, string clientRoot, string toolDirectory)
    {
        string inputRoot = Path.Combine(root, "sparse-batch-input");
        foreach (AdtCoordinate adt in new[] { new AdtCoordinate(46, 33), new AdtCoordinate(46, 35) })
        {
            string folder = Path.Combine(inputRoot, $"adt{adt.X:D2}_{adt.Y:D2}", "raw-geometry");
            Directory.CreateDirectory(folder);
            WriteBatchFixture(Path.Combine(folder, $"map0000_adt{adt.X:D2}_{adt.Y:D2}.rawgeo.json"), adt.X, adt.Y);
        }

        string tileListPath = Path.Combine(root, "sparse-mapextractor-list.log");
        string cascDataPath = Path.Combine(clientRoot, "Data");
        File.WriteAllText(tileListPath,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 46 35\ntile 0 46 33\n");
        AdtCoordinate[] adts = ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0);
        if (!adts.SequenceEqual([new AdtCoordinate(46, 33), new AdtCoordinate(46, 35)]))
        {
            throw new InvalidOperationException("The mapextractor ADT list was not de-duplicated and normalized by Y/X.");
        }
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 1),
            "The ADT list parser accepted a mapextractor record for a different MapID.");
        File.WriteAllText(tileListPath, "46,33\n46,33\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0),
            "The ADT list parser accepted a duplicate ADT coordinate.");
        File.WriteAllText(tileListPath, "64,46\n");
        ExpectFailure(() => ForeverGeometryBatchRunner.ReadAdtList(tileListPath, 0),
            "The ADT list parser accepted an out-of-range ADT coordinate.");
        File.WriteAllText(tileListPath,
            $"Opened casc storage '{cascDataPath}'\nDetected client build: 70094\nRestricted extraction to MapID 0.\ntile 0 46 35\ntile 0 46 33\n");

        string outputRoot = Path.Combine(root, "sparse-batch-state");
        string dnmRoot = Path.Combine(root, "sparse-batch-dnm");
        ForeverGeometryBatchRequest request = new(0, 0, 0, 0, 0, inputRoot,
            outputRoot, dnmRoot, "Azeroth", AdtList: [new(46, 35), new(46, 33)]);
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
            AdtList = [new(46, 33), new(46, 35)],
            Resume = true,
        });
        if (reorderedResume.ReusedSourceCount != 2 || reorderedResume.ReusedTileCount != first.TileCount)
        {
            throw new InvalidOperationException("A reordered but equivalent normalized ADT set did not resume its successful tasks.");
        }

        ForeverGeometryBatchResult changedSet = ForeverGeometryBatchRunner.Run(request with
        {
            AdtList = [new(46, 33)],
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

        ForeverGeometryPublicationResult publication = ForeverGeometryBatchRunner.SealForPublicationForTest(
            restoredSet.StateFile, clientRoot, toolDirectory, "zhCN",
            CreateWdtFixtureProcessFactory("success", "46,33;46,35"));
        if (publication.SourceCount != 2 || publication.TileCount != restoredSet.TileCount)
        {
            throw new InvalidOperationException("A matching sparse WDT list did not seal the complete sparse batch.");
        }
        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094,
            new NavmeshBakeOptions());
        ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, "wow_cn_beta", 70094,
            profile.SettingsHash, 0, "Azeroth");
    }

    private static void RunEmptyNavmeshBakeRegression(string root)
    {
        const int tileX = 1;
        const int tileZ = 2;
        NavmeshTileBakeResult flat = NavmeshTileBuilder.BakeWithResult(
            CreateWalkableGeometry(tileX, tileZ), tileX, tileZ, new NavmeshBakeOptions());
        if (!flat.HasNavmesh || flat.MeshData!.header.polyCount <= 0)
            throw new InvalidOperationException("A flat ground tile did not produce a positive-polygon navmesh result.");

        NavmeshTileBakeResult wall = NavmeshTileBuilder.BakeWithResult(
            CreateVerticalWallGeometry(tileX, tileZ), tileX, tileZ, new NavmeshBakeOptions());
        AssertLegalEmptyBake("vertical wall", wall);

        NavmeshTileBakeResult steep = NavmeshTileBuilder.BakeWithResult(
            CreateSteepSlopeGeometry(tileX, tileZ), tileX, tileZ, new NavmeshBakeOptions());
        AssertLegalEmptyBake("slope above walkable threshold", steep);

        NavmeshTileBakeResult mixed = NavmeshTileBuilder.BakeWithResult(
            CombineGeometry(CreateWalkableGeometry(tileX, tileZ), CreateVerticalWallGeometry(tileX, tileZ)),
            tileX, tileZ, new NavmeshBakeOptions());
        if (!mixed.HasNavmesh || mixed.MeshData!.header.polyCount <= 0)
            throw new InvalidOperationException("Mixed walkable ground and vertical collision did not keep its valid navmesh polygons.");

        TileGeometry noGeometry = CreateEmptyGeometry(tileX, tileZ);
        ExpectFailure(() => NavmeshTileBuilder.BakeWithResult(noGeometry, tileX, tileZ, new NavmeshBakeOptions()),
            "Geometry-free input was classified as a legal empty tile instead of an extraction failure.");

        NavmeshTileBakeResult classicEmpty = NavmeshTileBuilder.BakeLegacyDynamic(
            noGeometry, tileX, tileZ, new NavmeshBakeOptions());
        if (classicEmpty.Disposition != NavmeshTileBakeDisposition.NoWalkablePolygons ||
            classicEmpty.MeshData is not null || classicEmpty.PolygonCount != 0)
        {
            throw new InvalidOperationException("Classic dynamic baking no longer returns null for a clean geometry-free tile.");
        }

        NavmeshTileBakeResult classicGround = NavmeshTileBuilder.BakeLegacyDynamic(
            CreateWalkableGeometry(tileX, tileZ), tileX, tileZ, new NavmeshBakeOptions());
        if (!classicGround.HasNavmesh)
            throw new InvalidOperationException("Classic dynamic baking changed normal walkable ground behavior.");

        // An ocean/coast tile with no source chunk is accepted as empty only
        // when extraction itself succeeded; the next terrain tile must still
        // bake normally in the same test process.
        _ = NavmeshTileBuilder.BakeLegacyDynamic(CreateEmptyGeometry(tileX, tileZ), tileX, tileZ);
        if (!NavmeshTileBuilder.BakeLegacyDynamic(CreateWalkableGeometry(tileX + 1, tileZ), tileX + 1, tileZ)
                .HasNavmesh)
        {
            throw new InvalidOperationException("A clean empty coast tile prevented a following Classic terrain tile from baking.");
        }

        TileGeometry partialCoast = CreateEmptyGeometry(tileX, tileZ, failedChunkLoads: 1);
        NavmeshTileBakeResult partialCoastEmpty = NavmeshTileBuilder.BakeLegacyDynamic(partialCoast, tileX, tileZ);
        if (partialCoastEmpty.Disposition != NavmeshTileBakeDisposition.NoWalkablePolygons ||
            partialCoastEmpty.MeshData is not null)
        {
            throw new InvalidOperationException("Classic dynamic baking changed its legacy empty result at a partially missing coast/ocean boundary.");
        }

        TileGeometry badIndices = new()
        {
            GroundVerts = CreateWalkableGeometry(tileX, tileZ).GroundVerts,
            GroundTris = [0, 1, 99],
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [],
            BMin = CreateWalkableGeometry(tileX, tileZ).BMin,
            BMax = CreateWalkableGeometry(tileX, tileZ).BMax,
        };
        ExpectFailure(() => NavmeshTileBuilder.BakeLegacyDynamic(badIndices, tileX, tileZ),
            "Classic dynamic baking accepted a bad triangle index.");
        ExpectFailure(() => NavmeshTileBuilder.BakeWithResult(badIndices, tileX, tileZ),
            "The strict Forever bake accepted a bad triangle index.");

        RunPaddingOnlyEmptyTileRegression(root);
        RunEmptyBatchRecoveryRegression(root);
        RunEmptyNeighborConnectivityRegression(root);
        RunForgedEmptyManifestRegression(root);
    }

    private static void AssertLegalEmptyBake(string scenario, NavmeshTileBakeResult result)
    {
        if (result.Disposition != NavmeshTileBakeDisposition.NoWalkablePolygons ||
            result.PolygonCount != 0 || result.MeshData is not null)
        {
            throw new InvalidOperationException($"{scenario} did not produce the explicit successful zero-polygon classification.");
        }
    }

    private static TileGeometry CreateWalkableGeometry(int tileX, int tileZ)
    {
        float tileMinX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float tileMinZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        return new TileGeometry
        {
            GroundVerts =
            [
                tileMinX + 20, 10, tileMinZ + 20,
                tileMinX + 20, 10, tileMinZ + 60,
                tileMinX + 60, 10, tileMinZ + 20,
                tileMinX + 60, 10, tileMinZ + 60,
            ],
            GroundTris = [0, 1, 2, 2, 1, 3],
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [],
            BMin = new Vector3(tileMinX, 0, tileMinZ),
            BMax = new Vector3(tileMinX + NavmeshSettings.TileWorldSize, 100, tileMinZ + NavmeshSettings.TileWorldSize),
        };
    }

    private static TileGeometry CreateEmptyGeometry(int tileX, int tileZ, int failedChunkLoads = 0)
    {
        float minX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float minZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        return new TileGeometry
        {
            GroundVerts = [], GroundTris = [],
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [],
            BMin = new Vector3(minX, 0, minZ),
            BMax = new Vector3(minX + NavmeshSettings.TileWorldSize, 1, minZ + NavmeshSettings.TileWorldSize),
            FailedChunkLoads = failedChunkLoads,
        };
    }

    private static TileGeometry CreateVerticalWallGeometry(int tileX, int tileZ)
    {
        float tileMinX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float tileMinZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        return new TileGeometry
        {
            GroundVerts =
            [
                tileMinX + 20, 10, tileMinZ + 20,
                tileMinX + 20, 60, tileMinZ + 20,
                tileMinX + 20, 10, tileMinZ + 60,
                tileMinX + 20, 60, tileMinZ + 60,
            ],
            GroundTris = [0, 2, 1, 2, 1, 3],
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [],
            BMin = new Vector3(tileMinX, 0, tileMinZ),
            BMax = new Vector3(tileMinX + NavmeshSettings.TileWorldSize, 100, tileMinZ + NavmeshSettings.TileWorldSize),
        };
    }

    private static TileGeometry CreateSteepSlopeGeometry(int tileX, int tileZ)
    {
        float tileMinX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float tileMinZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        return new TileGeometry
        {
            GroundVerts =
            [
                tileMinX + 20, 0, tileMinZ + 20,
                tileMinX + 20, 0, tileMinZ + 60,
                tileMinX + 60, 80, tileMinZ + 20,
                tileMinX + 60, 80, tileMinZ + 60,
            ],
            GroundTris = [0, 1, 2, 2, 1, 3],
            LiquidVerts = [], LiquidTris = [], LiquidAreas = [],
            BMin = new Vector3(tileMinX, 0, tileMinZ),
            BMax = new Vector3(tileMinX + NavmeshSettings.TileWorldSize, 100, tileMinZ + NavmeshSettings.TileWorldSize),
        };
    }

    private static TileGeometry CombineGeometry(TileGeometry left, TileGeometry right)
    {
        int[] rightTriangles = right.GroundTris.Select(index => index + left.GroundVerts.Length / 3).ToArray();
        return new TileGeometry
        {
            GroundVerts = [.. left.GroundVerts, .. right.GroundVerts],
            GroundTris = [.. left.GroundTris, .. rightTriangles],
            LiquidVerts = [.. left.LiquidVerts, .. right.LiquidVerts],
            LiquidTris = [.. left.LiquidTris, .. right.LiquidTris],
            LiquidAreas = [.. left.LiquidAreas, .. right.LiquidAreas],
            BMin = left.BMin,
            BMax = left.BMax,
        };
    }

    private static void RunPaddingOnlyEmptyTileRegression(string root)
    {
        NavmeshCoords.GetTileIndex(-1050f, -7800f, out int geometryTileX, out int geometryTileZ);
        NavmeshCoords.GetTileWowBounds(geometryTileX, geometryTileZ, out float tileMinX, out float tileMinY,
            out _, out _);
        string path = Path.Combine(root, "padding-only-edge.rawgeo.json");
        WriteRawGeoWithTerrain(path, 46, 33,
        [
            [tileMinX + 20, 10, tileMinY + 2],
            [tileMinX + 60, 10, tileMinY + 2],
            [tileMinX + 20, 10, tileMinY + 42],
        ], [[0, 1, 2]]);
        RawGeometryTileExportResult exported = RawGeometryTileExporter.ExportWithIndependentCoverage(path,
            new GeometryExportRequest(0, 46, 33, PaddingYards: TileGeometryExtractor.PaddingYd));
        ForeverGeometryTile paddedNeighbor = exported.Tiles.SingleOrDefault(tile =>
            tile.DtTileZ == geometryTileZ && tile.DtTileX == geometryTileX - 1)
            ?? throw new InvalidOperationException("The padding-only fixture did not export its neighboring Detour tile.");
        if (paddedNeighbor.TileCounts.TerrainTriangles <= 0)
            throw new InvalidOperationException("The padding-only tile lost its source triangle before baking.");
        NavmeshTileBakeResult result = NavmeshTileBuilder.BakeWithResult(
            paddedNeighbor.ToTileGeometry(), paddedNeighbor.DtTileX, paddedNeighbor.DtTileZ,
            new NavmeshBakeOptions());
        AssertLegalEmptyBake("padding-only edge tile", result);
    }

    private static void RunEmptyBatchRecoveryRegression(string root)
    {
        string inputRoot = Path.Combine(root, "batch-empty-input");
        string adtFolder = Path.Combine(inputRoot, "adt46_33", "raw-geometry");
        Directory.CreateDirectory(adtFolder);
        string rawPath = Path.Combine(adtFolder, "map0000_adt46_33.rawgeo.json");
        WriteNonWalkableBatchFixture(rawPath, steepSlope: false);
        string outputRoot = Path.Combine(root, "batch-empty-state");
        string dnmRoot = Path.Combine(root, "batch-empty-dnm");
        ForeverGeometryBatchRequest request = new(0, 46, 33, 46, 33,
            inputRoot, outputRoot, dnmRoot, "Azeroth", AdtList: [new(46, 33)]);

        ForeverGeometryBatchResult first = ForeverGeometryBatchRunner.Run(request);
        if (first.TileCount != 1 || first.FailedTaskCount != 0 || first.Verification.TileCount != 0 ||
            Directory.EnumerateFiles(dnmRoot, "*.dnm").Any())
        {
            throw new InvalidOperationException("A real DotRecast zero-polygon tile did not complete without creating a fake DNM.");
        }
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(first.StateFile)))
        {
            JsonElement task = state.RootElement.GetProperty("TileTasks")[0];
            if (task.GetProperty("Status").GetString() != "empty" ||
                !task.GetProperty("EmptyNavmeshVerified").GetBoolean() ||
                task.GetProperty("DnmOutputPath").GetString() != "")
                throw new InvalidOperationException("The batch state did not persist a distinct verified-empty tile result.");
        }
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dnmRoot, "manifest.json"))))
        {
            if (manifest.RootElement.GetProperty("tiles").GetArrayLength() != 0 ||
                manifest.RootElement.GetProperty("empty_navmesh_tiles").GetArrayLength() != 1)
                throw new InvalidOperationException("The selected manifest did not separate empty tiles from DNM tiles.");
        }
        ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(dnmRoot, "wow_cn_beta", 70094,
            ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, new NavmeshBakeOptions()).SettingsHash, 0);
        _ = ForeverGeometryBatchRunner.VerifyState(first.StateFile);

        ForeverGeometryBatchResult resumed = ForeverGeometryBatchRunner.Run(request with { Resume = true });
        if (resumed.ReusedSourceCount != 1 || resumed.ReusedTileCount != 1)
            throw new InvalidOperationException("A valid unchanged empty tile was not independently re-baked and reused on resume.");

        File.AppendAllText(rawPath, Environment.NewLine);
        ForeverGeometryBatchResult changedSource = ForeverGeometryBatchRunner.Run(request with { Resume = true });
        if (changedSource.ReusedSourceCount != 0 || changedSource.ReusedTileCount != 0)
            throw new InvalidOperationException("A rawgeo SHA change did not invalidate the previously empty tile result.");

        JsonObject corrupted = JsonNode.Parse(File.ReadAllText(rawPath))!.AsObject();
        corrupted["terrain_triangles"] = new JsonArray(new JsonArray(0, 1, 999), new JsonArray(2, 1, 3));
        File.WriteAllText(rawPath, corrupted.ToJsonString());
        ExpectFailure(() => ForeverGeometryBatchRunner.Run(request with { Resume = true }),
            "A malformed source triangle was swallowed as a legal empty tile.");
        using JsonDocument failedState = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outputRoot, "batch-state.json")));
        if (failedState.RootElement.GetProperty("Status").GetString() != "failed" ||
            failedState.RootElement.GetProperty("TileTasks")[0].GetProperty("Status").GetString() == "empty")
            throw new InvalidOperationException("A malformed geometry input was recorded as a legal empty tile.");
    }

    private static void RunEmptyNeighborConnectivityRegression(string root)
    {
        string directory = Path.Combine(root, "adjacent-with-empty-neighbor-dnm");
        Directory.CreateDirectory(directory);
        foreach (int tileX in new[] { 1, 2 })
        {
            DtMeshData data = NavmeshTileBuilder.Bake(CreateBoundaryCrossingGeometry(tileX, 2),
                tileX, 2, new NavmeshBakeOptions())
                ?? throw new InvalidOperationException($"Walkable neighbor tile ({tileX},2) did not bake.");
            using FileStream stream = File.Create(Path.Combine(directory, $"tile_{tileX}_2.dnm"));
            using BinaryWriter writer = new(stream);
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        }
        AssertLegalEmptyBake("third empty neighbor tile",
            NavmeshTileBuilder.BakeWithResult(CreateVerticalWallGeometry(3, 2), 3, 2, new NavmeshBakeOptions()));
        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(directory, [(1, 2), (2, 2)]);
        if (!verification.ConnectedPathSucceeded || verification.CrossTileLinkCount == 0)
            throw new InvalidOperationException("A certified empty tile changed the valid Detour portal between adjacent baked neighbors.");
    }

    private static void RunForgedEmptyManifestRegression(string root)
    {
        string source = Path.Combine(root, "batch-dnm");
        string forged = Path.Combine(root, "forged-empty-manifest");
        if (!Directory.Exists(source)) throw new InvalidOperationException("The valid baked fixture cache is missing.");
        CopyDirectory(source, forged);
        string manifestPath = Path.Combine(forged, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        JsonObject tile = manifest["tiles"]!.AsArray()[0]!.AsObject();
        int tileX = tile["dt_tile_x"]!.GetValue<int>();
        int tileZ = tile["dt_tile_z"]!.GetValue<int>();
        string dnmPath = Path.Combine(forged, tile["file"]!.GetValue<string>());
        string settingsHash = manifest["settings_hash"]!.GetValue<string>();
        JsonArray sources = JsonNode.Parse(manifest["source_adts"]!.ToJsonString())!.AsArray();
        JsonObject sourceRecord = sources[0]!.AsObject();
        int[] sourcePair = JsonSerializer.Deserialize<int[]>(sourceRecord["adt_xy"]!.ToJsonString())!;
        string sourceHash = sourceRecord["sha256"]!.GetValue<string>();
        JsonArray expectedAdts = new(new JsonArray(sourcePair[0], sourcePair[1]));
        manifest["tiles"] = new JsonArray();
        manifest["empty_navmesh_tiles"] = new JsonArray(new JsonObject
        {
            ["evidence_file"] = $"coverage/empty-tile-{tileX}-{tileZ}-{new string('0', 64)}.json",
            ["evidence_sha256"] = new string('0', 64),
            ["evidence"] = new JsonObject
            {
                ["format"] = ForeverNavmeshCacheManifest.EmptyNavmeshEvidenceFormat,
                ["result"] = "completed_no_walkable_polygons",
                ["client_product"] = "wow_cn_beta",
                ["client_build"] = 70094,
                ["map_id"] = 0,
                ["dt_tile_x"] = tileX,
                ["dt_tile_z"] = tileZ,
                ["settings_hash"] = settingsHash,
                ["dotrecast_version"] = manifest["bake_settings"]!["dot_recast_version"]!.GetValue<string>(),
                ["geometry_export_version"] = manifest["bake_settings"]!["geometry_export_version"]!.GetValue<string>(),
                ["raw_geometry_format"] = manifest["bake_settings"]!["raw_geometry_format"]!.GetValue<string>(),
                ["polygon_count"] = 0,
                ["geometry_sha256"] = new string('a', 64),
                ["source_set_sha256"] = new string('b', 64),
                ["source_adts_xy"] = expectedAdts,
                ["source_sha256s"] = new JsonArray(JsonValue.Create(sourceHash)),
                ["terrain_triangles"] = 2,
                ["collision_triangles"] = 0,
                ["liquid_triangles"] = 0,
            },
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.Delete(dnmPath);
        ExpectFailure(() => ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(forged,
                "wow_cn_beta", 70094, settingsHash, 0),
            "An ordinary baked navmesh tile was accepted after a manifest-only rewrite to legal-empty without an actual bake evidence file.");
    }

    private static void WriteNonWalkableBatchFixture(string path, bool steepSlope)
    {
        WriteBatchFixture(path);
        JsonObject raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        float[][] vertices = steepSlope
            ? [
                [-1050f, 100f, -7800f], [-1050f, 100f, -7760f],
                [-1010f, 180f, -7800f], [-1010f, 180f, -7760f],
            ]
            : [
                [-1050f, 100f, -7800f], [-1050f, 150f, -7800f],
                [-1010f, 100f, -7800f], [-1010f, 150f, -7800f],
            ];
        raw["terrain_vertices"] = JsonSerializer.SerializeToNode(vertices);
        raw["terrain_triangles"] = JsonSerializer.SerializeToNode(new[] { new[] { 0, 2, 1 }, new[] { 2, 1, 3 } });
        raw["terrain_vertex_count"] = vertices.Length;
        raw["terrain_triangle_count"] = 2;
        File.WriteAllText(path, raw.ToJsonString());
    }

    private static void WriteRawGeoWithTerrain(string path, int adtX, int adtY,
        float[][] vertices, int[][] triangles)
    {
        WriteBatchFixture(path, adtX, adtY);
        JsonObject raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        raw["terrain_vertices"] = JsonSerializer.SerializeToNode(vertices);
        raw["terrain_triangles"] = JsonSerializer.SerializeToNode(triangles);
        raw["terrain_vertex_count"] = vertices.Length;
        raw["terrain_triangle_count"] = triangles.Length;
        File.WriteAllText(path, raw.ToJsonString());
    }

    private static string CreateFakeMapExtractorTool(string root, bool validProvenance = true)
    {
        string toolDirectory = Path.Combine(root, validProvenance ? "fake-mapextractor" : "fake-mapextractor-invalid");
        Directory.CreateDirectory(toolDirectory);
        string executable = Path.Combine(toolDirectory, "mapextractor.exe");
        File.WriteAllText(executable, "controlled test fixture; never executed as a native tool");
        string executableHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();
        var provenance = new
        {
            format = ForeverWdtTileEnumerator.ProvenanceFormat,
            source_repository = ForeverWdtTileEnumerator.SourceRepository,
            source_commit = ForeverWdtTileEnumerator.SourceCommit,
            patch_sha256 = validProvenance ? ForeverWdtTileEnumerator.ExpectedPatchSha256 : new string('a', 64),
            mapextractor_sha256 = executableHash,
        };
        File.WriteAllText(Path.Combine(toolDirectory, ForeverWdtTileEnumerator.ProvenanceFileName),
            JsonSerializer.Serialize(provenance));
        return toolDirectory;
    }

    private static WdtEnumeratorProcessFactory CreateWdtFixtureProcessFactory(string mode,
        string adts, string? markerPath = null) => (executable, arguments) =>
    {
        string host = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the CoreTests process host for the WDT fixture.");
        ProcessStartInfo startInfo = new(host)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        startInfo.ArgumentList.Add("--forever-wdt-mapextractor-fixture");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["WOW_TEST_WDT_MODE"] = mode;
        startInfo.Environment["WOW_TEST_WDT_ADTS"] = adts;
        if (markerPath is not null) startInfo.Environment["WOW_TEST_WDT_MARKER"] = markerPath;
        return startInfo;
    };

    private static void ExpectSealRejectedBeforeWdt(string stateFile, string clientRoot, string toolDirectory,
        string message)
    {
        string dnmRoot;
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(stateFile)))
        {
            dnmRoot = state.RootElement.GetProperty("DnmOutputDirectory").GetString()!;
        }
        string manifestPath = Path.Combine(dnmRoot, "manifest.json");
        byte[]? originalManifest = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        string markerPath = Path.Combine(Path.GetDirectoryName(stateFile)!, $"wdt-not-started-{Guid.NewGuid():N}.marker");
        Exception? failure = null;
        try
        {
            ForeverGeometryBatchRunner.SealForPublicationForTest(stateFile, clientRoot, toolDirectory,
                "zhCN", CreateWdtFixtureProcessFactory("success", "46,33", markerPath));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
            throw new InvalidOperationException(message);
        if (File.Exists(markerPath))
            throw new InvalidOperationException($"{message} The WDT child process started before rejection.", failure);
        if (originalManifest is null)
        {
            if (File.Exists(manifestPath))
                throw new InvalidOperationException($"{message} A failed seal created a manifest.", failure);
        }
        else if (!originalManifest.SequenceEqual(File.ReadAllBytes(manifestPath)))
        {
            throw new InvalidOperationException($"{message} The existing manifest changed on failure.", failure);
        }
    }

    private static void ExpectSealFailure(string stateFile, string clientRoot, string toolDirectory,
        string mode, string adts, string message, TimeSpan? timeout = null,
        string? expectedFailureContains = null)
    {
        string dnmRoot;
        using (JsonDocument state = JsonDocument.Parse(File.ReadAllBytes(stateFile)))
        {
            dnmRoot = state.RootElement.GetProperty("DnmOutputDirectory").GetString()!;
        }
        string manifestPath = Path.Combine(dnmRoot, "manifest.json");
        byte[]? originalManifest = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        string markerPath = Path.Combine(Path.GetDirectoryName(stateFile)!, $"wdt-fixture-{Guid.NewGuid():N}.marker");
        Exception? failure = null;
        try
        {
            ForeverGeometryBatchRunner.SealForPublicationForTest(stateFile, clientRoot, toolDirectory,
                "zhCN", CreateWdtFixtureProcessFactory(mode, adts, markerPath), timeout);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
            throw new InvalidOperationException(message);
        if (expectedFailureContains is not null &&
            !failure.Message.Contains(expectedFailureContains, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message} Expected diagnostic fragment '{expectedFailureContains}', got '{failure.Message}'.", failure);
        }
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != mode)
            throw new InvalidOperationException($"{message} The controlled child process did not run for mode '{mode}'.", failure);
        if (originalManifest is null)
        {
            if (File.Exists(manifestPath))
                throw new InvalidOperationException($"{message} A failed seal created a manifest.", failure);
        }
        else if (!originalManifest.SequenceEqual(File.ReadAllBytes(manifestPath)))
        {
            throw new InvalidOperationException($"{message} The existing manifest changed on failure.", failure);
        }
        File.Delete(markerPath);
    }

    private static void WriteBatchFixture(string path, int adtX = 46, int adtY = 33)
    {
        float adtSize = NavmeshSettings.TileWorldSize * 4f;
        float offsetX = (33 - adtY) * adtSize;
        float offsetY = (46 - adtX) * adtSize;
        float[][] terrain =
        [
            [-1050f + offsetX, 100f, -7800f + offsetY],
            [-1050f + offsetX, 100f, -7760f + offsetY],
            [-1010f + offsetX, 100f, -7800f + offsetY],
            [-1010f + offsetX, 100f, -7760f + offsetY],
        ];
        var raw = new
        {
            format = NavmeshSettings.ForeverRawGeometryFormat,
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

    private static void RunDisconnectedIslandRegression(string root)
    {
        string directory = Path.Combine(root, "disconnected-island-dnm");
        Directory.CreateDirectory(directory);
        foreach ((int tileX, int tileZ) in new[] { (1, 2), (3, 2) })
        {
            DtMeshData data = CreateGroundTileData(tileX, tileZ);
            using FileStream stream = File.Create(Path.Combine(directory, $"tile_{tileX}_{tileZ}.dnm"));
            using BinaryWriter writer = new(stream);
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        }

        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(directory, [(1, 2), (3, 2)]);
        if (verification.TileCount != 2 || !verification.HeightQuerySucceeded ||
            !verification.ConnectedPathSucceeded || verification.CrossTileLinkCount != 0)
        {
            throw new InvalidOperationException("A valid isolated navmesh island was rejected or falsely connected to another component.");
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

    private static DtMeshData CreateGroundTileData(int tileX, int tileZ, float groundHeight = 10f)
    {
        float tileMinX = NavmeshCoords.Origin + tileX * NavmeshSettings.TileWorldSize;
        float tileMinZ = NavmeshCoords.Origin + tileZ * NavmeshSettings.TileWorldSize;
        float[] verts =
        [
            tileMinX + 20, groundHeight, tileMinZ + 20,
            tileMinX + 20, groundHeight, tileMinZ + 60,
            tileMinX + 60, groundHeight, tileMinZ + 20,
            tileMinX + 60, groundHeight, tileMinZ + 60,
        ];
        TileGeometry geometry = new()
        {
            GroundVerts = verts,
            GroundTris = [0, 1, 2, 2, 1, 3],
            LiquidVerts = [],
            LiquidTris = [],
            LiquidAreas = [],
            BMin = new Vector3(tileMinX, groundHeight - 10f, tileMinZ),
            BMax = new Vector3(tileMinX + NavmeshSettings.TileWorldSize, groundHeight + 90f,
                tileMinZ + NavmeshSettings.TileWorldSize),
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
        float[] terrain = adtY == 33
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
        if (adtY == 34)
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
            ["format"] = NavmeshSettings.ForeverRawGeometryFormat,
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
            ["liquid_triangle_count"] = adtY == 33 ? 1 : 2,
            ["liquid_type_count"] = adtY == 33 ? 1 : 2,
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
            ["liquid_triangles"] = adtY == 33
                ? new[] { new[] { 0, 1, 2 } }
                : new[] { new[] { 0, 1, 2 }, new[] { 0, 1, 2 } },
            ["liquid_types"] = adtY == 33 ? new[] { 8 } : new[] { 8, 9 },
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
