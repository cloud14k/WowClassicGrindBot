using System;
using System.IO;
using System.Linq;

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
