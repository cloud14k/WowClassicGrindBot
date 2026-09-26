using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Core.Training;

/// <summary>Reads V1 inline navigation and resolves V2 session-scoped navigation references.</summary>
public sealed class TrainingSessionReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly string directory;
    private readonly Dictionary<string, TrainingRouteRecord> routes;
    private readonly Dictionary<string, TrainingPathRecord> paths;

    public int SchemaVersion { get; }

    public TrainingSessionReader(string sessionDirectory)
    {
        directory = sessionDirectory;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        SchemaVersion = manifest.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
            ? version.GetInt32() : 1;
        routes = LoadRoutes().ToDictionary(r => r.RouteId, StringComparer.Ordinal);
        paths = LoadPaths().ToDictionary(p => p.PathId, StringComparer.Ordinal);
    }

    public IReadOnlyList<TrainingRouteRecord> LoadRoutes() =>
        ReadLines<TrainingRouteRecord>("routes.jsonl");

    public IReadOnlyList<TrainingPathRecord> LoadPaths() =>
        ReadLines<TrainingPathRecord>("paths.jsonl");

    public IReadOnlyList<TrainingPoint> ResolveRoute(string? routeId) =>
        routeId is null ? Array.Empty<TrainingPoint>() : routes.TryGetValue(routeId, out var route)
            ? route.Points : throw new KeyNotFoundException($"Unknown routeId '{routeId}'.");

    public IReadOnlyList<TrainingPoint> ResolvePath(string? pathId) =>
        pathId is null ? Array.Empty<TrainingPoint>() : paths.TryGetValue(pathId, out var path)
            ? path.Points : throw new KeyNotFoundException($"Unknown pathId '{pathId}'.");

    public NavigationState ResolveNavigation(NavigationState navigation)
    {
        IReadOnlyList<TrainingPoint> route = navigation.Route ?? ResolveRoute(navigation.RouteId);
        IReadOnlyList<TrainingPoint> path = navigation.PathToWaypoint ?? ResolvePath(navigation.PathId);
        return navigation with { Route = route, PathToWaypoint = path };
    }

    public IEnumerable<GameStateSnapshot> ReadStates()
    {
        IEnumerable<string> files = SchemaVersion >= 2
            ? Directory.EnumerateFiles(directory, "states_*.jsonl").OrderBy(f => f, StringComparer.Ordinal)
            : ExistingLegacyStateFiles();
        foreach (string file in files)
        foreach (string line in File.ReadLines(file))
        {
            GameStateSnapshot? snapshot = JsonSerializer.Deserialize<GameStateSnapshot>(line, Options);
            if (snapshot is not null)
                yield return snapshot with { Navigation = ResolveNavigation(snapshot.Navigation) };
        }
    }

    private IEnumerable<string> ExistingLegacyStateFiles()
    {
        string legacy = Path.Combine(directory, "raw-states.jsonl");
        if (File.Exists(legacy)) yield return legacy;
    }

    private T[] ReadLines<T>(string name)
    {
        string file = Path.Combine(directory, name);
        if (!File.Exists(file)) return Array.Empty<T>();
        return File.ReadLines(file).Select(line => JsonSerializer.Deserialize<T>(line, Options)!)
            .Where(value => value is not null).ToArray();
    }
}
