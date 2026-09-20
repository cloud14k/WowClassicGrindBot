using Core;
using Core.GOAP;
using Core.Goals;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.NpcFinder;

using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Live integration tests for the production NPC finder and target selection path.
/// This tester only selects the mode, drives the normal reader/finder update loop,
/// calls TargetFinder, and prints the production results.
/// </summary>
internal static class Test_Target
{
    private const int PreflightTimeoutMs = 5000;
    private const int ScanTimeoutMs = 5000;
    private const int TargetTimeoutMs = 10_000;
    private const int TargetConfirmationTimeoutMs = 2000;
    private const int UpdateIntervalMs = 50;

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;

        GameTestEnvironment? environment = null;
        ServiceProvider? targetServices = null;
        IServiceScope? targetScope = null;
        TargetFinder? targetFinder = null;
        NpcNameTargeting? npcNameTargeting = null;
        ConfigurableInput? input = null;
        CancellationTokenSource<GoapAgent>? targetCancellation = null;
        Thread? updateThread = null;
        CancellationTokenSource? updateCancellation = null;
        IWowScreen? screen = null;
        NpcNameFinder? npcNameFinder = null;
        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            if (!TryParseCommand(args, out TargetCommand command, out string parseError))
            {
                PrintUsage(parseError);
                return;
            }

            if (!GameTestEnvironment.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out environment,
                    out string environmentReason))
            {
                Fail(environmentReason);
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
                targetCancellation?.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            if (command.Kind == TargetCommandKind.ScanEnemy)
            {
                RunScan(
                    screen,
                    addonReader,
                    npcNameFinder,
                    environment.Cancellation.Token);
                Environment.ExitCode = 0;
                return;
            }

            if (!WaitForPlayerData(
                    screen,
                    addonReader,
                    playerReader,
                    environment.Cancellation.Token,
                    out string playerDataError))
            {
                Fail(playerDataError);
                return;
            }

            // Do not construct ConfigurableInput until this completes: its
            // constructor snapshots InteractMouseOver into WowProcessInput.
            logger.LogInformation(
                "Refreshing official DataToColor queues before ClassConfiguration.Initialise");
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
                "Addon refresh wait: Ready={Ready}; Addon updates during wait: {UpdateCount}; " +
                "Frequency={UpdatesPerSecond:F1}/s; DataReady={DataReady}; " +
                "KeyBindingsInitialized={KeyBindingsInitialized}; ExpectedCount={ExpectedCount}; " +
                "ReceivedCount={ReceivedCount}",
                readersReady,
                refreshStats.UpdateCount,
                refreshStats.UpdatesPerSecond,
                addonReader.DataReady.IsSet,
                keyBindingsReader.IsInitialized,
                keyBindingsReader.ExpectedCount,
                keyBindingsReader.ReceivedCount);

            if (!readersReady)
            {
                Fail(keyBindingsError);
                return;
            }

            // Use the same session graph as the bot for TargetFinder,
            // NpcNameTargeting, ConfigurableInput, and the production blacklist.
            ServiceCollection registrations = new();
            ClassConfiguration classConfig = new() { Mode = Mode.Grind };

            // Match BotController.InitialiseFromFile: resolve all KeyActions only
            // after the addon has delivered the complete in-game binding queue, and
            // before GoalFactory can construct ConfigurableInput. ConfigurableInput
            // copies InteractMouseOver into WowProcessInput in its constructor.
            classConfig.Initialise(root, new Dictionary<int, string>());

            registrations.AddScoped<ClassConfiguration>(_ => classConfig);
            targetServices = (ServiceProvider)GoalFactory.Create(
                registrations,
                root,
                classConfig);
            targetScope = targetServices.CreateScope();

            IServiceProvider session = targetScope.ServiceProvider;
            targetFinder = session.GetRequiredService<TargetFinder>();
            npcNameTargeting = session.GetRequiredService<NpcNameTargeting>();
            input = session.GetRequiredService<ConfigurableInput>();
            WowProcessInput wowProcessInput = session.GetRequiredService<WowProcessInput>();
            targetCancellation = session.GetRequiredService<CancellationTokenSource<GoapAgent>>();

            logger.LogInformation(
                "Target input initialization: " +
                "KeyBindingsReader.IsInitialized={IsInitialized}; " +
                "TargetNearestTarget.BindingID={TargetBindingID}; " +
                "TargetNearestTarget.ConsoleKey={TargetKey}; " +
                "TargetNearestTarget.Modifier={TargetModifier}; " +
                "InteractMouseOver.BindingID={InteractBindingID}; " +
                "InteractMouseOver.ConsoleKey={InteractKey}; " +
                "InteractMouseOver.Modifier={InteractModifier}; " +
                "WowProcessInput.InteractMouseover={InputInteractKey}; " +
                "WowProcessInput.InteractMouseoverModifier={InputInteractModifier}",
                keyBindingsReader.IsInitialized,
                classConfig.TargetNearestTarget.BindingID,
                classConfig.TargetNearestTarget.ConsoleKey,
                classConfig.TargetNearestTarget.Modifier,
                classConfig.InteractMouseOver.BindingID,
                classConfig.InteractMouseOver.ConsoleKey,
                classConfig.InteractMouseOver.Modifier,
                wowProcessInput.InteractMouseover,
                wowProcessInput.InteractMouseoverModifier);

            // TargetFinder.WaitForUpdate relies on the normal screenshot thread. The
            // tester supplies only that production reader/finder tick; it does not
            // implement recognition, candidate selection, or mouse targeting.
            npcNameTargeting.ChangeNpcType(NpcNames.Enemy);
            updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                environment.Cancellation.Token,
                targetCancellation.Token);
            updateThread = StartUpdateLoop(
                screen,
                addonReader,
                npcNameFinder,
                updateCancellation.Token);

            Stopwatch timer = Stopwatch.StartNew();
            bool searchResult = false;
            while (!searchResult &&
                !environment.Cancellation.IsCancellationRequested &&
                !targetCancellation.IsCancellationRequested &&
                timer.ElapsedMilliseconds < TargetTimeoutMs)
            {
                // This is the exact validTarget delegate used by the formal
                // FollowRouteGoal call site: targetFinder.Search(..., bits.Target_NotDead, ...).
                searchResult = targetFinder.Search(
                    NpcNames.Enemy,
                    bits.Target_NotDead,
                    targetCancellation.Token);

                if (!searchResult)
                    targetCancellation.Token.WaitHandle.WaitOne(UpdateIntervalMs);
            }

            bool targetConfirmed = searchResult &&
                WaitForTargetConfirmation(
                    bits,
                    targetCancellation.Token,
                    TargetConfirmationTimeoutMs);

            if (searchResult && targetConfirmed)
            {
                PrintTargetPass(addonReader, playerReader, bits, npcNameFinder);
                Environment.ExitCode = 0;
            }
            else if (environment.Cancellation.IsCancellationRequested ||
                targetCancellation.IsCancellationRequested)
            {
                Console.WriteLine("TARGET STOPPED (Ctrl+C)");
                Environment.ExitCode = 130;
            }
            else
            {
                Console.WriteLine("TARGET FAIL");
                Console.WriteLine($"NpcNameFinder Found: {npcNameFinder.NpcCount}");
                Console.WriteLine($"TargetFinder.Search: {searchResult}");
                Console.WriteLine($"Has Target: {bits.Target()}");
                Console.WriteLine($"Target Not Dead: {bits.Target_NotDead()}");
                Console.WriteLine($"Target Hostile: {bits.Target_Hostile()}");
            }
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (cancelHandler is not null)
                Console.CancelKeyPress -= cancelHandler;

            // Reset only the finder/screen mode. The selected game target is left
            // selected because selecting it is the purpose of target enemy.
            try { targetFinder?.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "TargetFinder.Reset failed"); }
            try { npcNameTargeting?.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameTargeting reset failed"); }
            try { npcNameFinder?.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameFinder reset failed"); }
            try
            {
                if (screen is not null)
                    screen.Enabled = false;
            }
            catch (Exception ex) { logger.LogWarning(ex, "Screen disable failed"); }

            try { targetCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Target cancellation failed"); }
            try { updateCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Target update cancellation failed"); }
            try { environment?.Cancellation.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Environment cancellation failed"); }

            if (updateThread is not null && updateThread.IsAlive)
                updateThread.Join(1000);

            try { input?.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "ConfigurableInput.Reset failed"); }

            targetScope?.Dispose();
            targetServices?.Dispose();
            updateCancellation?.Dispose();
            environment?.Dispose();
        }
    }

    private static void RunScan(
        IWowScreen screen,
        AddonReader addonReader,
        NpcNameFinder npcNameFinder,
        CancellationToken token)
    {
        npcNameFinder.ChangeNpcType(NpcNames.Enemy);
        screen.Enabled = true;

        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ScanTimeoutMs && !token.IsCancellationRequested)
        {
            screen.Update();
            addonReader.Update();
            npcNameFinder.Update();
            token.WaitHandle.WaitOne(UpdateIntervalMs);
        }

        Console.WriteLine("TARGET SCAN");
        Console.WriteLine("Type: Enemy");
        Console.WriteLine($"Found: {npcNameFinder.NpcCount}");

        int index = 1;
        foreach (NpcPosition npc in npcNameFinder.Npcs)
        {
            Console.WriteLine($"Enemy #{index++}");
            Console.WriteLine($"ClickPoint: {Format(npc.ClickPoint)}");
            Console.WriteLine($"Rect: {Format(npc.Rect)}");
        }

        if (token.IsCancellationRequested)
            Console.WriteLine("SCAN STOPPED (Ctrl+C)");
        else
            Console.WriteLine("SCAN COMPLETE");
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
            Name = "CoreTests.Target.LiveUpdates"
        };

        thread.Start();
        return thread;
    }

    private static void PrintTargetPass(
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits,
        NpcNameFinder npcNameFinder)
    {
        Console.WriteLine("TARGET PASS");
        Console.WriteLine($"NpcNameFinder Found: {npcNameFinder.NpcCount}");
        Console.WriteLine($"Has Target: {bits.Target()}");
        Console.WriteLine($"Target Name: {addonReader.TargetName}");
        Console.WriteLine($"Target Health: {playerReader.TargetHealth()}/{playerReader.TargetMaxHealth()} ({playerReader.TargetHealthPercent()}%)");
        Console.WriteLine($"Target Level: {playerReader.TargetLevel}");
        Console.WriteLine($"Target Dead: {bits.Target_Dead()}");
        Console.WriteLine($"Target Hostile: {bits.Target_Hostile()}");
        Console.WriteLine($"Target GUID: {playerReader.TargetGuid}");
        Console.WriteLine($"Target ID: {playerReader.TargetId}");
    }

    private static bool WaitForTargetConfirmation(
        AddonBits bits,
        CancellationToken token,
        int timeoutMs)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!HasValidTarget(bits) &&
            !token.IsCancellationRequested &&
            timer.ElapsedMilliseconds < timeoutMs)
        {
            token.WaitHandle.WaitOne(UpdateIntervalMs);
        }

        return HasValidTarget(bits);
    }

    private static bool HasValidTarget(AddonBits bits) =>
        bits.Target() && bits.Target_NotDead() && bits.Target_Hostile();

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
            $"Live player data was not ready after {PreflightTimeoutMs} ms " +
            $"(DataReady={addonReader.DataReady.IsSet}, " +
            $"UIMapId={playerReader.UIMapId.Value}).";
        return false;
    }

    private static bool TryParseCommand(
        string[] args,
        out TargetCommand command,
        out string error)
    {
        command = default;
        error = string.Empty;

        if (args.Length == 2 &&
            args[0].Equals("scan", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            command = new(TargetCommandKind.ScanEnemy);
            return true;
        }

        if (args.Length == 1 && args[0].Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            command = new(TargetCommandKind.Enemy);
            return true;
        }

        error = "Expected: scan enemy or enemy.";
        return false;
    }

    private static string Format(Point point) =>
        $"({point.X},{point.Y})";

    private static string Format(Rectangle rect) =>
        $"({rect.X},{rect.Y},{rect.Width},{rect.Height})";

    private static void PrintUsage(string error)
    {
        Console.WriteLine($"TARGET: INVALID ({error})");
        Console.WriteLine("Usage:");
        Console.WriteLine("  target scan enemy");
        Console.WriteLine("  target enemy");
    }

    private static void Fail(string reason) =>
        Console.WriteLine($"TARGET FAIL ({reason})");

    private enum TargetCommandKind
    {
        ScanEnemy,
        Enemy
    }

    private readonly record struct TargetCommand(TargetCommandKind Kind);
}
