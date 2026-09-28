using Core.GOAP;
using Core.Goals;
using Microsoft.Extensions.Logging;
using SharedLib.NpcFinder;
using System;
using System.Diagnostics;
using System.Threading;

namespace Core.Decision;

/// <summary>Executes the AI's Loot choice without invoking a GOAP goal.</summary>
public sealed class AiLootExecutor(
    ConfigurableInput input, AddonBits bits, PlayerReader player,
    BagReader bags, Wait wait, StopMoving stopMoving, NpcNameTargeting targeting,
    GoapAgentState state, ClassConfiguration config, ILogger<AiLootExecutor> logger)
{
    public bool CanLoot(AIObservation observation) =>
        config.Loot && observation.LootPending && state.PendingLootGuid != 0 &&
        !state.RecentlyLooted.Contains(state.PendingLootGuid);

    public bool Execute(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int corpseGuid = state.PendingLootGuid;
        if (corpseGuid == 0 || state.RecentlyLooted.Contains(corpseGuid))
            return false;

        stopMoving.Stop();
        bool reset = WaitFor(Loot.LOOTFRAME_OPEN_TIME_MS,
            () => (LootStatus)player.LootEvent.Value == LootStatus.CORPSE, token);
        int bagBaseline = bags.HashNewOrStackGain;
        int moneyBaseline = player.Money.Value;

        bool ExpectedCorpseSelected() => bits.Target() && bits.Target_Dead() &&
            player.TargetGuid == corpseGuid;

        if (!ExpectedCorpseSelected())
        {
            if (bits.Target())
            {
                input.PressClearTarget(token);
                wait.Update(token);
            }

            input.PressLastTargetAndWait(wait, bits.Target, 500, token);
            if (!ExpectedCorpseSelected())
            {
                if (bits.Target())
                {
                    input.PressClearTarget(token);
                    wait.Update(token);
                }

                targeting.ChangeNpcType(NpcNames.Corpse);
                try
                {
                    targeting.WaitForUpdate(token);
                    ReadOnlySpan<CursorType> cursors = [CursorType.Loot, CursorType.Vendor];
                    targeting.FindBy(cursors, token);
                    wait.Update(token);
                }
                finally
                {
                    targeting.ChangeNpcType(NpcNames.Enemy);
                }
            }
        }

        if (!ExpectedCorpseSelected())
        {
            logger.LogWarning("AI loot could not select the pending corpse: TargetGuid={TargetGuid}",
                corpseGuid);
            return false;
        }

        bool TransactionObserved() =>
            bags.HashNewOrStackGain != bagBaseline ||
            player.Money.Value != moneyBaseline ||
            player.LootWindowCount.Value > 0 ||
            (LootStatus)player.LootEvent.Value == LootStatus.READY ||
            (reset && (LootStatus)player.LootEvent.Value == LootStatus.CLOSED);

        if (ExpectedCorpseSelected() && !player.MinRangeZero())
        {
            input.PressApproach(token);
            if (!WaitFor(10_000, () => player.MinRangeZero() || TransactionObserved(), token))
            {
                logger.LogWarning("AI loot could not reach corpse: TargetGuid={TargetGuid}", corpseGuid);
                return false;
            }
        }

        bool observed = TransactionObserved();
        for (int attempt = 0; attempt < 2 && !observed; attempt++)
        {
            if (!ExpectedCorpseSelected())
                break;
            input.PressInteract(token);
            observed = WaitFor(Math.Max(player.DoubleNetworkLatency, Loot.LOOTFRAME_OPEN_TIME_MS),
                TransactionObserved, token);
        }
        bool closed = observed && WaitFor(
            Math.Max(player.LootWindowCount.Value, 1) *
                (player.DoubleNetworkLatency + Loot.LOOT_PER_ITEM_TIME_MS),
            () => !bits.LootFrameShown(), token);

        if (!closed || (bits.Target() && player.TargetGuid != corpseGuid))
        {
            logger.LogWarning("AI loot failed: TargetGuid={TargetGuid} Observed={Observed} Closed={Closed}",
                corpseGuid, observed, closed);
            return false;
        }

        state.RecentlyLooted.Add(corpseGuid);
        state.PendingLootGuid = 0;
        state.ConsumableCorpseCount = Math.Max(0, state.ConsumableCorpseCount - 1);
        state.LootableCorpseCount = Math.Max(0, state.LootableCorpseCount - 1);
        if (bits.Target())
        {
            input.PressClearTarget(token);
            wait.Update(token);
        }
        logger.LogInformation("AI loot completed: TargetGuid={TargetGuid}", corpseGuid);
        return true;
    }

    private bool WaitFor(int timeoutMs, Func<bool> condition, CancellationToken token)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            if (condition())
                return true;
            wait.Update(token);
        }
        return condition();
    }
}
