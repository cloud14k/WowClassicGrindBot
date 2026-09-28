using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Threading;

namespace Core.Goals;

/// <summary>
/// Acquires nearby enemies without requiring FollowRouteGoal. Used by the
/// behavior-test Combat flow when profile routes are intentionally omitted.
/// </summary>
public sealed class FindNearbyTargetGoal : GoapGoal
{
    public override float Cost => 6.5f;

    private readonly ILogger<FindNearbyTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly AddonBits bits;
    private readonly TargetFinder targetFinder;
    private readonly IBlacklist targetBlacklist;
    private readonly Wait wait;
    private readonly NpcNames targets;

    private CancellationTokenSource? cancellation;
    private Thread? worker;
    private Exception? workerFailure;

    public FindNearbyTargetGoal(ILogger<FindNearbyTargetGoal> logger,
        ConfigurableInput input, AddonBits bits, TargetFinder targetFinder,
        IBlacklist targetBlacklist, Wait wait, ClassConfiguration classConfig)
        : base(nameof(FindNearbyTargetGoal))
    {
        this.logger = logger;
        this.input = input;
        this.bits = bits;
        this.targetFinder = targetFinder;
        this.targetBlacklist = targetBlacklist;
        this.wait = wait;
        targets = classConfig.TargetNeutral
            ? NpcNames.Enemy | NpcNames.Neutral
            : NpcNames.Enemy;

        AddPrecondition(GoapKey.hastarget, false);
        AddPrecondition(GoapKey.incombat, false);
        AddEffect(GoapKey.hastarget, true);
    }

    public override void OnEnter()
    {
        StopWorker();
        workerFailure = null;
        targetFinder.Reset();
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        worker = new Thread(() => Search(token))
        {
            IsBackground = true,
            Name = nameof(FindNearbyTargetGoal)
        };
        worker.Start();
    }

    public override void Update()
    {
        if (Volatile.Read(ref workerFailure) is Exception failure)
            throw new InvalidOperationException("Nearby target search failed.", failure);
    }

    public override void OnExit()
    {
        StopWorker();
        targetFinder.Reset();
    }

    private void Search(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (targetFinder.Search(targets, bits.Target_NotDead, token))
                {
                    if (bits.Target() && targetBlacklist.Is())
                    {
                        logger.LogInformation("Nearby target is blacklisted; clearing it.");
                        input.PressClearTarget();
                        wait.Update(token);
                        continue;
                    }

                    if (bits.Target())
                        return;
                }

                wait.Update(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Session stopped or another goal took over after target acquisition.
        }
        catch (Exception ex)
        {
            Volatile.Write(ref workerFailure, ex);
        }
    }

    private void StopWorker()
    {
        cancellation?.Cancel();
        if (worker != null && Thread.CurrentThread != worker)
            worker.Join();

        worker = null;
        cancellation?.Dispose();
        cancellation = null;
    }
}
