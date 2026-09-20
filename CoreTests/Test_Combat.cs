using Core;
using Core.GOAP;
using Core.Goals;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CoreTests;

/// <summary>
/// One live production fight. This class observes the target and drives the
/// production GoapAgent; it never selects or presses combat skills itself.
/// </summary>
internal static class Test_Combat
{
    private const int TargetTimeoutMs = 10_000;
    private const int PullTimeoutMs = 20_000;
    private const int CombatTimeoutMs = 60_000;
    private const int CombatStallTimeoutMs = 15_000;
    private const int UpdateIntervalMs = 50;
    private const int AddonStallTimeoutMs = 2500;

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;

        if (args.Length != 1 || !args[0].Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("COMBAT: INVALID (Expected: combat enemy.)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  combat enemy");
            return;
        }

        ProductionTestSession? session = null;
        Action? targetDeathHandler = null;
        Action? playerDeathHandler = null;
        PullTargetGoal? pullGoal = null;
        CombatGoal? combatGoal = null;
        bool targetDeathObserved = false;
        bool playerDeathObserved = false;
        string? lastAction = null;
        long targetGuid = 0;
        string targetName = string.Empty;
        int targetId = 0;
        int targetLevel = 0;
        int targetHealthBefore = 0;
        int playerHealthBefore = 0;
        bool pullPassed = false;
        bool combatStarted = false;
        Stopwatch? combatTimer = null;

        try
        {
            Console.WriteLine("COMBAT TEST");
            Console.WriteLine("Phase 1: Initializing addon/bindings");

            if (!ProductionTestSession.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out session,
                    out string setupError))
            {
                CombatFail($"Addon state stopped updating ({setupError})");
                return;
            }

            if (!HasUsableCombatAction(session.Config))
            {
                CombatFail("No usable combat action");
                return;
            }

            Console.WriteLine($"Profile: {session.ProfilePath}");
            Console.WriteLine($"Combat Actions: {ProductionTestSession.FormatActions(session.Config.Combat.Sequence)}");

            pullGoal = session.Session.GetServices<GoapGoal>().OfType<PullTargetGoal>().Single();
            combatGoal = session.Session.GetServices<GoapGoal>().OfType<CombatGoal>().Single();

            Console.WriteLine("Phase 2: Acquiring enemy target");
            session.NpcNameTargeting.ChangeNpcType(NpcNames.Enemy);

            CancellationToken token = session.GoalCancellation.Token;
            bool targetReady = ProductionTestSession.HasValidTarget(session.Bits);
            Stopwatch targetTimer = Stopwatch.StartNew();

            while (!targetReady &&
                !token.IsCancellationRequested &&
                targetTimer.ElapsedMilliseconds < TargetTimeoutMs)
            {
                session.TargetFinder.Search(NpcNames.Enemy, session.Bits.Target_NotDead, token);
                targetReady = ProductionTestSession.HasValidTarget(session.Bits);

                if (!targetReady)
                    token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            if (!targetReady)
            {
                CombatFail("No valid enemy target", session, null, targetTimer.ElapsedMilliseconds);
                return;
            }

            targetGuid = session.PlayerReader.TargetGuid;
            targetName = session.AddonReader.TargetName;
            targetId = session.PlayerReader.TargetId;
            targetLevel = session.PlayerReader.TargetLevel;
            targetHealthBefore = session.PlayerReader.TargetHealth();
            playerHealthBefore = session.PlayerReader.HealthCurrent();
            ProductionTestSession.PrintTargetAcquired(
                session.AddonReader,
                session.PlayerReader,
                session.Bits);

            targetDeathHandler = () =>
            {
                if (session.CombatLog.DeadGuid.Value == targetGuid)
                    targetDeathObserved = true;
            };
            playerDeathHandler = () => playerDeathObserved = true;
            session.CombatLog.KillCredit += targetDeathHandler;
            session.CombatLog.PlayerDeath += playerDeathHandler;

            Console.WriteLine("Phase 3: Pulling target");
            Console.WriteLine("Pull is driven by production GoapAgent/PullTargetGoal");

            // PullTargetGoal and CombatGoal are the same production goals used by
            // GoapAgent. Drive only this target's production path so route-side
            // target acquisition cannot select or clear a second target.
            pullGoal.OnEnter();

            Stopwatch pullTimer = Stopwatch.StartNew();
            long lastGlobalTime = session.AddonReader.GlobalTime.Value;
            Stopwatch addonStallTimer = Stopwatch.StartNew();
            GoapGoal? previousGoal = null;
            CombatSnapshot? previous = null;

            while (!token.IsCancellationRequested &&
                pullTimer.ElapsedMilliseconds < PullTimeoutMs)
            {
                pullGoal.Update();

                if (playerDeathObserved || session.Bits.Dead())
                {
                    CombatFail("Player died", session, lastAction, pullTimer.ElapsedMilliseconds);
                    return;
                }

                if (!session.Bits.Target() && !HasTargetDeathEvidence(session, targetGuid, targetDeathObserved))
                {
                    CombatFail("Target lost while alive", session, lastAction, pullTimer.ElapsedMilliseconds);
                    return;
                }

                GoapGoal? currentGoal = pullGoal;
                string? clickedAction = ResolveAction(session, lastAction);
                if (clickedAction is not null && clickedAction != lastAction)
                {
                    lastAction = clickedAction;
                    Console.WriteLine($"Combat Action: {lastAction}");
                }

                if (currentGoal != previousGoal)
                {
                    if (currentGoal is not null)
                        Console.WriteLine($"Production Goal: {currentGoal.Name}");
                    previousGoal = currentGoal;
                }

                if (session.AddonReader.GlobalTime.Value != lastGlobalTime)
                {
                    lastGlobalTime = session.AddonReader.GlobalTime.Value;
                    addonStallTimer.Restart();
                }

                pullPassed = ProductionTestSession.IsProductionPullComplete(
                    session.Bits,
                    session.CombatLog,
                    session.PlayerReader);

                if (pullPassed)
                {
                    pullGoal.OnExit();
                    combatGoal.OnEnter();
                    combatStarted = true;
                    combatTimer = Stopwatch.StartNew();
                    Console.WriteLine("Phase 4: Running production combat");
                    Console.WriteLine("BEFORE COMBAT");
                    PrintBeforeCombat(session, targetHealthBefore, playerHealthBefore, lastAction, combatGoal);
                    previous = Capture(session, combatGoal, lastAction);
                    break;
                }

                if (addonStallTimer.ElapsedMilliseconds >= AddonStallTimeoutMs)
                {
                    CombatFail("Addon state stopped updating", session, lastAction, pullTimer.ElapsedMilliseconds);
                    return;
                }

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            if (!combatStarted)
            {
                if (playerDeathObserved || session.Bits.Dead())
                    CombatFail("Player died", session, lastAction, pullTimer.ElapsedMilliseconds);
                else if (!pullPassed)
                    CombatFail("Pull failed", session, lastAction, pullTimer.ElapsedMilliseconds);
                else
                    CombatFail("Combat logic did not start", session, lastAction, pullTimer.ElapsedMilliseconds);
                return;
            }

            Stopwatch progressTimer = Stopwatch.StartNew();
            long combatLastGlobalTime = session.AddonReader.GlobalTime.Value;
            Stopwatch combatAddonStallTimer = Stopwatch.StartNew();

            while (!token.IsCancellationRequested &&
                combatTimer!.ElapsedMilliseconds < CombatTimeoutMs)
            {
                combatGoal.Update();
                CombatSnapshot current = Capture(session, combatGoal, lastAction);

                if (playerDeathObserved || session.Bits.Dead())
                {
                    CombatFail("Player died", session, lastAction, combatTimer.ElapsedMilliseconds);
                    return;
                }

                string? clickedAction = ResolveAction(session, lastAction);
                if (clickedAction is not null && clickedAction != lastAction)
                {
                    lastAction = clickedAction;
                    current = current with { Action = lastAction };
                    Console.WriteLine($"Combat Action: {lastAction}");
                    progressTimer.Restart();
                }

                if (previous is not null)
                {
                    PrintChanges(previous.Value, current);
                    if (current.TargetHealth != previous.Value.TargetHealth)
                        progressTimer.Restart();
                }

                if (current.Goal != previous?.Goal)
                    progressTimer.Restart();

                if (session.AddonReader.GlobalTime.Value != combatLastGlobalTime)
                {
                    combatLastGlobalTime = session.AddonReader.GlobalTime.Value;
                    combatAddonStallTimer.Restart();
                }

                bool targetDead = HasTargetDeathEvidence(
                    session,
                    targetGuid,
                    targetDeathObserved) &&
                    (current.TargetDead || targetDeathObserved ||
                        session.CombatLog.RecentlyDead.Contains((int)targetGuid));

                if (!targetDead && current.TargetDead &&
                    session.PlayerReader.TargetGuid == targetGuid)
                    targetDead = true;

                if (targetDead)
                {
                    Console.WriteLine("Phase 5: Confirming target death");
                    Console.WriteLine("Target Dead: True");
                    bool lootPassed = ProductionLootDriver.RunAfterCombat(
                        session,
                        combatGoal,
                        targetGuid,
                        targetName,
                        targetId,
                        targetLevel,
                        combatTimer.ElapsedMilliseconds);

                    if (!lootPassed)
                    {
                        CombatFail("Loot failed after confirmed combat kill", session, lastAction, combatTimer.ElapsedMilliseconds);
                        return;
                    }

                    PrintCombatPass(
                        session,
                        targetHealthBefore,
                        current.TargetHealth,
                        playerHealthBefore,
                        targetName,
                        targetId,
                        targetGuid,
                        targetLevel,
                        combatTimer.ElapsedMilliseconds,
                        pullPassed,
                        lastAction);
                    Console.WriteLine("Combat sequence: kill -> corpse location -> production loot");
                    Environment.ExitCode = 0;
                    return;
                }

                if (!session.Bits.Target() ||
                    (session.PlayerReader.TargetGuid != targetGuid && !current.TargetDead))
                {
                    CombatFail("Target lost while alive", session, lastAction, combatTimer.ElapsedMilliseconds);
                    return;
                }

                if (combatAddonStallTimer.ElapsedMilliseconds >= AddonStallTimeoutMs)
                {
                    CombatFail("Addon state stopped updating", session, lastAction, combatTimer.ElapsedMilliseconds);
                    return;
                }

                if (progressTimer.ElapsedMilliseconds >= CombatStallTimeoutMs)
                {
                    CombatFail("Combat stalled", session, lastAction, combatTimer.ElapsedMilliseconds);
                    return;
                }

                previous = current;
                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            if (playerDeathObserved || session.Bits.Dead())
                CombatFail("Player died", session, lastAction, combatTimer!.ElapsedMilliseconds);
            else
                CombatFail("Combat timeout", session, lastAction, combatTimer!.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            CombatFail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                if (targetDeathHandler is not null)
                    session.CombatLog.KillCredit -= targetDeathHandler;
                if (playerDeathHandler is not null)
                    session.CombatLog.PlayerDeath -= playerDeathHandler;
                session.Dispose();
            }
        }
    }

    private static bool HasUsableCombatAction(ClassConfiguration config) =>
        config.Combat.Sequence.Any(action =>
            action.BaseAction || action.ConsoleKey != ConsoleKey.NoName) ||
        config.AutoAttack.BaseAction ||
        config.AutoAttack.ConsoleKey != ConsoleKey.NoName;

    private static string? ResolveAction(ProductionTestSession session, string? previous) =>
        ProductionTestSession.ResolveClickedAction(
            session.Config,
            KeyAction.LastKeyClicked());

    private static CombatSnapshot Capture(
        ProductionTestSession session,
        GoapGoal? goal,
        string? action) =>
        new(
            session.Bits.Target(),
            session.Bits.Target_NotDead(),
            session.Bits.Target_Dead(),
            session.Bits.Combat(),
            session.Bits.Target_Combat(),
            session.Bits.Auto_Attack(),
            session.PlayerReader.IsMeleeSwingingDefault(),
            session.PlayerReader.TargetHealth(),
            session.PlayerReader.HealthPercent(),
            session.PlayerReader.MinRange(),
            session.PlayerReader.MaxRange(),
            goal?.Name,
            action);

    private static void PrintBeforeCombat(
        ProductionTestSession session,
        int targetHealthBefore,
        int playerHealthBefore,
        string? action,
        GoapGoal goal)
    {
        CombatSnapshot snapshot = Capture(session, goal, action);
        Console.WriteLine($"Target Name: {session.AddonReader.TargetName}");
        Console.WriteLine($"Target GUID: {session.PlayerReader.TargetGuid}");
        Console.WriteLine($"Target HP: {targetHealthBefore}");
        Console.WriteLine($"Player HP: {playerHealthBefore} ({snapshot.PlayerHealthPercent}%)");
        Console.WriteLine($"PlayerInCombat: {snapshot.PlayerInCombat}");
        Console.WriteLine($"TargetInCombat: {snapshot.TargetInCombat}");
        Console.WriteLine($"AutoAttacking: {snapshot.AutoAttacking}");
        Console.WriteLine($"MeleeSwinging: {snapshot.MeleeSwinging}");
        Console.WriteLine($"Range: {snapshot.MinRange}-{snapshot.MaxRange}");
        Console.WriteLine($"Current Combat Action: {action ?? "<none>"}");
    }

    private static void PrintChanges(CombatSnapshot previous, CombatSnapshot current)
    {
        if (previous.TargetHealth != current.TargetHealth)
            Console.WriteLine($"Target HP: {previous.TargetHealth} -> {current.TargetHealth}");
        if (previous.PlayerHealthPercent != current.PlayerHealthPercent)
            Console.WriteLine($"Player HP: {previous.PlayerHealthPercent}% -> {current.PlayerHealthPercent}%");
        if (previous.MinRange != current.MinRange || previous.MaxRange != current.MaxRange)
            Console.WriteLine($"Range: {previous.MinRange}-{previous.MaxRange} -> {current.MinRange}-{current.MaxRange}");
        if (previous.PlayerInCombat != current.PlayerInCombat)
            Console.WriteLine($"PlayerInCombat: {previous.PlayerInCombat} -> {current.PlayerInCombat}");
        if (previous.TargetInCombat != current.TargetInCombat)
            Console.WriteLine($"TargetInCombat: {previous.TargetInCombat} -> {current.TargetInCombat}");
        if (previous.AutoAttacking != current.AutoAttacking)
            Console.WriteLine($"AutoAttacking: {previous.AutoAttacking} -> {current.AutoAttacking}");
        if (previous.MeleeSwinging != current.MeleeSwinging)
            Console.WriteLine($"MeleeSwinging: {previous.MeleeSwinging} -> {current.MeleeSwinging}");
        if (previous.TargetDead != current.TargetDead)
            Console.WriteLine($"Target Dead: {previous.TargetDead} -> {current.TargetDead}");
    }

    private static bool HasTargetDeathEvidence(
        ProductionTestSession session,
        long targetGuid,
        bool targetDeathObserved) =>
        targetDeathObserved ||
        session.CombatLog.RecentlyDead.Contains((int)targetGuid) ||
        session.CombatLog.DeadGuid.Value == targetGuid;

    private static void PrintCombatPass(
        ProductionTestSession session,
        int targetHealthBefore,
        int targetHealthAfter,
        int playerHealthBefore,
        string targetName,
        int targetId,
        long targetGuid,
        int targetLevel,
        long durationMs,
        bool pullPassed,
        string? lastAction)
    {
        Console.WriteLine("COMBAT PASS");
        Console.WriteLine($"Target Name: {targetName}");
        Console.WriteLine($"Target ID: {targetId}");
        Console.WriteLine($"Target GUID: {targetGuid}");
        Console.WriteLine($"Target Level: {targetLevel}");
        Console.WriteLine($"Target HP Before: {targetHealthBefore}");
        Console.WriteLine($"Target HP After: {targetHealthAfter}");
        Console.WriteLine($"Player HP Before: {playerHealthBefore}");
        Console.WriteLine($"Player HP After: {session.PlayerReader.HealthCurrent()}");
        Console.WriteLine("Target Dead: True");
        Console.WriteLine($"Combat Duration: {durationMs} ms");
        Console.WriteLine($"Passed Pull: {pullPassed}");
        Console.WriteLine($"Last Combat Action: {lastAction ?? "<none>"}");
    }

    private static void CombatFail(string reason) =>
        Console.WriteLine($"COMBAT FAIL: {reason}");

    private static void CombatFail(
        string reason,
        ProductionTestSession? session,
        string? action,
        long elapsedMs)
    {
        Console.WriteLine($"COMBAT FAIL: {reason}");
        if (session is null)
            return;

        Console.WriteLine($"Target(): {session.Bits.Target()}");
        Console.WriteLine($"Target GUID: {session.PlayerReader.TargetGuid}");
        Console.WriteLine($"Target HP: {session.PlayerReader.TargetHealth()}");
        Console.WriteLine($"Target Dead: {session.Bits.Target_Dead()}");
        Console.WriteLine($"Player HP: {session.PlayerReader.HealthCurrent()}");
        Console.WriteLine($"Player Dead: {session.Bits.Dead()}");
        Console.WriteLine($"PlayerInCombat: {session.Bits.Combat()}");
        Console.WriteLine($"TargetInCombat: {session.Bits.Target_Combat()}");
        Console.WriteLine($"AutoAttacking: {session.Bits.Auto_Attack()}");
        Console.WriteLine($"MeleeSwinging: {session.PlayerReader.IsMeleeSwingingDefault()}");
        Console.WriteLine($"Current Goal: {session.Agent.CurrentGoal?.Name ?? "<none>"}");
        Console.WriteLine($"Last Action: {action ?? "<none>"}");
        Console.WriteLine($"Elapsed: {elapsedMs} ms");
    }

    private readonly record struct CombatSnapshot(
        bool HasTarget,
        bool TargetAlive,
        bool TargetDead,
        bool PlayerInCombat,
        bool TargetInCombat,
        bool AutoAttacking,
        bool MeleeSwinging,
        int TargetHealth,
        int PlayerHealthPercent,
        int MinRange,
        int MaxRange,
        string? Goal,
        string? Action);
}
