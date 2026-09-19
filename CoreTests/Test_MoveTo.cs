using Core;
using Core.Goals;
using Core.GOAP;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Live integration test for the production Navigation component.
/// The tester owns only command parsing, target construction, observation, and
/// the test timeout. All movement and steering are delegated to Navigation.
/// </summary>
internal static class Test_MoveTo
{
    private const int PreflightTimeoutMs = 5000;
    private const int NavigationTimeoutMs = 120_000;
    private const int UpdateIntervalMs = 100;
    private const int LogIntervalMs = 500;

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        Environment.ExitCode = 1;
        GameTestEnvironment? environment = null;
        ServiceProvider? navigationServices = null;
        IServiceScope? navigationScope = null;
        Navigation? navigation = null;
        StopMoving? stopMoving = null;
        ConfigurableInput? input = null;
        CancellationTokenSource<GoapAgent>? navigationCancellation = null;

        ConsoleCancelEventHandler? cancelHandler = null;

        try
        {
            if (!TryParseCommand(args, out MoveCommand command, out string parseError))
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
            IWowScreen screen = root.GetRequiredService<IWowScreen>();
            AddonReader addonReader = root.GetRequiredService<AddonReader>();
            PlayerReader playerReader = root.GetRequiredService<PlayerReader>();
            environment.MarkReaderGraphInitialized();

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

            if (!TryBuildTarget(command, playerReader, out MoveTarget target, out string targetError))
            {
                Fail(targetError);
                return;
            }

            // GoalFactory wires ConfigurableInput, PlayerDirection, StopMoving,
            // StuckDetector, IMountHandler, and Navigation exactly as the bot does.
            // The tester does not provide replacements for any of those components.
            ServiceCollection registrations = new();
            ClassConfiguration classConfig = new()
            {
                // No route/combat goal is started by this test. AttendedGrind keeps
                // GoalFactory's navigation dependencies lightweight while retaining
                // the production movement key configuration defaults.
                Mode = Mode.AttendedGrind
            };
            registrations.AddScoped<ClassConfiguration>(_ => classConfig);
            navigationServices = (ServiceProvider)GoalFactory.Create(
                registrations,
                root,
                classConfig);
            navigationScope = navigationServices.CreateScope();

            IServiceProvider session = navigationScope.ServiceProvider;
            navigation = session.GetRequiredService<Navigation>();
            stopMoving = session.GetRequiredService<StopMoving>();
            input = session.GetRequiredService<ConfigurableInput>();
            navigationCancellation = session.GetRequiredService<CancellationTokenSource<GoapAgent>>();

            bool destinationReached = false;
            bool noPathFound = false;

            void OnDestinationReached() => destinationReached = true;
            void OnNoPathFound() => noPathFound = true;

            navigation.OnDestinationReached += OnDestinationReached;
            navigation.OnNoPathFound += OnNoPathFound;

            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                environment.Cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                // All command forms converge here. Navigation itself converts map
                // points, computes paths, steers, follows them, and detects arrival.
                Vector3[] points = [target.NavigationPoint];
                navigation.SetWayPoints(points);

                Console.WriteLine($"MoveTo target ({target.Description})");
                Console.WriteLine($"Current WorldPos={Format(playerReader.WorldPos)} " +
                    $"Direction={playerReader.Direction:F3}");

                Stopwatch timer = Stopwatch.StartNew();
                long nextLogMs = 0;

                while (!destinationReached && !noPathFound &&
                    !environment.Cancellation.IsCancellationRequested &&
                    timer.ElapsedMilliseconds < NavigationTimeoutMs)
                {
                    // This is the same live update order used by the bot's addon thread.
                    screen.Update();
                    addonReader.Update();

                    // Use the production navigation tick; no movement or steering is
                    // performed by the tester itself.
                    navigation.Update(environment.Cancellation.Token);

                    if (timer.ElapsedMilliseconds >= nextLogMs)
                    {
                        Vector3 current = playerReader.WorldPos;
                        float distance = current.WorldDistanceXYTo(target.WorldPointForObservation);
                        logger.LogInformation(
                            "MoveTo current={Current} direction={Direction:F3} target={Target} distance={Distance:F2} waypoint={HasWaypoint} next={HasNext}",
                            Format(current), playerReader.Direction,
                            Format(target.WorldPointForObservation), distance,
                            navigation.HasWaypoint(), navigation.HasNext());
                        nextLogMs = timer.ElapsedMilliseconds + LogIntervalMs;
                    }

                    environment.Cancellation.Token.WaitHandle.WaitOne(UpdateIntervalMs);
                }

                if (destinationReached)
                {
                    Console.WriteLine("MoveTo: PASS (Navigation.OnDestinationReached)");
                    Environment.ExitCode = 0;
                }
                else if (noPathFound)
                {
                    Fail("Navigation.OnNoPathFound");
                }
                else if (environment.Cancellation.IsCancellationRequested)
                {
                    Console.WriteLine("MoveTo: STOPPED (Ctrl+C)");
                    Environment.ExitCode = 130;
                }
                else
                {
                    Fail($"Navigation timeout after {NavigationTimeoutMs} ms");
                }
            }
            finally
            {
                navigation.OnDestinationReached -= OnDestinationReached;
                navigation.OnNoPathFound -= OnNoPathFound;
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

            // Safety is deliberately layered on the formal components. Stop() releases
            // turn state, StopMovement() releases forward movement, StopMoving handles
            // the normal production stop path, and ConfigurableInput.Reset() is the
            // final key-state fallback.
            try { navigation?.Stop(); } catch (Exception ex) { logger.LogWarning(ex, "MoveTo Navigation.Stop failed"); }
            try { navigation?.StopMovement(); } catch (Exception ex) { logger.LogWarning(ex, "MoveTo Navigation.StopMovement failed"); }
            try { stopMoving?.Stop(); } catch (Exception ex) { logger.LogWarning(ex, "MoveTo StopMoving.Stop failed"); }
            try { input?.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "MoveTo ConfigurableInput.Reset failed"); }

            // Navigation owns a pathfinder thread whose token is session-scoped and
            // separate from the live reader cancellation token. Cancel it before
            // disposing the scope so the test process can terminate cleanly.
            try { navigationCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "MoveTo navigation cancellation failed"); }

            navigationScope?.Dispose();
            navigationServices?.Dispose();
            environment?.Dispose();
        }
    }

    private static bool TryBuildTarget(
        MoveCommand command,
        PlayerReader playerReader,
        out MoveTarget target,
        out string error)
    {
        switch (command.Kind)
        {
            case MoveKind.Forward:
            {
                Vector3 current = playerReader.WorldPos;
                Vector2 direction = DirectionCalculator.ToNormalRadianNoFlip(playerReader.Direction);
                Vector3 offset = new(direction.X * command.A, direction.Y * command.A, 0);

                // The target calculation is the only movement-specific calculation in
                // this tester. In the project's WoW direction convention this is the
                // actual forward vector; Navigation receives the resulting world point.
                Vector3 world = current + offset;
                target = new MoveTarget(world, world, $"forward {command.A.ToString("F2", CultureInfo.InvariantCulture)}");
                error = string.Empty;
                return true;
            }
            case MoveKind.World:
            {
                Vector3 world = new(command.A, command.B, command.C);
                target = new MoveTarget(world, world, $"world {Format(world)}");
                error = string.Empty;
                return true;
            }
            case MoveKind.Map:
            {
                Vector3 map = new(command.A, command.B, 0);
                Vector3 world = WorldMapAreaDB.ToWorld_FlipXY(map, playerReader.WorldMapArea);
                target = new MoveTarget(map, world, $"map {command.A.ToString("F2", CultureInfo.InvariantCulture)} {command.B.ToString("F2", CultureInfo.InvariantCulture)}");
                error = string.Empty;
                return true;
            }
            default:
                target = default;
                error = "Unknown moveto mode.";
                return false;
        }
    }

    private static bool TryParseCommand(
        string[] args,
        out MoveCommand command,
        out string error)
    {
        command = default;
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "Missing mode.";
            return false;
        }

        string mode = args[0].ToLowerInvariant();
        switch (mode)
        {
            case "forward" when args.Length == 2 && TryFloat(args[1], out float distance) && distance > 0:
                command = new MoveCommand(MoveKind.Forward, distance, 0, 0);
                return true;
            case "world" when args.Length == 4 &&
                TryFloat(args[1], out float worldX) &&
                TryFloat(args[2], out float worldY) &&
                TryFloat(args[3], out float worldZ):
                command = new MoveCommand(MoveKind.World, worldX, worldY, worldZ);
                return true;
            case "map" when args.Length == 3 &&
                TryFloat(args[1], out float mapX) &&
                TryFloat(args[2], out float mapY):
                command = new MoveCommand(MoveKind.Map, mapX, mapY, 0);
                return true;
            default:
                error = "Expected: forward <distance>, world <x> <y> <z>, or map <x> <y>.";
                return false;
        }
    }

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        float.IsFinite(result);

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

            token.WaitHandle.WaitOne(50);
        }

        error =
            $"Live player data was not ready after {PreflightTimeoutMs} ms " +
            $"(DataReady={addonReader.DataReady.IsSet}, UIMapId={playerReader.UIMapId.Value}).";
        return false;
    }

    private static string Format(Vector3 value) =>
        $"({value.X:F3},{value.Y:F3},{value.Z:F3})";

    private static void PrintUsage(string error)
    {
        Console.WriteLine($"MoveTo: INVALID ({error})");
        Console.WriteLine("Usage:");
        Console.WriteLine("  moveto forward <distance>");
        Console.WriteLine("  moveto world <x> <y> <z>");
        Console.WriteLine("  moveto map <x> <y>");
    }

    private static void Fail(string reason) =>
        Console.WriteLine($"MoveTo: FAIL ({reason})");

    private enum MoveKind
    {
        Forward,
        World,
        Map
    }

    private readonly record struct MoveCommand(MoveKind Kind, float A, float B, float C);

    private readonly record struct MoveTarget(
        Vector3 NavigationPoint,
        Vector3 WorldPointForObservation,
        string Description);
}
