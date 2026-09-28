using System;
using System.Collections.Generic;

namespace Core.Decision;

public enum DecisionMode { Local, AI }
public enum ActionIntent { Wait, AcquireTarget, ApproachTarget, MoveAwayFromTarget, StopMovement, StartAutoShot, CastRaptorStrike, ContinueCurrentAction, Flee, Loot, ContinueRoute, CastConfiguredSkill, PetAttack, MaintainRange }

internal static class AiRangePolicy
{
    // The addon reports five-yard range buckets. Retreat to the bucket whose
    // lower bound is 20, leaving room for a target to close during the next cast.
    public const int HunterMinRange = 20;
    public const int HunterMaxRange = 30;
    public const int RetreatTimeoutMs = 6_000;
}

public sealed record AIObservation
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public int PlayerHP { get; init; }
    public int Mana { get; init; }
    public bool PlayerAlive { get; init; }
    public bool HasTarget { get; init; }
    public bool TargetAlive { get; init; }
    public bool TargetHostile { get; init; }
    public int TargetHP { get; init; }
    public float TargetDistance { get; init; }
    public int PreferredRangeMin { get; init; }
    public int PreferredRangeMax { get; init; }
    public bool TargetTargetsMe { get; init; }
    public bool AutoShotActive { get; init; }
    public int RangedSwingElapsedMs { get; init; }
    public int MainHandSwingElapsedMs { get; init; }
    public bool RaptorStrikeReady { get; init; }
    public int RaptorStrikeCooldownMs { get; init; }
    public bool AutoShotReady { get; init; }
    public int AutoShotCooldownMs { get; init; }
    public bool Moving { get; init; }
    public bool CorpseInRange { get; init; }
    public bool LootPending { get; init; }
    public float? Distance500MsAgo { get; init; }
    public float? Distance1000MsAgo { get; init; }
    public int? PlayerHP1000MsAgo { get; init; }
    public int? TargetHP1000MsAgo { get; init; }
}

public sealed record ActionResult(ActionIntent Action, bool Succeeded, long DurationMs, string? FailureReason = null);
public sealed record AIHistoryEntry(AIObservation Observation, ActionIntent? LastAction, long ActionDurationMs, bool? ActionSucceeded, string? FailureReason);
public sealed record AIConfiguredSkill(string Id, string Name, string Description);
public sealed record AIDecisionRequest(string Objective, AIObservation CurrentObservation,
    IReadOnlyList<AIHistoryEntry> RecentHistory, ActionResult? LastActionResult,
    IReadOnlyList<ActionIntent> AvailableCapabilities,
    string Question = "Choose the best next action based on the current observation, recent history, previous action result and objective.",
    IReadOnlyList<AIConfiguredSkill>? AvailableSkills = null);
public sealed record DecisionResult(ActionIntent Action, double Confidence, long LatencyMs, string? SkillId = null);

public sealed record AIServiceSnapshot(bool Connected = false, bool Running = false,
    ActionIntent? CurrentAction = null, double? Confidence = null, long? LatencyMs = null,
    string? LastError = null, DateTimeOffset? LastSuccessfulDecisionTime = null);

public sealed class AIServiceStatus
{
    private AIServiceSnapshot current = new();
    public AIServiceSnapshot Current => System.Threading.Volatile.Read(ref current);
    public event Action? Changed;
    public void Set(AIServiceSnapshot value)
    {
        System.Threading.Volatile.Write(ref current, value);
        Changed?.Invoke();
    }
}
