using Core;
using Core.GOAP;
using Core.Goals;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using SharedLib;
using SharedLib.NpcFinder;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Live integration test for the production target -> pull -> combat path.
/// The test owns orchestration and observation only. Pull input, approach,
/// casting, pet attack, and the success state all come from production code.
/// </summary>
internal static class Test_Pull
{
    private const int PreflightTimeoutMs = 5000;
    private const int TargetTimeoutMs = 10_000;
    private const int PullTimeoutMs = 20_000;
    private const int UpdateIntervalMs = 50;
    private const int AddonStallTimeoutMs = 2500;

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;

        if (!TryParseCommand(args, out string parseError))
        {
            PrintUsage(parseError);
            return;
        }

        GameTestEnvironment? environment = null;
        ServiceProvider? pullServices = null;
        IServiceScope? pullScope = null;
        TargetFinder? targetFinder = null;
        NpcNameTargeting? npcNameTargeting = null;
        ConfigurableInput? input = null;
        PullTargetGoal? pullGoal = null;
        StopMoving? stopMoving = null;
        CancellationTokenSource<GoapAgent>? goalCancellation = null;
        CancellationTokenSource? updateCancellation = null;
        Thread? updateThread = null;
        IWowScreen? screen = null;
        NpcNameFinder? npcNameFinder = null;
        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            Console.WriteLine("PULL TEST");
            Console.WriteLine("Phase 1: Initializing addon/bindings");

            if (!GameTestEnvironment.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out environment,
                    out string environmentReason))
            {
                PullFail($"Addon state did not update ({environmentReason})");
                return;
            }

            IServiceProvider root = environment.Services;
            screen = root.GetRequiredService<IWowScreen>();
            AddonReader addonReader = root.GetRequiredService<AddonReader>();
            KeyBindingsReader keyBindingsReader = root.GetRequiredService<KeyBindingsReader>();
            WowProcessInput flushInput = root.GetRequiredService<WowProcessInput>();
            npcNameFinder = root.GetRequiredService<NpcNameFinder>();
            AddonBits bits = root.GetRequiredService<AddonBits>();
            PlayerReader playerReader = root.GetRequiredService<PlayerReader>();
            environment.MarkReaderGraphInitialized();

            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                environment.Cancellation.Cancel();
                goalCancellation?.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            bool readersReady = AddonRefreshHelper.RefreshAddonAndWaitForReaders(
                screen,
                addonReader,
                flushInput,
                keyBindingsReader,
                environment.Cancellation.Token,
                AddonRefreshHelper.DefaultTimeoutMs,
                null,
                out AddonRefreshStats refreshStats,
                out string keyBindingsError);

            logger.LogInformation(
                "PULL INITIALIZATION refresh: Ready={Ready}; Updates={UpdateCount}; " +
                "Frequency={UpdatesPerSecond:F1}/s; DataReady={DataReady}; " +
                "KeyBindingsInitialized={KeyBindingsInitialized}; ExpectedCount={ExpectedCount}; " +
                "ReceivedCount={ReceivedCount}; FullResets={FullResetCount}; " +
                "ExpectedRefreshResets={ExpectedRefreshResetCount}; LastResetReason={LastResetReason}; " +
                "GlobalTime={GlobalTime}; BindingRaw={BindingRaw}; " +
                "SpellBookInitialized={SpellBookInitialized}; SpellBookRaw={SpellBookRaw}; " +
                "TextureInitialized={TextureInitialized}; TextureRaw={TextureRaw}",
                readersReady,
                refreshStats.UpdateCount,
                refreshStats.UpdatesPerSecond,
                addonReader.DataReady.IsSet,
                keyBindingsReader.IsInitialized,
                keyBindingsReader.ExpectedCount,
                keyBindingsReader.ReceivedCount,
                refreshStats.FullResetCount,
                refreshStats.ExpectedRefreshResetCount,
                refreshStats.LastResetReason,
                refreshStats.GlobalTime,
                refreshStats.BindingQueueRaw,
                refreshStats.SpellBookInitialized,
                refreshStats.SpellBookQueueRaw,
                refreshStats.TextureInitialized,
                refreshStats.TextureQueueRaw);

            if (!readersReady)
            {
                PullFail($"Addon state did not update ({keyBindingsError})");
                return;
            }

            // The official refresh must be the first reader-driving operation
            // in this test. Once the queue readers are ready, confirm the live
            // player fields needed to select the class profile.
            if (!WaitForPlayerData(
                    screen,
                    addonReader,
                    playerReader,
                    environment.Cancellation.Token,
                    out string playerDataError))
            {
                PullFail($"Addon state did not update ({playerDataError})");
                return;
            }

            if (!TryLoadClassConfiguration(
                    root,
                    playerReader,
                    out ClassConfiguration classConfig,
                    out string profilePath,
                    out string profileError))
            {
                PullFail($"Pull action could not be resolved ({profileError})");
                return;
            }

            if (classConfig.Pull.Sequence.Length == 0 ||
                !classConfig.Pull.Sequence.Any(CanResolveAction))
            {
                PullFail($"Pull action could not be resolved (profile '{profilePath}' has no usable Pull action)");
                return;
            }

            Console.WriteLine("PULL INITIALIZATION");
            Console.WriteLine($"Profile: {profilePath}");
            Console.WriteLine($"KeyBindingsInitialized: {keyBindingsReader.IsInitialized}");
            Console.WriteLine($"TargetNearestTarget: {FormatAction(classConfig.TargetNearestTarget)}");
            Console.WriteLine($"InteractMouseOver: {FormatAction(classConfig.InteractMouseOver)}");
            Console.WriteLine($"AutoAttack: {FormatAction(classConfig.AutoAttack)}");
            Console.WriteLine($"PetAttack: {FormatAction(classConfig.PetAttack)}");
            Console.WriteLine($"Pull Actions: {FormatActions(classConfig.Pull.Sequence)}");

            ServiceCollection registrations = new();
            registrations.AddScoped<ClassConfiguration>(_ => classConfig);
            pullServices = (ServiceProvider)GoalFactory.Create(registrations, root, classConfig);
            pullScope = pullServices.CreateScope();

            IServiceProvider session = pullScope.ServiceProvider;
            targetFinder = session.GetRequiredService<TargetFinder>();
            npcNameTargeting = session.GetRequiredService<NpcNameTargeting>();
            input = session.GetRequiredService<ConfigurableInput>();
            pullGoal = session.GetRequiredService<IEnumerable<GoapGoal>>()
                .OfType<PullTargetGoal>()
                .Single();
            stopMoving = session.GetRequiredService<StopMoving>();
            CombatLog combatLog = session.GetRequiredService<CombatLog>();
            goalCancellation = session.GetRequiredService<CancellationTokenSource<GoapAgent>>();

            updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                environment.Cancellation.Token,
                goalCancellation.Token);
            updateThread = StartUpdateLoop(
                screen,
                addonReader,
                npcNameFinder,
                updateCancellation.Token);

            Console.WriteLine("Phase 2: Acquiring enemy target");
            npcNameTargeting.ChangeNpcType(NpcNames.Enemy);

            CancellationToken token = goalCancellation.Token;
            bool targetReady = HasValidTarget(bits);
            Stopwatch targetTimer = Stopwatch.StartNew();
            while (!targetReady &&
                !token.IsCancellationRequested &&
                targetTimer.ElapsedMilliseconds < TargetTimeoutMs)
            {
                targetFinder.Search(NpcNames.Enemy, bits.Target_NotDead, token);
                targetReady = HasValidTarget(bits);

                if (!targetReady)
                    token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            if (!targetReady)
            {
                PullFail("No valid enemy target", bits, playerReader, combatLog, null, targetTimer.ElapsedMilliseconds);
                return;
            }

            int targetGuid = playerReader.TargetGuid;
            PrintTargetAcquired(addonReader, playerReader, bits);

            Console.WriteLine("Phase 3: Starting production pull");
            PullSnapshot before = Capture(playerReader, bits);
            Console.WriteLine("BEFORE PULL");
            PrintSnapshot(before);
            Console.WriteLine($"Pull Action: {FormatActions(classConfig.Pull.Sequence)}");

            pullGoal.OnEnter();

            Console.WriteLine("Phase 4: Waiting for combat confirmation");
            Stopwatch pullTimer = Stopwatch.StartNew();
            PullSnapshot previous = before;
            string? pullAction = null;
            int lastClickedKey = KeyAction.LastKeyClicked();
            long lastGlobalTime = addonReader.GlobalTime.Value;
            Stopwatch addonStallTimer = Stopwatch.StartNew();
            bool pullPassed = false;

            while (!token.IsCancellationRequested &&
                pullTimer.ElapsedMilliseconds < PullTimeoutMs)
            {
                pullGoal.Update();

                PullSnapshot current = Capture(playerReader, bits);
                int clickedKey = KeyAction.LastKeyClicked();
                string? clickedAction = ResolveClickedAction(classConfig, clickedKey);
                if (clickedAction != null &&
                    (clickedKey != lastClickedKey || pullAction == null))
                {
                    pullAction = clickedAction;
                    Console.WriteLine($"Pull action: {pullAction}");
                }

                // LastKeyClicked is a production-wide diagnostic value, not an
                // event stream. Remember the value after observing it so one
                // held/repeated production action is reported once rather than
                // once per tester update.
                lastClickedKey = clickedKey;

                PrintChanges(previous, current);
                previous = current;

                if (addonReader.GlobalTime.Value != lastGlobalTime)
                {
                    lastGlobalTime = addonReader.GlobalTime.Value;
                    addonStallTimer.Restart();
                }

                if (!bits.Target() || playerReader.TargetGuid != targetGuid)
                {
                    PullFail("Target was lost during pull", bits, playerReader, combatLog,
                        pullAction, pullTimer.ElapsedMilliseconds);
                    return;
                }

                // This is the same pulled state used by GoapAgent.UpdateWorldState.
                if (IsProductionPullComplete(bits, combatLog, playerReader))
                {
                    pullPassed = true;
                    break;
                }

                if (addonStallTimer.ElapsedMilliseconds >= AddonStallTimeoutMs)
                {
                    PullFail("Addon state did not update", bits, playerReader, combatLog,
                        pullAction, pullTimer.ElapsedMilliseconds);
                    return;
                }

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            PullSnapshot after = Capture(playerReader, bits);
            if (pullPassed)
            {
                Console.WriteLine("PULL PASS");
                Console.WriteLine($"Target Name: {addonReader.TargetName}");
                Console.WriteLine($"Target ID: {playerReader.TargetId}");
                Console.WriteLine($"Pull Action: {pullAction ?? FormatActions(classConfig.Pull.Sequence)}");
                Console.WriteLine($"Player In Combat: {after.PlayerInCombat}");
                Console.WriteLine($"Target In Combat: {after.TargetInCombat}");
                Console.WriteLine($"Auto Attacking: {after.AutoAttacking}");
                Console.WriteLine($"Target Health Before: {before.TargetHealth}");
                Console.WriteLine($"Target Health After: {after.TargetHealth}");
                Console.WriteLine($"Elapsed: {pullTimer.ElapsedMilliseconds} ms");
                Environment.ExitCode = 0;
            }
            else if (environment.Cancellation.IsCancellationRequested ||
                token.IsCancellationRequested)
            {
                Console.WriteLine("PULL STOPPED (Ctrl+C)");
                Environment.ExitCode = 130;
            }
            else if (!playerReader.WithInPullRange() && !after.PlayerInCombat)
            {
                PullFail("Target out of range and production approach failed", bits,
                    playerReader, combatLog, pullAction, pullTimer.ElapsedMilliseconds);
            }
            else
            {
                PullFail("Pull action was executed but combat was not entered", bits,
                    playerReader, combatLog, pullAction, pullTimer.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            PullFail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (cancelHandler is not null)
                Console.CancelKeyPress -= cancelHandler;

            // Stop movement through the production component. Do not clear the target;
            // keeping it selected makes the result inspectable in WoW after the test.
            try { pullGoal?.OnExit(); } catch (Exception ex) { logger.LogWarning(ex, "PullTargetGoal.OnExit failed"); }
            try { stopMoving?.Stop(); } catch (Exception ex) { logger.LogWarning(ex, "StopMoving.Stop failed"); }
            try { input?.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "ConfigurableInput.Reset failed"); }
            try { targetFinder?.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "TargetFinder.Reset failed"); }
            try { npcNameTargeting?.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameTargeting reset failed"); }
            try { npcNameFinder?.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameFinder reset failed"); }
            try { goalCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Pull cancellation failed"); }
            try { updateCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Pull update cancellation failed"); }
            try { environment?.Cancellation.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Environment cancellation failed"); }

            if (updateThread is not null && updateThread.IsAlive)
                updateThread.Join(1000);

            try
            {
                if (screen is not null)
                    screen.Enabled = false;
            }
            catch (Exception ex) { logger.LogWarning(ex, "Screen disable failed"); }

            pullScope?.Dispose();
            pullServices?.Dispose();
            updateCancellation?.Dispose();
            environment?.Dispose();
        }
    }

    private static bool TryLoadClassConfiguration(
        IServiceProvider root,
        PlayerReader playerReader,
        out ClassConfiguration config,
        out string profilePath,
        out string error)
    {
        config = null!;
        profilePath = string.Empty;

        DataConfig dataConfig = root.GetRequiredService<DataConfig>();
        if (!Directory.Exists(dataConfig.Class))
        {
            error = $"class profile directory does not exist: {dataConfig.Class}";
            return false;
        }

        string className = playerReader.Class.ToString();
        int level = Math.Max(1, playerReader.Level.Value);
        List<ProfileCandidate> candidates = [];

        foreach (string file in Directory.EnumerateFiles(dataConfig.Class, "*.json*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (!fileName.StartsWith(className + "_", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                ClassConfiguration? candidate = JsonConvert.DeserializeObject<ClassConfiguration>(File.ReadAllText(file));
                if (candidate?.Pull.Sequence.Length > 0)
                {
                    candidates.Add(new(candidate, file, ParseProfileLevel(fileName)));
                }
            }
            catch
            {
                // The production profile loader will report a selected profile's error;
                // malformed profiles are not candidates for an automatic live test.
            }
        }

        if (candidates.Count == 0)
        {
            error = $"no Pull profile found for {className}";
            return false;
        }

        ProfileCandidate selected = candidates
            .Where(x => x.StartLevel <= level)
            .OrderByDescending(x => x.StartLevel)
            .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (selected.Config is null)
        {
            selected = candidates
                .OrderBy(x => x.StartLevel)
                .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        try
        {
            // Initialise is the same production profile initialization used by
            // BotController before GoalFactory constructs ConfigurableInput/goals.
            selected.Config.FileName = Path.GetRelativePath(dataConfig.Class, selected.File);
            selected.Config.Initialise(root, new Dictionary<int, string>());
            config = selected.Config;
            profilePath = selected.File;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"profile '{selected.File}' failed ClassConfiguration.Initialise: {ex.Message}";
            return false;
        }
    }

    private static int ParseProfileLevel(string fileName)
    {
        int separator = fileName.IndexOf('_');
        if (separator < 0)
            return 0;

        string suffix = fileName[(separator + 1)..];
        int end = 0;
        while (end < suffix.Length && char.IsDigit(suffix[end]))
            end++;

        return end > 0 && int.TryParse(suffix[..end], out int level) ? level : 0;
    }

    private static bool CanResolveAction(KeyAction action) =>
        action.BaseAction || action.ConsoleKey != ConsoleKey.NoName;

    private static string? ResolveClickedAction(ClassConfiguration config, int clickedKey)
    {
        if (clickedKey == (int)ConsoleKey.NoName)
            return null;

        foreach (KeyAction action in config.Pull.Sequence)
        {
            if (action.ConsoleKeyFormHash == clickedKey ||
                (int)action.ConsoleKey == clickedKey % 1000)
                return action.Name;
        }

        foreach (KeyAction action in new[] { config.AutoAttack, config.PetAttack, config.Approach })
        {
            if (action.ConsoleKeyFormHash == clickedKey ||
                (int)action.ConsoleKey == clickedKey % 1000)
                return action.Name;
        }

        return null;
    }

    private static string FormatActions(IEnumerable<KeyAction> actions) =>
        string.Join(" -> ", actions.Select(FormatAction));

    private static string FormatAction(KeyAction action) =>
        $"{action.Name} [{action.BindingID}] {action.Modifier.ToPrefix()}{action.ConsoleKey}";

    private static void PrintTargetAcquired(
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits)
    {
        Console.WriteLine("TARGET ACQUIRED");
        Console.WriteLine($"Target Name: {addonReader.TargetName}");
        Console.WriteLine($"Target ID: {playerReader.TargetId}");
        Console.WriteLine($"Target GUID: {playerReader.TargetGuid}");
        Console.WriteLine($"Level: {playerReader.TargetLevel}");
        Console.WriteLine($"Health: {playerReader.TargetHealth()}/{playerReader.TargetMaxHealth()} ({playerReader.TargetHealthPercent()}%)");
        Console.WriteLine($"Hostile: {bits.Target_Hostile()}");
        Console.WriteLine($"Dead: {bits.Target_Dead()}");
    }

    private static bool IsProductionPullComplete(
        AddonBits bits,
        CombatLog combatLog,
        PlayerReader playerReader) =>
        combatLog.PlayerOrPetCombat() &&
        bits.Target_Combat() &&
        combatLog.ToPull.Contains(playerReader.TargetGuid);

    private static PullSnapshot Capture(PlayerReader playerReader, AddonBits bits) =>
        new(
            bits.Target(),
            bits.Target_NotDead(),
            bits.Target_Hostile(),
            bits.Combat(),
            bits.Target_Combat(),
            bits.Auto_Attack(),
            playerReader.IsMeleeSwingingDefault(),
            playerReader.TargetHealth(),
            playerReader.TargetGuid,
            playerReader.MinRange(),
            playerReader.MaxRange());

    private static void PrintSnapshot(PullSnapshot snapshot)
    {
        Console.WriteLine($"Target: {snapshot.HasTarget}");
        Console.WriteLine($"Target HP: {snapshot.TargetHealth}");
        Console.WriteLine($"PlayerInCombat: {snapshot.PlayerInCombat}");
        Console.WriteLine($"TargetInCombat: {snapshot.TargetInCombat}");
        Console.WriteLine($"AutoAttacking: {snapshot.AutoAttacking}");
        Console.WriteLine($"MeleeSwinging: {snapshot.MeleeSwinging}");
        Console.WriteLine($"Distance/Range: {snapshot.MinRange}-{snapshot.MaxRange} yd");
    }

    private static void PrintChanges(PullSnapshot previous, PullSnapshot current)
    {
        if (previous.PlayerInCombat != current.PlayerInCombat)
            Console.WriteLine($"PlayerInCombat: {previous.PlayerInCombat} -> {current.PlayerInCombat}");
        if (previous.TargetInCombat != current.TargetInCombat)
            Console.WriteLine($"TargetInCombat: {previous.TargetInCombat} -> {current.TargetInCombat}");
        if (previous.AutoAttacking != current.AutoAttacking)
            Console.WriteLine($"AutoAttacking: {previous.AutoAttacking} -> {current.AutoAttacking}");
        if (previous.MeleeSwinging != current.MeleeSwinging)
            Console.WriteLine($"MeleeSwinging: {previous.MeleeSwinging} -> {current.MeleeSwinging}");
        if (previous.TargetHealth != current.TargetHealth)
            Console.WriteLine($"Target HP: {previous.TargetHealth} -> {current.TargetHealth}");
        if (previous.MinRange != current.MinRange || previous.MaxRange != current.MaxRange)
            Console.WriteLine(
                $"Distance/Range: {previous.MinRange}-{previous.MaxRange} -> " +
                $"{current.MinRange}-{current.MaxRange} yd");
    }

    private static Thread StartUpdateLoop(
        IWowScreen screen,
        AddonReader addonReader,
        NpcNameFinder npcNameFinder,
        CancellationToken token)
    {
        Thread thread = new(() =>
        {
            while (!token.IsCancellationRequested)
            {
                screen.Update();
                addonReader.Update();
                if (screen.Enabled)
                    npcNameFinder.Update();

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }
        })
        {
            IsBackground = true,
            Name = "CoreTests.Pull.LiveUpdates"
        };

        screen.Enabled = true;
        thread.Start();
        return thread;
    }

    private static bool WaitForPlayerData(
        IWowScreen screen,
        AddonReader addonReader,
        PlayerReader playerReader,
        CancellationToken token,
        out string error)
    {
        Stopwatch timer = Stopwatch.StartNew();
        screen.Enabled = true;

        while (timer.ElapsedMilliseconds < PreflightTimeoutMs && !token.IsCancellationRequested)
        {
            screen.Update();
            addonReader.Update();

            if (addonReader.DataReady.IsSet &&
                playerReader.UIMapId.Value > 0 &&
                playerReader.HealthMax() > 0)
            {
                error = string.Empty;
                return true;
            }

            token.WaitHandle.WaitOne(UpdateIntervalMs);
        }

        error =
            $"live player data was not ready after {PreflightTimeoutMs} ms " +
            $"(DataReady={addonReader.DataReady.IsSet}, UIMapId={playerReader.UIMapId.Value})";
        return false;
    }

    private static bool HasValidTarget(AddonBits bits) =>
        bits.Target() && bits.Target_NotDead() && bits.Target_Hostile();

    private static void PullFail(string reason) =>
        Console.WriteLine($"PULL FAIL: {reason}");

    private static void PullFail(
        string reason,
        AddonBits bits,
        PlayerReader playerReader,
        CombatLog combatLog,
        string? action,
        long timeoutMs)
    {
        Console.WriteLine($"PULL FAIL: {reason}");
        Console.WriteLine($"Target(): {bits.Target()}");
        Console.WriteLine($"Target_NotDead(): {bits.Target_NotDead()}");
        Console.WriteLine($"Target_Hostile(): {bits.Target_Hostile()}");
        Console.WriteLine($"PlayerInCombat: {bits.Combat()}");
        Console.WriteLine($"TargetInCombat: {bits.Target_Combat()}");
        Console.WriteLine($"AutoAttacking: {bits.Auto_Attack()}");
        Console.WriteLine($"Target HP: {playerReader.TargetHealth()}");
        Console.WriteLine($"Action: {action ?? "<none>"}");
        Console.WriteLine($"ToPullContainsTarget: {combatLog.ToPull.Contains(playerReader.TargetGuid)}");
        Console.WriteLine($"Timeout: {timeoutMs} ms");
    }

    private static bool TryParseCommand(string[] args, out string error)
    {
        if (args.Length == 1 && args[0].Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            error = string.Empty;
            return true;
        }

        error = "Expected: pull enemy.";
        return false;
    }

    private static void PrintUsage(string error)
    {
        Console.WriteLine($"PULL: INVALID ({error})");
        Console.WriteLine("Usage:");
        Console.WriteLine("  pull enemy");
    }

    private readonly record struct PullSnapshot(
        bool HasTarget,
        bool TargetNotDead,
        bool TargetHostile,
        bool PlayerInCombat,
        bool TargetInCombat,
        bool AutoAttacking,
        bool MeleeSwinging,
        int TargetHealth,
        int TargetGuid,
        int MinRange,
        int MaxRange);

    private readonly record struct ProfileCandidate(
        ClassConfiguration Config,
        string File,
        int StartLevel);
}
