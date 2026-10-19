using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace PPather.Navmesh;

/// <summary>
/// Immutable tile coverage captured only after the complete continent manifest
/// and its WDT proof have passed runtime validation.
/// </summary>
public sealed class ForeverNavmeshRuntimeCoverage
{
    private readonly HashSet<(int X, int Z)> expectedTiles;
    private readonly HashSet<(int X, int Z)> bakedTiles;
    private readonly HashSet<(int X, int Z)> emptyTiles;
    private readonly Dictionary<(int X, int Z), string> bakedTileSha256;

    internal ForeverNavmeshRuntimeCoverage(string product, int build, string settingsHash,
        int mapId, string continent, IEnumerable<(int X, int Z)> expectedTiles,
        IReadOnlyDictionary<(int X, int Z), string> bakedTileSha256,
        IEnumerable<(int X, int Z)> emptyTiles)
    {
        Product = product;
        Build = build;
        SettingsHash = settingsHash;
        MapId = mapId;
        Continent = continent;
        this.expectedTiles = expectedTiles.ToHashSet();
        this.bakedTileSha256 = bakedTileSha256.ToDictionary(static entry => entry.Key,
            static entry => entry.Value.ToLowerInvariant());
        this.bakedTiles = this.bakedTileSha256.Keys.ToHashSet();
        this.emptyTiles = emptyTiles.ToHashSet();
    }

    public string Product { get; }
    public int Build { get; }
    public string SettingsHash { get; }
    public int MapId { get; }
    public string Continent { get; }
    public IReadOnlyCollection<(int X, int Z)> ExpectedTiles => expectedTiles.ToArray();
    public IReadOnlyCollection<(int X, int Z)> BakedTiles => bakedTiles.ToArray();
    public IReadOnlyCollection<(int X, int Z)> EmptyTiles => emptyTiles.ToArray();

    public bool ContainsExpectedTile(int x, int z) => expectedTiles.Contains((x, z));
    public bool ContainsBakedTile(int x, int z) => bakedTiles.Contains((x, z));
    public bool ContainsVerifiedEmptyTile(int x, int z) => emptyTiles.Contains((x, z));

    public bool TryGetBakedTileSha256(int x, int z, out string sha256) =>
        bakedTileSha256.TryGetValue((x, z), out sha256!);
}

/// <summary>
/// Scope-aware validation for Forever navmesh packs. A selected test pack can
/// be checked only through the explicitly named test API; it can never satisfy
/// the continent eligibility checks used by publication or runtime loading.
/// </summary>
public static class ForeverNavmeshCacheManifest
{
    public const string Format = "wow-forever-dnm-manifest-v2";
    // v3 keeps the original process paths as historical provenance. Runtime
    // validates their lexical relationship without requiring the build-time
    // client, tool or temporary directory to still exist.
    public const string WdtCoverageEvidenceFormat = "wow-forever-wdt-coverage-v3";
    public const string EmptyNavmeshEvidenceFormat = "wow-forever-empty-navmesh-v1";
    public const string EmptyNavmeshEvidencePathPrefix = "coverage/empty-tile-";
    public const string WdtCoverageEvidenceRelativePath = "coverage/wdt-coverage.json";
    public const string WdtCoverageEvidencePathPrefix = "coverage/wdt-proof-";
    public const string WdtNativeTileListPathPrefix = "coverage/wdt-native-";
    public const string WdtStdoutPathPrefix = "coverage/wdt-stdout-";
    public const string WdtStderrPathPrefix = "coverage/wdt-stderr-";
    public const string WdtEnumeratorSourceRepository = "https://github.com/advocaite/TrinityCore.git";
    public const string WdtEnumeratorSourceCommit = "440d365e791baff1cdf555ed2179c71bd6ebd014";
    public const string WdtEnumeratorPatchSha256 = "7acf42579137365809265819cd685ec7b8f1749e81a74f9bb2e7a51314d91d54";

    /// <summary>Explicit test entry for packs that are intentionally incomplete.</summary>
    public static void ValidateSelectedPackForTest(string cacheDirectory, string expectedProduct,
        int expectedBuild, string expectedSettingsHash, int expectedMapId)
    {
        ManifestSnapshot manifest = ReadAndValidateFiles(cacheDirectory, expectedProduct,
            expectedBuild, expectedSettingsHash, expectedMapId, expectedContinent: null);
        if (!string.Equals(manifest.Scope, "explicit_adt_and_tile_set", StringComparison.Ordinal) ||
            !manifest.CompleteForRequestedSourcesAndTiles || manifest.PublishableAsContinentCache)
        {
            throw new InvalidDataException("The selected-pack test entry accepts only complete-for-requested, non-publishable test packs.");
        }
        if (manifest.ExpectedTiles.Count > 0)
        {
            ValidateTileResultSets(manifest);
        }
        else if (manifest.EmptyTiles.Count > 0)
        {
            throw new InvalidDataException("A selected pack with empty navmesh tiles must record its complete expected tile set.");
        }
    }

    /// <summary>
    /// Validates a proposed continent pack against the independently supplied
    /// WDT source set and target tile set before it may be marked publishable.
    /// </summary>
    public static void ValidateForPublication(string cacheDirectory, string expectedProduct,
        int expectedBuild, string expectedSettingsHash, int expectedMapId, string expectedContinent,
        IReadOnlyCollection<(int X, int Y)> expectedAdts,
        IReadOnlyCollection<(int X, int Z)> expectedTiles)
    {
        ManifestSnapshot manifest = ReadAndValidateFiles(cacheDirectory, expectedProduct,
            expectedBuild, expectedSettingsHash, expectedMapId, expectedContinent);
        ValidateContinentEligibility(manifest);
        ValidateExpectedCoverage(manifest, expectedAdts, expectedTiles);
    }

    /// <summary>
    /// Runtime entry. It requires a continent-scoped, complete and publishable
    /// pack and verifies its self-contained WDT coverage proof before loading.
    /// </summary>
    public static void ValidateForRuntime(string cacheDirectory, string expectedProduct,
        int expectedBuild, string expectedSettingsHash, int expectedMapId, string expectedContinent)
    {
        ManifestSnapshot manifest = ReadAndValidateFiles(cacheDirectory, expectedProduct,
            expectedBuild, expectedSettingsHash, expectedMapId, expectedContinent);
        ValidateContinentEligibility(manifest);
    }

    /// <summary>Returns certified no-navmesh tiles after full runtime validation.</summary>
    public static IReadOnlyCollection<(int X, int Z)> ReadEmptyTilesForRuntime(string cacheDirectory,
        string expectedProduct, int expectedBuild, string expectedSettingsHash, int expectedMapId,
        string expectedContinent)
    {
        return ReadCoverageForRuntime(cacheDirectory, expectedProduct, expectedBuild,
            expectedSettingsHash, expectedMapId, expectedContinent).EmptyTiles;
    }

    /// <summary>
    /// Returns tile coverage only after the same full manifest, WDT proof,
    /// DNM hashes and empty-tile evidence required by the runtime have passed.
    /// </summary>
    public static ForeverNavmeshRuntimeCoverage ReadCoverageForRuntime(string cacheDirectory,
        string expectedProduct, int expectedBuild, string expectedSettingsHash, int expectedMapId,
        string expectedContinent)
    {
        ManifestSnapshot manifest = ReadAndValidateFiles(cacheDirectory, expectedProduct,
            expectedBuild, expectedSettingsHash, expectedMapId, expectedContinent);
        ValidateContinentEligibility(manifest);
        return new ForeverNavmeshRuntimeCoverage(expectedProduct, expectedBuild, expectedSettingsHash,
            expectedMapId, expectedContinent, manifest.ExpectedTiles, manifest.DnmSha256s, manifest.EmptyTiles);
    }

    private static ManifestSnapshot ReadAndValidateFiles(string cacheDirectory, string expectedProduct,
        int expectedBuild, string expectedSettingsHash, int expectedMapId, string? expectedContinent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProduct);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSettingsHash);
        if (expectedBuild <= 0 || expectedMapId < 0 ||
            expectedContinent is not null && string.IsNullOrWhiteSpace(expectedContinent))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBuild), "Forever cache identity requires a positive Build, non-negative MapID, and a non-empty continent when specified.");
        }
        if (!Directory.Exists(cacheDirectory) ||
            (File.GetAttributes(Path.GetFullPath(cacheDirectory)) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DirectoryNotFoundException($"Forever navmesh cache directory does not exist or is a reparse point: {cacheDirectory}");
        }

        string manifestPath = Path.Combine(cacheDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Forever navmesh cache has no verified manifest: {manifestPath}");
        }

        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        string product = ReadRequiredString(root, "client_product", manifestPath);
        int build = ReadRequiredInt(root, "client_build", manifestPath);
        int mapId = ReadRequiredInt(root, "map_id", manifestPath);
        string dataSetId = ReadRequiredString(root, "data_set_id", manifestPath);
        string settingsHash = ReadRequiredString(root, "settings_hash", manifestPath);
        string scope = ReadRequiredString(root, "scope", manifestPath);
        string continent = ReadOptionalString(root, "continent");

        if (ReadRequiredString(root, "format", manifestPath) != Format ||
            !string.Equals(product, expectedProduct, StringComparison.OrdinalIgnoreCase) ||
            build != expectedBuild || mapId != expectedMapId ||
            !string.Equals(dataSetId, $"{product.ToLowerInvariant()}-{build}", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settingsHash, expectedSettingsHash, StringComparison.OrdinalIgnoreCase) ||
            expectedContinent is not null && !string.Equals(continent, expectedContinent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Forever navmesh manifest identity does not match {expectedProduct} Build {expectedBuild}, " +
                $"MapID {expectedMapId}, continent '{expectedContinent ?? "<selected-test>"}' and settings {expectedSettingsHash}: {manifestPath}");
        }

        ValidateGeometryBakeIdentity(root, manifestPath);

        bool completeForRequested = ReadOptionalBool(root, "complete_for_requested_sources_and_tiles");
        bool completeForContinent = ReadOptionalBool(root, "complete_for_continent");
        bool publishable = ReadOptionalBool(root, "publishable_as_continent_cache");
        Dictionary<(int X, int Y), string> sourceHashes = ReadSourceAdtHashes(root, manifestPath);
        HashSet<(int X, int Y)> sourceAdts = sourceHashes.Keys.ToHashSet();
        TileRecord[] tiles = ReadAndValidateTiles(cacheDirectory, root, manifestPath);
        HashSet<(int X, int Z)> emptyTiles = ReadEmptyNavmeshTiles(cacheDirectory, root,
            manifestPath, product, build, mapId, settingsHash, sourceHashes);
        (string tileListFile, string tileListSha256, string tileListProduct, int tileListBuild,
            int tileListMapId, HashSet<(int X, int Y)> wdtAdts) = ReadCoverageTileList(cacheDirectory,
                root, manifestPath, product, build, mapId);
        if (tileListProduct.Length > 0 &&
            (!string.Equals(tileListProduct, product, StringComparison.OrdinalIgnoreCase) ||
             tileListBuild != build || tileListMapId != mapId))
        {
            throw new InvalidDataException($"WDT tile-list evidence identity does not match the cache manifest: {manifestPath}");
        }

        return new ManifestSnapshot(scope, continent, completeForRequested, completeForContinent,
            publishable, ReadOptionalString(root, "coverage_provenance"), tileListFile, tileListSha256,
            tileListProduct, tileListBuild, tileListMapId, sourceAdts, wdtAdts,
            ReadOptionalCoverageAdts(root, "expected_source_adts_xy", manifestPath),
            ReadOptionalCoverageAdts(root, "processed_source_adts_xy", manifestPath),
            ReadOptionalCoverageTiles(root, "expected_tile_indices", manifestPath),
            ReadOptionalCoverageTiles(root, "completed_tile_indices", manifestPath),
            tiles.Select(static tile => (tile.X, tile.Z)).ToHashSet(),
            tiles.ToDictionary(static tile => (tile.X, tile.Z), static tile => tile.Sha256), emptyTiles);
    }

    private static void ValidateContinentEligibility(ManifestSnapshot manifest)
    {
        if (!string.Equals(manifest.Scope, "continent", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Continent) ||
            !manifest.CompleteForRequestedSourcesAndTiles || !manifest.CompleteForContinent ||
            !manifest.PublishableAsContinentCache ||
            !string.Equals(manifest.CoverageProvenance, "wdt_tile_list", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.WdtTileListFile) ||
            !IsSha256(manifest.WdtTileListSha256) || string.IsNullOrWhiteSpace(manifest.WdtTileListProduct) ||
            manifest.WdtTileListBuild <= 0 || manifest.WdtTileListMapId < 0)
        {
            throw new InvalidDataException(
                "Forever runtime requires scope=continent, complete_for_continent=true, " +
                "publishable_as_continent_cache=true and a hashed WDT tile-list coverage proof.");
        }

        if (manifest.ExpectedAdts.Count == 0 || !manifest.ExpectedAdts.SetEquals(manifest.WdtAdts) ||
            !manifest.ExpectedAdts.SetEquals(manifest.ProcessedAdts) ||
            !manifest.ExpectedAdts.SetEquals(manifest.SourceAdts) ||
            manifest.ExpectedTiles.Count == 0 || !manifest.ExpectedTiles.SetEquals(manifest.CompletedTiles))
        {
            throw new InvalidDataException("Forever continent coverage proof is incomplete or does not match its source/tile records.");
        }
        ValidateTileResultSets(manifest);
    }

    private static void ValidateTileResultSets(ManifestSnapshot manifest)
    {
        HashSet<(int X, int Z)> overlap = manifest.ManifestTiles.Intersect(manifest.EmptyTiles).ToHashSet();
        HashSet<(int X, int Z)> completed = manifest.ManifestTiles.Concat(manifest.EmptyTiles).ToHashSet();
        if (manifest.ExpectedTiles.Count == 0 || manifest.CompletedTiles.Count == 0 || overlap.Count != 0 ||
            !manifest.ExpectedTiles.SetEquals(manifest.CompletedTiles) ||
            !manifest.ExpectedTiles.SetEquals(completed))
        {
            string Format(IEnumerable<(int X, int Z)> tiles) => string.Join(',', tiles.OrderBy(static tile => tile.X)
                .ThenBy(static tile => tile.Z).Select(static tile => $"({tile.X},{tile.Z})"));
            throw new InvalidDataException(
                "Navmesh tile result sets are incomplete or overlap; " +
                $"expected=[{Format(manifest.ExpectedTiles)}], baked=[{Format(manifest.ManifestTiles)}], " +
                $"verified-empty=[{Format(manifest.EmptyTiles)}], overlap=[{Format(overlap)}].");
        }
    }

    private static void ValidateExpectedCoverage(ManifestSnapshot manifest,
        IReadOnlyCollection<(int X, int Y)> expectedAdts,
        IReadOnlyCollection<(int X, int Z)> expectedTiles)
    {
        if (expectedAdts is null || expectedTiles is null || expectedAdts.Count == 0 || expectedTiles.Count == 0)
        {
            throw new ArgumentException("Publication requires a non-empty independent WDT ADT set and target tile set.");
        }

        HashSet<(int X, int Y)> expectedAdtSet = expectedAdts.ToHashSet();
        HashSet<(int X, int Z)> expectedTileSet = expectedTiles.ToHashSet();
        if (expectedAdtSet.Count != expectedAdts.Count || expectedTileSet.Count != expectedTiles.Count ||
            !expectedAdtSet.SetEquals(manifest.ExpectedAdts) ||
            !expectedTileSet.SetEquals(manifest.ExpectedTiles))
        {
            throw new InvalidDataException("The manifest coverage does not match the independently supplied WDT ADT and target tile sets.");
        }
    }

    private static Dictionary<(int X, int Y), string> ReadSourceAdtHashes(JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("source_adts", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Forever manifest has no source_adts array: {manifestPath}");
        }

        Dictionary<(int X, int Y), string> result = [];
        foreach (JsonElement source in sources.EnumerateArray())
        {
            string fileName = ReadRequiredString(source, "file_name", manifestPath);
            string sourceHash = ReadRequiredString(source, "sha256", manifestPath);
            if (!source.TryGetProperty("adt_xy", out JsonElement pair) || pair.ValueKind != JsonValueKind.Array ||
                pair.GetArrayLength() != 2 || !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int y) ||
                x is < 0 or >= 64 || y is < 0 or >= 64 || !result.TryAdd((x, y), sourceHash) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"Forever manifest has an invalid or duplicate source ADT record: {manifestPath}");
            }
        }

        return result;
    }

    private static TileRecord[] ReadAndValidateTiles(string cacheDirectory, JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("tiles", out JsonElement tiles) || tiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Forever navmesh manifest has no tile array: {manifestPath}");
        }

        HashSet<string> expectedFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<(int X, int Z)> expectedIndices = [];
        List<TileRecord> records = [];
        foreach (JsonElement tile in tiles.EnumerateArray())
        {
            string file = ReadRequiredString(tile, "file", manifestPath);
            string sha256 = ReadRequiredString(tile, "sha256", manifestPath);
            int x = ReadRequiredInt(tile, "dt_tile_x", manifestPath);
            int z = ReadRequiredInt(tile, "dt_tile_z", manifestPath);
            string expectedName = $"tile_{x}_{z}.dnm";
            if (!string.Equals(file, expectedName, StringComparison.Ordinal) ||
                !NavmeshCoords.IsValidTile(x, z) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
                !expectedFiles.Add(file) || !expectedIndices.Add((x, z)))
            {
                throw new InvalidDataException($"Forever navmesh manifest has an invalid or duplicate tile record: {manifestPath}");
            }

            string tilePath = Path.Combine(cacheDirectory, file);
            if (!File.Exists(tilePath))
            {
                throw new InvalidDataException($"Forever navmesh tile is missing: {tilePath}");
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tilePath))).ToLowerInvariant();
            if (!actualHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Forever navmesh tile hash does not match its manifest: {tilePath}");
            }

            ValidateDnmHeader(tilePath, x, z);
            records.Add(new TileRecord(x, z, file, sha256));
        }

        HashSet<string> actualFiles = Directory.EnumerateFiles(cacheDirectory, "*.dnm")
            .Select(Path.GetFileName)
            .Where(static file => file != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            throw new InvalidDataException(
                $"Forever navmesh tile files do not match the manifest in {cacheDirectory}; " +
                $"unlisted={string.Join(',', actualFiles.Except(expectedFiles))}, missing={string.Join(',', expectedFiles.Except(actualFiles))}.");
        }

        return [.. records];
    }

    private static HashSet<(int X, int Z)> ReadEmptyNavmeshTiles(string cacheDirectory,
        JsonElement root, string manifestPath, string product, int build, int mapId,
        string settingsHash, Dictionary<(int X, int Y), string> sourceHashes)
    {
        if (!root.TryGetProperty("empty_navmesh_tiles", out JsonElement records)) return [];
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Forever manifest empty_navmesh_tiles is not an array: {manifestPath}");
        }

        string cacheRoot = Path.GetFullPath(cacheDirectory);
        string expectedDotRecastVersion = ReadOptionalNestedString(root, "bake_settings", "dot_recast_version");
        string expectedGeometryVersion = ReadOptionalNestedString(root, "bake_settings", "geometry_export_version");
        string expectedRawFormat = ReadOptionalNestedString(root, "bake_settings", "raw_geometry_format");
        HashSet<(int X, int Z)> result = [];
        foreach (JsonElement record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("evidence", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Empty navmesh manifest record has no evidence payload: {manifestPath}");
            }
            int x = ReadRequiredInt(payload, "dt_tile_x", manifestPath);
            int z = ReadRequiredInt(payload, "dt_tile_z", manifestPath);
            string evidenceFile = ReadRequiredString(record, "evidence_file", manifestPath);
            string evidenceSha = ReadRequiredString(record, "evidence_sha256", manifestPath);
            string evidencePrefix = $"{EmptyNavmeshEvidencePathPrefix}{x}-{z}-";
            byte[] bytes = ReadVerifiedCoverageFile(cacheRoot, evidenceFile, evidenceSha,
                evidencePrefix, ".json", manifestPath);
            byte[] expectedPayloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            if (!expectedPayloadBytes.SequenceEqual(bytes))
            {
                throw new InvalidDataException($"Empty navmesh evidence file does not match its manifest record: {evidenceFile}");
            }

            string evidencePath = Path.Combine(cacheRoot, evidenceFile.Replace('/', Path.DirectorySeparatorChar));
            if (!NavmeshCoords.IsValidTile(x, z) || !result.Add((x, z)) ||
                ReadRequiredString(payload, "format", evidencePath) != EmptyNavmeshEvidenceFormat ||
                ReadRequiredString(payload, "result", evidencePath) != "completed_no_walkable_polygons" ||
                !ReadRequiredString(payload, "client_product", evidencePath).Equals(product, StringComparison.OrdinalIgnoreCase) ||
                ReadRequiredInt(payload, "client_build", evidencePath) != build ||
                ReadRequiredInt(payload, "map_id", evidencePath) != mapId ||
                !ReadRequiredString(payload, "settings_hash", evidencePath).Equals(settingsHash, StringComparison.OrdinalIgnoreCase) ||
                ReadRequiredInt(payload, "polygon_count", evidencePath) != 0)
            {
                throw new InvalidDataException($"Empty navmesh evidence identity, result or tile coordinate is invalid: {evidencePath}");
            }

            string geometrySha = ReadRequiredString(payload, "geometry_sha256", evidencePath);
            string sourceSetSha = ReadRequiredString(payload, "source_set_sha256", evidencePath);
            if (!IsSha256(geometrySha) || !IsSha256(sourceSetSha))
            {
                throw new InvalidDataException($"Empty navmesh evidence is missing geometry or source-set hashes: {evidencePath}");
            }

            int[][] sourceCoordinates = ReadRequiredPairArray(payload, "source_adts_xy", evidencePath);
            string[] sourceFileHashes = ReadRequiredStringArray(payload, "source_sha256s", evidencePath);
            if (sourceCoordinates.Length == 0 || sourceCoordinates.Length != sourceFileHashes.Length ||
                sourceFileHashes.Any(hash => !IsSha256(hash)))
            {
                throw new InvalidDataException($"Empty navmesh evidence has invalid source ADT/hash records: {evidencePath}");
            }

            for (int i = 0; i < sourceCoordinates.Length; i++)
            {
                (int sourceX, int sourceY) = (sourceCoordinates[i][0], sourceCoordinates[i][1]);
                if (!sourceHashes.TryGetValue((sourceX, sourceY), out string? manifestHash) ||
                    !manifestHash.Equals(sourceFileHashes[i], StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Empty navmesh evidence source ADT ({sourceX},{sourceY}) or hash differs from the manifest: {evidencePath}");
                }
            }

            string canonicalSourceSet = string.Join('\n', sourceCoordinates
                .Select((pair, index) => (Pair: pair, Hash: sourceFileHashes[index]))
                .OrderBy(static source => source.Pair[1]).ThenBy(static source => source.Pair[0])
                .Select(static source => $"{source.Pair[0]},{source.Pair[1]}:{source.Hash}"));
            string computedSourceSetSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSourceSet))).ToLowerInvariant();
            if (!computedSourceSetSha.Equals(sourceSetSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Empty navmesh evidence source-set hash is inconsistent: {evidencePath}");
            }

            int terrainTriangles = ReadRequiredInt(payload, "terrain_triangles", evidencePath);
            int collisionTriangles = ReadRequiredInt(payload, "collision_triangles", evidencePath);
            int liquidTriangles = ReadRequiredInt(payload, "liquid_triangles", evidencePath);
            if (terrainTriangles < 0 || collisionTriangles < 0 || liquidTriangles < 0 ||
                (long)terrainTriangles + collisionTriangles + liquidTriangles <= 0)
            {
                throw new InvalidDataException($"Empty navmesh evidence has no raw geometry contribution: {evidencePath}");
            }

            if (expectedDotRecastVersion.Length == 0 || expectedGeometryVersion.Length == 0 || expectedRawFormat.Length == 0 ||
                ReadRequiredString(payload, "dotrecast_version", evidencePath) != expectedDotRecastVersion ||
                ReadRequiredString(payload, "geometry_export_version", evidencePath) != expectedGeometryVersion ||
                ReadRequiredString(payload, "raw_geometry_format", evidencePath) != expectedRawFormat)
            {
                throw new InvalidDataException($"Empty navmesh evidence bake algorithm identity is missing or inconsistent: {evidencePath}");
            }
        }
        return result;
    }

    private static int[][] ReadRequiredPairArray(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Empty navmesh evidence field '{name}' is missing: {path}");
        }
        List<int[]> result = [];
        HashSet<(int X, int Y)> unique = [];
        foreach (JsonElement pair in values.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int y) ||
                x is < 0 or >= 64 || y is < 0 or >= 64 || !unique.Add((x, y)))
            {
                throw new InvalidDataException($"Empty navmesh evidence field '{name}' has an invalid or duplicate pair: {path}");
            }
            result.Add([x, y]);
        }
        return [.. result];
    }

    private static string ReadOptionalNestedString(JsonElement parent, string objectName, string propertyName) =>
        parent.TryGetProperty(objectName, out JsonElement child) && child.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(child, propertyName)
            : "";

    private static void ValidateDnmHeader(string tilePath, int expectedX, int expectedZ)
    {
        try
        {
            using FileStream stream = File.OpenRead(tilePath);
            using BinaryReader reader = new(stream);
            DtMeshData data = new DtMeshDataReader().Read(reader, NavmeshSettings.VertsPerPoly);
            if (data.header.x != expectedX || data.header.y != expectedZ ||
                data.header.polyCount <= 0 || data.header.vertCount <= 0)
            {
                throw new InvalidDataException($"DNM header identity does not match its manifest tile: {tilePath}");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"DNM tile is unreadable or malformed: {tilePath}", ex);
        }
    }

    private static HashSet<(int X, int Y)> ReadOptionalCoverageAdts(JsonElement root, string name, string manifestPath)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage))
        {
            return [];
        }

        if (coverage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Forever coverage is malformed: {manifestPath}");
        }
        if (!coverage.TryGetProperty(name, out JsonElement values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Forever coverage field {name} is malformed: {manifestPath}");

        HashSet<(int X, int Y)> result = [];
        foreach (JsonElement pair in values.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int y) ||
                x is < 0 or >= 64 || y is < 0 or >= 64 || !result.Add((x, y)))
            {
                throw new InvalidDataException($"Continent coverage has an invalid or duplicate ADT coordinate in {name}: {manifestPath}");
            }
        }

        return result;
    }

    private static HashSet<(int X, int Z)> ReadOptionalCoverageTiles(JsonElement root, string name, string manifestPath)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage))
        {
            return [];
        }

        if (coverage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Forever coverage is malformed: {manifestPath}");
        }
        if (!coverage.TryGetProperty(name, out JsonElement values)) return [];
        if (values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Forever coverage field {name} is malformed: {manifestPath}");

        HashSet<(int X, int Z)> result = [];
        foreach (JsonElement pair in values.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int z) ||
                !NavmeshCoords.IsValidTile(x, z) || !result.Add((x, z)))
            {
                throw new InvalidDataException($"Continent coverage has an invalid or duplicate tile coordinate in {name}: {manifestPath}");
            }
        }

        return result;
    }

    private static void ValidateGeometryBakeIdentity(JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("bake_settings", out JsonElement settings) || settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("padding_yards", out JsonElement paddingValue) ||
            !paddingValue.TryGetSingle(out float padding) || padding != TileGeometryExtractor.PaddingYd ||
            !string.Equals(ReadOptionalString(settings, "geometry_export_version"),
                NavmeshSettings.ForeverGeometryExportAlgorithmVersion, StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(settings, "raw_geometry_format"),
                NavmeshSettings.ForeverRawGeometryFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Forever manifest geometry settings are missing or incompatible; regenerate raw geometry and DNM tiles: {manifestPath}");
        }
    }

    private static (string File, string Sha256, string Product, int Build, int MapId,
        HashSet<(int X, int Y)> Adts) ReadCoverageTileList(string cacheDirectory, JsonElement root,
        string manifestPath, string product, int build, int mapId)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object)
        {
            return ("", "", "", 0, -1, []);
        }

        string file = ReadOptionalString(coverage, "wdt_tile_list_file");
        string sha256 = ReadOptionalString(coverage, "wdt_tile_list_sha256");
        string proofProduct = ReadOptionalString(coverage, "wdt_client_product");
        int proofBuild = coverage.TryGetProperty("wdt_client_build", out JsonElement buildValue) &&
                    buildValue.TryGetInt32(out int parsedBuild) ? parsedBuild : 0;
        int proofMapId = coverage.TryGetProperty("wdt_map_id", out JsonElement mapValue) &&
                    mapValue.TryGetInt32(out int parsedMapId) ? parsedMapId : -1;
        bool anyProofField = file.Length > 0 || sha256.Length > 0 || proofProduct.Length > 0 || proofBuild > 0 || proofMapId >= 0;
        if (!anyProofField)
        {
            return ("", "", "", 0, -1, []);
        }

        if (file.Length == 0 || !string.Equals(file, WdtCoverageEvidencePathPrefix + sha256 + ".json", StringComparison.Ordinal) ||
            Path.IsPathRooted(file) || file.Contains('\\'))
        {
            throw new InvalidDataException($"WDT coverage proof path is missing, non-canonical, or does not bind its SHA-256: {manifestPath}");
        }

        if (!IsSha256(sha256))
        {
            throw new InvalidDataException($"WDT tile-list evidence SHA-256 is malformed: {manifestPath}");
        }

        if (proofProduct.Length == 0 || proofBuild <= 0 || proofMapId < 0 ||
            !string.Equals(proofProduct, product, StringComparison.OrdinalIgnoreCase) ||
            proofBuild != build || proofMapId != mapId)
        {
            throw new InvalidDataException($"WDT tile-list evidence identity is incomplete or differs from the cache manifest: {manifestPath}");
        }

        string cacheRoot = Path.GetFullPath(cacheDirectory);
        byte[] evidenceBytes = ReadVerifiedCoverageFile(cacheRoot, file, sha256,
            WdtCoverageEvidencePathPrefix, ".json", manifestPath);
        string evidencePath = Path.Combine(cacheRoot, file.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument evidenceDocument = JsonDocument.Parse(evidenceBytes);
        JsonElement evidence = evidenceDocument.RootElement;
        string evidenceProduct = ReadRequiredString(evidence, "client_product", evidencePath);
        int evidenceBuild = ReadRequiredInt(evidence, "client_build", evidencePath);
        int evidenceMap = ReadRequiredInt(evidence, "map_id", evidencePath);
        if (ReadRequiredString(evidence, "format", evidencePath) != WdtCoverageEvidenceFormat ||
            !string.Equals(ReadRequiredString(evidence, "source", evidencePath), "mapextractor --list-tiles process", StringComparison.Ordinal) ||
            !string.Equals(evidenceProduct, proofProduct, StringComparison.OrdinalIgnoreCase) || evidenceBuild != proofBuild ||
            evidenceMap != proofMapId ||
            ReadRequiredString(evidence, "tool_source_repository", evidencePath) != WdtEnumeratorSourceRepository ||
            ReadRequiredString(evidence, "tool_source_commit", evidencePath) != WdtEnumeratorSourceCommit ||
            !ReadRequiredString(evidence, "tool_patch_sha256", evidencePath).Equals(WdtEnumeratorPatchSha256, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(ReadRequiredString(evidence, "mapextractor_sha256", evidencePath)) ||
            ReadRequiredInt(evidence, "process_exit_code", evidencePath) != 0 ||
            ReadOptionalBool(evidence, "process_timed_out") ||
            ReadRequiredInt(evidence, "wdt_slots_visited", evidencePath) != ForeverWdtTileList.TileCount)
        {
            throw new InvalidDataException($"WDT coverage evidence format or client identity is invalid: {evidencePath}");
        }

        string locale = ReadRequiredString(evidence, "locale", evidencePath);
        if (!IsSupportedLocale(locale))
        {
            throw new InvalidDataException($"WDT coverage evidence has an unsupported mapextractor locale: {evidencePath}");
        }
        string clientRoot = ReadRequiredString(evidence, "client_root", evidencePath);
        string dataRoot = ReadRequiredString(evidence, "casc_data_root", evidencePath);
        string historicalCacheRoot = ReadRequiredString(evidence, "publication_cache_root", evidencePath);
        string executablePath = ReadRequiredString(evidence, "mapextractor_path", evidencePath);
        if (!IsCanonicalAbsolutePath(clientRoot) || !IsCanonicalAbsolutePath(dataRoot) ||
            !IsCanonicalAbsolutePath(historicalCacheRoot) || !IsCanonicalAbsolutePath(executablePath) ||
            !Path.GetFileName(executablePath).Equals("mapextractor.exe", StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(dataRoot, Path.Combine(clientRoot, "Data")))
        {
            throw new InvalidDataException($"WDT coverage evidence contains non-canonical historical paths or a CASC root inconsistent with its client directory: {evidencePath}");
        }

        string[] arguments = ReadRequiredStringArray(evidence, "arguments", evidencePath);
        if (arguments.Length != 13 || arguments[0] != "-i" || arguments[1] != clientRoot ||
            arguments[2] != "-o" || arguments[4] != "-p" || !arguments[5].Equals(proofProduct, StringComparison.OrdinalIgnoreCase) ||
            arguments[6] != "-l" || arguments[7] != locale || arguments[8] != "-e" || arguments[9] != "1" ||
            arguments[10] != "-m" || arguments[11] != proofMapId.ToString(CultureInfo.InvariantCulture) ||
            arguments[12] != "--list-tiles")
        {
            throw new InvalidDataException($"WDT coverage evidence does not record the exact MapID-only invocation: {evidencePath}");
        }
        string outputDirectory = arguments[3];
        string expectedHistoricalCoverageRoot = Path.Combine(historicalCacheRoot, "coverage");
        string outputName = Path.GetFileName(outputDirectory);
        if (!IsCanonicalAbsolutePath(outputDirectory) ||
            !PathEquals(Path.GetDirectoryName(outputDirectory)!, expectedHistoricalCoverageRoot) ||
            !outputName.StartsWith(".wdt-listing-", StringComparison.Ordinal) ||
            outputName.Length != ".wdt-listing-".Length + 32 ||
            !outputName[".wdt-listing-".Length..].All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"WDT coverage evidence output directory is not the canonical isolated historical cache workspace: {evidencePath}");
        }

        if (!evidence.TryGetProperty("adt_count", out JsonElement adtCountValue) ||
            !adtCountValue.TryGetInt32(out int expectedAdtCount) || expectedAdtCount <= 0)
        {
            throw new InvalidDataException($"WDT coverage evidence ADT count is missing or invalid: {evidencePath}");
        }

        if (!evidence.TryGetProperty("adt_xy", out JsonElement adtValues) || adtValues.ValueKind != JsonValueKind.Array ||
            adtValues.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"WDT coverage evidence has no ADT coordinate list: {evidencePath}");
        }
        HashSet<(int X, int Y)> adts = [];
        (int X, int Y)? previous = null;
        foreach (JsonElement pair in adtValues.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int y) ||
                x is < 0 or >= 64 || y is < 0 or >= 64 || !adts.Add((x, y)) ||
                previous.HasValue && (y < previous.Value.Y || y == previous.Value.Y && x <= previous.Value.X))
            {
                throw new InvalidDataException($"WDT coverage evidence has an invalid, duplicate, or non-normalized ADT record: {evidencePath}");
            }
            previous = (x, y);
        }

        if (adts.Count != expectedAdtCount)
        {
            throw new InvalidDataException($"WDT coverage evidence ADT count does not match its unique coordinate list: {evidencePath}");
        }

        string nativeFile = ReadRequiredString(evidence, "native_tile_list_file", evidencePath);
        string nativeSha = ReadRequiredString(evidence, "native_tile_list_sha256", evidencePath);
        byte[] nativeBytes = ReadVerifiedCoverageFile(cacheRoot, nativeFile, nativeSha,
            WdtNativeTileListPathPrefix, ".tilelist", evidencePath);
        ForeverWdtTileList.Parsed nativeTileList = ForeverWdtTileList.Parse(nativeBytes);
        if (nativeTileList.Build != proofBuild || !nativeTileList.Adts.SetEquals(adts))
        {
            throw new InvalidDataException($"Native WDT .tilelist does not match the process evidence ADT set or Build: {evidencePath}");
        }

        string stdoutFile = ReadRequiredString(evidence, "stdout_file", evidencePath);
        string stdoutSha = ReadRequiredString(evidence, "stdout_sha256", evidencePath);
        byte[] stdoutBytes = ReadVerifiedCoverageFile(cacheRoot, stdoutFile, stdoutSha,
            WdtStdoutPathPrefix, ".txt", evidencePath);
        string stderrFile = ReadRequiredString(evidence, "stderr_file", evidencePath);
        string stderrSha = ReadRequiredString(evidence, "stderr_sha256", evidencePath);
        byte[] stderrBytes = ReadVerifiedCoverageFile(cacheRoot, stderrFile, stderrSha,
            WdtStderrPathPrefix, ".txt", evidencePath);
        ValidateProcessOutput(Encoding.UTF8.GetString(stdoutBytes), Encoding.UTF8.GetString(stderrBytes),
            dataRoot, proofProduct, locale, proofBuild, proofMapId, adts, expectedAdtCount, evidencePath);

        return (file, sha256, proofProduct, proofBuild, proofMapId, adts);
    }

    private static byte[] ReadVerifiedCoverageFile(string cacheRoot, string relativePath, string sha256,
        string expectedPrefix, string suffix, string ownerPath)
    {
        if (!IsSha256(sha256) || !string.Equals(relativePath, expectedPrefix + sha256 + suffix, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new InvalidDataException($"Coverage evidence path does not bind its SHA-256: {ownerPath}");
        }
        string fullPath = Path.GetFullPath(Path.Combine(cacheRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string coverageDirectory = Path.Combine(cacheRoot, "coverage");
        if (!IsSameOrChild(fullPath, cacheRoot) ||
            (File.GetAttributes(cacheRoot) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(coverageDirectory) ||
            (File.GetAttributes(coverageDirectory) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Coverage evidence is missing or resolves outside the cache directory: {fullPath}");
        }
        byte[] bytes = File.ReadAllBytes(fullPath);
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Coverage evidence SHA-256 does not match: {fullPath}");
        }
        return bytes;
    }

    private static string[] ReadRequiredStringArray(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"WDT coverage evidence field '{name}' is missing or malformed: {path}");
        }
        string[] result = values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"WDT coverage evidence field '{name}' contains a non-string: {path}")).ToArray();
        return result;
    }

    private static void ValidateProcessOutput(string stdout, string stderr, string dataRoot,
        string product, string locale, int build, int mapId, HashSet<(int X, int Y)> expectedAdts,
        int expectedAdtCount, string evidencePath)
    {
        string[] lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim()).ToArray();
        string expectedBuild = $"Detected client build: {build.ToString(CultureInfo.InvariantCulture)}";
        string expectedMap = $"Restricted extraction to MapID {mapId.ToString(CultureInfo.InvariantCulture)}.";
        string[] storage = lines.Where(static line => line.StartsWith("Opened casc storage '", StringComparison.Ordinal)).ToArray();
        string[] storageIdentity = storage.Where(static line => line.Contains("' product ", StringComparison.Ordinal)).ToArray();
        string[] buildMarkers = lines.Where(static line => line.StartsWith("Detected client build:", StringComparison.Ordinal)).ToArray();
        string[] mapMarkers = lines.Where(static line => line.StartsWith("Restricted extraction to MapID ", StringComparison.Ordinal)).ToArray();
        string[] completion = lines.Where(static line => line.StartsWith("wdt-enumeration-complete ", StringComparison.Ordinal)).ToArray();
        string expectedCompletion = $"wdt-enumeration-complete map={mapId} build={build} visited={ForeverWdtTileList.TileCount} tiles={expectedAdtCount}";
        if (storage.Length <= storageIdentity.Length ||
            storage.Any(line => !StoragePathOutputMatches(line, dataRoot)) ||
            storageIdentity.Length != 2 || storageIdentity.Any(line => !StorageIdentityOutputMatches(line, product, locale)) ||
            buildMarkers.Length != 1 || buildMarkers[0] != expectedBuild || mapMarkers.Length != 1 || mapMarkers[0] != expectedMap ||
            completion.Length != 1 || completion[0] != expectedCompletion || lines.Length == 0 || lines[^1] != expectedCompletion ||
            lines.Any(static line => line.StartsWith("FATAL:", StringComparison.OrdinalIgnoreCase)) ||
            stderr.Contains("fatal", StringComparison.OrdinalIgnoreCase) || stderr.Contains("error opening casc", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Saved mapextractor output is incomplete or contradicts its WDT process proof: {evidencePath}");
        }

        HashSet<(int X, int Y)> observedAdts = [];
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length == 0 || !fields[0].Equals("tile", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length != 4 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int listedMap) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
                listedMap != mapId || x is < 0 or >= 64 || y is < 0 or >= 64 || !observedAdts.Add((x, y)))
            {
                throw new InvalidDataException($"Saved mapextractor output contains an invalid or duplicate WDT ADT row: {evidencePath}");
            }
        }
        if (!observedAdts.SetEquals(expectedAdts) || observedAdts.Count != expectedAdtCount)
        {
            throw new InvalidDataException($"Saved mapextractor output ADT rows do not match the WDT coverage proof: {evidencePath}");
        }
    }

    private static bool StoragePathOutputMatches(string line, string dataRoot)
    {
        const string markerPrefix = "Opened casc storage '";
        if (!line.StartsWith(markerPrefix, StringComparison.Ordinal)) return false;
        int pathEnd = line.IndexOf('\'', markerPrefix.Length);
        if (pathEnd < markerPrefix.Length) return false;
        string listedPath = line[markerPrefix.Length..pathEnd];
        string suffix = line[(pathEnd + 1)..];
        if (suffix.Length != 0 && !suffix.StartsWith(" product ", StringComparison.Ordinal)) return false;
        return PathEquals(listedPath, dataRoot);
    }

    private static bool StorageIdentityOutputMatches(string line, string product, string locale)
    {
        const string identitySeparator = "' product ";
        int identityStart = line.IndexOf(identitySeparator, StringComparison.Ordinal);
        if (identityStart < 0) return false;
        string suffix = line[(identityStart + identitySeparator.Length)..];
        return suffix.Equals($"{product} locale {locale}", StringComparison.Ordinal);
    }

    private static bool IsSupportedLocale(string locale) => locale is
        "enUS" or "enGB" or "koKR" or "frFR" or "deDE" or "zhCN" or "zhTW" or "esES" or "esMX" or "ruRU" or "ptBR" or "itIT";

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonicalAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        string fullPath = Path.GetFullPath(path);
        return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ReadRequiredString(JsonElement parent, string name, string manifestPath) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Forever manifest field '{name}' is missing or malformed: {manifestPath}");

    private static string ReadOptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadRequiredInt(JsonElement parent, string name, string manifestPath) =>
        parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException($"Forever manifest field '{name}' is missing or malformed: {manifestPath}");

    private static bool ReadOptionalBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private sealed record TileRecord(int X, int Z, string File, string Sha256);

    private sealed record ManifestSnapshot(string Scope, string Continent,
        bool CompleteForRequestedSourcesAndTiles, bool CompleteForContinent, bool PublishableAsContinentCache,
        string CoverageProvenance, string WdtTileListFile, string WdtTileListSha256,
        string WdtTileListProduct, int WdtTileListBuild, int WdtTileListMapId, HashSet<(int X, int Y)> SourceAdts,
        HashSet<(int X, int Y)> WdtAdts,
        HashSet<(int X, int Y)> ExpectedAdts, HashSet<(int X, int Y)> ProcessedAdts,
        HashSet<(int X, int Z)> ExpectedTiles, HashSet<(int X, int Z)> CompletedTiles,
        HashSet<(int X, int Z)> ManifestTiles, Dictionary<(int X, int Z), string> DnmSha256s,
        HashSet<(int X, int Z)> EmptyTiles);
}
