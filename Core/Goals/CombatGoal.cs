using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;

using System;
using System.Diagnostics;
using static System.MathF;

namespace Core.Goals;

public sealed class CombatGoal : GoapGoal
{
    public const int COMBAT_TIMEOUT_MS = 180_000;

    public override float Cost => 4f;

    private readonly ILogger<CombatGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private readonly ActionBarCastTimeReader castTimeReader;
    private readonly ThreatFinder threatFinder;
    private readonly bool selectedTargetOnly;

    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;
    private long combatStartedAt;
    private bool timeoutHandled;

    public CombatGoal(ILogger<CombatGoal> logger, ConfigurableInput input,
        Wait wait, PlayerReader playerReader, StopMoving stopMoving, AddonBits bits,
        ClassConfiguration classConfiguration, ClassConfiguration classConfig,
        CastingHandler castingHandler, CombatLog combatLog,
        IMountHandler mountHandler,
        ActionBarCastTimeReader castTimeReader,
        ThreatFinder threatFinder, bool selectedTargetOnly = false)
        : base(nameof(CombatGoal))
    {
        this.threatFinder = threatFinder;
        this.selectedTargetOnly = selectedTargetOnly;

        this.logger = logger;
        this.input = input;

        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;

        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.classConfig = classConfig;
        this.castTimeReader = castTimeReader;

        AddPrecondition(GoapKey.incombat, true);
        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        //AddPrecondition(GoapKey.targettargetsus, true);
        AddPrecondition(GoapKey.incombatrange, true);

        AddEffect(GoapKey.producedcorpse, true);
        AddEffect(GoapKey.targetisalive, false);
        AddEffect(GoapKey.hastarget, false);

        Keys = classConfiguration.Combat.Sequence;
    }

    // Retained for production Goal drivers that explicitly publish a corpse
    // context after driving CombatGoal outside the GoapAgent loop. In normal
    // sessions GoapAgent publishes corpse events directly from kill credit so
    // Loot-only sessions do not need CombatGoal registered as an action goal.
    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent state && state.Key == GoapKey.producedcorpse)
        {
            float distance = (lastMaxDistance + lastMinDistance) / 2f;
            SendGoapEvent(new CorpseEvent(
                GetCorpseLocation(distance),
                distance,
                playerReader.Direction,
                playerReader.MapPos,
                combatLog.DeadGuid.Value));
        }
    }

    public override void OnEnter()
    {
        combatStartedAt = Stopwatch.GetTimestamp();
        timeoutHandled = false;

        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();
    }

    public override void OnExit()
    {
        if (combatLog.DamageTakenCount() > 0 && !bits.Target())
        {
            stopMoving.Stop();
        }
    }

    public override void Update()
    {
        wait.Update();

        if (HandleCombatTimeout())
        {
            return;
        }

        if (Abs(lastDirection - playerReader.Direction) > PI / 2)
        {
            logger.LogInformation("Turning too fast!");
            stopMoving.Stop();
        }

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();
        if (bits.Drowning())
        {
            input.PressJumpAscend();
            return;
        }

        if (!selectedTargetOnly && bits.SoftInteract_Enabled())
        {
            threatFinder.UnstuckDeadSoftTargetLock();
        }

        if (classConfig.AutoPetAttack &&
            bits.Pet() &&
            bits.Target_Alive() &&
            (!playerReader.PetTarget() || playerReader.PetTargetGuid != playerReader.TargetGuid) &&
            !input.PetAttack.OnCooldown())
        {
            input.PressPetAttack();
        }

        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
        {
            KeyAction keyAction = span[i];

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
            {
                continue;
            }

            // Items / auto-repeat actions (Shoot, Auto Shot, trinkets,
            // on-use items) cannot be queued against an in-progress spell
            // cast — pressing them now triggers ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS.
            // Cast-time spells DO benefit from SQW so they are not gated here.
            if (playerReader.IsCasting() &&
                !keyAction.BaseAction &&
                castTimeReader.IsItem(keyAction))
            {
                continue;
            }

            bool interrupt() => bits.Target_Alive() && keyAction.CanBeInterrupted();

            if (castingHandler.CastIfReady(keyAction, interrupt))
            {
                break;
            }
        }

        if (!bits.Target() || (bits.Target() && bits.Target_Dead()))
        {
            logger.LogInformation("Lost target!");

            if (!selectedTargetOnly && combatLog.DamageTakenCount() > 0)
            {
                if (bits.Target() && bits.Target_Dead())
                {
                    logger.LogInformation("Clear current dead target!");
                    input.PressClearTarget();
                    wait.Update();
                }

                logger.LogWarning("Search Possible Threats!");
                stopMoving.Stop();

                threatFinder.FindPossibleThreats(Keys);
            }
            else
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
    }

    private bool HandleCombatTimeout()
    {
        if (timeoutHandled ||
            Stopwatch.GetElapsedTime(combatStartedAt).TotalMilliseconds < COMBAT_TIMEOUT_MS)
        {
            return timeoutHandled;
        }

        timeoutHandled = true;
        logger.LogWarning(
            "Combat exceeded {TimeoutMs}ms for target {TargetGuid}; clearing target to replan",
            COMBAT_TIMEOUT_MS, playerReader.TargetGuid);

        stopMoving.Stop();
        if (bits.Target())
        {
            if (playerReader.PetTarget() ||
                playerReader.IsCasting() ||
                bits.Any_AutoAttack())
            {
                input.PressStopAttack();
            }

            input.PressClearTarget();
            wait.Update();
        }

        return true;
    }

    private System.Numerics.Vector3 GetCorpseLocation(float distance)
    {
        return PointEstimator.GetMapPos(playerReader.WorldMapArea, playerReader.WorldPos, playerReader.Direction, distance);
    }

}
