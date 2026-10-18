using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PPather.Navmesh;

/// <summary>
/// Verifies the identity and exact file set of an offline Forever navmesh pack
/// before the runtime lets Detour load any of its tiles.
/// </summary>
public static class ForeverNavmeshCacheManifest
{
    public const string Format = "wow-forever-dnm-manifest-v1";

    public static void Validate(string cacheDirectory, string expectedProduct, int expectedBuild,
        string expectedSettingsHash, int expectedMapId)
    {
        string manifestPath = Path.Combine(cacheDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Forever navmesh cache has no verified manifest: {manifestPath}");
        }

        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (ReadString(root, "format") != Format ||
            !string.Equals(ReadString(root, "client_product"), expectedProduct, StringComparison.OrdinalIgnoreCase) ||
            ReadInt(root, "client_build") != expectedBuild ||
            ReadInt(root, "map_id") != expectedMapId ||
            !string.Equals(ReadString(root, "settings_hash"), expectedSettingsHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Forever navmesh manifest identity does not match {expectedProduct} Build {expectedBuild} " +
                $"MapID {expectedMapId} and settings {expectedSettingsHash}: {manifestPath}");
        }

        if (!root.TryGetProperty("complete_for_requested_sources_and_tiles", out JsonElement complete) ||
            complete.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"Forever navmesh pack is incomplete or unverified: {manifestPath}");
        }

        if (!root.TryGetProperty("tiles", out JsonElement tiles) || tiles.ValueKind != JsonValueKind.Array || tiles.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Forever navmesh manifest lists no tiles: {manifestPath}");
        }

        HashSet<string> expectedFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<(int X, int Z)> expectedIndices = [];
        foreach (JsonElement tile in tiles.EnumerateArray())
        {
            string file = ReadString(tile, "file");
            string sha256 = ReadString(tile, "sha256");
            int x = ReadInt(tile, "dt_tile_x");
            int z = ReadInt(tile, "dt_tile_z");
            if (string.IsNullOrWhiteSpace(file) || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal) ||
                sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
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
        }

        HashSet<string> actualFiles = Directory.EnumerateFiles(cacheDirectory, "tile_*.dnm")
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
    }

    public static void ValidateForPublication(string cacheDirectory, string expectedProduct,
        int expectedBuild, string expectedSettingsHash, int expectedMapId)
    {
        Validate(cacheDirectory, expectedProduct, expectedBuild, expectedSettingsHash, expectedMapId);
        using FileStream stream = File.OpenRead(Path.Combine(cacheDirectory, "manifest.json"));
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("publishable_as_continent_cache", out JsonElement publishable) ||
            publishable.ValueKind != JsonValueKind.True ||
            !string.Equals(ReadString(root, "scope"), "continent", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Forever navmesh pack is a selected-scope test cache and cannot be published as a continent cache.");
        }
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : -1;
}
