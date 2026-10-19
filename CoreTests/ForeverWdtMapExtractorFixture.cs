using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using PPather.Navmesh;

namespace CoreTests;

internal static class ForeverWdtMapExtractorFixture
{
    public static int Run(string[] args)
    {
        string mode = Environment.GetEnvironmentVariable("WOW_TEST_WDT_MODE") ?? "success";
        string? markerPath = Environment.GetEnvironmentVariable("WOW_TEST_WDT_MARKER");
        if (markerPath is not null) File.WriteAllText(markerPath, mode);
        Dictionary<string, string> values = Parse(args);
        string clientRoot = Path.GetFullPath(values["-i"]);
        string outputRoot = Path.GetFullPath(values["-o"]);
        string product = values["-p"];
        string locale = values["-l"];
        int mapId = int.Parse(values["-m"], CultureInfo.InvariantCulture);
        int build = 70094;
        string dataRoot = Path.Combine(clientRoot, "Data");
        if (mode == "timeout")
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 0;
        }

        string[] coordinates = (Environment.GetEnvironmentVariable("WOW_TEST_WDT_ADTS") ?? "33,46")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        (int X, int Y)[] adts = coordinates.Select(value =>
        {
            string[] parts = value.Split(',');
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }).ToArray();
        int listedMap = mode == "wrong-map" ? mapId + 1 : mapId;
        int listedBuild = mode == "wrong-build" ? build + 1 : build;
        string listedProduct = mode == "wrong-product" ? "wow_classic" : product;
        string listedLocale = mode == "wrong-locale" ? "zhTW" : locale;
        string listedRoot = mode == "wrong-casc-root" ? Path.Combine(clientRoot, "WrongData") : dataRoot;

        Console.WriteLine($"Opened casc storage '{listedRoot}' product {listedProduct} locale {listedLocale}");
        Console.WriteLine($"Detected client build: {listedBuild}");
        Console.WriteLine($"Opened casc storage '{listedRoot}' product {listedProduct} locale {listedLocale}");
        Console.WriteLine($"Restricted extraction to MapID {listedMap}.");
        foreach ((int x, int y) in adts)
        {
            Console.WriteLine($"tile\t{listedMap}\t{x}\t{y}");
        }
        if (mode == "duplicate") Console.WriteLine($"tile\t{listedMap}\t{adts[0].X}\t{adts[0].Y}");
        if (mode == "fatal") Console.WriteLine("FATAL: fixture simulated a WDT read error.");
        if (mode == "stderr-fatal") Console.Error.WriteLine("fatal fixture error");
        if (mode == "nonzero") return 23;
        if (mode == "truncated") return WriteTileList(outputRoot, mapId, build, adts, truncate: true);

        int tileCount = mode == "duplicate" ? adts.Length + 1 : adts.Length;
        if (mode != "missing-native")
        {
            int nativeBuild = mode == "native-wrong-build" ? build + 1 : build;
            (int X, int Y)[] nativeAdts = mode == "native-mismatch"
                ? adts.Select(static adt => ((adt.X + 1) % 64, adt.Y)).ToArray()
                : adts;
            WriteTileList(outputRoot, mapId, nativeBuild, nativeAdts, truncate: mode == "native-truncated");
        }

        if (mode != "incomplete")
        {
            Console.WriteLine($"wdt-enumeration-complete map={listedMap} build={listedBuild} visited=4096 tiles={tileCount}");
        }
        return 0;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }
        foreach (string required in new[] { "-i", "-o", "-p", "-l", "-e", "-m" })
        {
            if (!values.ContainsKey(required)) throw new ArgumentException($"Fixture is missing {required}.");
        }
        if (values["-e"] != "1" || !args.Contains("--list-tiles", StringComparer.Ordinal))
        {
            throw new ArgumentException("Fixture requires the map-only --list-tiles invocation.");
        }
        return values;
    }

    private static int WriteTileList(string outputRoot, int mapId, int build,
        IReadOnlyCollection<(int X, int Y)> adts, bool truncate)
    {
        string maps = Path.Combine(outputRoot, "maps");
        Directory.CreateDirectory(maps);
        string path = Path.Combine(maps, mapId.ToString("D4", CultureInfo.InvariantCulture) + ".tilelist");
        byte[] bytes = new byte[ForeverWdtTileList.FileLength];
        "MAPS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)), ForeverWdtTileList.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, sizeof(uint)), checked((uint)build));
        bytes.AsSpan(12).Fill((byte)'0');
        foreach ((int x, int y) in adts)
        {
            bytes[12 + ForeverWdtTileList.TileCount - 1 - (y * 64 + x)] = (byte)'1';
        }
        File.WriteAllBytes(path, truncate ? bytes[..100] : bytes);
        return 0;
    }
}
