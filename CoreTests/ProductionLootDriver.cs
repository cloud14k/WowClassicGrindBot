using Core;
using Core.GOAP;
using Core.Goals;

using Microsoft.Extensions.DependencyInjection;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Runs the production corpse-consumption/loot bookkeeping immediately after a
/// live combat driver confirms the target death. This is intentionally not a
/// suite or command: the combat context owns the corpse location and target GUID.
/// </summary>
internal static class ProductionLootDriver
{
    private const int LootTimeoutMs = 30_000;
    private const int UpdateIntervalMs = 50;

    public static bool RunAfterCombat(
        ProductionTestSession session,
        CombatGoal combatGoal,
        long targetGuid,
        string targetName,
        int targetId,
        int targetLevel,
        long combatDurationMs,
        bool corpseContextAlreadyPublished = false)
    {
        GoapGoal[] goals = session.Session.GetServices<GoapGoal>().ToArray();
        ConsumeCorpseGoal consumeCorpseGoal = goals.OfType<ConsumeCorpseGoal>().Single();
        LootGoal lootGoal = goals.OfType<LootGoal>().Single();
        CorpseConsumedGoal corpseConsumedGoal = goals.OfType<CorpseConsumedGoal>().Single();
        GoapAgentState state = session.Session.GetRequiredService<GoapAgentState>();
        BagReader bagReader = session.Root.GetRequiredService<BagReader>();

        Console.WriteLine("Production loot: consuming the corpse from the just-completed combat");
        Console.WriteLine("Production chain: ConsumeCorpseGoal -> LootGoal -> CorpseConsumedGoal");
        Console.WriteLine($"Corpse Target Name: {targetName}");
        Console.WriteLine($"Corpse Target ID: {targetId}");
        Console.WriteLine($"Corpse Target GUID: {targetGuid}");
        Console.WriteLine($"Corpse Target Level: {targetLevel}");
        Console.WriteLine($"Combat Duration: {combatDurationMs} ms");
        Console.WriteLine($"Kill Credit GUID: {session.CombatLog.DeadGuid.Value}");
        Console.WriteLine($"Recently Dead: {session.CombatLog.RecentlyDead.Contains((int)targetGuid)}");
        Console.WriteLine($"Player Position: {session.PlayerReader.MapPos}");
        Console.WriteLine($"Target/Corpse Position: {session.PlayerReader.TargetMapPos}");

        if (!session.Config.Loot)
        {
            Console.WriteLine("LOOT FAIL: Selected class profile has Loot disabled");
            return false;
        }

        // CombatGoal owns the last range/direction values used to estimate the
        // corpse location. Its event is the same event GoapAgent emits after
        // kill credit in the normal Grind flow. Grind-one may have published
        // it from the KillCredit callback while DeadGuid still held the mob;
        // do not publish a second corpse event in that case.
        if (!corpseContextAlreadyPublished)
            combatGoal.OnGoapEvent(new GoapStateEvent(GoapKey.producedcorpse, true));
        consumeCorpseGoal.OnEnter();

        int bagHashBefore = bagReader.HashNewOrStackGain;
        int moneyBefore = session.PlayerReader.Money.Value;
        int lootEventBefore = session.PlayerReader.LootEvent.Value;
        Console.WriteLine($"Loot state before: event={lootEventBefore} bags={bagHashBefore} money={moneyBefore}");
        Console.WriteLine("Production Goal: LootGoal");

        RunLootAndObserve(
            session,
            lootGoal,
            state,
            bagReader,
            targetGuid,
            session.GoalCancellation.Token,
            out bool lootReturned,
            out Exception? lootException);

        bool productionCompleted = lootReturned &&
            state.RecentlyLooted.Contains((int)targetGuid) &&
            !session.Bits.LootFrameShown();

        Console.WriteLine($"Loot Goal returned: {lootReturned}");
        Console.WriteLine($"Loot exception: {lootException?.GetType().Name ?? "<none>"}");
        Console.WriteLine($"Loot state after: event={session.PlayerReader.LootEvent.Value} " +
            $"windowItems={session.PlayerReader.LootWindowCount.Value} " +
            $"bags={bagReader.HashNewOrStackGain} money={session.PlayerReader.Money.Value}");
        Console.WriteLine($"RecentlyLooted contains corpse GUID: {state.RecentlyLooted.Contains((int)targetGuid)}");

        if (lootReturned)
        {
            // Preserve the result before this production bookkeeping goal can
            // clear RecentlyLooted when it consumes the last corpse counter.
            corpseConsumedGoal.OnEnter();
        }

        if (!productionCompleted)
        {
            Console.WriteLine(lootException is null
                ? "LOOT FAIL: Production LootGoal did not confirm this corpse as looted"
                : $"LOOT FAIL: Production LootGoal threw {lootException.GetType().Name}: {lootException.Message}");
            return false;
        }

        Console.WriteLine("LOOT PASS");
        Console.WriteLine("Completion: production RecentlyLooted + LootWindowOpen/LootWindowClosed");
        Console.WriteLine("No-drop/auto-loot is accepted by production READY/CLOSED, bag, or money checks.");
        return true;
    }

    private static void RunLootAndObserve(
        ProductionTestSession session,
        LootGoal lootGoal,
        GoapAgentState state,
        BagReader bagReader,
        long targetGuid,
        CancellationToken token,
        out bool lootReturned,
        out Exception? lootException)
    {
        bool returned = false;
        Exception? exception = null;
        Thread lootThread = new(() =>
        {
            try
            {
                lootGoal.OnEnter();
                returned = true;
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        })
        {
            IsBackground = true,
            Name = "CoreTests.ProductionLootGoal"
        };

        using CursorClassifier cursorClassifier = new();
        cursorClassifier.Classify(out CursorType previousCursor, out _);
        Console.WriteLine($"Cursor: {previousCursor}");
        lootThread.Start();

        Stopwatch timer = Stopwatch.StartNew();
        int previousLootEvent = session.PlayerReader.LootEvent.Value;
        int previousLootItems = session.PlayerReader.LootWindowCount.Value;
        int previousBagHash = bagReader.HashNewOrStackGain;
        bool cursorLootSeen = previousCursor == CursorType.Loot;

        while (lootThread.IsAlive &&
            !token.IsCancellationRequested &&
            timer.ElapsedMilliseconds < LootTimeoutMs)
        {
            cursorClassifier.Classify(out CursorType cursor, out _);
            cursorLootSeen |= cursor == CursorType.Loot;
            if (cursor != previousCursor)
            {
                Console.WriteLine($"Cursor: {previousCursor} -> {cursor}");
                previousCursor = cursor;
            }

            int lootEvent = session.PlayerReader.LootEvent.Value;
            int lootItems = session.PlayerReader.LootWindowCount.Value;
            int bagHash = bagReader.HashNewOrStackGain;
            if (lootEvent != previousLootEvent || lootItems != previousLootItems || bagHash != previousBagHash)
            {
                Console.WriteLine($"Loot state: event={previousLootEvent}->{lootEvent} " +
                    $"windowItems={previousLootItems}->{lootItems} bags={previousBagHash}->{bagHash} " +
                    $"money={session.PlayerReader.Money.Value}");
                previousLootEvent = lootEvent;
                previousLootItems = lootItems;
                previousBagHash = bagHash;
            }

            token.WaitHandle.WaitOne(UpdateIntervalMs);
        }

        if (lootThread.IsAlive)
        {
            Console.WriteLine($"LOOT TIMEOUT: production LootGoal exceeded {LootTimeoutMs} ms");
            session.Environment.Cancellation.Cancel();
        }

        lootThread.Join(1000);
        lootReturned = returned;
        lootException = exception;
        Console.WriteLine($"Cursor loot classification observed: {cursorLootSeen}");
        Console.WriteLine($"Production RecentlyLooted contains corpse: {state.RecentlyLooted.Contains((int)targetGuid)}");
    }
}
