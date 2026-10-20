using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using DotRecast.Detour;
using DotRecast.Detour.Io;
using Game;
using ForeverGeometryTool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPather;
using PPather.Navmesh;
using SharedLib;

namespace CoreTests;

/// <summary>Read-only real regional pack checks through the production service.
/// Explicit external coordinates are mandatory; polygon centres are not route inputs.</summary>
internal static class ForeverRegionRuntimeRegression
{
    public static void Run(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("forever-region <prepared runtime directory containing appsettings/data_config/routes.json>");
        string root = Path.GetFullPath(args[0]);
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "appsettings.json")));
        JsonElement nav = config.RootElement.GetProperty("Navmesh");
        NavmeshBakeOptions bake = nav.GetProperty("Bake").Deserialize<NavmeshBakeOptions>()!;
        NavmeshQueryOptions queryOptions = nav.GetProperty("Query").Deserialize<NavmeshQueryOptions>()!;
        ForeverNavmeshRegionOptions region = queryOptions.ForeverRegion
            ?? throw new InvalidDataException("This test requires explicit regional runtime configuration.");
        WowClientBuildInfo identity = WowClientBuildInfo.ReadForeverConfigured(
            config.RootElement.GetProperty("Pathing").GetProperty("ForeverClientRoot").GetString(), null);
        JsonElement pathing = config.RootElement.GetProperty("Pathing");
        string toolDirectory = pathing.GetProperty("ForeverToolDirectory").GetString()!;
        ForeverWdtEnumerationResult wdt = ForeverWdtTileEnumerator.Enumerate(
            pathing.GetProperty("ForeverClientRoot").GetString()!, toolDirectory, identity.Product, identity.Build,
            region.MapId, "zhCN", root);
        if (region.SourceAdts.Any(pair => !wdt.Adts.Contains((pair[0], pair[1]))))
            throw new InvalidDataException("A regional source ADT is absent from the actual client's verified WDT enumeration.");
        File.WriteAllText(Path.Combine(root, "wdt-real-stdout.log"), wdt.Stdout);
        File.WriteAllBytes(Path.Combine(root, "wdt-real.tilelist"), wdt.NativeTileList);
        using JsonDocument dataConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data_config.json")));
        DataConfig data = new()
        {
            Root = dataConfig.RootElement.GetProperty("Root").GetString()!, Exp = "forever",
            ClientProduct = identity.Product, ClientBuild = identity.Build,
        };
        WorldMapAreaDB areas = new(data);
        if (!areas.TryGet(1453, out WorldMapArea city) || city.MapID != 0 || city.AreaID != 1519)
            throw new InvalidDataException("The real client metadata does not identify Stormwind UIMap 1453 / Area 1519 / Map 0.");
        using PPatherService service = new(new QueryLog(), data, areas,
            Options.Create(bake), Options.Create(queryOptions));
        service.Initialise(region.MapId);
        NavmeshPathfinder mesh = service.GetQueryNavmeshForMap(region.MapId)
            ?? throw new InvalidDataException("No certified regional DNM was loaded by the production service.");
        Route[] routes = JsonSerializer.Deserialize<Route[]>(File.ReadAllText(Path.Combine(root, "routes.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (routes.Length == 0) throw new InvalidDataException("Real route coordinates are required.");
        List<object> results = [];
        int failures = 0;
        foreach (Route route in routes)
        {
            Vector3 from = Point(route.From), to = Point(route.To);
            // Query through PPatherService, retaining this exact result before
            // starting another request. A nonempty partial path is insufficient.
            PPather.Graph.Path? path = service.DoSearchAtLocations(new(from, region.MapId), new(to, region.MapId), SearchStrategy.A_Star);
            NavmeshPathQueryResult result = service.LatestNavmeshQueryResult!;
            float startShift = path is null ? float.MaxValue : Vector3.Distance(from, path.GetFirst);
            float endShift = path is null ? float.MaxValue : Vector3.Distance(to, path.GetLast);
            bool complete = path is not null && result.Diagnostic is null && result.Stats.EndGapYd <= 0.5f &&
                startShift <= route.MaxEndpointShift && endShift <= route.MaxEndpointShift;
            int blockedSegments = path is null ? 0 : CountBlockedSegments(service.NavmeshPathfinder, path.locations);
            bool pass = route.ExpectReachable ? complete && blockedSegments == 0 : path is null && result.Diagnostic is not null;
            if (!pass) failures++;
            results.Add(new
            {
                route.Name, status = pass ? "PASS" : "FAIL", route.ExpectReachable,
                from = route.From, to = route.To, start_shift = startShift, end_shift = endShift,
                diagnostic = result.Diagnostic?.Reason.ToString(), end_gap = result.Stats.EndGapYd,
                polygon_count = result.Stats.PolyPathLength, result.Stats.Retargeted, result.Stats.RetargetDropYd,
                blocked_segments = blockedSegments,
                points = path?.locations.Select(static p => new[] { p.X, p.Y, p.Z }).ToArray() ?? [],
            });
        }
        object report = new
        {
            status = failures == 0 ? "PASS" : "FAIL", failures,
            kind = "real client DNM / production service / explicit world-coordinate routes; no game movement",
            product = identity.Product, build = identity.Build, disk_only = service.DiskOnly,
            source_adts = region.SourceAdts, target_tiles = region.Tiles,
            manifest_sha256 = region.ManifestSha256, routes = results,
            wdt = new { status = "PASS", adt_count = wdt.Adts.Count, slots = 4096,
                wdt.Arguments, wdt.ExecutableSha256, patch_sha256 = ForeverWdtTileEnumerator.ExpectedPatchSha256 },
        };
        File.WriteAllText(Path.Combine(root, "route-verification.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        ExportMap(root, data, region, bake);
        Console.WriteLine($"Forever regional routes: {routes.Length - failures}/{routes.Length} PASS; report: {root}/route-verification.json");
        if (failures > 0) throw new InvalidOperationException($"{failures} real world-coordinate routes failed; inspect the report.");
    }

    private static Vector3 Point(float[] xyz) => xyz is { Length: 3 } && xyz.All(float.IsFinite) && xyz[2] != 0
        ? new(xyz[0], xyz[1], xyz[2]) : throw new InvalidDataException("Each real route endpoint requires finite world XYZ, including a nonzero floor height.");

    internal static int CountBlockedSegments(NavmeshPathfinder mesh, List<Vector3> points)
    {
        // Load before taking the Detour read lock; never wait for its workers
        // while holding the lock they need for AddTile.
        foreach (Vector3 point in points) mesh.Tiles.EnsureTilesForSegment(point, point);
        DtNavMeshQuery query = new(mesh.Tiles.NavMesh);
        WowQueryFilter filter = new();
        Span<long> visited = stackalloc long[512];
        int blocked = 0;
        mesh.Tiles.Lock.EnterReadLock();
        try
        {
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 a = NavmeshCoords.ToRc(points[i - 1]), b = NavmeshCoords.ToRc(points[i]);
                DtStatus nearest = query.FindNearestPoly(a, new Vector3(0.2f, 1f, 0.2f), filter, out long refs, out _, out _);
                if (!nearest.Succeeded() || refs == 0) { blocked++; continue; }
                DtStatus ray = query.Raycast(refs, a, b, filter, out float t, out _, visited, out int count, visited.Length);
                if (!ray.Succeeded() || t < 0.9999f || count == 0 ||
                    !query.GetPolyHeight(visited[count - 1], b, out float z).Succeeded() || MathF.Abs(z - b.Y) > 1.6f)
                {
                    // A ray starting exactly at a shared vertex can select the
                    // exited polygon and hit at t=0. Independently walk dense
                    // sub-voxel samples through Detour instead of weakening t.
                    if (!DenseSurfaceCheck(query, filter, refs, a, b)) blocked++;
                }
            }
        }
        finally { mesh.Tiles.Lock.ExitReadLock(); }
        return blocked;
    }

    private static bool DenseSurfaceCheck(DtNavMeshQuery query, WowQueryFilter filter, long polyRef, Vector3 from, Vector3 to)
    {
        const float tolerance = 0.004f; // two world-coordinate float ULP, < 1/64 voxel
        Vector3 delta = to - from, current = from;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) /
            (NavmeshSettings.CellSize / 8)));
        Span<long> visited = stackalloc long[512];
        for (int i = 1; i <= steps; i++)
        {
            Vector3 target = Vector3.Lerp(from, to, (float)i / steps);
            DtStatus moved = query.MoveAlongSurface(polyRef, current, target, filter, out Vector3 next,
                visited, out int count, visited.Length);
            if (!moved.Succeeded() || moved.Has(DtStatus.DT_BUFFER_TOO_SMALL) || count == 0 ||
                MathF.Abs(target.X - next.X) > tolerance || MathF.Abs(target.Z - next.Z) > tolerance)
                return false;
            polyRef = visited[count - 1];
            if (!query.ClosestPointOnPoly(polyRef, next, out Vector3 onPoly, out _).Succeeded() ||
                MathF.Abs(target.X - onPoly.X) > tolerance || MathF.Abs(target.Z - onPoly.Z) > tolerance ||
                MathF.Abs(target.Y - onPoly.Y) > 1.6f)
                return false;
            current = onPoly;
        }
        return true;
    }

    private static void ExportMap(string root, DataConfig data, ForeverNavmeshRegionOptions region, NavmeshBakeOptions bake)
    {
        string hash = ForeverNavmeshBakeProfile.Create(region.Continent, data.ClientProduct, data.ClientBuild!.Value, bake).SettingsHash;
        string cache = Path.Combine(data.Navmesh, region.Continent, hash);
        List<object> polygons = [];
        foreach (string file in Directory.EnumerateFiles(cache, "*.dnm").Order(StringComparer.Ordinal))
        {
            using BinaryReader reader = new(File.OpenRead(file));
            DtMeshData tile = new DtMeshDataReader().Read(reader, NavmeshSettings.VertsPerPoly);
            for (int i = 0; i < tile.header.polyCount; i++)
            {
                DtPoly poly = tile.polys[i];
                float[][] vertices = Enumerable.Range(0, poly.vertCount).Select(j =>
                {
                    int n = poly.verts[j] * 3;
                    return new[] { tile.verts[n + 2], tile.verts[n], tile.verts[n + 1] };
                }).ToArray();
                polygons.Add(new { area = poly.GetArea(), vertices });
            }
        }
        File.WriteAllText(Path.Combine(root, "navmesh-polygons.json"), JsonSerializer.Serialize(polygons));
    }

    private sealed record Route(string Name, float[] From, float[] To, bool ExpectReachable = true, float MaxEndpointShift = 3f);

    private sealed class QueryLog : ILogger<PPatherService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Console.WriteLine(formatter(state, exception));
    }
}
