using Core;
using Core.GOAP;
using Core.Goals;
using Core.Training;

using Game;

using Microsoft.Extensions.Logging.Abstractions;

using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace CoreTests;

/// <summary>Replays the no-target combat gap without a game client or native input.</summary>
internal static class Test_CombatRecovery
{
    public static void Run()
    {
        using Fixture fixture = new();
        FindThreatGoal recovery = new(fixture.Finder, fixture.Config, fixture.CombatLog);
        GoapAgentState corpseState = new();
        ConsumeCorpseGoal consume = new(NullLogger<ConsumeCorpseGoal>.Instance,
            fixture.Config, corpseState);
        CorpseConsumedGoal finish = new(NullLogger<CorpseConsumedGoal>.Instance,
            fixture.Config, corpseState, fixture.Wait);
        GoapGoal[] goals = [recovery, consume, finish];

        BitVector32 world = new(1 << (int)GoapKey.incombat);
        Assert(Selected(goals, world) == recovery,
            "Logged state: combat, no target, no damage selects recovery.");

        world[1 << (int)GoapKey.producedcorpse] = true;
        Assert(Selected(goals, world) == consume,
            "A kill awaiting corpse processing wins while combat lingers.");
        world[1 << (int)GoapKey.producedcorpse] = false;
        world[1 << (int)GoapKey.consumecorpse] = true;
        Assert(Selected(goals, world) == finish,
            "Corpse bookkeeping finishes before no-damage recovery.");

        fixture.CombatLog.DamageTaken.Add(987);
        world[1 << (int)GoapKey.damagetaken] = true;
        world[1 << (int)GoapKey.dangercombat] = true;
        Assert(Selected(goals, world) == recovery,
            "An incoming attacker takes priority over corpse processing.");
        fixture.CombatLog.DamageTaken.Clear();

        world = new(1 << (int)GoapKey.incombat);
        world[1 << (int)GoapKey.targetisalive] = true;
        Assert(Selected([recovery], world) == null, "A live target leaves recovery.");
        world[1 << (int)GoapKey.targetisalive] = false;
        world[1 << (int)GoapKey.isdead] = true;
        Assert(Selected([recovery], world) == null, "Death leaves recovery.");
        world = new();
        Assert(Selected([recovery], world) == null, "Out of combat leaves recovery.");

        fixture.Backend.OnPress = key =>
        {
            if (key == ConsoleKey.Tab) fixture.SetTarget(987, hostile: true, targetsUs: true);
        };
        fixture.Finder.FindPossibleThreats([]);
        Assert(fixture.Backend.Pressed.SequenceEqual([ConsoleKey.Tab]) && fixture.Bits.Target_Alive(),
            "Defensive pet without a target falls through immediately to Tab.");

        fixture.Reset();
        fixture.Provider.Data[PlayerReader.PetTargetGuidCell] = 987;
        fixture.Backend.OnPress = key =>
        {
            if (key == fixture.Config.TargetPet.ConsoleKey)
                fixture.SetTarget(1, hostile: false, targetsUs: false);
            // Simulate assist failing: the target stays on the pet.
            if (key == fixture.Config.ClearTarget.ConsoleKey)
                fixture.SetTarget(0, hostile: false, targetsUs: false);
            if (key == ConsoleKey.Tab)
                fixture.SetTarget(987, hostile: true, targetsUs: true);
        };
        fixture.Finder.FindPossibleThreats([]);
        Assert(fixture.Backend.Pressed.Contains(ConsoleKey.Tab) && fixture.Player.TargetGuid == 987,
            "Failed pet assist falls back to Tab rather than retaining the pet.");

        fixture.Reset();
        fixture.Backend.OnPress = _ => { };
        fixture.Finder.FindPossibleThreats([]);
        Assert(fixture.Backend.Pressed.SequenceEqual([ConsoleKey.Tab, fixture.Config.TurnLeftKey]),
            "No target after Tab turns to search another direction.");

        fixture.Reset();
        fixture.Backend.OnPress = key =>
        {
            if (key == ConsoleKey.Tab) fixture.SetTarget(555, hostile: true, targetsUs: false);
            if (key == fixture.Config.ClearTarget.ConsoleKey)
                fixture.SetTarget(0, hostile: false, targetsUs: false);
        };
        fixture.Finder.FindPossibleThreats([]);
        Assert(!fixture.Bits.Target() && fixture.Backend.Pressed.Contains(fixture.Config.ClearTarget.ConsoleKey),
            "An unrelated hostile is cleared instead of handed to CombatGoal.");

        fixture.Reset();
        fixture.Config.TargetNearestTarget.Cooldown = 60000;
        fixture.Config.TargetNearestTarget.SetClicked();
        fixture.Finder.FindPossibleThreats([]);
        Assert(fixture.Backend.Pressed.Count == 0, "Tab cooldown prevents search input spam.");

        Console.WriteLine("Combat recovery regression: PASS");
    }

    private static GoapGoal Selected(GoapGoal[] goals, BitVector32 world)
    {
        Stack<GoapGoal> plan = GoapPlanner.Plan(goals, world, GoapPlanner.EmptyGoalState);
        return plan.Count > 0 ? plan.Pop() : null;
    }

    private static void Assert(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly ManualResetEventSlim frameReady = new(true);
        private readonly TrainingRecorder recorder = new(new TrainingCollectionSettings());
        private readonly WowProcessInput processInput;
        private readonly Timer frameTicker;

        public NullAddonDataProvider Provider { get; } = new(120);
        public AddonBits Bits { get; } = new();
        public ClassConfiguration Config { get; } = new();
        public CombatLog CombatLog { get; }
        public PlayerReader Player { get; }
        public Wait Wait { get; }
        public FakeInput Backend { get; }
        public ThreatFinder Finder { get; }

        public Fixture()
        {
            Config.TargetNearestTarget.ConsoleKey = ConsoleKey.Tab;
            Config.TargetNearestTarget.Cooldown = 0;
            Config.ClearTarget.ConsoleKey = ConsoleKey.Delete;
            Config.TargetPet.ConsoleKey = ConsoleKey.F1;
            Config.TargetTargetOfTarget.ConsoleKey = ConsoleKey.F2;
            processInput = new(NullLogger<WowProcessInput>.Instance, stopping, null);
            Backend = new(frameReady);
            // Replace the constructed backend before any input call. All keys
            // stay inside this fake; the test never sends Windows/game input.
            object router = typeof(WowProcessInput).GetField("nativeInput",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(processInput);
            router.GetType().GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(router, Backend);

            ConfigurableInput input = new(NullLogger<ConfigurableInput>.Instance,
                processInput, Config, recorder);
            Player = new(Provider, null, null, Bits, null, null);
            CombatLog = new(Bits);
            Wait = new(frameReady, stopping);
            Finder = new(NullLogger<ThreatFinder>.Instance, input, Wait, Player, Bits, CombatLog);
            frameTicker = new(_ => frameReady.Set(), null, 0, 10);
            Reset();
        }

        public void Reset()
        {
            Array.Clear(Provider.Data);
            Provider.Data[8] = (1 << 14) | (1 << 6); // Combat + pet exists.
            Provider.Data[9] = 1 << 23; // Pet defensive, pet target not dead.
            Provider.Data[39] = 100;
            Provider.Data[68] = 1;
            Bits.Update(Provider);
            CombatLog.Reset();
            Config.TargetNearestTarget.ResetCooldown();
            Backend.Pressed.Clear();
            frameReady.Set();
        }

        public void SetTarget(int guid, bool hostile, bool targetsUs)
        {
            Provider.Data[57] = guid;
            Provider.Data[8] = (1 << 14) | (1 << 6) |
                (guid != 0 ? 1 << 17 : 0) |
                (hostile ? 1 << 5 : 0) | (targetsUs ? 1 << 15 : 0);
            Bits.Update(Provider);
        }

        public void Dispose()
        {
            frameTicker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            processInput.Dispose();
            recorder.Dispose();
            frameReady.Dispose();
            stopping.Dispose();
        }
    }

    private sealed class FakeInput(ManualResetEventSlim frameReady) : IInput
    {
        public List<ConsoleKey> Pressed { get; } = [];
        public Action<ConsoleKey> OnPress { get; set; } = _ => { };
        public int PressRandom(int key, int milliseconds) => PressRandom(key, milliseconds, default);
        public int PressRandom(int key, int milliseconds, CancellationToken token)
        {
            Pressed.Add((ConsoleKey)key);
            OnPress((ConsoleKey)key);
            frameReady.Set();
            return milliseconds;
        }
        public void PressFixed(int key, int milliseconds, CancellationToken token)
            => PressRandom(key, milliseconds, token);
        public void KeyDown(int key) { }
        public void KeyUp(int key) { }
        public void SetCursorPos(Point point) { }
        public void RightClick(Point point) { }
        public void LeftClick(Point point) { }
        public void SendText(string value) { }
    }
}
