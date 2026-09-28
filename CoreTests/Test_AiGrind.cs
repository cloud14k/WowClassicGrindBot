using Core.Decision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedLib.NpcFinder;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CoreTests;

/// <summary>Runs the production AI-controlled target, combat and loot flow for one kill.</summary>
internal static class Test_AiGrind
{
    private const int TargetTimeoutMs = 90_000;
    private const int LootTimeoutMs = 90_000;
    private const int OverallTimeoutMs = 600_000;
    private const int PollIntervalMs = 100;

    public static async Task RunAsync(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;
        if (!TryParseArguments(args, out string aiName, out Uri endpoint, out string error))
        {
            logger.LogError("AI live test arguments invalid: {Reason}", error);
            logger.LogInformation("Usage: ai-test --ai-name <name> --endpoint <predict-url> [--offline]");
            return;
        }

        logger.LogInformation(
            "AI live grind starting: AI={AiName} Endpoint={Endpoint} Goal=AcquireOneHostile-Kill-Loot; settings override enabled",
            aiName, endpoint.GetLeftPart(UriPartial.Path));

        DecisionConfiguration overrideSettings = new()
        {
            Mode = DecisionMode.AI,
            AllowFlee = false,
            Laya = new LayaSettings
            {
                BaseUrl = endpoint.ToString(),
                TimeoutMs = 300_000,
                MinimumConfidence = 0,
                MinimumIntervalMs = 400
            }
        };

        if (!ProductionTestSession.TryCreate(logger, loggerFactory, useDxgi,
            out ProductionTestSession? session, out string setupError, overrideSettings))
        {
            logger.LogError("AI live test initialization failed: {Reason}", setupError);
            return;
        }

        using (session!)
        {
            if (!ValidateStartingState(session!, logger))
                return;

            int targetGuid = 0;
            int killedGuid = 0;
            int killCountAtStart = session.Agent.SessionStat.Kills;
            bool playerDied = false;
            Action killHandler = () =>
            {
                int dead = session.CombatLog.DeadGuid.Value;
                if (targetGuid != 0 && dead == targetGuid)
                    killedGuid = dead;
            };
            Action playerDeathHandler = () => playerDied = true;

            session.CombatLog.KillCredit += killHandler;
            session.CombatLog.PlayerDeath += playerDeathHandler;
            Stopwatch overall = Stopwatch.StartNew();
            Stopwatch targetWait = Stopwatch.StartNew();
            Stopwatch lootWait = new();

            try
            {
                Console.WriteLine("AI LIVE: enabling production GoapAgent with the command-line endpoint");
                session.NpcNameTargeting.ChangeNpcType(NpcNames.Enemy);
                session.Agent.Active = true;

                while (!session.Environment.Cancellation.IsCancellationRequested &&
                    overall.ElapsedMilliseconds < OverallTimeoutMs)
                {
                    if (playerDied || session.Bits.Dead())
                    {
                        logger.LogError("AI live test failed: player died before the one-kill loot cycle completed");
                        return;
                    }

                    if (targetGuid == 0 && ProductionTestSession.HasValidTarget(session.Bits))
                    {
                        targetGuid = session.PlayerReader.TargetGuid;
                        Console.WriteLine(
                            $"AI LIVE TARGET: {session.AddonReader.TargetName} GUID={targetGuid} " +
                            $"HP={session.PlayerReader.TargetHealth()} level={session.PlayerReader.TargetLevel}");
                        logger.LogInformation("AI live phase entered: TargetAcquired AI={AiName} TargetGuid={TargetGuid}",
                            aiName, targetGuid);
                    }

                    if (targetGuid == 0 && targetWait.ElapsedMilliseconds >= TargetTimeoutMs)
                    {
                        logger.LogError("AI live test failed: no hostile target acquired in {TimeoutMs} ms", TargetTimeoutMs);
                        return;
                    }

                    if (targetGuid != 0 && killedGuid == 0 &&
                        session.PlayerReader.TargetGuid != 0 &&
                        session.PlayerReader.TargetGuid != targetGuid &&
                        ProductionTestSession.HasValidTarget(session.Bits))
                    {
                        logger.LogError("AI live test failed: target changed before the first target died. Original={Original}, Current={Current}",
                            targetGuid, session.PlayerReader.TargetGuid);
                        return;
                    }

                    if (targetGuid != 0 && killedGuid == targetGuid && !lootWait.IsRunning)
                    {
                        lootWait.Start();
                        Console.WriteLine($"AI LIVE KILL: confirmed GUID={targetGuid}; waiting for production loot completion");
                        logger.LogInformation("AI live phase entered: TargetKilled AI={AiName} TargetGuid={TargetGuid}",
                            aiName, targetGuid);
                    }

                    bool lootComplete = killedGuid == targetGuid &&
                        session.Agent.State.RecentlyLooted.Contains(targetGuid) &&
                        session.Agent.State.LootableCorpseCount == 0 &&
                        !session.Bits.LootFrameShown();
                    if (lootComplete)
                    {
                        Console.WriteLine($"AI LIVE LOOT: verified GUID={targetGuid}");
                        Console.WriteLine("AI LIVE PASS: one hostile target killed and looted");
                        logger.LogInformation(
                            "AI live grind passed: AI={AiName} TargetGuid={TargetGuid} Kills={KillCount} DurationMs={DurationMs}",
                            aiName, targetGuid, session.Agent.SessionStat.Kills - killCountAtStart,
                            overall.ElapsedMilliseconds);
                        Environment.ExitCode = 0;
                        return;
                    }

                    if (lootWait.IsRunning && lootWait.ElapsedMilliseconds >= LootTimeoutMs)
                    {
                        AIServiceSnapshot status = session.Session.GetRequiredService<AIServiceStatus>().Current;
                        logger.LogError(
                            "AI live test failed: loot did not complete in {TimeoutMs} ms. LastAction={Action} LastError={LastError} LootableCorpses={LootableCorpseCount}",
                            LootTimeoutMs, status.CurrentAction, status.LastError,
                            session.Agent.State.LootableCorpseCount);
                        return;
                    }

                    if (session.Agent.SessionStat.Kills - killCountAtStart > 1)
                    {
                        logger.LogError("AI live test failed: more than one mob was killed before loot verification");
                        return;
                    }

                    Thread.Sleep(PollIntervalMs);
                }

                AIServiceSnapshot finalStatus = session.Session.GetRequiredService<AIServiceStatus>().Current;
                logger.LogError(
                    "AI live test failed: overall timeout. TargetGuid={TargetGuid} KilledGuid={KilledGuid} LastAction={Action} LastError={LastError}",
                    targetGuid, killedGuid, finalStatus.CurrentAction, finalStatus.LastError);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI live test failed with an unexpected error");
            }
            finally
            {
                session.Agent.Active = false;
                session.Input.Reset();
                session.TargetFinder.Reset();
                session.CombatLog.KillCredit -= killHandler;
                session.CombatLog.PlayerDeath -= playerDeathHandler;
                logger.LogInformation("AI live test stopped: AI={AiName} ElapsedMs={ElapsedMs}",
                    aiName, overall.ElapsedMilliseconds);
            }
        }
    }

    private static bool ValidateStartingState(ProductionTestSession session, ILogger logger)
    {
        if (!session.Config.Loot)
        {
            logger.LogError("AI live test requires Loot enabled in the selected class profile");
            return false;
        }
        if (session.Bits.Dead() || session.Bits.Combat() || session.Bits.Target_Combat())
        {
            logger.LogError("AI live test requires a living character outside combat");
            return false;
        }
        if (session.Bits.Target())
        {
            logger.LogError("AI live test requires no target selected at start; clear the current target and retry");
            return false;
        }
        logger.LogInformation("AI live preflight passed: Profile={Profile} Class={Class} Mode={Mode} Loot={Loot}",
            session.ProfilePath, session.PlayerReader.Class, session.Config.Mode, session.Config.Loot);
        return true;
    }

    private static bool TryParseArguments(string[] args, out string aiName, out Uri endpoint, out string error)
    {
        aiName = string.Empty;
        endpoint = null;
        error = string.Empty;
        for (int index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Missing value for {args[index]}";
                return false;
            }

            string value = args[++index];
            switch (args[index - 1].ToLowerInvariant())
            {
                case "--ai-name":
                    aiName = value.Trim();
                    break;
                case "--endpoint":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint) ||
                        endpoint.Scheme is not ("http" or "https"))
                    {
                        error = "--endpoint must be an absolute HTTP or HTTPS URL";
                        return false;
                    }
                    break;
                default:
                    error = $"Unknown option: {args[index - 1]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(aiName))
        {
            error = "--ai-name is required";
            return false;
        }
        if (endpoint is null || !endpoint.AbsolutePath.TrimEnd('/').EndsWith("/predict", StringComparison.OrdinalIgnoreCase))
        {
            error = "--endpoint must point to the Laya /predict route";
            return false;
        }
        return true;
    }
}
