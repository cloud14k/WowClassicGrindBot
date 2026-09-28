using Core.GOAP;
using Core.Database;

using Microsoft.Extensions.Logging;

using PPather;
using PPather.Navmesh;

using SharedLib;
using SharedLib.Extensions;
using SharedLib.NpcFinder;

using System;
using System.Numerics;
using System.Threading;

namespace Core.Goals;

/// <summary>Roams around the player's position captured when a behavior test starts.</summary>
public sealed class WanderGoal : GoapGoal, IGoapEventListener, IRouteProvider, IDisposable
{
    public override float Cost => FollowRouteGoal.DEFAULT_COST;
    public DateTime LastActive => navigation.LastActive;

    private readonly ILogger<WanderGoal> logger;
    private readonly PlayerReader player;
    private readonly AddonBits bits;
    private readonly ConfigurableInput input;
    private readonly Wait wait;
    private readonly Navigation navigation;
    private readonly PPatherService pather;
    private readonly TargetFinder targetFinder;
    private readonly IBlacklist blacklist;
    private readonly NpcNames targetNames;
    private Vector3 center;
    private readonly int radius;
    private readonly bool searchTargets;
    private readonly int mapId;
    private NavmeshPathfinder? navmesh;
    private NavmeshConnectivity? connectivity;
    private int centerComponent = -1;
    private Vector3 destination;
    private CancellationTokenSource? searchCancellation;
    private Thread? searchThread;
    private Exception? searchFailure;
    private int targetAcquired;
    private bool paused;

    public WanderGoal(Vector3 center, int radius, bool searchTargets,
        ILogger<WanderGoal> logger, PlayerReader player, AddonBits bits,
        ConfigurableInput input, Wait wait, Navigation navigation,
        PPatherService pather, TargetFinder targetFinder, IBlacklist blacklist,
        ClassConfiguration config)
        : base(nameof(WanderGoal))
    {
        this.center = center;
        this.radius = radius;
        this.searchTargets = searchTargets;
        this.logger = logger;
        this.player = player;
        this.bits = bits;
        this.input = input;
        this.wait = wait;
        this.navigation = navigation;
        this.pather = pather;
        this.targetFinder = targetFinder;
        this.blacklist = blacklist;
        mapId = player.MapId;
        targetNames = config.TargetNeutral ? NpcNames.Enemy | NpcNames.Neutral : NpcNames.Enemy;

        AddPrecondition(GoapKey.incombat, false);
        if (searchTargets)
            AddPrecondition(GoapKey.hastarget, false);
        AddPrecondition(GoapKey.damagedone, false);
        AddPrecondition(GoapKey.damagetaken, false);
        AddPrecondition(GoapKey.producedcorpse, false);
        AddPrecondition(GoapKey.consumecorpse, false);
        navigation.SparseWaypoints = true;
        navigation.OnPathCalculated += CheckRouteBounds;
        navigation.OnDestinationReached += ChooseDestination;
        navigation.OnNoPathFound += ChooseDestination;
        InitialiseNavmesh();
    }

    public bool HasNext() => navigation.HasNext();
    public Vector3 NextMapPoint() => navigation.NextMapPoint();
    public Vector3[] MapRoute() => destination == default ? [] : [WorldMapAreaDB.ToMap_FlipXY(destination, player.WorldMapArea)];
    public Vector3[] PathingRoute() => navigation.TotalRoute;

    public override void OnEnter()
    {
        paused = false;
        if (!navigation.HasWaypoint())
            ChooseDestination();
        else
            navigation.Resume();
        StartSearch();
    }

    public override void Update()
    {
        if (Volatile.Read(ref searchFailure) is Exception failure)
            throw new InvalidOperationException("Wander target search failed.", failure);

        if (bits.Target() && bits.Target_Dead())
            input.PressClearTarget();
        if (bits.Drowning())
            input.PressJumpAscend();

        if (!paused && !bits.Combat() && Volatile.Read(ref targetAcquired) == 0)
        {
            if (navigation.IsUnreachable)
                ChooseDestination();
            navigation.Update();
        }
        wait.Update();
    }

    public override void OnExit()
    {
        StopSearch();
        navigation.StopMovement();
        navigation.Stop();
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is PauseEvent)
        {
            paused = true;
            StopSearch();
            navigation.Pause();
        }
        else if (e is ResumeEvent)
        {
            paused = false;
            navigation.Resume();
            StartSearch();
        }
        else if (e is AbortEvent)
        {
            OnExit();
        }
    }

    public void Dispose()
    {
        StopSearch();
        navigation.OnPathCalculated -= CheckRouteBounds;
        navigation.OnDestinationReached -= ChooseDestination;
        navigation.OnNoPathFound -= ChooseDestination;
        navigation.Dispose();
    }

    private void InitialiseNavmesh()
    {
        navmesh = pather.GetQueryNavmeshForMap(mapId)
            ?? throw new InvalidOperationException("当前地图没有可用的导航网格，无法在指定半径内漫游。");
        // The addon provides map X/Y but no player height. On a fresh session
        // WorldPosZ is zero, so resolve the walkable surface before the tight
        // snap and floor checks below.
        if (center.Z == 0f && navmesh.TryGetHeight(center.X, center.Y, out float height))
            center = new Vector3(center.X, center.Y, height);
        connectivity = pather.BuildConnectivity(mapId,
            center.X - radius, center.Y - radius, center.X + radius, center.Y + radius)
            ?? throw new InvalidOperationException("漫游范围内没有可用的导航网格。");
        if (!navmesh.TrySnapWalkable(center, out Vector3 snapped, out long centerRef) ||
            center.WorldDistanceXYTo(snapped) > 5f)
            throw new InvalidOperationException("角色当前位置不在可行走导航网格上。");
        centerComponent = connectivity.ComponentOf(centerRef);
        if (centerComponent < 0)
            throw new InvalidOperationException("无法确定角色所在的可行走区域。");
    }

    private void ChooseDestination()
    {
        if (navmesh == null || connectivity == null)
            return;

        for (int attempt = 0; attempt < 48; attempt++)
        {
            float angle = Random.Shared.NextSingle() * 2f * MathF.PI;
            float distance = radius * MathF.Sqrt(Random.Shared.NextSingle());
            Vector3 hint = center + new Vector3(MathF.Cos(angle) * distance,
                MathF.Sin(angle) * distance, 0f);
            if (!navmesh.TrySnapWalkable(hint, out Vector3 point, out long polyRef) ||
                center.WorldDistanceXYTo(point) > radius ||
                player.WorldPos.WorldDistanceXYTo(point) < MathF.Min(5f, radius / 2f) ||
                MathF.Abs(point.Z - center.Z) > 5f ||
                connectivity.ComponentOf(polyRef) != centerComponent)
                continue;

            destination = point;
            navigation.SetWayPoints([point]);
            logger.LogInformation("Wander destination {Destination}; center {Center}, radius {Radius} yd",
                point, center, radius);
            return;
        }

        logger.LogWarning("No reachable wander destination within {Radius} yd of {Center}", radius, center);
    }

    private void CheckRouteBounds()
    {
        // A reachable destination may still require a detour outside the circle.
        // Inspect the path before Navigation begins following it.
        foreach (Vector3 point in navigation.TotalRoute)
        {
            if (center.WorldDistanceXYTo(point) <= radius)
                continue;

            logger.LogInformation("Wander route leaves the radius; choosing another destination");
            ChooseDestination();
            return;
        }
    }

    private void StartSearch()
    {
        if (!searchTargets || searchThread != null)
            return;
        searchFailure = null;
        Volatile.Write(ref targetAcquired, 0);
        targetFinder.Reset();
        searchCancellation = new CancellationTokenSource();
        CancellationToken token = searchCancellation.Token;
        searchThread = new Thread(() => Search(token)) { IsBackground = true, Name = nameof(WanderGoal) + "TargetSearch" };
        searchThread.Start();
    }

    private void StopSearch()
    {
        searchCancellation?.Cancel();
        if (searchThread != null && Thread.CurrentThread != searchThread)
            searchThread.Join();
        searchThread = null;
        searchCancellation?.Dispose();
        searchCancellation = null;
        targetFinder.Reset();
    }

    private void Search(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (targetFinder.Search(targetNames, bits.Target_NotDead, token) && bits.Target())
                {
                    if (blacklist.Is())
                    {
                        input.PressClearTarget();
                    }
                    else
                    {
                        Volatile.Write(ref targetAcquired, 1);
                        return;
                    }
                }
                wait.Update(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref searchFailure, ex); }
    }
}
