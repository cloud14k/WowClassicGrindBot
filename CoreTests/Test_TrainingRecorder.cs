using Core.Training;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CoreTests;

internal static class Test_TrainingRecorder
{
    public static void Run(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Usage: record validate | record status");
            return;
        }

        if (args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("record validate runs the offline session and JSONL checks. " +
                "Live collection is controlled by Settings / Training.");
            return;
        }

        if (!args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Usage: record validate | record status");

        string root = Path.Combine(Path.GetTempPath(), "wow-training-test-" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(root, "appsettings.json");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(configPath,
                "{ // keep this comment\n \"Training\": { \"EnableTrainingDataCollection\": false } }");
            TrainingCollectionSettings settings = new(false, configPath);
            using TrainingRecorder recorder = new(settings, Path.Combine(root, "training-data"));
            recorder.SetBotActive(true);
            Check(!recorder.IsRecording && !Directory.Exists(Path.Combine(root, "training-data")),
                "OFF created a session");

            settings.SetEnabled(true);
            Check(recorder.IsRecording && File.ReadAllText(configPath).Contains("true"),
                "ON failed to persist or start");
            Check(File.ReadAllText(configPath).Contains("// keep this comment"),
                "setting persistence removed JSONC comments");
            string first = recorder.SessionDirectory!;
            GameStateSnapshot before = Snapshot(1);
            GameStateSnapshot after = Snapshot(2);
            recorder.ObserveTick(before, "Navigation");
            recorder.RecordRequestedAction(new("MoveForward", Key: "W"));
            recorder.OnKeyboard(ConsoleKey.W, true);
            recorder.OnKeyboard(ConsoleKey.W, false, 50);
            recorder.RecordEvent("WaypointReached");
            recorder.ObserveTick(after, "Combat");
            settings.SetEnabled(false);
            Check(!recorder.IsRecording && File.ReadAllText(configPath).Contains("false"),
                "OFF failed to persist or stop");

            string[] transitions = Directory.GetFiles(first, "transitions_*.jsonl")
                .SelectMany(File.ReadAllLines).ToArray();
            Check(transitions.Length > 0, "transition JSONL empty");
            using JsonDocument transition = JsonDocument.Parse(transitions[0]);
            JsonElement row = transition.RootElement;
            Check(row.GetProperty("stateBefore").GetProperty("player")
                .GetProperty("mapPosition").GetProperty("x").GetSingle() == 1,
                "stateBefore lost");
            Check(row.GetProperty("stateAfter").GetProperty("player")
                .GetProperty("mapPosition").GetProperty("x").GetSingle() == 2,
                "stateAfter lost");
            Check(row.GetProperty("outcome").GetProperty("positionDelta")
                .GetProperty("x").GetSingle() == 1,
                "outcome delta lost");
            Check(row.GetProperty("requestedActions").GetArrayLength() > 0 &&
                row.GetProperty("executedActions").GetArrayLength() == 2,
                "requested/executed action lost");
            Check(row.GetProperty("stateBefore").GetProperty("screenObservedEntities")
                .GetProperty("npcs").GetArrayLength() == 1,
                "screen detection list lost");
            Check(row.GetProperty("stateBefore").GetProperty("rawAddonCells")
                .GetArrayLength() == 3, "raw addon cells lost");
            Check(row.GetProperty("stateBefore").GetProperty("movementInput")
                .GetProperty("forward").GetBoolean(), "movement input lost");
            Check(row.GetProperty("actionSource").GetString() == "RULE" &&
                row.GetProperty("eventReferences").GetArrayLength() > 0,
                "action source or event link lost");
            Check(File.ReadAllLines(Path.Combine(first, "events.jsonl"))
                .All(line => JsonDocument.Parse(line).RootElement.GetProperty("sessionId")
                    .GetString() is not null), "event JSONL invalid");
            Check(File.Exists(Path.Combine(first, "manifest.json")), "manifest missing");
            using JsonDocument manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(first, "manifest.json")));
            Check(manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
                manifest.RootElement.GetProperty("endTime").ValueKind != JsonValueKind.Null,
                "manifest schema/end missing");
            Check(Directory.GetFiles(first, "states_*.jsonl").SelectMany(File.ReadAllLines).Any(),
                "raw state sampling missing");
            ValidateDedupAndReader(root);
            ValidateSharding(root);
            ValidateTimeSharding(root);
            ValidateLegacyReader(root);

            settings.SetEnabled(true);
            string second = recorder.SessionDirectory!;
            Check(second != first, "new ON reused closed session");
            settings.SetEnabled(false);
            Console.WriteLine("PASS: OFF, ON, transition, event, JSONL, navigation dedup/restore, shards, OFF -> ON new session");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateDedupAndReader(string root)
    {
        string output = Path.Combine(root, "dedup");
        TrainingCollectionSettings settings = new(true, Path.Combine(root, "dedup-settings.json"));
        using TrainingRecorder recorder = new(settings, output);
        recorder.SetBotActive(true);
        string session = recorder.SessionDirectory!;
        TrainingPoint[] route = [new(1, 2, 3), new(4, 5, 6)];
        TrainingPoint[] path = [new(7, 8, 9), new(10, 11, 12)];
        for (int i = 0; i < 100; i++) recorder.ObserveTick(Snapshot(i) with
        {
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(i + 1),
            Navigation = new(route, path, route[1], 12)
        }, "Navigation");
        recorder.ObserveTick(Snapshot(101) with
        {
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(101),
            Navigation = new([new(1, 2, 3), new(40, 50, 60)], path, null, 5)
        }, "Navigation");
        recorder.ObserveTick(Snapshot(102) with
        {
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(102),
            Navigation = new(route, [new(70, 80, 90)], null, 4)
        }, "Combat");
        recorder.SetBotActive(false);

        TrainingSessionReader reader = new(session);
        Check(reader.LoadRoutes().Count == 2, "route content dedup/change count incorrect");
        Check(reader.LoadPaths().Count == 2, "path content dedup/change count incorrect");
        Check(reader.LoadRoutes()[0].RouteId == "route_000001" &&
            reader.LoadPaths()[0].PathId == "path_000001", "stable first IDs incorrect");
        JsonElement[] states = Directory.GetFiles(session, "states_*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Check(states.Length >= 100, "repeated states missing");
        JsonElement navigation = states[0].GetProperty("navigation");
        Check(navigation.TryGetProperty("routeId", out _) && !navigation.TryGetProperty("route", out _) &&
            !navigation.TryGetProperty("pathToWaypoint", out _), "state retained inline navigation arrays");
        NavigationState resolved = reader.ReadStates().First().Navigation;
        Check(resolved.Route!.SequenceEqual(route) && resolved.PathToWaypoint!.SequenceEqual(path),
            "route/path resolution did not reproduce original points");
        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "index.json")));
        Check(index.RootElement.GetProperty("files").EnumerateArray().Any(f =>
            f.GetProperty("type").GetString() == "states" && f.GetProperty("recordCount").GetInt64() >= 100),
            "index state metadata missing");
    }

    private static void ValidateSharding(string root)
    {
        string output = Path.Combine(root, "shards");
        TrainingCollectionSettings settings = new(true, Path.Combine(root, "shard-settings.json"));
        using TrainingRecorder recorder = new(settings, output, maxShardBytes: 1800,
            maxShardDuration: TimeSpan.FromSeconds(3));
        recorder.SetBotActive(true);
        string session = recorder.SessionDirectory!;
        DateTimeOffset start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 12; i++)
        {
            recorder.ObserveTick(Snapshot(i) with
            {
                Timestamp = start.AddSeconds(i),
                RawAddonCells = Enumerable.Range(0, 200).Select(n => n + i).ToArray()
            }, i % 2 == 0 ? "Navigation" : "Combat");
            recorder.RecordRequestedAction(new("MoveForward", Key: "W"));
        }
        recorder.SetBotActive(false);
        string[] stateFiles = Directory.GetFiles(session, "states_*.jsonl").OrderBy(f => f).ToArray();
        string[] transitionFiles = Directory.GetFiles(session, "transitions_*.jsonl").OrderBy(f => f).ToArray();
        Check(stateFiles.Length > 1 && transitionFiles.Length > 1, "size threshold did not roll both record types");
        long[] stateSeq = stateFiles.SelectMany(File.ReadAllLines).Select(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()).ToArray();
        Check(stateSeq.SequenceEqual(stateSeq.Order()) && stateSeq.Distinct().Count() == stateSeq.Length,
            "state sequence reset or reordered across shards");
        string sessionId = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "manifest.json")))
            .RootElement.GetProperty("sessionId").GetString()!;
        Check(stateFiles.SelectMany(File.ReadAllLines).All(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("sessionId").GetString() == sessionId),
            "session id changed across shards");
        Check(stateFiles.Select(Path.GetFileName).First() == "states_000001.jsonl" &&
            transitionFiles.Select(Path.GetFileName).First() == "transitions_000001.jsonl",
            "shard numbering did not start at one");
        long[] transitionSeq = transitionFiles.SelectMany(File.ReadAllLines).Select(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()).ToArray();
        Check(transitionSeq.SequenceEqual(transitionSeq.Order()) &&
            transitionSeq.Distinct().Count() == transitionSeq.Length,
            "transition sequence reset or reordered across shards");
    }

    private static void ValidateTimeSharding(string root)
    {
        string output = Path.Combine(root, "time-shards");
        TrainingCollectionSettings settings = new(true, Path.Combine(root, "time-settings.json"));
        using TrainingRecorder recorder = new(settings, output, maxShardBytes: 10_000_000,
            maxShardDuration: TimeSpan.FromSeconds(3));
        recorder.SetBotActive(true);
        string session = recorder.SessionDirectory!;
        DateTimeOffset start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8; i++)
            recorder.ObserveTick(Snapshot(i) with { Timestamp = start.AddSeconds(i) },
                i % 2 == 0 ? "Navigation" : "Combat");
        recorder.SetBotActive(false);
        Check(Directory.GetFiles(session, "states_*.jsonl").Length > 1 &&
            Directory.GetFiles(session, "transitions_*.jsonl").Length > 1,
            "duration threshold did not roll both record types");
    }

    private static void ValidateLegacyReader(string root)
    {
        string session = Path.Combine(root, "legacy-v1");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "manifest.json"), "{\"schemaVersion\":1}");
        GameStateSnapshot snapshot = Snapshot(1) with
        { Navigation = new([new(1, 2, 3)], [new(4, 5, 6)], new(1, 2, 3), 7) };
        File.WriteAllText(Path.Combine(session, "raw-states.jsonl"), JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        TrainingSessionReader reader = new(session);
        GameStateSnapshot restored = reader.ReadStates().Single();
        Check(reader.SchemaVersion == 1 && restored.Navigation.Route!.Count == 1 &&
            restored.Navigation.PathToWaypoint!.Count == 1, "V1 inline navigation no longer reads");
    }

    private static GameStateSnapshot Snapshot(float x) => new(
        DateTimeOffset.UtcNow.AddSeconds(x), 0,
        new(new(x, 2, 3), 1, 7, true, false, false, false, 0,
            false, false, false, 100, 100, 50, 50, 10, 10,
            10, 20, 100, 1, 1, "Mage", "Human", "SoM"),
        new(false, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null),
        new(false, null, null, null, null, null, null),
        new(1, 0, 0, "Enemy", [new(1, 2, 30, 10, 16, 7, 15, 12, true, false)]),
        new([], [], null, null),
        new(true, false, false, false, false, false, false),
        new(false, false, false, 0, 0, null, null, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0),
        new("Navigation", 0, 0, 0, false, false, 0, null, false))
    { RawAddonCells = [10, 20, 30] };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
