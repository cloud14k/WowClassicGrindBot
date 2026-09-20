using Core;
using Core.GOAP;
using Core.Goals;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Runs one or more complete production target -> pull -> combat -> loot cycles.
/// The session and reader graph are created once; RunOneGrindCycle only
/// orchestrates the production goals for the current round.
/// </summary>
internal static class Test_Grind
{
    private const int MaxCount = 100;
    private const int TargetTimeoutMs = 10_000;
    private const int PullTimeoutMs = 20_000;
    private const int CombatTimeoutMs = 60_000;
    private const int LootTimeoutMs = 30_000;
    private const int OverallTimeoutMs = 120_000;
    private const int AddonStallTimeoutMs = 2500;
    private const int UpdateIntervalMs = 50;
    private const string LastLootAction = "<production LootGoal>";

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;

        if (!TryParseCount(args, out int count, out string error))
        {
            Console.WriteLine($"GRIND INVALID: {error}");
            Console.WriteLine("Usage:");
            Console.WriteLine("  grind <count>   (count: 1-100)");
            return;
        }

        ProductionTestSession session = null;
        PullTargetGoal pullGoal = null;
        CombatGoal combatGoal = null;
        Action targetDeathHandler = null;
        Action playerDeathHandler = null;
        CycleContext activeCycle = null;

        try
        {
            Console.WriteLine($"GRIND TEST: {count} round(s)");
            Console.WriteLine("Phase 1: Initializing once");

            if (!ProductionTestSession.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out session,
                    out string setupError))
            {
                Console.WriteLine($"GRIND FAIL: Initialization - {setupError}");
                return;
            }

            Console.WriteLine("Addon refresh completed");
            Console.WriteLine("ClassConfiguration loaded");
            Console.WriteLine("ConfigurableInput ready");
            Console.WriteLine($"Profile: {session.ProfilePath}");

            pullGoal = session.Session.GetServices<GoapGoal>()
                .OfType<PullTargetGoal>()
                .Single();
            combatGoal = session.Session.GetServices<GoapGoal>()
                .OfType<CombatGoal>()
                .Single();

            if (!HasUsableCombatAction(session.Config))
            {
                Console.WriteLine(
                    "GRIND FAIL: Initialization - The selected class profile has no usable combat action");
                return;
            }

            Console.WriteLine("Production goals initialized");

            // The production CombatLog reader is shared by all rounds. The
            // callback publishes the corpse event at the instant KillCredit is
            // observed, before production naturally clears DeadGuid on leaving
            // combat. No test-side DeadGuid or combat state is written.
            targetDeathHandler = () =>
            {
                CycleContext current = activeCycle;
                if (current is null || session.CombatLog.DeadGuid.Value != current.TargetGuid)
                    return;

                current.TargetDeathObserved = true;
                if (!current.CorpseContextPublished)
                {
                    combatGoal.OnGoapEvent(new GoapStateEvent(GoapKey.producedcorpse, true));
                    current.CorpseContextPublished = true;
                }
            };
            playerDeathHandler = () =>
            {
                if (activeCycle is not null)
                    activeCycle.PlayerDeathObserved = true;
            };
            session.CombatLog.KillCredit += targetDeathHandler;
            session.CombatLog.PlayerDeath += playerDeathHandler;

            long previousTargetGuid = 0;
            for (int round = 1; round <= count; round++)
            {
                CycleContext cycle = new(round, count);
                activeCycle = cycle;
                Console.WriteLine();
                Console.WriteLine($"ROUND {round}/{count}");

                CycleResult result = RunOneGrindCycle(
                    session,
                    pullGoal,
                    combatGoal,
                    cycle,
                    previousTargetGuid);

                activeCycle = null;
                if (!result.Success)
                {
                    Console.WriteLine($"GRIND FAIL: Round {round} / {count} - {result.Reason}");
                    Environment.ExitCode = 1;
                    return;
                }

                previousTargetGuid = cycle.TargetGuid;
                Console.WriteLine("→ Round complete");
            }

            Console.WriteLine();
            Console.WriteLine($"GRIND PASS: {count} / {count}");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GRIND FAIL: Initialization/runtime - {ex}");
            Environment.ExitCode = 1;
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

    private static CycleResult RunOneGrindCycle(
        ProductionTestSession session,
        PullTargetGoal pullGoal,
        CombatGoal combatGoal,
        CycleContext cycle,
        long previousTargetGuid)
    {
        Stopwatch overall = Stopwatch.StartNew();
        CancellationToken token = session.GoalCancellation.Token;

        try
        {
            cycle.Phase = "Target";
            Console.WriteLine("→ Target");
            session.NpcNameTargeting.ChangeNpcType(NpcNames.Enemy);

            Stopwatch targetTimer = Stopwatch.StartNew();
            bool targetReady = ProductionTestSession.HasValidTarget(session.Bits);
            long lastGlobalTime = session.AddonReader.GlobalTime.Value;
            Stopwatch addonStallTimer = Stopwatch.StartNew();

            while (!targetReady &&
                !token.IsCancellationRequested &&
                targetTimer.ElapsedMilliseconds < TargetTimeoutMs &&
                overall.ElapsedMilliseconds < OverallTimeoutMs)
            {
                session.TargetFinder.Search(NpcNames.Enemy, session.Bits.Target_NotDead, token);
                targetReady = ProductionTestSession.HasValidTarget(session.Bits);
                UpdateAddonStallTimer(session, ref lastGlobalTime, addonStallTimer);

                if (!targetReady)
                    token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            if (!targetReady)
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    targetTimer.ElapsedMilliseconds >= TargetTimeoutMs
                        ? "Target acquisition timeout"
                        : "Target acquisition failed");
            }

            cycle.TargetGuid = session.PlayerReader.TargetGuid;
            if (cycle.TargetGuid == previousTargetGuid)
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    "Target reused previous round's GUID");
            }

            cycle.TargetName = session.AddonReader.TargetName;
            cycle.TargetId = session.PlayerReader.TargetId;
            cycle.TargetLevel = session.PlayerReader.TargetLevel;
            cycle.InitialTargetHealth = session.PlayerReader.TargetHealth();
            cycle.PlayerHealthBefore = session.PlayerReader.HealthCurrent();
            ProductionTestSession.PrintTargetAcquired(
                session.AddonReader,
                session.PlayerReader,
                session.Bits);
            Console.WriteLine(
                $"Target acquired: {cycle.TargetName} GUID={cycle.TargetGuid} HP={cycle.InitialTargetHealth}");

            cycle.Phase = "Pull";
            Console.WriteLine("→ Pull");
            Console.WriteLine("Pull started (production PullTargetGoal)");
            pullGoal.OnEnter();

            Stopwatch pullTimer = Stopwatch.StartNew();
            bool pullComplete = false;
            bool previousPlayerCombat = session.Bits.Combat();
            bool previousTargetCombat = session.Bits.Target_Combat();
            lastGlobalTime = session.AddonReader.GlobalTime.Value;
            addonStallTimer.Restart();

            while (!token.IsCancellationRequested &&
                pullTimer.ElapsedMilliseconds < PullTimeoutMs &&
                overall.ElapsedMilliseconds < OverallTimeoutMs)
            {
                pullGoal.Update();
                cycle.PullDurationMs = pullTimer.ElapsedMilliseconds;

                if (cycle.PlayerDeathObserved || session.Bits.Dead())
                    return FailCycle(session, cycle, overall, "Player died");

                if (HasUnexpectedTargetChange(session, cycle))
                    return FailCycle(session, cycle, overall, "Target changed unexpectedly");

                if (!session.Bits.Target() &&
                    !HasTargetDeathEvidence(session, cycle))
                {
                    return FailCycle(session, cycle, overall, "Target lost while alive");
                }

                PrintCombatActionChange(session, cycle);
                PrintCombatStateChange(
                    session.Bits.Combat(),
                    session.Bits.Target_Combat(),
                    cycle);
                UpdateAddonStallTimer(session, ref lastGlobalTime, addonStallTimer);

                pullComplete = ProductionTestSession.IsProductionPullComplete(
                    session.Bits,
                    session.CombatLog,
                    session.PlayerReader);
                if (pullComplete)
                    break;

                if (addonStallTimer.ElapsedMilliseconds >= AddonStallTimeoutMs)
                    return FailCycle(session, cycle, overall, "Addon state stopped updating");

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            cycle.PullDurationMs = pullTimer.ElapsedMilliseconds;
            if (!pullComplete)
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    overall.ElapsedMilliseconds >= OverallTimeoutMs
                        ? "Overall timeout"
                        : "Pull failed");
            }

            pullGoal.OnExit();
            Console.WriteLine("→ Pull PASS");

            cycle.Phase = "Combat";
            Console.WriteLine("→ Combat");
            combatGoal.OnEnter();

            Stopwatch combatTimer = Stopwatch.StartNew();
            cycle.CombatStarted = session.Bits.Combat() || session.Bits.Target_Combat();
            bool targetDead = HasTargetDeathEvidence(session, cycle);
            int previousTargetHealth = session.PlayerReader.TargetHealth();
            cycle.PreviousPlayerCombat = session.Bits.Combat();
            cycle.PreviousTargetCombat = session.Bits.Target_Combat();
            lastGlobalTime = session.AddonReader.GlobalTime.Value;
            addonStallTimer.Restart();

            while (!token.IsCancellationRequested &&
                combatTimer.ElapsedMilliseconds < CombatTimeoutMs &&
                overall.ElapsedMilliseconds < OverallTimeoutMs)
            {
                combatGoal.Update();
                cycle.CombatDurationMs = combatTimer.ElapsedMilliseconds;

                if (cycle.PlayerDeathObserved || session.Bits.Dead())
                    return FailCycle(session, cycle, overall, "Player died");

                PrintCombatActionChange(session, cycle);
                int currentTargetHealth = session.PlayerReader.TargetHealth();
                if (currentTargetHealth != previousTargetHealth)
                {
                    Console.WriteLine($"Target HP: {previousTargetHealth} -> {currentTargetHealth}");
                    previousTargetHealth = currentTargetHealth;
                }

                PrintCombatStateChange(
                    session.Bits.Combat(),
                    session.Bits.Target_Combat(),
                    cycle);
                UpdateAddonStallTimer(session, ref lastGlobalTime, addonStallTimer);

                targetDead = HasTargetDeathEvidence(session, cycle) ||
                    (session.PlayerReader.TargetGuid == cycle.TargetGuid &&
                        session.Bits.Target_Dead());
                if (targetDead)
                    break;

                if (HasUnexpectedTargetChange(session, cycle) || !session.Bits.Target())
                {
                    return FailCycle(
                        session,
                        cycle,
                        overall,
                        session.Bits.Target()
                            ? "Target changed unexpectedly"
                            : "Target lost while alive");
                }

                if (!cycle.CombatStarted && (session.Bits.Combat() || session.Bits.Target_Combat()))
                {
                    cycle.CombatStarted = true;
                    Console.WriteLine("Combat started");
                }

                if (addonStallTimer.ElapsedMilliseconds >= AddonStallTimeoutMs)
                    return FailCycle(session, cycle, overall, "Addon state stopped updating");

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            cycle.CombatDurationMs = combatTimer.ElapsedMilliseconds;
            cycle.FinalTargetHealth = session.PlayerReader.TargetHealth();
            if (!targetDead)
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    cycle.PlayerDeathObserved || session.Bits.Dead()
                        ? "Player died"
                        : overall.ElapsedMilliseconds >= OverallTimeoutMs
                            ? "Overall timeout"
                            : !cycle.CombatStarted
                                ? "Combat did not start"
                                : "Combat timeout");
            }

            if (!cycle.TargetDeathObserved &&
                session.CombatLog.DeadGuid.Value != cycle.TargetGuid &&
                !session.CombatLog.RecentlyDead.Contains((int)cycle.TargetGuid))
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    "Corpse context lost");
            }

            combatGoal.OnExit();
            Console.WriteLine("→ Combat PASS");
            Console.WriteLine("Target dead confirmed");

            cycle.Phase = "Loot";
            Console.WriteLine("→ Loot");
            Console.WriteLine($"Corpse context GUID={cycle.TargetGuid}");
            Stopwatch lootTimer = Stopwatch.StartNew();
            bool lootComplete = ProductionLootDriver.RunAfterCombat(
                session,
                combatGoal,
                cycle.TargetGuid,
                cycle.TargetName,
                cycle.TargetId,
                cycle.TargetLevel,
                cycle.CombatDurationMs,
                cycle.CorpseContextPublished);
            cycle.LootDurationMs = lootTimer.ElapsedMilliseconds;

            if (!lootComplete)
            {
                return FailCycle(
                    session,
                    cycle,
                    overall,
                    cycle.LootDurationMs >= LootTimeoutMs || overall.ElapsedMilliseconds >= OverallTimeoutMs
                        ? "Loot timeout"
                        : "Loot failed");
            }

            cycle.FinalTargetHealth = session.PlayerReader.TargetHealth();
            cycle.TotalDurationMs = overall.ElapsedMilliseconds;
            Console.WriteLine("→ Loot PASS");
            return new CycleResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            return FailCycle(session, cycle, overall, $"Unexpected error: {ex.Message}");
        }
    }

    private static bool TryParseCount(string[] args, out int count, out string error)
    {
        count = 0;
        error = string.Empty;
        if (args.Length != 1)
        {
            error = "expected exactly one count";
            return false;
        }

        if (!int.TryParse(args[0], out count))
        {
            error = $"'{args[0]}' is not a number";
            return false;
        }

        if (count < 1)
        {
            error = "count must be at least 1";
            return false;
        }

        if (count > MaxCount)
        {
            error = $"count must not exceed {MaxCount}";
            return false;
        }

        return true;
    }

    private static bool HasUsableCombatAction(ClassConfiguration config) =>
        config.Combat.Sequence.Any(action =>
            action.BaseAction || action.ConsoleKey != ConsoleKey.NoName) ||
        config.AutoAttack.BaseAction ||
        config.AutoAttack.ConsoleKey != ConsoleKey.NoName;

    private static void PrintCombatActionChange(
        ProductionTestSession session,
        CycleContext cycle)
    {
        string action = ProductionTestSession.ResolveClickedAction(
            session.Config,
            KeyAction.LastKeyClicked());
        if (action is not null && action != cycle.LastCombatAction)
        {
            cycle.LastCombatAction = action;
            Console.WriteLine($"Combat Action: {action}");
        }
    }

    private static void PrintCombatStateChange(
        bool playerCombat,
        bool targetCombat,
        CycleContext cycle)
    {
        if (playerCombat != cycle.PreviousPlayerCombat)
        {
            Console.WriteLine($"PlayerInCombat: {cycle.PreviousPlayerCombat} -> {playerCombat}");
            cycle.PreviousPlayerCombat = playerCombat;
        }

        if (targetCombat != cycle.PreviousTargetCombat)
        {
            Console.WriteLine($"TargetInCombat: {cycle.PreviousTargetCombat} -> {targetCombat}");
            cycle.PreviousTargetCombat = targetCombat;
        }
    }

    private static bool HasUnexpectedTargetChange(
        ProductionTestSession session,
        CycleContext cycle)
    {
        long currentGuid = session.PlayerReader.TargetGuid;
        if (currentGuid == 0 || currentGuid == cycle.TargetGuid || cycle.TargetDeathObserved)
            return false;

        cycle.AdditionalTargets++;
        cycle.TargetChanged = true;
        return true;
    }

    private static bool HasTargetDeathEvidence(
        ProductionTestSession session,
        CycleContext cycle) =>
        cycle.TargetDeathObserved ||
        session.CombatLog.RecentlyDead.Contains((int)cycle.TargetGuid) ||
        session.CombatLog.DeadGuid.Value == cycle.TargetGuid;

    private static void UpdateAddonStallTimer(
        ProductionTestSession session,
        ref long lastGlobalTime,
        Stopwatch addonStallTimer)
    {
        long current = session.AddonReader.GlobalTime.Value;
        if (current != lastGlobalTime)
        {
            lastGlobalTime = current;
            addonStallTimer.Restart();
        }
    }

    private static CycleResult FailCycle(
        ProductionTestSession session,
        CycleContext cycle,
        Stopwatch overall,
        string reason)
    {
        cycle.TotalDurationMs = overall.ElapsedMilliseconds;
        Console.WriteLine($"Current Phase: {cycle.Phase}");
        Console.WriteLine($"originalTargetGuid: {cycle.TargetGuid}");
        Console.WriteLine($"current Target GUID: {session.PlayerReader.TargetGuid}");
        Console.WriteLine($"DeadGuid: {session.CombatLog.DeadGuid.Value}");
        Console.WriteLine($"Target HP: {session.PlayerReader.TargetHealth()}");
        Console.WriteLine($"Player HP: {session.PlayerReader.HealthCurrent()}");
        Console.WriteLine($"TargetDead: {session.Bits.Target_Dead()}");
        Console.WriteLine($"PlayerInCombat: {session.Bits.Combat()}");
        Console.WriteLine($"TargetInCombat: {session.Bits.Target_Combat()}");
        Console.WriteLine($"Last Combat Action: {cycle.LastCombatAction ?? "<none>"}");
        Console.WriteLine($"Last Loot Action: {LastLootAction}");
        Console.WriteLine($"elapsed: {cycle.TotalDurationMs} ms");
        return new CycleResult(false, reason);
    }

    private sealed class CycleContext
    {
        public CycleContext(int round, int totalRounds)
        {
            Round = round;
            TotalRounds = totalRounds;
        }

        public int Round { get; }
        public int TotalRounds { get; }
        public string Phase { get; set; } = "Target";
        public string TargetName { get; set; } = string.Empty;
        public long TargetGuid { get; set; }
        public int TargetId { get; set; }
        public int TargetLevel { get; set; }
        public int InitialTargetHealth { get; set; }
        public int FinalTargetHealth { get; set; }
        public int PlayerHealthBefore { get; set; }
        public bool TargetDeathObserved { get; set; }
        public bool CorpseContextPublished { get; set; }
        public bool PlayerDeathObserved { get; set; }
        public bool CombatStarted { get; set; }
        public bool TargetChanged { get; set; }
        public int AdditionalTargets { get; set; }
        public bool PreviousPlayerCombat { get; set; }
        public bool PreviousTargetCombat { get; set; }
        public string LastCombatAction { get; set; }
        public long PullDurationMs { get; set; }
        public long CombatDurationMs { get; set; }
        public long LootDurationMs { get; set; }
        public long TotalDurationMs { get; set; }
    }

    private readonly record struct CycleResult(bool Success, string Reason);
}
