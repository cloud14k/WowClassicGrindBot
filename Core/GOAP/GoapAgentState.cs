using System.Collections.Generic;

using System.Threading;

namespace Core.GOAP;

public sealed class GoapAgentState
{
    private int pendingLootGuid;
    public int PendingLootGuid
    {
        get => Volatile.Read(ref pendingLootGuid);
        set => Volatile.Write(ref pendingLootGuid, value);
    }

    public bool ShouldConsumeCorpse { get; set; }
    public int LootableCorpseCount { get; set; }
    public int GatherableCorpseCount { get; set; }
    public int ConsumableCorpseCount { get; set; }
    public int LastCombatKillCount { get; set; }
    public bool Gathering { get; set; }

    public HashSet<int> RecentlyLooted { get; } = [];
}
