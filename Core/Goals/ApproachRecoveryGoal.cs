using Core.GOAP;

using Microsoft.Extensions.Logging;

using System;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

/// <summary>
/// Recovers the narrow GOAP gap where a live hostile target is in combat range
/// but not in pull range, so neither ApproachTargetGoal nor PullTargetGoal runs.
/// </summary>
public sealed class ApproachRecoveryGoal : GoapGoal
{
    private const double STALL_TIMEOUT_MS = 2500;
    private const double LAST_RETRY_GRACE_MS = ApproachThrottle.STATIONARY_TARGET_REPEAT_MS;
    private const int MAX_RETRIES = 3;

    private readonly ILogger<ApproachRecoveryGoal> logger;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly CombatLog combatLog;
    private readonly ApproachExecutor approachExecutor;
    private readonly ConfigurableInput input;

    private int trackedTargetGuid;
    private int exhaustedTargetGuid;
    private long stalledSince;
    private int retries;
    private bool stalledLogged;
    private long lastRetryAt;
    private int lastBlockedTargetGuid;
    private string? lastBlockedReason;

    public override float Cost => 6.5f;

    public ApproachRecoveryGoal(
        ILogger<ApproachRecoveryGoal> logger,
        PlayerReader playerReader,
        AddonBits bits,
        CombatLog combatLog,
        ApproachExecutor approachExecutor,
        ConfigurableInput input)
        : base(nameof(ApproachRecoveryGoal))
    {
        this.logger = logger;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;
        this.approachExecutor = approachExecutor;
        this.input = input;

        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        AddPrecondition(GoapKey.incombat, false);
        AddPrecondition(GoapKey.incombatrange, true);
        AddPrecondition(GoapKey.withinpullrange, false);
    }

    public override bool CanRun()
    {
        int targetGuid = playerReader.TargetGuid;
        bool pullRange = playerReader.WithInPullRange();

        if (exhaustedTargetGuid != 0)
        {
            if (targetGuid == exhaustedTargetGuid)
                return false;

            logger.LogInformation("[ApproachRecovery] target changed, reset exhausted={OldTargetGuid} new={NewTargetGuid}", exhaustedTargetGuid, targetGuid);
            exhaustedTargetGuid = 0;
        }

        string? blockedReason = GetStallBlockReason(targetGuid, pullRange);
        if (blockedReason != null)
        {
            if (stalledLogged && trackedTargetGuid == targetGuid && pullRange)
                logger.LogInformation("[ApproachRecovery] recovered PullRange=true target={TargetGuid}", trackedTargetGuid);

            if (lastBlockedTargetGuid != targetGuid || lastBlockedReason != blockedReason)
                logger.LogInformation("[ApproachRecovery] blocked target={TargetGuid} reason={Reason}", targetGuid, blockedReason);
            lastBlockedTargetGuid = targetGuid;
            lastBlockedReason = blockedReason;

            ResetTracking();
            return false;
        }

        lastBlockedTargetGuid = 0;
        lastBlockedReason = null;

        if (trackedTargetGuid != targetGuid)
        {
            if (trackedTargetGuid != 0)
                logger.LogInformation("[ApproachRecovery] target changed, reset old={OldTargetGuid} new={NewTargetGuid}", trackedTargetGuid, targetGuid);

            trackedTargetGuid = targetGuid;
            stalledSince = GetTimestamp();
            retries = 0;
            stalledLogged = false;
            lastRetryAt = 0;
            logger.LogInformation("[ApproachRecovery] tracking target={TargetGuid}", targetGuid);
            return false;
        }

        double elapsedMs = GetElapsedTime(stalledSince).TotalMilliseconds;
        if (elapsedMs < STALL_TIMEOUT_MS)
            return false;

        if (!stalledLogged)
        {
            logger.LogWarning("[ApproachRecovery] stalled target={TargetGuid} elapsed={ElapsedMs:F0}ms", targetGuid, elapsedMs);
            stalledLogged = true;
        }

        if (retries >= MAX_RETRIES)
            return true;

        return true;
    }

    public override void Update()
    {
        if (GetStallBlockReason(trackedTargetGuid, playerReader.WithInPullRange()) != null)
        {
            ResetTracking();
            return;
        }

        if (retries >= MAX_RETRIES)
        {
            if (GetElapsedTime(lastRetryAt).TotalMilliseconds < LAST_RETRY_GRACE_MS)
                return;

            logger.LogWarning("[ApproachRecovery] max retries reached target={TargetGuid}; clearing target", trackedTargetGuid);
            exhaustedTargetGuid = trackedTargetGuid;
            input.PressClearTarget();
            ResetTracking();
            return;
        }

        if (!approachExecutor.TryPress(arrived: false))
            return;

        retries++;
        lastRetryAt = GetTimestamp();
        logger.LogInformation("[ApproachRecovery] retry={Retry} target={TargetGuid}", retries, trackedTargetGuid);
    }

    private string? GetStallBlockReason(int targetGuid, bool pullRange)
    {
        if (targetGuid == 0 || !bits.Target()) return "no target";
        if (!bits.Target_NotDead()) return "target dead";
        if (!bits.Target_Hostile()) return "target not hostile";
        if (bits.Combat() || bits.Target_Combat() || combatLog.PlayerOrPetCombat()) return "combat active";
        if (!playerReader.WithInCombatRange()) return "CombatRange=false";
        if (pullRange) return "PullRange=true";
        return null;
    }

    private void ResetTracking()
    {
        trackedTargetGuid = 0;
        stalledSince = 0;
        retries = 0;
        stalledLogged = false;
        lastRetryAt = 0;
    }
}
