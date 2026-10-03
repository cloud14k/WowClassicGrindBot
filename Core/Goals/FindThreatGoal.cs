using Core.GOAP;

namespace Core.Goals;

/// <summary>
/// Re-acquires a target while in combat, including before the first damage event.
///
/// <para>
/// Before this goal existed nothing could be planned in that state:
/// <see cref="CombatGoal"/>, <see cref="ApproachTargetGoal"/> and
/// <see cref="PullTargetGoal"/> all need <c>targetisalive</c>;
/// <see cref="ConsumeCorpseGoal"/>, <see cref="LootGoal"/>,
/// <see cref="CorpseConsumedGoal"/>, Parallel and WrongZone can be gated on being
/// out of combat or out of danger. FollowRoute is normally gated too, but can
/// take over briefly after repeated failed target searches. The result was the long
/// standing <c>NO PLAN</c> idle while a second mob beat the character to death -
/// issues #795, #823.
/// </para>
///
/// <para>
/// With recorded incoming damage recovery has priority over corpse processing.
/// Without it recovery runs after the loot/corpse goals, allowing the normal
/// post-kill sequence to finish while the player's combat flag lingers.
/// </para>
/// </summary>
public sealed class FindThreatGoal : GoapGoal
{
    // Loses to TargetPetTargetGoal (4.01f) so the pet path keeps priority while
    // it can run; this is the fallback for no pet, a passive pet, or a pet
    // target that just died. Mutually exclusive with CombatGoal (4f) via
    // targetisalive, so the ordering between those two never matters.
    private const float THREAT_COST = 4.02f;
    private const float NO_DAMAGE_RECOVERY_COST = 4.8f;

    public override float Cost => combatLog.DamageTakenCount() > 0
        ? THREAT_COST
        : NO_DAMAGE_RECOVERY_COST;

    private readonly ThreatFinder threatFinder;
    private readonly CombatLog combatLog;

    // The same array CombatGoal drives, so a target acquired here resets the
    // per-target cooldowns exactly as it would on CombatGoal's own recovery path.
    private readonly KeyAction[] combatSequence;

    public FindThreatGoal(ThreatFinder threatFinder,
        ClassConfiguration classConfig, CombatLog combatLog)
        : base(nameof(FindThreatGoal))
    {
        this.threatFinder = threatFinder;
        this.combatSequence = classConfig.Combat.Sequence;
        this.combatLog = combatLog;

        AddPrecondition(GoapKey.incombat, true);
        AddPrecondition(GoapKey.isdead, false);
        AddPrecondition(GoapKey.targetisalive, false);

        AddEffect(GoapKey.hastarget, true);
        AddEffect(GoapKey.targetisalive, true);
    }

    public override bool CanRun() => !threatFinder.RouteFallbackActive;

    public override void Update()
    {
        threatFinder.FindPossibleThreats(combatSequence);
    }
}
