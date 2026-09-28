using Core.Goals;
using Core.GOAP;
using SharedLib.NpcFinder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Decision;

public interface IAiCapability
{
    ActionIntent Action { get; }
    bool CanExecute(AIObservation observation);
    Task<ActionResult> ExecuteAsync(CancellationToken token);
}

public sealed class ActionValidator(CapabilityExecutor executor)
{
    public bool TryValidate(ActionIntent action, AIObservation observation,
        string? skillId, out string? reason)
    {
        if (!Enum.IsDefined(action) || !executor.CanExecute(action, observation) ||
            (action == ActionIntent.CastConfiguredSkill &&
             !executor.IsSkillAvailable(skillId, observation)))
        {
            reason = $"Action {action} is unavailable in the current observation";
            return false;
        }
        reason = null;
        return true;
    }
}

// Adapter around the existing input, casting, approach and navigation executors.
// CanExecute checks only the immediate legality of an action, never its tactics.
public sealed class CapabilityExecutor(
    ConfigurableInput input, AddonBits bits,
    ClassConfiguration config, ApproachExecutor approach, CastingHandler casting,
    Navigation navigation, RouteInfo route, SafeSpotCollector safeSpots,
    StopMoving stopMoving, TargetFinder targetFinder, AiLootExecutor loot,
    PlayerReader player, DecisionSettings settings, Wait wait)
{
    private ActionIntent? heldAction;
    private readonly Dictionary<ActionIntent, IAiCapability> registered = new();
    private sealed record SkillEntry(string Id, KeyAction Key, bool RequiresHostileTarget);
    private readonly SkillEntry[] skills = BuildSkillEntries(config);
    private const NpcNames TargetTypes = NpcNames.Enemy;

    public IReadOnlyCollection<IAiCapability> Capabilities
    {
        get
        {
            if (registered.Count == 0)
                foreach (ActionIntent action in Enum.GetValues<ActionIntent>())
                    registered.Add(action, new CapabilityAdapter(this, action));
            return registered.Values;
        }
    }
    private KeyAction? AutoShot => config.Combat.Sequence.FirstOrDefault(x =>
        x.Name.Equals("Auto Shot", StringComparison.OrdinalIgnoreCase));
    private KeyAction? RaptorStrike => config.Combat.Sequence.FirstOrDefault(x =>
        x.Name.Equals("Raptor Strike", StringComparison.OrdinalIgnoreCase));

    public int AutoShotCooldownMs => AutoShot?.GetRemainingCooldown() ?? -1;
    public int RaptorStrikeCooldownMs => RaptorStrike?.GetRemainingCooldown() ?? -1;
    public bool AutoShotReady => AutoShot is { } key && key.CanRun() && !key.OnCooldown();
    public bool RaptorStrikeReady => RaptorStrike is { } key && key.CanRun() && !key.OnCooldown();

    public IReadOnlyList<ActionIntent> Available(AIObservation observation) =>
        Capabilities.Where(x => x.CanExecute(observation)).Select(x => x.Action).ToArray();

    public IReadOnlyList<AIConfiguredSkill> AvailableSkills(AIObservation observation) =>
        skills.Where(skill => SkillAvailable(skill, observation))
            .Select(skill => new AIConfiguredSkill(skill.Id, skill.Key.Name,
                $"Use the configured {skill.Key.Name} action. Requirements: " +
                string.Join(", ", skill.Key.Requirements)))
            .ToArray();

    public bool IsSkillAvailable(string? skillId, AIObservation observation) =>
        skillId is not null && skills.Any(skill => skill.Id == skillId &&
            SkillAvailable(skill, observation));

    public string? SkillName(string? skillId) =>
        skills.FirstOrDefault(skill => skill.Id == skillId)?.Key.Name;

    private static bool SkillAvailable(SkillEntry skill, AIObservation observation) =>
        observation.PlayerAlive && !observation.LootPending &&
        (!skill.RequiresHostileTarget ||
         (observation.HasTarget && observation.TargetAlive && observation.TargetHostile)) &&
        skill.Key.CanRun() && !skill.Key.OnCooldown();

    private static SkillEntry[] BuildSkillEntries(ClassConfiguration config)
    {
        List<SkillEntry> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<KeyAction> actions, bool requiresHostileTarget)
        {
            foreach (KeyAction key in actions)
            {
                if (key.ConsoleKey == default || string.IsNullOrWhiteSpace(key.Name) ||
                    key.Name.Equals("Auto Shot", StringComparison.OrdinalIgnoreCase) ||
                    key.Name.Equals("Raptor Strike", StringComparison.OrdinalIgnoreCase) ||
                    !names.Add(key.Name))
                    continue;
                result.Add(new($"Skill_{result.Count}", key, requiresHostileTarget));
            }
        }
        Add(config.Combat.Sequence, true);
        Add(config.Pull.Sequence, true);
        Add(config.Adhoc.Sequence, false);
        return result.ToArray();
    }

    public bool CanExecute(ActionIntent action, AIObservation o) =>
        Enum.IsDefined(action) && CanExecuteCore(action, o);

    private bool CanExecuteCore(ActionIntent action, AIObservation o) =>
        (o.PlayerAlive || action is ActionIntent.Wait or ActionIntent.StopMovement) && (action switch
    {
        ActionIntent.Wait => true,
        ActionIntent.StopMovement => o.Moving,
        ActionIntent.AcquireTarget => !o.LootPending && (!o.HasTarget || !o.TargetHostile),
        ActionIntent.ApproachTarget => o.HasTarget && o.TargetAlive && o.TargetHostile &&
            (o.PreferredRangeMax == 0 || o.TargetDistance > o.PreferredRangeMax),
        ActionIntent.MoveAwayFromTarget => o.HasTarget && o.TargetAlive && o.TargetHostile &&
            o.PreferredRangeMin == 0,
        ActionIntent.MaintainRange => o.HasTarget && o.TargetAlive && o.TargetHostile &&
            o.PreferredRangeMin > 0 && o.TargetDistance > 0 &&
            o.TargetDistance < o.PreferredRangeMin,
        ActionIntent.StartAutoShot => o.HasTarget && o.TargetAlive && o.TargetHostile && !o.AutoShotActive && AutoShotReady,
        ActionIntent.CastRaptorStrike => o.HasTarget && o.TargetAlive && o.TargetHostile && RaptorStrikeReady,
        ActionIntent.CastConfiguredSkill => AvailableSkills(o).Count > 0,
        ActionIntent.PetAttack => config.AutoPetAttack && bits.Pet() && o.HasTarget &&
            o.TargetAlive && o.TargetHostile && !input.PetAttack.OnCooldown() &&
            (!player.PetTarget() || player.PetTargetGuid != player.TargetGuid),
        ActionIntent.ContinueCurrentAction => heldAction is { } held && CanExecute(held, o),
        ActionIntent.Flee => settings.Current.AllowFlee && o.TargetHostile &&
            o.TargetTargetsMe && o.PlayerHP <= 20 && SafeSpotAvailable(),
        ActionIntent.Loot => loot.CanLoot(o),
        ActionIntent.ContinueRoute => route.Route.Length > 0 && !o.HasTarget && !o.LootPending,
        _ => false
    });

    public Task<ActionResult> ExecuteAsync(ActionIntent action, CancellationToken token,
        string? skillId = null) => ExecuteCoreAsync(action, token, skillId);

    private Task<ActionResult> ExecuteCoreAsync(ActionIntent action, CancellationToken token,
        string? skillId = null)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            bool success = Execute(action, token, skillId);
            return Task.FromResult(new ActionResult(action, success, timer.ElapsedMilliseconds,
                success ? null : action == ActionIntent.MaintainRange
                    ? "Retreat ended before preferred shooting range was reached"
                    : "Existing executor did not perform the action"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult(action, false, timer.ElapsedMilliseconds, ex.Message));
        }
    }

    private bool Execute(ActionIntent action, CancellationToken token, string? skillId)
    {
        if (action == ActionIntent.ContinueCurrentAction)
            action = heldAction ?? ActionIntent.Wait;

        // Interact approach keeps running after the key press. Cancel that run
        // before a new ranged action or backpedal, or it will carry us into melee.
        if (heldAction == ActionIntent.ApproachTarget && action != ActionIntent.ApproachTarget)
            stopMoving.StopForward();

        if (action is not (ActionIntent.MoveAwayFromTarget or ActionIntent.MaintainRange))
            input.StopBackward(true);
        if (action is not (ActionIntent.ContinueRoute or ActionIntent.Flee))
        {
            navigation.Stop();
            input.StopForward(true);
        }

        switch (action)
        {
            case ActionIntent.Wait:
            case ActionIntent.StopMovement:
                stopMoving.Stop();
                input.Reset();
                heldAction = null;
                return true;
            case ActionIntent.AcquireTarget:
                bool foundTarget = TryAcquireTarget(token);
                if (foundTarget)
                {
                    navigation.Stop();
                    input.StopForward(true);
                }
                heldAction = null;
                return foundTarget;
            case ActionIntent.ApproachTarget:
                heldAction = action;
                if (bits.Target_Dead())
                    return input.PressedApproachOnCooldown();
                return approach.TryPress(false);
            case ActionIntent.MoveAwayFromTarget:
                input.StartBackward(true);
                heldAction = action;
                return true;
            case ActionIntent.MaintainRange:
                heldAction = null;
                return RetreatToPreferredRange(token);
            case ActionIntent.StartAutoShot:
                heldAction = null;
                return AutoShot is { } auto && casting.CastIfReady(auto, () => bits.Target_Alive());
            case ActionIntent.CastRaptorStrike:
                heldAction = null;
                return RaptorStrike is { } raptor && casting.CastIfReady(raptor, () => bits.Target_Alive());
            case ActionIntent.CastConfiguredSkill:
                heldAction = null;
                SkillEntry? selected = skills.FirstOrDefault(skill => skill.Id == skillId);
                return selected is not null && !selected.Key.OnCooldown() &&
                    casting.CastIfReady(selected.Key,
                        () => bits.Target_Alive() && selected.Key.CanBeInterrupted());
            case ActionIntent.PetAttack:
                heldAction = null;
                input.PressPetAttack(token);
                return true;
            case ActionIntent.Loot:
                heldAction = null;
                return loot.Execute(token);
            case ActionIntent.Flee:
                if (heldAction != ActionIntent.Flee || !navigation.HasWaypoint())
                {
                    Vector3 point;
                    lock (safeSpots.MapLocations)
                        point = safeSpots.MapLocations.Peek();
                    navigation.SetWayPoints([point]);
                }
                navigation.Update(token);
                heldAction = action;
                return true;
            case ActionIntent.ContinueRoute:
                if (bits.Target() && bits.Target_NotDead() && bits.Target_Hostile())
                {
                    navigation.Stop();
                    input.StopForward(true);
                    heldAction = null;
                    return true;
                }
                if (TryAcquireTarget(token))
                {
                    navigation.Stop();
                    input.StopForward(true);
                    heldAction = null;
                    return true;
                }
                if (heldAction != ActionIntent.ContinueRoute || !navigation.HasWaypoint())
                    navigation.SetWayPoints(route.Route);
                navigation.Update(token);
                heldAction = action;
                return true;
            default:
                return false;
        }
    }

    private bool TryAcquireTarget(CancellationToken token)
    {
        if (bits.Target() && !bits.Target_Hostile())
        {
            input.PressClearTarget(token);
            wait.Update(token);
        }

        bool found = targetFinder.Search(TargetTypes,
            () => bits.Target_NotDead() && bits.Target_Hostile(), token);
        if (!found && bits.Target() && !bits.Target_Hostile())
        {
            input.PressClearTarget(token);
            wait.Update(token);
        }
        return found;
    }

    private bool RetreatToPreferredRange(CancellationToken token)
    {
        int targetGuid = player.TargetGuid;
        if (targetGuid == 0)
            return false;

        Stopwatch timer = Stopwatch.StartNew();
        input.StartBackward(true);
        try
        {
            while (timer.ElapsedMilliseconds < AiRangePolicy.RetreatTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                if (!bits.Target() || !bits.Target_Alive() || player.TargetGuid != targetGuid)
                    return false;
                if (player.MinRange() >= AiRangePolicy.HunterMinRange)
                    return true;
                wait.Update(100);
            }
            return player.MinRange() >= AiRangePolicy.HunterMinRange;
        }
        finally
        {
            input.StopBackward(true);
        }
    }

    private bool SafeSpotAvailable()
    {
        lock (safeSpots.MapLocations) return safeSpots.MapLocations.Count > 0;
    }

    public void Stop()
    {
        navigation.Stop();
        stopMoving.Stop();
        input.Reset();
        heldAction = null;
    }

    private sealed class CapabilityAdapter(CapabilityExecutor owner, ActionIntent action) : IAiCapability
    {
        public ActionIntent Action => action;
        public bool CanExecute(AIObservation observation) => owner.CanExecuteCore(action, observation);
        public Task<ActionResult> ExecuteAsync(CancellationToken token) => owner.ExecuteCoreAsync(action, token);
    }
}
