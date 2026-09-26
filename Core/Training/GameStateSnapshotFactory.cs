using SharedLib.NpcFinder;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Training;

public static class GameStateSnapshotFactory
{
    public static GameStateSnapshot Capture(long sequence, PlayerReader reader,
        AddonBits bits, AddonReader addon, NpcNameFinder finder,
        RouteInfo route, string? goal, int worldState,
        Core.GOAP.GoapAgentState botState, StuckDetector stuckDetector,
        CombatLog combatLog, ConfigurableInput input)
    {
        bool hasTarget = bits.Target();
        bool hasMouseOver = bits.MouseOver();
        PlayerState player = new(TrainingPoint.From(reader.MapPos), reader.Direction,
            reader.RunSpeed, bits.Moving(), bits.Dead(), bits.Combat(),
            reader.IsCasting(), reader.SpellBeingCast, bits.Mounted(),
            bits.Swimming(), bits.Falling(), reader.HealthCurrent(),
            reader.HealthMax(), reader.ManaCurrent(), reader.ManaMax(),
            reader.PTCurrent(), reader.PTMax(), reader.Level.Value,
            reader.PlayerXp.Value, reader.PlayerMaxXp, reader.UIMapId.Value,
            reader.MapId, reader.Class.ToString(), reader.Race.ToString(),
            reader.Version.ToString());

        // TargetMapPos is estimated from range and heading, so it is deliberately
        // excluded from the world target state. The addon does not supply target XYZ.
        TargetState target = new(hasTarget,
            hasTarget ? reader.TargetGuid : null,
            hasTarget ? reader.TargetId : null,
            hasTarget ? addon.TargetName : null,
            hasTarget ? reader.TargetHealth() : null,
            hasTarget ? reader.TargetMaxHealth() : null,
            hasTarget ? reader.TargetLevel : null,
            hasTarget ? reader.TargetClassification.ToString() : null,
            hasTarget ? reader.MinRange() : null,
            hasTarget ? reader.MaxRange() : null,
            hasTarget ? bits.Target_Dead() : null,
            hasTarget ? bits.Target_Hostile() : null,
            hasTarget ? reader.IsTargetCasting() : null,
            hasTarget ? reader.SpellBeingCastByTarget : null,
            hasTarget ? reader.TargetTarget.ToString() : null);

        MouseOverState mouseOver = new(hasMouseOver,
            hasMouseOver ? reader.MouseOverGuid : null,
            hasMouseOver ? reader.MouseOverId : null,
            hasMouseOver ? addon.MouseOverName : null,
            hasMouseOver ? reader.MouseOverLevel : null,
            hasMouseOver ? reader.MouseOverClassification.ToString() : null,
            hasMouseOver ? bits.MouseOver_Hostile() : null);

        // Npcs are screen detections, never a count of all nearby world entities.
        List<ScreenDetectedEntity> entities = [];
        foreach (NpcPosition npc in finder.Npcs)
        {
            bool inAddRegion = finder.IsAdd(npc);
            bool inTargetRegion = !inAddRegion &&
                Math.Abs(npc.ClickPoint.X - finder.screenMid) < finder.screenTargetBuffer;
            entities.Add(new(npc.Rect.X, npc.Rect.Y, npc.Rect.Width,
                npc.Rect.Height, npc.Rect.X + npc.Rect.Width / 2,
                npc.Rect.Y + npc.Rect.Height / 2,
                npc.ClickPoint.X, npc.ClickPoint.Y,
                inTargetRegion, inAddRegion));
        }
        ScreenObservedEntities observed = new(entities.Count,
            finder.TargetCount, finder.AddCount, finder.nameType.ToString(), entities);

        List<TrainingPoint> routePoints = [];
        foreach (Vector3 point in route.Route)
            routePoints.Add(TrainingPoint.From(point));
        List<TrainingPoint> pathPoints = [];
        foreach (Vector3 point in route.RouteToWaypoint)
            pathPoints.Add(TrainingPoint.From(point));
        TrainingPoint? next = pathPoints.Count > 0 ? pathPoints[0] : null;
        float? distance = next.HasValue
            ? Vector2.Distance(new(player.MapPosition.X, player.MapPosition.Y),
                new(next.Value.X, next.Value.Y))
            : null;
        NavigationState navigation = new(routePoints, pathPoints, next, distance);
        MovementInputState movement = new(
            Held(input.ForwardKey), Held(input.BackwardKey),
            Held(input.TurnLeftKey), Held(input.TurnRightKey),
            Held(input.StrafeLeft.ConsoleKey), Held(input.StrafeRight.ConsoleKey),
            Held(input.Jump.ConsoleKey));

        AuraCount aura = reader.AuraCount;
        CombatState combat = new(bits.Combat(), combatLog.PlayerOrPetCombat(),
            bits.Any_AutoAttack(), reader.GCD.Value,
            reader.ComboPoints(), target.Health, target.MaxHealth,
            reader.SpellBeingCast, reader.SpellBeingCastByTarget,
            reader.RemainCastMs, aura.PlayerBuff, aura.PlayerDebuff,
            aura.TargetBuff, aura.TargetDebuff, combatLog.DamageTakenCount(),
            combatLog.DamageDoneCount(), combatLog.ToPullCount());
        BotInternalState bot = new(goal, worldState,
            botState.LootableCorpseCount, botState.LastCombatKillCount,
            botState.Gathering, stuckDetector.IsRecovering,
            stuckDetector.AttemptCount,
            stuckDetector.IsRecovering ? stuckDetector.ActionDurationMs : null,
            stuckDetector.IsUnreachable);
        return new(DateTimeOffset.UtcNow, sequence, player, target, mouseOver,
            observed, navigation, movement, combat, bot)
        {
            RawAddonCells = addon.CopyRawData()
        };

        bool Held(ConsoleKey key) => key != default && input.IsKeyDown(key);
    }
}
