using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Training;

public readonly record struct TrainingPoint(float X, float Y, float Z)
{
    public static TrainingPoint From(Vector3 value) => new(value.X, value.Y, value.Z);
}

public sealed record FrameReference(string FrameId, string ImagePath,
    DateTimeOffset Timestamp);

public sealed record ScreenDetectedEntity(int X, int Y, int Width, int Height,
    int CenterX, int CenterY, int ClickX, int ClickY,
    bool InTargetRegion, bool InAddRegion);

public sealed record ScreenObservedEntities(int NpcCount, int TargetCount, int AddCount,
    string DetectionType, IReadOnlyList<ScreenDetectedEntity> Npcs);

public sealed record PlayerState(TrainingPoint MapPosition, float Heading, float RunSpeed,
    bool Moving, bool Dead, bool InCombat, bool Casting, int CastingSpellId,
    bool Mounted, bool Swimming, bool Falling, int Health, int MaxHealth,
    int Mana, int MaxMana, int Power, int MaxPower, int Level, int Experience,
    int MaxExperience, int UiMapId, int MapId, string Class, string Race,
    string ClientVersion);

public sealed record TargetState(bool HasTarget, int? Guid, int? Id, string? Name,
    int? Health, int? MaxHealth, int? Level, string? Classification,
    int? MinRange, int? MaxRange, bool? Dead, bool? Hostile,
    bool? Casting, int? CastingSpellId, string? TargetOfTarget);

public sealed record MouseOverState(bool HasMouseOver, int? Guid, int? Id,
    string? Name, int? Level, string? Classification, bool? Hostile);

public sealed record NavigationState(IReadOnlyList<TrainingPoint>? Route,
    IReadOnlyList<TrainingPoint>? PathToWaypoint, TrainingPoint? NextWaypoint,
    float? DistanceToWaypoint)
{
    public string? RouteId { get; init; }
    public string? PathId { get; init; }
}

public sealed record TrainingRouteRecord(string RouteId, string Hash, int PointCount,
    IReadOnlyList<TrainingPoint> Points, DateTimeOffset FirstSeenTimestamp);

public sealed record TrainingPathRecord(string PathId, string Hash, int PointCount,
    IReadOnlyList<TrainingPoint> Points, DateTimeOffset FirstSeenTimestamp);

public sealed record TrainingShardInfo(string File, string Type, long? StartSequence,
    long? EndSequence, DateTimeOffset? StartTime, DateTimeOffset? EndTime,
    long RecordCount, long SizeBytes);

public sealed class TrainingSessionIndex
{
    public int SchemaVersion { get; init; } = 1;
    public List<TrainingShardInfo> Files { get; init; } = [];
}

public sealed record MovementInputState(bool Forward, bool Backward,
    bool TurnLeft, bool TurnRight, bool StrafeLeft, bool StrafeRight,
    bool Jump);

public sealed record CombatState(bool InCombat, bool PlayerOrPetCombat,
    bool AutoAttack, int Gcd,
    int ComboPoints, int? TargetHealth, int? TargetMaxHealth,
    int PlayerCastingSpellId, int TargetCastingSpellId, int RemainingCastMs,
    int PlayerBuffCount, int PlayerDebuffCount, int TargetBuffCount,
    int TargetDebuffCount, int DamageTakenCount, int DamageDoneCount,
    int PendingPullCount);

public sealed record BotInternalState(string? CurrentGoal, int WorldState,
    int LootableCorpseCount, int LastCombatKillCount, bool Gathering,
    bool StuckRecovering, int StuckAttemptCount, double? StuckDurationMs,
    bool DestinationUnreachable);

public sealed record GameStateSnapshot(DateTimeOffset Timestamp, long Sequence,
    PlayerState Player, TargetState Target, MouseOverState MouseOver,
    ScreenObservedEntities ScreenObservedEntities, NavigationState Navigation,
    MovementInputState MovementInput, CombatState Combat, BotInternalState Bot)
{
    public string? SessionId { get; init; }
    public FrameReference? Frame { get; init; }
    public int[] RawAddonCells { get; init; } = [];
}

public sealed record BotAction(string Type, string? Name = null, string? Key = null);
public sealed record ExecutedAction(string DeviceType, string Operation,
    string? Key, int? KeyCode, string? Modifier, int? X, int? Y, int? DurationMs,
    DateTimeOffset Timestamp);

public sealed record TrainingEvent(string SessionId, string EpisodeId, long Sequence,
    DateTimeOffset Timestamp, string Type, string? Detail = null);

public sealed record TrainingOutcome(TrainingPoint PositionDelta, float HeadingDelta,
    int HealthDelta, int? TargetHealthDelta, float? WaypointDistanceDelta,
    int ScreenNpcCountDelta);

public sealed class TrainingTransition
{
    public required string SessionId { get; init; }
    public required string EpisodeId { get; init; }
    public long Sequence { get; set; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string BehaviorContext { get; init; }
    public required GameStateSnapshot StateBefore { get; init; }
    public string? Decision { get; set; }
    public List<BotAction> RequestedActions { get; } = [];
    public List<ExecutedAction> ExecutedActions { get; } = [];
    public string ActionSource { get; set; } = "UNKNOWN";
    public int? ActionDurationMs { get; set; }
    public GameStateSnapshot? StateAfter { get; set; }
    public string? Result { get; set; }
    public TrainingOutcome? Outcome { get; set; }
    public List<long> EventReferences { get; } = [];
}

public sealed class TrainingSessionManifest
{
    public int SchemaVersion { get; init; } = 2;
    public required string SessionId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public required string BotVersion { get; init; }
    public string? GitCommit { get; init; }
    public Dictionary<string, string> Configuration { get; init; } = [];
    public string? GameVersion { get; set; }
    public string? CharacterClass { get; set; }
    public string? CharacterRace { get; set; }
    public int? MapId { get; set; }
    public int? UiMapId { get; set; }
    public bool EnableTrainingDataCollection { get; init; } = false;
    public string RecorderVersion { get; init; } = "1";
    public string[] Capabilities { get; init; } = ["addon-player", "addon-target", "mouse-over",
        "screen-npc-list", "route", "goap-goal", "keyboard", "mouse", "state-transitions"];
    public long DroppedRecords { get; set; }
}
