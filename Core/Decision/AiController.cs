using Microsoft.Extensions.Logging;
using Core.GOAP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Decision;

public sealed class AiController(
    PlayerReader player, AddonBits bits, CapabilityExecutor capabilities,
    ActionValidator validator, AIServiceStatus status, GoapAgentState state,
    ILogger<AiController> logger)
{
    private readonly Queue<AIHistoryEntry> history = new();
    private ActionResult? lastResult;
    private ActionIntent? lastAction;
    private DateTimeOffset? lastActionAt;

    public AIObservation Observe()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AIObservation? half = history.LastOrDefault(x => x.Observation.Time <= now.AddMilliseconds(-500))?.Observation;
        AIObservation? second = history.LastOrDefault(x => x.Observation.Time <= now.AddMilliseconds(-1000))?.Observation;
        bool target = bits.Target();
        return new AIObservation
        {
            Time = now, PlayerHP = player.HealthPercent(), Mana = player.ManaPercent(),
            PlayerAlive = !bits.Dead(), HasTarget = target,
            TargetAlive = target && bits.Target_Alive(), TargetHostile = target && bits.Target_Hostile(),
            TargetHP = target ? player.TargetHealthPercent() : 0,
            TargetDistance = target ? (player.MinRange() + player.MaxRange()) / 2f : 0,
            PreferredRangeMin = player.Class == UnitClass.Hunter ? AiRangePolicy.HunterMinRange : 0,
            PreferredRangeMax = player.Class == UnitClass.Hunter ? AiRangePolicy.HunterMaxRange : 0,
            TargetTargetsMe = target && player.TargetsMe(), AutoShotActive = bits.AutoShot(),
            RangedSwingElapsedMs = player.AutoShot.ElapsedMs(),
            MainHandSwingElapsedMs = player.MainHandSwing.ElapsedMs(),
            AutoShotReady = capabilities.AutoShotReady, AutoShotCooldownMs = capabilities.AutoShotCooldownMs,
            RaptorStrikeReady = capabilities.RaptorStrikeReady,
            RaptorStrikeCooldownMs = capabilities.RaptorStrikeCooldownMs,
            Moving = bits.Moving(), CorpseInRange = target && bits.Target_Dead() && player.MinRangeZero(),
            LootPending = state.PendingLootGuid != 0 &&
                !state.RecentlyLooted.Contains(state.PendingLootGuid),
            Distance500MsAgo = half?.TargetDistance, Distance1000MsAgo = second?.TargetDistance,
            PlayerHP1000MsAgo = second?.PlayerHP, TargetHP1000MsAgo = second?.TargetHP
        };
    }

    public AIDecisionRequest Prepare(AIObservation observation)
    {
        AIHistoryEntry[] recent = history.ToArray();
        long actionDuration = lastActionAt is null ? 0 :
            Math.Max(0, (long)(observation.Time - lastActionAt.Value).TotalMilliseconds);
        history.Enqueue(new(observation, lastAction, actionDuration,
            lastResult?.Succeeded, lastResult?.FailureReason));
        while (history.Count > 1 && history.Peek().Observation.Time < observation.Time.AddSeconds(-3))
            history.Dequeue();
        AIDecisionRequest request = new("Find a hostile target, kill it, loot the corpse, and continue the configured grind route",
            observation,
            recent.Where(x => x.Observation.Time >= observation.Time.AddSeconds(-3)).ToArray(),
            lastResult, capabilities.Available(observation),
            Question: observation.LootPending
                ? "The last kill still needs looting. Choose Loot to locate, approach, and loot that corpse before acquiring another target."
                : observation.PreferredRangeMin > 0 && observation.HasTarget &&
                  observation.TargetAlive && observation.TargetHostile &&
                  observation.TargetDistance > 0 &&
                  observation.TargetDistance < observation.PreferredRangeMin
                ? "This hunter is too close to the target for ranged attacks. Choose MaintainRange to back away toward the preferred shooting distance."
                : "Choose the best next action based on the current observation, recent history, previous action result and objective.",
            AvailableSkills: capabilities.AvailableSkills(observation));
        logger.LogInformation(
            "DecisionMode=AI Request Target={HasTarget} Alive={TargetAlive} Hostile={TargetHostile} " +
            "TargetHP={TargetHP} Distance={TargetDistance:F1} PreferredRange={RangeMin}-{RangeMax} CorpseInRange={CorpseInRange} " +
            "LootPending={LootPending} Available={AvailableActions} Skills={Skills}",
            observation.HasTarget, observation.TargetAlive, observation.TargetHostile,
            observation.TargetHP, observation.TargetDistance,
            observation.PreferredRangeMin, observation.PreferredRangeMax, observation.CorpseInRange,
            observation.LootPending, string.Join("|", request.AvailableCapabilities),
            string.Join("|", request.AvailableSkills?.Select(skill => skill.Name) ?? []));
        return request;
    }

    // Called only under GoapAgent's control gate after the HTTP result returns.
    public void Apply(DecisionResult decision, CancellationToken token)
    {
        AIObservation current = Observe();
        if (!validator.TryValidate(decision.Action, current, decision.SkillId, out string? reason))
        {
            capabilities.Stop();
            lastAction = decision.Action;
            lastResult = new ActionResult(decision.Action, false, 0, reason);
            lastActionAt = DateTimeOffset.UtcNow;
            status.Set(new(true, true, null, decision.Confidence, decision.LatencyMs,
                reason, DateTimeOffset.UtcNow));
            logger.LogInformation("DecisionMode=AI Skipped stale action={Action} Skill={Skill} Reason={Reason}",
                decision.Action, capabilities.SkillName(decision.SkillId), reason);
            return;
        }
        if (!status.Current.Connected)
            capabilities.Stop(); // recovery boundary: release stale keys before first resumed action
        ActionResult result = capabilities.ExecuteAsync(decision.Action, token,
            decision.SkillId).GetAwaiter().GetResult();
        lastAction = decision.Action;
        lastResult = result;
        lastActionAt = DateTimeOffset.UtcNow;
        status.Set(new(true, true, decision.Action, decision.Confidence, decision.LatencyMs,
            result.FailureReason, DateTimeOffset.UtcNow));
        logger.LogInformation("DecisionMode=AI ExecutedSource=AI Action={Action} Skill={Skill} Success={Success} Confidence={Confidence} LatencyMs={LatencyMs} FailureReason={FailureReason}",
            decision.Action, capabilities.SkillName(decision.SkillId), result.Succeeded,
            decision.Confidence, decision.LatencyMs, result.FailureReason);
    }

    public void Failure(Exception ex)
    {
        capabilities.Stop();
        status.Set(status.Current with { Connected = false, Running = false, CurrentAction = null, LastError = ex.Message });
        logger.LogError(ex, "DecisionMode=AI AI service unavailable; AI control paused, retrying");
    }

    public void Stop()
    {
        capabilities.Stop();
        status.Set(status.Current with { Running = false, CurrentAction = null });
    }

    public void Waiting()
    {
        capabilities.Stop();
        status.Set(status.Current with { Connected = false, Running = false,
            CurrentAction = null, LastError = "Waiting for Laya decision" });
    }
}
