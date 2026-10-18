using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace PPather.Navmesh;

/// <summary>
/// Scope-aware validation for Forever navmesh packs. A selected test pack can
/// be checked only through the explicitly named test API; it can never satisfy
/// the continent eligibility checks used by publication or runtime loading.
/// </summary>
public static class ForeverNavmeshCacheManifest
{
    public const string Format = "wow-forever-dnm-manifest-v1";

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
        if (!Directory.Exists(cacheDirectory))
        {
            throw new DirectoryNotFoundException($"Forever navmesh cache directory does not exist: {cacheDirectory}");
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

        bool completeForRequested = ReadOptionalBool(root, "complete_for_requested_sources_and_tiles");
        bool completeForContinent = ReadOptionalBool(root, "complete_for_continent");
        bool publishable = ReadOptionalBool(root, "publishable_as_continent_cache");
        HashSet<(int X, int Y)> sourceAdts = ReadSourceAdts(root, manifestPath);
        TileRecord[] tiles = ReadAndValidateTiles(cacheDirectory, root, manifestPath);
        (string tileListFile, string tileListSha256, string tileListProduct, int tileListBuild,
            int tileListMapId) = ReadCoverageTileList(root, manifestPath);
        if (tileListProduct.Length > 0 &&
            (!string.Equals(tileListProduct, product, StringComparison.OrdinalIgnoreCase) ||
             tileListBuild != build || tileListMapId != mapId))
        {
            throw new InvalidDataException($"WDT tile-list evidence identity does not match the cache manifest: {manifestPath}");
        }

        return new ManifestSnapshot(scope, continent, completeForRequested, completeForContinent,
            publishable, ReadOptionalString(root, "coverage_provenance"), tileListFile, tileListSha256,
            tileListProduct, tileListBuild, tileListMapId, sourceAdts,
            ReadOptionalCoverageAdts(root, "expected_source_adts_xy", manifestPath),
            ReadOptionalCoverageAdts(root, "processed_source_adts_xy", manifestPath),
            ReadOptionalCoverageTiles(root, "expected_tile_indices", manifestPath),
            ReadOptionalCoverageTiles(root, "completed_tile_indices", manifestPath),
            tiles.Select(static tile => (tile.X, tile.Z)).ToHashSet());
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

        if (manifest.ExpectedAdts.Count == 0 || !manifest.ExpectedAdts.SetEquals(manifest.ProcessedAdts) ||
            !manifest.ExpectedAdts.SetEquals(manifest.SourceAdts) ||
            manifest.ExpectedTiles.Count == 0 || !manifest.ExpectedTiles.SetEquals(manifest.CompletedTiles) ||
            !manifest.ExpectedTiles.SetEquals(manifest.ManifestTiles))
        {
            throw new InvalidDataException("Forever continent coverage proof is incomplete or does not match its source/tile records.");
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

    private static HashSet<(int X, int Y)> ReadSourceAdts(JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("source_adts", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Forever manifest has no source_adts array: {manifestPath}");
        }

        HashSet<(int X, int Y)> result = [];
        foreach (JsonElement source in sources.EnumerateArray())
        {
            string fileName = ReadRequiredString(source, "file_name", manifestPath);
            string sourceHash = ReadRequiredString(source, "sha256", manifestPath);
            if (!source.TryGetProperty("adt_xy", out JsonElement pair) || pair.ValueKind != JsonValueKind.Array ||
                pair.GetArrayLength() != 2 || !pair[0].TryGetInt32(out int x) || !pair[1].TryGetInt32(out int y) ||
                x is < 0 or >= 64 || y is < 0 or >= 64 || !result.Add((x, y)) ||
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
        if (!root.TryGetProperty("tiles", out JsonElement tiles) || tiles.ValueKind != JsonValueKind.Array || tiles.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Forever navmesh manifest lists no tiles: {manifestPath}");
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

        if (coverage.ValueKind != JsonValueKind.Object ||
            !coverage.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Continent coverage is missing {name}: {manifestPath}");
        }

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

        if (coverage.ValueKind != JsonValueKind.Object ||
            !coverage.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Continent coverage is missing {name}: {manifestPath}");
        }

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

    private static (string File, string Sha256, string Product, int Build, int MapId) ReadCoverageTileList(
        JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("coverage", out JsonElement coverage) || coverage.ValueKind != JsonValueKind.Object)
        {
            return ("", "", "", 0, -1);
        }

        string file = ReadOptionalString(coverage, "wdt_tile_list_file");
        string sha256 = ReadOptionalString(coverage, "wdt_tile_list_sha256");
        string product = ReadOptionalString(coverage, "wdt_client_product");
        int build = coverage.TryGetProperty("wdt_client_build", out JsonElement buildValue) &&
                    buildValue.TryGetInt32(out int parsedBuild) ? parsedBuild : 0;
        int mapId = coverage.TryGetProperty("wdt_map_id", out JsonElement mapValue) &&
                    mapValue.TryGetInt32(out int parsedMapId) ? parsedMapId : -1;
        if (file.Length > 0 && (!string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal) ||
                                file is "." or ".."))
        {
            throw new InvalidDataException($"WDT tile-list evidence file name is unsafe: {manifestPath}");
        }

        if (sha256.Length > 0 && !IsSha256(sha256))
        {
            throw new InvalidDataException($"WDT tile-list evidence SHA-256 is malformed: {manifestPath}");
        }

        bool anyIdentity = product.Length > 0 || build > 0 || mapId >= 0;
        bool completeIdentity = product.Length > 0 && build > 0 && mapId >= 0;
        if (anyIdentity && !completeIdentity)
        {
            throw new InvalidDataException($"WDT tile-list evidence identity is incomplete: {manifestPath}");
        }

        return (file, sha256, product, build, mapId);
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
        HashSet<(int X, int Y)> ExpectedAdts, HashSet<(int X, int Y)> ProcessedAdts,
        HashSet<(int X, int Z)> ExpectedTiles, HashSet<(int X, int Z)> CompletedTiles,
        HashSet<(int X, int Z)> ManifestTiles);
}
