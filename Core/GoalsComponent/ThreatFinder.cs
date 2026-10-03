using Microsoft.Extensions.Logging;

using System;
using System.Threading;

using static System.MathF;

namespace Core.Goals;

/// <summary>
/// Re-acquires whatever is currently attacking the player once the previous
/// target is gone or dead.
///
/// <para>
/// Lifted out of <see cref="CombatGoal"/> so <see cref="FindThreatGoal"/> can
/// reach it. The recovery only ever ran while CombatGoal was the active goal,
/// which requires a live target - so the exact state that needs it most
/// (in combat, target dead, still being hit) could not reach it.
/// </para>
/// </summary>
public sealed class ThreatFinder
{
    private const int FAILED_SEARCHES_BEFORE_ROUTE_FALLBACK = 2;
    private const int ROUTE_FALLBACK_TIMEOUT_MS = 120_000;

    private readonly ILogger<ThreatFinder> logger;
    private readonly ConfigurableInput input;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly CombatLog combatLog;
    private int failedSearches;
    private long routeFallbackUntil;
    private int skipWaypointPending;

    public bool RouteFallbackActive =>
        Environment.TickCount64 < Interlocked.Read(ref routeFallbackUntil);

    public bool ConsumeRouteFallbackSkip() =>
        Interlocked.Exchange(ref skipWaypointPending, 0) != 0;

    public void OnRouteWaypointReached()
    {
        failedSearches = 0;
        Interlocked.Exchange(ref routeFallbackUntil, 0);
        Interlocked.Exchange(ref skipWaypointPending, 0);
    }

    public ThreatFinder(ILogger<ThreatFinder> logger,
        ConfigurableInput input, Wait wait,
        PlayerReader playerReader, AddonBits bits,
        CombatLog combatLog)
    {
        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;
    }

    /// <param name="resetOnNewTarget">
    /// The combat key sequence whose <see cref="KeyAction.ResetOnNewTarget"/>
    /// entries are cleared once a new target is acquired, so per-target debuffs
    /// are not considered still on cooldown against a different mob.
    /// </param>
    public void FindPossibleThreats(ReadOnlySpan<KeyAction> resetOnNewTarget)
    {
        if (bits.Pet() && playerReader.PetAlive() && bits.Pet_Defensive() &&
            playerReader.PetTarget() && bits.PetTarget_Alive())
        {
            input.PressTargetPet();
            wait.Update();
            input.PressTargetOfTarget();
            wait.Update();

            if (bits.Target_Alive() && bits.Target_Hostile() &&
                playerReader.TargetGuid != playerReader.PetGuid)
            {
                ResetCooldowns(resetOnNewTarget);
                ResetSearchFallback();
                logger.LogInformation("Found new target by pet.");
                return;
            }

            logger.LogInformation("Pet target acquisition failed; falling back to Tab.");
            if (bits.Target())
            {
                input.PressClearTarget();
                wait.Update();
            }
        }

        // FindThreatGoal can own many consecutive ticks, unlike CombatGoal which
        // only reached this path on the tick its target died. Respect the key's
        // own cooldown so it cannot turn into Tab spam.
        if (input.TargetNearestTarget.OnCooldown())
        {
            wait.Update();
            return;
        }

        logger.LogInformation("Checking target in front...");
        input.PressNearestTarget();
        wait.Update();

        if (bits.Target() && !bits.Target_Dead() && bits.Target_Hostile())
        {
            // Only keep a threat to us or the pet. A nearby mob fighting
            // somebody else must not become the next combat goal's target.
            if (bits.TargetTarget_PlayerOrPet() ||
                combatLog.DamageTaken.Contains(playerReader.TargetGuid))
            {
                ResetCooldowns(resetOnNewTarget);
                ResetSearchFallback();

                logger.LogWarning("Found new target!");
                wait.Update();
                return;
            }
        }

        logger.LogWarning("Possible threats {DamageTakenCount}!", combatLog.DamageTakenCount());

        if (bits.Target())
        {
            input.PressClearTarget();
            wait.Update();
        }

        if (bits.SoftInteract_Enabled())
        {
            UnstuckDeadSoftTargetLock();
        }

        if (combatLog.PlayerOrPetCombat() && !bits.Dead() && !bits.Target_Alive())
        {
            failedSearches++;
            if (failedSearches >= FAILED_SEARCHES_BEFORE_ROUTE_FALLBACK && !RouteFallbackActive)
            {
                Interlocked.Exchange(ref routeFallbackUntil,
                    Environment.TickCount64 + ROUTE_FALLBACK_TIMEOUT_MS);
                Interlocked.Exchange(ref skipWaypointPending, 1);
                logger.LogWarning(
                    "No target after {FailedSearches} searches; yielding combat recovery and skipping to the next route waypoint.",
                    failedSearches);
            }
        }
    }

    private void ResetSearchFallback()
    {
        failedSearches = 0;
        Interlocked.Exchange(ref routeFallbackUntil, 0);
        Interlocked.Exchange(ref skipWaypointPending, 0);
    }

    private static void ResetCooldowns(ReadOnlySpan<KeyAction> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            KeyAction keyAction = span[i];
            if (keyAction.ResetOnNewTarget)
            {
                keyAction.ResetCooldown();
                keyAction.ResetCharges();
            }
        }
    }

    public void UnstuckDeadSoftTargetLock()
    {
        if (!bits.SoftInteract() ||
            !bits.SoftInteract_Dead() ||
            !bits.Auto_Attack() ||
            combatLog.LastDamageDoneTime.ElapsedMs() < playerReader.MainHandSpeedMs() * 2 ||
            combatLog.DamageTakenCount() == 0)
        {
            return;
        }

        logger.LogWarning("Turn away from dead softTarget due locking current target interaction!");

        float startDirection = playerReader.Direction;
        float totalRotation = 0f;

        ConsoleKey turnKey = Random.Shared.Next(2) == 0
            ? input.TurnLeftKey
            : input.TurnRightKey;

        input.SetKeyState(turnKey, true, false);

        while (bits.SoftInteract() && bits.SoftInteract_Dead())
        {
            wait.Update();

            float currentDirection = playerReader.Direction;
            float delta = Abs(currentDirection - startDirection);
            if (delta > PI)
            {
                delta = Tau - delta;
            }

            totalRotation = delta;

            // Safety: if we've turned nearly 360°, soft target is everywhere - strafe instead
            if (totalRotation >= Tau - 0.2f)
            {
                input.SetKeyState(turnKey, false, false);
                logger.LogWarning("Full rotation without clearing soft target - strafe!");

                KeyAction strafeAction = Random.Shared.Next(2) == 0
                    ? input.StrafeLeft
                    : input.StrafeRight;

                input.PressFixed(strafeAction.ConsoleKey, 500, default);
                wait.Update();

                return;
            }
        }

        input.SetKeyState(turnKey, false, false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Cleared dead soft target after {TurnDegrees:F0} degree turn", totalRotation * 180f / PI);
        }
    }
}
