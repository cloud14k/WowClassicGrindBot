using System.Globalization;
using System.Text.Json;

using SharedLib;

namespace ForeverGeometryTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> RepeatableOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--input", "--adt", "--expect-adt", "--bake-tile",
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            Dictionary<string, List<string>> values = ParseArgs(args);
            string output = RequiredSingle(values, "--output");
            int map = int.Parse(RequiredSingle(values, "--map"), CultureInfo.InvariantCulture);
            string[] inputs = RequiredMany(values, "--input");
            string[] adts = RequiredMany(values, "--adt");
            string[] expectedAdts = RequiredMany(values, "--expect-adt");
            if (inputs.Length != adts.Length)
            {
                throw new ArgumentException("Each --input must have one matching --adt in the same order.");
            }

            List<GeometryInputFile> geometryInputs = [];
            for (int i = 0; i < inputs.Length; i++)
            {
                (int x, int y) = ParsePair(adts[i], "--adt");
                geometryInputs.Add(new GeometryInputFile(inputs[i], x, y));
            }

            NavmeshBakeOptions defaults = new();
            float radius = OptionalFloat(values, "--agent-radius", defaults.AgentRadius);
            float climb = OptionalFloat(values, "--agent-climb", defaults.AgentMaxClimb);
            float slope = OptionalFloat(values, "--walkable-slope", defaults.WalkableSlope);
            float padding = OptionalFloat(values, "--padding-yards", PPather.Navmesh.TileGeometryExtractor.PaddingYd);
            float? minWorldZ = OptionalNullableFloat(values, "--min-world-z");

            bool hasDnmOutput = values.TryGetValue("--dnm-output", out List<string>? dnmOutputs);
            string[] bakeTileValues = OptionalMany(values, "--bake-tile");
            if (hasDnmOutput != (bakeTileValues.Length > 0) || hasDnmOutput && dnmOutputs!.Count != 1)
            {
                throw new ArgumentException("Supply one --dnm-output and one or more --bake-tile options together.");
            }

            List<AdtCoordinate> expectedAdtCoordinates = expectedAdts
                .Select(value => ParsePair(value, "--expect-adt"))
                .Select(static pair => new AdtCoordinate(pair.X, pair.Y))
                .ToList();
            GeometryAggregate aggregate = ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
                map, geometryInputs, expectedAdtCoordinates, radius, climb, padding, slope, minWorldZ));
            ForeverGeometryAggregator.WriteDirectory(output, aggregate);

            BakeVerification? bakeVerification = null;
            if (hasDnmOutput)
            {
                List<(int X, int Z)> requestedTiles = bakeTileValues
                    .Select(value => ParsePair(value, "--bake-tile"))
                    .ToList();
                bakeVerification = ForeverGeometryBakeVerifier.BakeAndVerify(
                    aggregate, requestedTiles, dnmOutputs![0]);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "PASS",
                map_id = map,
                source_adts = aggregate.Sources.Select(static source => source.AdtXY).ToArray(),
                source_count = aggregate.Sources.Count,
                source_geometry_complete_for_explicit_set = true,
                client_product = aggregate.ClientProduct,
                client_build = aggregate.ClientBuild,
                geometry_output = Path.GetFullPath(output),
                geometry_tile_count = aggregate.Tiles.Count,
                source_terrain_triangles = aggregate.SourceTerrainTriangles,
                source_collision_triangles = aggregate.SourceCollisionTriangles,
                source_liquid_triangles = aggregate.SourceLiquidTriangles,
                aggregate_terrain_triangles = aggregate.Tiles.Sum(static tile => tile.TileCounts.TerrainTriangles),
                aggregate_collision_triangles = aggregate.Tiles.Sum(static tile => tile.TileCounts.CollisionTriangles),
                aggregate_liquid_triangles = aggregate.Tiles.Sum(static tile => tile.TileCounts.LiquidTriangles),
                duplicate_triangles_removed = aggregate.DuplicateTrianglesRemoved,
                baked = bakeVerification,
            }, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ForeverGeometryTool: {ex.Message}");
            return 2;
        }
    }

    private static Dictionary<string, List<string>> ParseArgs(string[] args)
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "--input", "--output", "--map", "--adt", "--expect-adt", "--dnm-output", "--bake-tile",
            "--agent-radius", "--agent-climb", "--walkable-slope", "--padding-yards", "--min-world-z",
        };
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (!allowed.Contains(name) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown or incomplete option '{name}'.");
            }

            if (!result.TryGetValue(name, out List<string>? entries))
            {
                entries = [];
                result.Add(name, entries);
            }
            else if (!RepeatableOptions.Contains(name))
            {
                throw new ArgumentException($"Option '{name}' was specified more than once.");
            }

            entries.Add(args[++i]);
        }

        return result;
    }

    private static string RequiredSingle(Dictionary<string, List<string>> values, string name)
    {
        string[] entries = RequiredMany(values, name);
        if (entries.Length != 1)
        {
            throw new ArgumentException($"Option '{name}' must be specified exactly once.");
        }

        return entries[0];
    }

    private static string[] RequiredMany(Dictionary<string, List<string>> values, string name) =>
        values.TryGetValue(name, out List<string>? entries) && entries.Count > 0
            ? [.. entries]
            : throw new ArgumentException($"Required option '{name}' is missing.");

    private static string[] OptionalMany(Dictionary<string, List<string>> values, string name) =>
        values.TryGetValue(name, out List<string>? entries) ? [.. entries] : [];

    private static float OptionalFloat(Dictionary<string, List<string>> values, string name, float fallback)
    {
        if (!values.TryGetValue(name, out List<string>? entries))
        {
            return fallback;
        }

        if (entries.Count != 1 || !float.TryParse(entries[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out float value))
        {
            throw new ArgumentException($"Option '{name}' must be a single invariant-culture number.");
        }

        return value;
    }

    private static float? OptionalNullableFloat(Dictionary<string, List<string>> values, string name)
    {
        return values.ContainsKey(name) ? OptionalFloat(values, name, 0f) : null;
    }

    private static (int X, int Y) ParsePair(string value, string option)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"{option} must be X,Y.");
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ForeverGeometryTool --input <rawgeo.json> --adt <X,Y> [--input <rawgeo.json> --adt <X,Y> ...] --expect-adt <X,Y> [--expect-adt ...] --output <empty directory> --map <MapID>");
        Console.WriteLine("       [--agent-radius <yd> --agent-climb <yd> --walkable-slope <degrees> --padding-yards <yd> --min-world-z <yd>]");
        Console.WriteLine("       [--dnm-output <empty directory> --bake-tile <DetourX,DetourZ> [--bake-tile ...]]");
        Console.WriteLine("Every input and source ADT is explicit. All selected source geometry is aggregated before each selected Detour tile is baked once.");
    }
}
