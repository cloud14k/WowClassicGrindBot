using System;
using System.IO;
using System.Linq;

using SharedLib;

namespace Game;

/// <summary>
/// Product and build identity from the CASC install's .build.info file.
/// File version alone is not enough to distinguish Classic-derived products.
/// </summary>
public sealed record WowClientBuildInfo(string Product, string Version, int Build)
{
    public static WowClientBuildInfo? Read(string executableDirectory, Version? executableVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);

        DirectoryInfo? directory = new(executableDirectory);
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, ".build.info");
            if (File.Exists(path))
            {
                return ReadFile(path, executableVersion);
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads the CASC identity for an independent Forever pathing process. It
    /// fails closed when .build.info is absent, ambiguous, or belongs to a
    /// different product; callers must not supply a guessed Build.
    /// </summary>
    public static WowClientBuildInfo ReadForever(string clientRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        WowClientBuildInfo? info = Read(clientRoot);
        if (info is null)
        {
            throw new InvalidDataException(
                $"Could not resolve one active Product/Version from '{Path.Combine(clientRoot, ".build.info")}'. Configure Pathing:ForeverClientRoot to the installed Forever client with an unambiguous .build.info.");
        }

        if (!string.Equals(info.Product, StartupClientVersion.ForeverProductId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"PathingAPI exp=forever requires Product '{StartupClientVersion.ForeverProductId}', but '{clientRoot}' reports '{info.Product}'.");
        }

        return info;
    }

    /// <summary>Resolves PathingAPI's explicit configuration/env fallback and client identity.</summary>
    public static WowClientBuildInfo ReadForeverConfigured(string? configuredRoot, string? environmentRoot)
    {
        string? root = !string.IsNullOrWhiteSpace(configuredRoot) ? configuredRoot : environmentRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "PathingAPI exp=forever requires Pathing:ForeverClientRoot or PATHING_FOREVER_CLIENT_ROOT pointing to the Forever CASC client directory; Product/Build are read from its .build.info.");
        }

        return ReadForever(root);
    }

    private static WowClientBuildInfo? ReadFile(string path, Version? executableVersion)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            return null;
        }

        string[] headers = lines[0].Split('|').Select(static header => header.Split('!')[0]).ToArray();
        int activeIndex = Array.IndexOf(headers, "Active");
        int versionIndex = Array.IndexOf(headers, "Version");
        int productIndex = Array.IndexOf(headers, "Product");
        if (activeIndex < 0 || versionIndex < 0 || productIndex < 0)
        {
            return null;
        }

        WowClientBuildInfo[] active = lines.Skip(1)
            .Select(line => line.Split('|'))
            .Where(fields => fields.Length > Math.Max(activeIndex, Math.Max(versionIndex, productIndex)) &&
                             fields[activeIndex] == "1")
            .Select(fields => Create(fields[productIndex], fields[versionIndex]))
            .Where(static item => item != null)
            .Select(static item => item!)
            .ToArray();

        if (executableVersion is { Major: > 0 })
        {
            WowClientBuildInfo[] matching = active
                .Where(item => System.Version.TryParse(item.Version, out Version? parsed) && parsed == executableVersion)
                .ToArray();
            if (matching.Length == 1)
            {
                return matching[0];
            }

            if (matching.Select(static item => item.Product).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                return null;
            }
        }

        return active.Length == 1 ? active[0] : null;
    }

    private static WowClientBuildInfo? Create(string product, string version)
    {
        string[] parts = version.Split('.');
        if (string.IsNullOrWhiteSpace(product) || parts.Length < 2 ||
            !int.TryParse(parts[^1], out int build) || build <= 0)
        {
            return null;
        }

        return new WowClientBuildInfo(product, version, build);
    }
}
