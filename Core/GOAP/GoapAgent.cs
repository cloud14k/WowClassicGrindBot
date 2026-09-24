using Core.Goals;
using Core.Session;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.GOAP;

public sealed partial class GoapAgent : IDisposable
{
    // The empty-plan state can flap at addon tick rate. The bare warning still
    // logs every transition; the full report is rate limited so an oscillating
    // state cannot flood the file users attach to issues.
    private const double NO_PLAN_REPORT_INTERVAL_MS = 1000;

    private readonly ILogger logger;
    private readonly ILogger globalLogger;

    private readonly ClassConfiguration classConfig;
    private readonly AddonReader addonReader;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly IWowScreen screen;
    private readonly RouteInfo routeInfo;
    private readonly ConfigurableInput input;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private readonly CorpseTracker corpseTracker;

    private readonly IGrindSessionHandler sessionHandler;
    private readonly StopMoving stopMoving;

    private readonly Thread goapThread;
    private readonly CancellationTokenSource<GoapAgent> cts;
    private readonly ManualResetEventSlim sessionPauseEvent;
    private readonly ManualResetEventSlim controlWakeEvent = new(false);
    // Serializes a GOAP tick with pause/stop. Without this, an Update() could
    // press a key after the UI had already released input for a pause/stop.
    private readonly object controlSync = new();

    private readonly IScreenCapture screenCapture;
    // Resolved only so the container constructs them - both subscribe in their
    // own ctor and nothing else asks for them.
    private readonly IBagChangeTracker bagChangeTracker;
    private readonly IMoneyChangeTracker moneyChangeTracker;
    // Not behind the LogBagChanges toggle the other two share - a level-up is one line
    // every few hours, not the per-item chatter that toggle exists to silence.
    private readonly LevelChangeTracker levelChangeTracker;

    private long lastNoPlanReport;
    private int lastCorpseTargetGuid;
    private float lastCorpseDistance;
    private bool hasLastCorpseTarget;

    private volatile bool active;
    private volatile bool paused;
    public bool Paused => paused;
    public bool Active
    {
        get => active;
        set
        {
            lock (controlSync)
            {
                if (active == value)
                    return;

                active = value;
                if (!active)
                {
                    paused = false;
                    sessionPauseEvent.Reset();
                    controlWakeEvent.Set();

                    ReleaseInputAndAbortGoals();

                    if (classConfig.Mode is Mode.AttendedGrind or Mode.Grind)
                    {
                        sessionHandler.Stop("Stopped", false);
                    }

                    screen.Enabled = false;
                }
                else
                {
                    paused = false;
                    addonReader.SessionReset();
                    SessionStat.Reset();

                    if (CurrentGoal is IGoapEventListener listener)
                    {
                        SendGoalEvent(listener, new ResumeEvent());
                    }

                    sessionPauseEvent.Set();
                    controlWakeEvent.Set();

                    if (classConfig.Mode is Mode.AttendedGrind or Mode.Grind)
                    {
                        SessionStat.Start();
                        sessionHandler.Start(classConfig.OverridePathFilename ?? classConfig.PathFilename);
                    }
                }
            }
        }
    }

    /// <summary>Temporarily suspends the current session while retaining its state and statistics.</summary>
    public void Pause()
    {
        lock (controlSync)
        {
            if (!active || paused)
                return;

            paused = true;
            sessionPauseEvent.Reset();
            controlWakeEvent.Set();

            // Pausing must not broadcast Abort to every goal: that tears down
            // unrelated state and can discard route progress. Pause only the
            // selected goal, preserving its in-memory route position.
            if (CurrentGoal is IGoapEventListener listener)
                SendGoalEvent(listener, new PauseEvent());
            try { stopMoving.Stop(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to stop movement while pausing"); }
            try { input.Reset(); }
            catch (Exception ex) { logger.LogError(ex, "Failed to release input while pausing"); }
        }
    }

    /// <summary>Resumes the paused session and lets the current goal continue from its retained state.</summary>
    public void Resume()
    {
        lock (controlSync)
        {
            if (!active || !paused)
                return;

            if (CurrentGoal is IGoapEventListener listener)
                SendGoalEvent(listener, new ResumeEvent());

            paused = false;
            sessionPauseEvent.Set();
            controlWakeEvent.Set();
        }
    }

    private void ReleaseInputAndAbortGoals()
    {
        try { stopMoving.Stop(); }
        catch (Exception ex) { logger.LogError(ex, "Failed to stop movement during session control"); }

        try { input.Reset(); }
        catch (Exception ex) { logger.LogError(ex, "Failed to release input during session control"); }

        try { screen.Enabled = false; }
        catch (Exception ex) { logger.LogError(ex, "Failed to disable screen processing during session control"); }

        foreach (IGoapEventListener goal in AvailableGoals.OfType<IGoapEventListener>())
            SendGoalEvent(goal, new AbortEvent());
    }

    private void SendGoalEvent(IGoapEventListener goal, GoapEventArgs args)
    {
        try { goal.OnGoapEvent(args); }
        catch (Exception ex) { logger.LogError(ex, "Goal {GoalType} failed while handling {EventType}", goal.GetType().Name, args.GetType().Name); }
    }

    public BitVector32 WorldState { get; private set; }

    public SessionStat SessionStat { get; }

    public GoapAgentState State { get; }
    public GoapGoal[] AvailableGoals { get; }

    public Stack<GoapGoal> Plan { get; private set; }
    public GoapGoal? CurrentGoal { get; private set; }

    public GoapAgent(
        ILogger<GoapAgent> logger,
        ILogger globalLogger,
        CancellationTokenSource<GoapAgent> cts,
        RouteInfo routeInfo,
        IScreenCapture screenCapture,
        ClassConfiguration classConfiguration,
        IWowScreen screen,
        GoapAgentState state,
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits,
        ConfigurableInput input,
        IMountHandler mountHandler,
        CombatLog combatLog,
        CorpseTracker corpseTracker,
        IBagChangeTracker bagChangeTracker,
        IMoneyChangeTracker moneyChangeTracker,
        LevelChangeTracker levelChangeTracker,
        SessionStat sessionStat,
        StopMoving stopMoving,
        IGrindSessionHandler sessionHandler,
        IEnumerable<GoapGoal> availableGoals
        )
    {
        this.routeInfo = routeInfo;

        this.cts = cts;

        this.logger = logger;
        this.globalLogger = globalLogger;

        this.screenCapture = screenCapture;
        this.classConfig = classConfiguration;

        this.screen = screen;
        this.State = state;
        this.addonReader = addonReader;
        this.playerReader = playerReader;
        this.bits = bits;

        this.input = input;
        this.mountHandler = mountHandler;

        this.combatLog = combatLog;
        this.corpseTracker = corpseTracker;
        this.bagChangeTracker = bagChangeTracker;
        this.moneyChangeTracker = moneyChangeTracker;
        this.levelChangeTracker = levelChangeTracker;

        SessionStat = sessionStat;

        this.stopMoving = stopMoving;

        this.sessionHandler = sessionHandler;

        this.AvailableGoals = availableGoals.OrderBy(a => a.Cost).ToArray();

        combatLog.KillCredit += OnKillCredit;
        combatLog.PlayerDeath += PlayerDied;

        addonReader.SessionReset();
        sessionStat.Reset();

        this.Plan = new();

        foreach (GoapGoal a in AvailableGoals)
        {
            a.GoapEvent += HandleGoapEvent;

            foreach (IGoapEventListener b in AvailableGoals.OfType<IGoapEventListener>())
            {
                if (b != a)
                    a.GoapEvent += b.OnGoapEvent;
            }
        }

        sessionPauseEvent = new(false);
        goapThread = new(GoapThread);
        goapThread.Start();
    }

    public void Dispose()
    {
        cts.Cancel();
        sessionPauseEvent.Set();
        controlWakeEvent.Set();

        // The session scope owns the cancellation source and disposes it as soon
        // as this method returns. Wait for the worker to finish before that can
        // happen; otherwise a worker just leaving the initial pause gate can
        // access cts.Token after the source has been disposed.
        if (Thread.CurrentThread != goapThread)
            goapThread.Join();

        foreach (GoapGoal a in AvailableGoals)
        {
            a.GoapEvent -= HandleGoapEvent;

            foreach (IGoapEventListener b in AvailableGoals.OfType<IGoapEventListener>())
            {
                if (b != a)
                    a.GoapEvent -= b.OnGoapEvent;
            }
        }

        combatLog.KillCredit -= OnKillCredit;
        combatLog.PlayerDeath -= PlayerDied;

        sessionPauseEvent.Dispose();
        controlWakeEvent.Dispose();
    }

    private void GoapThread()
    {
        bool wasEmpty = false;

        sessionPauseEvent.Wait();

        WaitHandle[] waitHandles = [
            addonReader.DataReady.WaitHandle,
            cts.Token.WaitHandle,
            controlWakeEvent.WaitHandle
        ];

        while (!cts.IsCancellationRequested)
        {
            bool waitForSession = false;
            lock (controlSync)
            {
                // Active/paused is checked on the execution thread as well as
                // at the wait gate. This closes the wake-up race where a tick
                // could begin immediately after Stop/Pause reset the gate.
                if (!active || paused)
                {
                    waitForSession = true;
                }
                else
                {
                    GoapGoal? newGoal = NextGoal();
                    if (newGoal != null)
                    {
                        if (newGoal != CurrentGoal)
                        {
                            wasEmpty = false;
                            CurrentGoal?.OnExit();
                            CurrentGoal = newGoal;

                            LogNewGoal(logger, newGoal.Name);
                            CurrentGoal.OnEnter();
                        }

                        newGoal.Update();
                    }
                    else if (!wasEmpty)
                    {
                        LogNewEmptyGoal(logger);
                        ReportNoPlan();
                        CurrentGoal?.OnExit();
                        CurrentGoal = null;
                        wasEmpty = true;
                    }
                }
            }

            if (waitForSession)
            {
                try
                {
                    sessionPauseEvent.Wait(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            Thread.Sleep(2);

            try
            {
                WaitHandle.WaitAny(waitHandles);
                controlWakeEvent.Reset();
                sessionPauseEvent.Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Thread stopped!");
    }

    private GoapGoal? NextGoal()
    {
        UpdateWorldState();

        if (Plan.Count == 0)
        {
            Plan = GoapPlanner.Plan(AvailableGoals, WorldState, GoapPlanner.EmptyGoalState);
        }

        return Plan.Count > 0 ? Plan.Pop() : null;
    }

    /// <summary>
    /// Dumps why nothing was runnable. Reuses <see cref="GoapPlanner.LastUsable"/>
    /// rather than calling <see cref="GoapGoal.CanRun"/> again - Blacklist keeps
    /// dedup state inside its check.
    /// </summary>
    private void ReportNoPlan()
    {
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        if (GetElapsedTime(lastNoPlanReport).TotalMilliseconds < NO_PLAN_REPORT_INTERVAL_MS)
        {
            return;
        }

        lastNoPlanReport = GetTimestamp();

        LogNoPlanReport(logger,
            NoPlanReport.Build(AvailableGoals, GoapPlanner.LastUsable,
                WorldState, playerReader, bits, combatLog, State));
    }

    private void UpdateWorldState()
    {
        AddonBits b = bits;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool dmgDone = combatLog.DamageDoneCount() > 0;
        bool hasTarget = b.Target();

        if (hasTarget)
        {
            lastCorpseTargetGuid = playerReader.TargetGuid;
            lastCorpseDistance = (playerReader.MaxRange() + playerReader.MinRange()) / 2f;
            hasLastCorpseTarget = true;
        }

        // Not b.Combat(): a pet opener leaves the player unflagged until the mob
        // walks over and swings, so every combat gated goal would sit out the first
        // seconds of a pull the pet already won. See CombatLog.PetEngaged.
        bool playerCombat = combatLog.PlayerOrPetCombat();

        int data =
            (B(hasTarget) << (int)GoapKey.hastarget) |
            (B(playerCombat && dmgTaken) << (int)GoapKey.dangercombat) |
            (B(dmgTaken) << (int)GoapKey.damagetaken) |
            (B(dmgDone) << (int)GoapKey.damagedone) |
            (B(dmgTaken || dmgDone) << (int)GoapKey.damagetakenordone) |
            (B(hasTarget && !b.Target_Dead()) << (int)GoapKey.targetisalive) |

            (B(((hasTarget &&
            playerReader.TargetHealthPercent() < 30) ||
            playerReader.TargetTarget is UnitsTarget.Me or
                UnitsTarget.Pet or UnitsTarget.PartyOrPet) &&
                !combatLog.ToPull.Contains(playerReader.TargetGuid)) << (int)GoapKey.targettargetsus) |

            (B(playerCombat) << (int)GoapKey.incombat) |
            (B(playerReader.PetTarget() && !b.PetTarget_Dead()) << (int)GoapKey.pethastarget) |
            (B(mountHandler.IsMounted()) << (int)GoapKey.ismounted) |
            (B(playerReader.WithInPullRange()) << (int)GoapKey.withinpullrange) |
            (B(playerReader.WithInCombatRange()) << (int)GoapKey.incombatrange) |
            (B(playerCombat && bits.Target_Combat() && combatLog.ToPullCount() > 0) << (int)GoapKey.pulled) |
            (B(b.Dead()) << (int)GoapKey.isdead) |
            (B(State.LootableCorpseCount > 0) << (int)GoapKey.shouldloot) |
            (B(State.GatherableCorpseCount > 0) << (int)GoapKey.shouldgather) |
            (B(State.LastCombatKillCount > 0) << (int)GoapKey.producedcorpse) |
            (B(State.ShouldConsumeCorpse) << (int)GoapKey.consumecorpse) |
            (B(b.Swimming()) << (int)GoapKey.isswimming) |
            (B(b.Items_Broken()) << (int)GoapKey.itemsbroken) |
            (B(State.Gathering) << (int)GoapKey.gathering) |
            (B(b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid))) << (int)GoapKey.targethostile) |
            (B(b.Focus()) << (int)GoapKey.hasfocus) |
            (B(b.FocusTarget()) << (int)GoapKey.focushastarget) |
            (B(State.ConsumableCorpseCount > 0) << (int)GoapKey.consumablecorpsenearby)
            ;

        WorldState = new(data);

        static int B(bool b) => b ? 1 : 0;
    }

    private void HandleGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent g)
        {
            switch (g.Key)
            {
                case GoapKey.consumecorpse:
                    State.ShouldConsumeCorpse = g.Value;
                    break;
                case GoapKey.gathering:
                    State.Gathering = g.Value;
                    break;
            }
        }
        else if (e is CorpseEvent c)
        {
            routeInfo.PoiList.Add(new RouteInfoPoi(c.MapLoc, CorpseEvent.NAME, CorpseEvent.COLOR, c.Radius));
            corpseTracker.AddCorpse(c.PackedGuid, c.MapLoc);
        }
        else if (e is SkinCorpseEvent s)
        {
            routeInfo.PoiList.Add(new RouteInfoPoi(s.MapLoc, SkinCorpseEvent.NAME, SkinCorpseEvent.COLOR, s.Radius));
        }
        else if (e is RemoveClosestPoi r)
        {
            RemoveClosestPoiByType(r.Name);
        }
        else if (e is ScreenCaptureEvent)
        {
            screenCapture.Request();
        }
    }

    private void OnKillCredit()
    {
        if (!Active)
        {
            LogInactiveKillDetected(logger);
            return;
        }

        SessionStat.Kills++;

        State.LastCombatKillCount++;
        State.ConsumableCorpseCount++;

        BroadcastGoapEvent(GoapKey.producedcorpse, true);

        // Corpse discovery belongs to session kill tracking, not CombatGoal.
        // Loot-only test sessions must still receive the corpse POI without
        // registering or running the combat action goal.
        int deadGuid = combatLog.DeadGuid.Value;
        bool hasMatchingTargetSnapshot = hasLastCorpseTarget && lastCorpseTargetGuid == deadGuid;
        float distance = hasMatchingTargetSnapshot
            ? lastCorpseDistance
            : (playerReader.MaxRange() + playerReader.MinRange()) / 2f;
        float direction = playerReader.Direction;
        Vector3 playerPosition = playerReader.MapPos;
        Vector3 corpsePosition = PointEstimator.GetMapPos(
            playerReader.WorldMapArea,
            playerReader.WorldPos,
            direction,
            distance);
        BroadcastGoapEvent(new CorpseEvent(
            corpsePosition,
            distance,
            direction,
            playerPosition,
            deadGuid));

        if (logger.IsEnabled(LogLevel.Information))
        {
            int damageTakenCount = combatLog.DamageTakenCount();
            LogActiveKillDetected(logger, SessionStat.Kills, State.LastCombatKillCount, damageTakenCount);
        }
    }

    public void PlayerDied()
    {
        SessionStat.Deaths++;
    }

    private void BroadcastGoapEvent(GoapKey goapKey, bool value)
    {
        BroadcastGoapEvent(new GoapStateEvent(goapKey, value));
    }

    private void BroadcastGoapEvent(GoapEventArgs args)
    {
        HandleGoapEvent(args);
        foreach (IGoapEventListener goal in AvailableGoals.OfType<IGoapEventListener>())
            SendGoalEvent(goal, args);
    }

    private void RemoveClosestPoiByType(string type)
    {
        if (routeInfo.PoiList.Count == 0)
            return;

        int index = -1;
        float minDistance = float.MaxValue;
        Vector3 playerMap = playerReader.MapPos;
        for (int i = 0; i < routeInfo.PoiList.Count; i++)
        {
            RouteInfoPoi poi = routeInfo.PoiList[i];
            if (poi.Name != type)
                continue;

            float mapMin = playerMap.MapDistanceXYTo(poi.MapLoc);
            if (mapMin < minDistance)
            {
                minDistance = mapMin;
                index = i;
            }
        }

        if (index > -1)
        {
            routeInfo.PoiList.RemoveAt(index);
        }
    }

    public bool HasState(GoapKey key) => WorldState[1 << (int)key];

    public void NodeFound()
    {
        State.Gathering = true;
        BroadcastGoapEvent(GoapKey.gathering, true);
    }

    #region Logging

    [LoggerMessage(
        EventId = 0050,
        Level = LogLevel.Information,
        Message = "Kill credit detected! Session Total: {sessionTotal} | Last Combat: {lastCombatCount} | Currently fighting: {currentCombatRemain}")]
    static partial void LogActiveKillDetected(ILogger logger, int sessionTotal, int lastCombatCount, int currentCombatRemain);

    [LoggerMessage(
        EventId = 0051,
        Level = LogLevel.Information,
        Message = "Inactive, kill credit detected!")]
    static partial void LogInactiveKillDetected(ILogger logger);

    [LoggerMessage(
        EventId = 0052,
        Level = LogLevel.Information,
        Message = "New Plan= {name}")]
    static partial void LogNewGoal(ILogger logger, string name);

    [LoggerMessage(
        EventId = 0053,
        Level = LogLevel.Warning,
        Message = "New Plan= NO PLAN")]
    static partial void LogNewEmptyGoal(ILogger logger);

    [LoggerMessage(
        EventId = 0054,
        Level = LogLevel.Warning,
        Message = "{report}")]
    static partial void LogNoPlanReport(ILogger logger, string report);

    #endregion
}
