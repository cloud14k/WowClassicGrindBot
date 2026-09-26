using Game;
using SharedLib;
using SixLabors.ImageSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Training;

/// <summary>Opt-in observer. Bot threads only enqueue immutable snapshots and facts.</summary>
public sealed class TrainingRecorder : IInputExecutionObserver, IDisposable
{
    private readonly TrainingCollectionSettings settings;
    private readonly string root;
    private readonly object sync = new();
    private readonly Dictionary<ConsoleKey, DateTimeOffset> heldKeys = [];
    private readonly Dictionary<string, string> routeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pathIds = new(StringComparer.Ordinal);
    private readonly long maxShardBytes;
    private readonly TimeSpan maxShardDuration;
    private BlockingCollection<(string File, object Record)>? queue;
    private Task? writerTask;
    private TrainingSessionManifest? manifest;
    private TrainingTransition? pending;
    private GameStateSnapshot? lastSnapshot;
    private string? sessionDirectory;
    private string episodeId = string.Empty;
    private string context = "IDLE";
    private string classProfile = string.Empty;
    private string botMode = string.Empty;
    private string pathProfile = string.Empty;
    private long sequence;
    private long lastRawStateTicks;
    private bool botActive;
    private volatile bool recording;
    private bool disposed;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public const long DefaultMaxShardBytes = 16L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaxShardDuration = TimeSpan.FromMinutes(10);

    public bool IsRecording => recording;
    public string? SessionDirectory { get { lock (sync) return sessionDirectory; } }

    public TrainingRecorder(TrainingCollectionSettings settings, string? root = null,
        long maxShardBytes = DefaultMaxShardBytes, TimeSpan? maxShardDuration = null)
    {
        this.settings = settings;
        this.root = root ?? Path.Combine(Environment.CurrentDirectory, "training-data");
        this.maxShardBytes = Math.Max(1, maxShardBytes);
        this.maxShardDuration = maxShardDuration ?? DefaultMaxShardDuration;
        settings.Changed += OnSettingChanged;
    }

    public void ConfigureSession(ClassConfiguration config)
    {
        lock (sync)
        {
            classProfile = config.FileName;
            botMode = config.Mode.ToString();
            pathProfile = string.IsNullOrEmpty(config.OverridePathFilename)
                ? config.PathFilename : config.OverridePathFilename;
        }
    }

    private void OnSettingChanged(bool enabled)
    {
        lock (sync)
        {
            if (enabled && botActive) StartSession();
            else if (!enabled) StopSession();
        }
    }

    public void SetBotActive(bool active)
    {
        lock (sync)
        {
            botActive = active;
            if (active && settings.EnableTrainingDataCollection) StartSession();
            else if (!active) StopSession();
        }
    }

    private void StartSession()
    {
        if (recording || disposed) return;
        string sessionId = $"session_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        string directory = Path.Combine(root, sessionId);
        Directory.CreateDirectory(directory);
        Assembly botAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? informationalVersion = botAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? revision = informationalVersion?.Split('+') is { Length: > 1 } parts
            ? parts[^1] : null;
        if (revision is not null)
        {
            if (revision.Length < 7) revision = null;
            else foreach (char digit in revision)
            {
                if (!Uri.IsHexDigit(digit)) { revision = null; break; }
            }
        }
        manifest = new TrainingSessionManifest
        {
            SessionId = sessionId,
            StartTime = DateTimeOffset.UtcNow,
            BotVersion = botAssembly.GetName().Version?.ToString() ?? "unknown",
            GitCommit = revision,
            Configuration = new Dictionary<string, string>
            {
                ["classProfile"] = classProfile,
                ["mode"] = botMode,
                ["pathProfile"] = pathProfile
            }
        };
        sessionDirectory = directory;
        episodeId = Guid.NewGuid().ToString("N");
        sequence = 0;
        routeIds.Clear();
        pathIds.Clear();
        lastSnapshot = null;
        pending = null;
        heldKeys.Clear();
        lastRawStateTicks = 0;
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOptions));
        queue = new BlockingCollection<(string, object)>(8192);
        BlockingCollection<(string File, object Record)> work = queue;
        writerTask = Task.Run(() => WriteLoop(directory, work, maxShardBytes, maxShardDuration));
        recording = true;
        AddEvent("SessionStart");
    }

    private void StopSession()
    {
        if (!recording || manifest is null || queue is null) return;
        if (pending is not null && pending.ExecutedActions.Count + pending.RequestedActions.Count > 0)
        {
            // No new reader tick means the outcome has not actually been observed.
            pending.Result = "SessionStoppedBeforeNextState";
            Enqueue("transitions", pending);
        }
        pending = null;
        AddEvent("SessionEnd");
        recording = false;
        queue.CompleteAdding();
        writerTask?.GetAwaiter().GetResult();
        manifest.EndTime = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(sessionDirectory!, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOptions));
        queue.Dispose();
        queue = null;
        writerTask = null;
        heldKeys.Clear();
        lastSnapshot = null;
    }

    private static void WriteLoop(string directory,
        BlockingCollection<(string File, object Record)> work, long maxBytes, TimeSpan maxDuration)
    {
        using RollingJsonlWriter transitions = new(directory, "transitions", maxBytes, maxDuration);
        using RollingJsonlWriter states = new(directory, "states", maxBytes, maxDuration);
        using StaticJsonlWriter events = new(directory, "events.jsonl", "events");
        using StaticJsonlWriter routes = new(directory, "routes.jsonl", "routes");
        using StaticJsonlWriter paths = new(directory, "paths.jsonl", "paths");
        TrainingSessionIndex index = new();
        DateTime nextFlush = DateTime.UtcNow.AddSeconds(1);
        while (!work.IsCompleted)
        {
            if (work.TryTake(out (string File, object Record) entry, 1000))
            {
                switch (entry.File)
                {
                    case "events": events.Write(entry.Record); break;
                    case "routes": routes.Write(entry.Record); break;
                    case "paths": paths.Write(entry.Record); break;
                    case "states": states.Write(entry.Record); break;
                    default: transitions.Write(entry.Record); break;
                }
            }
            if (DateTime.UtcNow >= nextFlush)
            {
                FlushAndIndex();
                nextFlush = DateTime.UtcNow.AddSeconds(1);
            }
        }
        FlushAndIndex();

        void FlushAndIndex()
        {
            transitions.Flush(); events.Flush(); routes.Flush(); paths.Flush(); states.Flush();
            index.Files.Clear();
            index.Files.AddRange(states.Snapshot());
            index.Files.AddRange(transitions.Snapshot());
            index.Files.Add(events.Snapshot());
            index.Files.Add(routes.Snapshot());
            index.Files.Add(paths.Snapshot());
            string temp = Path.Combine(directory, "index.json.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(index, jsonOptions));
            File.Move(temp, Path.Combine(directory, "index.json"), true);
        }
    }

    private sealed class StaticJsonlWriter : IDisposable
    {
        private readonly string file;
        private readonly string type;
        private readonly StreamWriter writer;
        private long size, count;
        private long? firstSequence, lastSequence;
        private DateTimeOffset? startTime, endTime;

        public StaticJsonlWriter(string directory, string file, string type)
        {
            this.file = file; this.type = type;
            writer = new StreamWriter(Path.Combine(directory, file), false, new UTF8Encoding(false));
        }
        public void Write(object record)
        {
            string json = JsonSerializer.Serialize(record, jsonOptions);
            writer.WriteLine(json); size += Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine); count++;
            UpdateRange(record, ref firstSequence, ref lastSequence, ref startTime, ref endTime);
        }
        public void Flush() => writer.Flush();
        public TrainingShardInfo Snapshot() => new(file, type, firstSequence, lastSequence,
            startTime, endTime, count, size);
        public void Dispose() => writer.Dispose();
    }

    private sealed class RollingJsonlWriter : IDisposable
    {
        private readonly string directory, type;
        private readonly long maxBytes;
        private readonly TimeSpan maxDuration;
        private readonly List<TrainingShardInfo> closed = [];
        private StreamWriter? writer;
        private string? file;
        private int shardNumber;
        private long size, count;
        private long? firstSequence, lastSequence;
        private DateTimeOffset? startTime, endTime;
        private DateTimeOffset openedAt;

        public RollingJsonlWriter(string directory, string type, long maxBytes, TimeSpan maxDuration)
        {
            this.directory = directory; this.type = type; this.maxBytes = maxBytes;
            this.maxDuration = maxDuration;
        }
        public void Write(object record)
        {
            string json = JsonSerializer.Serialize(record, jsonOptions);
            long lineBytes = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            DateTimeOffset timestamp = RecordTime(record);
            if (writer is not null && count > 0 &&
                (size + lineBytes > maxBytes || timestamp - openedAt >= maxDuration)) CloseShard();
            if (writer is null) OpenShard(timestamp);
            writer!.WriteLine(json); size += lineBytes; count++;
            UpdateRange(record, ref firstSequence, ref lastSequence, ref startTime, ref endTime);
        }
        public void Flush() => writer?.Flush();
        public List<TrainingShardInfo> Snapshot()
        {
            List<TrainingShardInfo> result = [.. closed];
            if (writer is not null) result.Add(Current());
            return result;
        }
        private void OpenShard(DateTimeOffset timestamp)
        {
            shardNumber++;
            file = $"{type}_{shardNumber:000000}.jsonl";
            writer = new StreamWriter(Path.Combine(directory, file), false, new UTF8Encoding(false));
            openedAt = timestamp; size = count = 0;
            firstSequence = lastSequence = null; startTime = endTime = null;
        }
        private void CloseShard()
        {
            writer!.Flush(); writer.Dispose();
            closed.Add(Current());
            writer = null; file = null;
        }
        private TrainingShardInfo Current() => new(file!, type, firstSequence, lastSequence,
            startTime, endTime, count, size);
        public void Dispose() { if (writer is not null) CloseShard(); }
    }

    private static DateTimeOffset RecordTime(object record) => record switch
    {
        GameStateSnapshot s => s.Timestamp,
        TrainingTransition t => t.Timestamp,
        TrainingEvent e => e.Timestamp,
        TrainingRouteRecord r => r.FirstSeenTimestamp,
        TrainingPathRecord p => p.FirstSeenTimestamp,
        _ => DateTimeOffset.UtcNow
    };

    private static void UpdateRange(object record, ref long? firstSequence, ref long? lastSequence,
        ref DateTimeOffset? startTime, ref DateTimeOffset? endTime)
    {
        long? sequenceValue = record switch
        {
            GameStateSnapshot s => s.Sequence,
            TrainingTransition t => t.Sequence,
            TrainingEvent e => e.Sequence,
            _ => null
        };
        DateTimeOffset time = RecordTime(record);
        if (sequenceValue is long seq) { firstSequence ??= seq; lastSequence = seq; }
        startTime ??= time; endTime = time;
    }

    private void Enqueue(string file, object record)
    {
        if (queue?.TryAdd((file, record)) != true && manifest is not null)
            manifest.DroppedRecords++;
    }

    private GameStateSnapshot PrepareStoredSnapshot(GameStateSnapshot snapshot)
    {
        NavigationState nav = snapshot.Navigation;
        string? routeId = RegisterPoints(nav.Route, routeIds, "route", "routes");
        string? pathId = RegisterPoints(nav.PathToWaypoint, pathIds, "path", "paths");
        return snapshot with { Navigation = nav with { Route = null, PathToWaypoint = null,
            RouteId = routeId, PathId = pathId } };
    }

    private string? RegisterPoints(IReadOnlyList<TrainingPoint>? points,
        Dictionary<string, string> ids, string prefix, string queueFile)
    {
        if (points is null || points.Count == 0) return null;
        string hash = HashPoints(points);
        if (ids.TryGetValue(hash, out string? existing)) return existing;
        string id = $"{prefix}_{ids.Count + 1:000000}";
        ids.Add(hash, id);
        if (prefix == "route")
            Enqueue(queueFile, new TrainingRouteRecord(id, hash, points.Count, points.ToArray(), DateTimeOffset.UtcNow));
        else
            Enqueue(queueFile, new TrainingPathRecord(id, hash, points.Count, points.ToArray(), DateTimeOffset.UtcNow));
        return id;
    }

    private static string HashPoints(IReadOnlyList<TrainingPoint> points)
    {
        StringBuilder canonical = new(points.Count * 32);
        foreach (TrainingPoint point in points)
            canonical.Append(Math.Round(point.X, 4).ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(Math.Round(point.Y, 4).ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(Math.Round(point.Z, 4).ToString("F4", CultureInfo.InvariantCulture)).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private long AddEvent(string type, string? detail = null)
    {
        if (manifest is null) return 0;
        long id = ++sequence;
        Enqueue("events", new TrainingEvent(manifest.SessionId,
            episodeId, id, DateTimeOffset.UtcNow, type, detail));
        pending?.EventReferences.Add(id);
        return id;
    }

    public void RecordEvent(string type, string? detail = null)
    {
        if (!recording) return;
        lock (sync) { if (recording) AddEvent(type, detail); }
    }

    public void ObserveTick(GameStateSnapshot snapshot, string behaviorContext)
    {
        if (!recording) return;
        lock (sync)
        {
            if (!recording || manifest is null) return;
            snapshot = snapshot with { SessionId = manifest.SessionId, Sequence = ++sequence };
            manifest.GameVersion ??= snapshot.Player.ClientVersion;
            manifest.CharacterClass ??= snapshot.Player.Class;
            manifest.CharacterRace ??= snapshot.Player.Race;
            manifest.MapId = snapshot.Player.MapId;
            manifest.UiMapId = snapshot.Player.UiMapId;
            GameStateSnapshot storedSnapshot = PrepareStoredSnapshot(snapshot);

            if (lastSnapshot is not null)
            {
                if (lastSnapshot.Target.Guid != snapshot.Target.Guid)
                {
                    AddEvent(snapshot.Target.HasTarget
                        ? lastSnapshot.Target.HasTarget ? "TargetChanged" : "TargetAcquired"
                        : "TargetLost", snapshot.Target.Guid?.ToString());
                    if (snapshot.Target.HasTarget) episodeId = Guid.NewGuid().ToString("N");
                }
                if (lastSnapshot.Combat.PlayerOrPetCombat != snapshot.Combat.PlayerOrPetCombat)
                    AddEvent(snapshot.Combat.PlayerOrPetCombat ? "CombatStart" : "CombatEnd");
                if (lastSnapshot.Player.Dead != snapshot.Player.Dead)
                    AddEvent(snapshot.Player.Dead ? "PlayerDeath" : "PlayerResurrected");
                if (lastSnapshot.ScreenObservedEntities.AddCount == 0 &&
                    snapshot.ScreenObservedEntities.AddCount > 0)
                    AddEvent("AddDetected", snapshot.ScreenObservedEntities.AddCount.ToString());
                if (lastSnapshot.ScreenObservedEntities.NpcCount == 0 &&
                    snapshot.ScreenObservedEntities.NpcCount > 0)
                    AddEvent("ScreenEntityDetected", snapshot.ScreenObservedEntities.DetectionType);
                if (lastSnapshot.ScreenObservedEntities.NpcCount < 2 &&
                    snapshot.ScreenObservedEntities.NpcCount >= 2)
                    AddEvent("MultipleScreenEntitiesDetected", snapshot.ScreenObservedEntities.DetectionType);
                if (lastSnapshot.Player.Casting != snapshot.Player.Casting)
                    AddEvent(snapshot.Player.Casting ? "CastStarted" : "CastFinished",
                        snapshot.Player.CastingSpellId.ToString());
                if (lastSnapshot.Player.Health * 100 >= lastSnapshot.Player.MaxHealth * 30 &&
                    snapshot.Player.Health * 100 < snapshot.Player.MaxHealth * 30)
                    AddEvent("HPThreshold", "Below 30 percent");
                if (lastSnapshot.Bot.CurrentGoal != snapshot.Bot.CurrentGoal)
                    AddEvent("GoalChanged", snapshot.Bot.CurrentGoal);
                if (!SamePoints(lastSnapshot.Navigation.Route, snapshot.Navigation.Route))
                    AddEvent("RouteChanged", "Map route");
            }

            if (pending is not null &&
                (context != behaviorContext ||
                 (pending.RequestedActions.Count + pending.ExecutedActions.Count > 0 &&
                  snapshot.Timestamp - pending.Timestamp >= TimeSpan.FromMilliseconds(250))))
            {
                pending.StateAfter = storedSnapshot;
                pending.ActionDurationMs = (int)Math.Max(0,
                    (snapshot.Timestamp - pending.Timestamp).TotalMilliseconds);
                pending.Result = "Observed";
                pending.Outcome = CalculateOutcome(pending.StateBefore, snapshot);
                Enqueue("transitions", pending);
                pending = null;
            }

            context = behaviorContext;
            lastSnapshot = snapshot;
            pending ??= new TrainingTransition
            {
                SessionId = manifest.SessionId,
                EpisodeId = episodeId,
                Sequence = ++sequence,
                Timestamp = snapshot.Timestamp,
                BehaviorContext = behaviorContext,
                Decision = snapshot.Bot.CurrentGoal,
                StateBefore = storedSnapshot
            };
            if (snapshot.Timestamp.UtcTicks - lastRawStateTicks >= TimeSpan.TicksPerSecond)
            {
                Enqueue("states", storedSnapshot);
                lastRawStateTicks = snapshot.Timestamp.UtcTicks;
            }
        }
    }

    public void RecordRequestedAction(BotAction action)
    {
        if (!recording) return;
        lock (sync)
        {
            if (recording && pending is not null &&
                (pending.RequestedActions.Count == 0 || pending.RequestedActions[^1] != action))
            {
                pending.RequestedActions.Add(action);
                pending.ActionSource = "RULE";
            }
        }
    }

    private static TrainingOutcome CalculateOutcome(GameStateSnapshot before,
        GameStateSnapshot after)
    {
        TrainingPoint a = before.Player.MapPosition;
        TrainingPoint b = after.Player.MapPosition;
        int? targetHealthDelta = before.Target.Guid is not null &&
            before.Target.Guid == after.Target.Guid &&
            before.Target.Health is int beforeHp && after.Target.Health is int afterHp
            ? afterHp - beforeHp : null;
        float? waypointDistanceDelta = before.Navigation.NextWaypoint is not null &&
            before.Navigation.NextWaypoint == after.Navigation.NextWaypoint &&
            before.Navigation.DistanceToWaypoint is float beforeDistance &&
            after.Navigation.DistanceToWaypoint is float afterDistance
            ? afterDistance - beforeDistance : null;
        return new(new(b.X - a.X, b.Y - a.Y, b.Z - a.Z),
            after.Player.Heading - before.Player.Heading,
            after.Player.Health - before.Player.Health,
            targetHealthDelta, waypointDistanceDelta,
            after.ScreenObservedEntities.NpcCount - before.ScreenObservedEntities.NpcCount);
    }

    private static bool SamePoints(IReadOnlyList<TrainingPoint>? left,
        IReadOnlyList<TrainingPoint>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    public void RecordExecutedAction(ExecutedAction action)
    {
        if (!recording) return;
        lock (sync)
        {
            if (recording && pending is not null)
                pending.ExecutedActions.Add(action);
        }
    }

    public void OnKeyboard(ConsoleKey key, bool down, int? durationMs = null,
        ModifierKey modifier = ModifierKey.None)
    {
        if (!recording) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (sync)
        {
            if (!recording) return;
            if (down) heldKeys[key] = now;
            else if (heldKeys.Remove(key, out DateTimeOffset start) && durationMs is null)
                durationMs = (int)Math.Max(0, (now - start).TotalMilliseconds);
            RecordExecutedAction(new("Keyboard", down ? "KeyDown" : "KeyUp",
                key.ToString(), (int)key, modifier.ToString(), null, null, durationMs, now));
        }
    }

    public void OnMouse(string operation, Point point)
    {
        if (!recording) return;
        RecordExecutedAction(new("Mouse", operation, null, null, null,
            point.X, point.Y, null, DateTimeOffset.UtcNow));
    }

    public void OnText(char character)
    {
        if (!recording) return;
        RecordExecutedAction(new("Keyboard", "Char", character.ToString(),
            character, null, null, null, null, DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            settings.Changed -= OnSettingChanged;
            StopSession();
            disposed = true;
        }
    }
}
