using Core;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Diagnoses the production game-reading chain without constructing BotController.
/// </summary>
internal static class Test_State
{
    private const int PreflightTimeoutMs = 5000;
    private const int WatchIntervalMs = 200;

    public static void Run(
        Microsoft.Extensions.Logging.ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        string[] args)
    {
        if (args.Length > 0 &&
            (args[0].Equals("help", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--help", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            Environment.ExitCode = 0;
            return;
        }

        if (args.Length > 0 &&
            args[0].Equals("bindings", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                Console.WriteLine("Usage: state bindings");
                Environment.ExitCode = 1;
                return;
            }

            Test_StateBindings.Run(logger, loggerFactory, useDxgi);
            return;
        }

        bool watch = args.Length > 0 &&
            args[0].Equals("watch", StringComparison.OrdinalIgnoreCase);

        if (args.Length > 1 || (args.Length > 0 && !watch))
        {
            PrintUsage();
            Environment.ExitCode = 1;
            return;
        }

        Environment.ExitCode = 1;
        Console.WriteLine("=== WoW State Preflight ===");

        GameTestEnvironment? environment = null;

        try
        {
            if (!GameTestEnvironment.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out environment,
                    out string environmentReason))
            {
                Fail("WoW Process", environmentReason);
                PrintResult(false);
                return;
            }

            WowProcess process = environment.Process;
            Pass("WoW Process", $"PID {process.Id}, Version {process.FileVersion}");

            if (process.MainWindowHandle == IntPtr.Zero)
            {
                Fail("Game Window", "WoW has no main window handle.");
                PrintResult(false);
                return;
            }

            Pass("Game Window", $"Handle 0x{process.MainWindowHandle.ToInt64():X}");

            DataFrame[] loadedFrames;
            try
            {
                if (!GameTestEnvironment.TryLoadFrameConfig(out loadedFrames, out string frameReason))
                    throw new InvalidOperationException(frameReason);

                Pass("FrameConfig", $"{loadedFrames.Length} frames");
                Pass("DataFrames", $"{loadedFrames.Length} frames loaded by FrameConfig.LoadFrames()");
            }
            catch (Exception ex)
            {
                Fail("FrameConfig", ex.Message);
                PrintResult(false);
                return;
            }

            IServiceProvider services = environment.Services;
            DataFrame[] serviceFrames = services.GetRequiredService<DataFrame[]>();
            if (serviceFrames.Length != loadedFrames.Length)
                throw new InvalidOperationException(
                    $"DI loaded {serviceFrames.Length} frames, expected {loadedFrames.Length}.");

            IWowScreen screen = services.GetRequiredService<IWowScreen>();
            IAddonDataProvider provider = services.GetRequiredService<IAddonDataProvider>();
            AddonReader addonReader = services.GetRequiredService<AddonReader>();
            PlayerReader playerReader = services.GetRequiredService<PlayerReader>();
            AddonBits bits = services.GetRequiredService<AddonBits>();
            environment.MarkReaderGraphInitialized();

            try
            {
                screen.Enabled = true;
                screen.Update();
                if (screen.ScreenRect.Width <= 0 || screen.ScreenRect.Height <= 0)
                    throw new InvalidOperationException($"Invalid capture rectangle {screen.ScreenRect}.");

                Pass("Screen Capture", $"{screen.GetType().Name} {screen.ScreenRect.Width}x{screen.ScreenRect.Height}");
            }
            catch (Exception ex)
            {
                Fail("Screen Capture", ex.Message);
                PrintResult(false);
                return;
            }

            if (!ReferenceEquals(screen, provider))
            {
                Fail("AddonDataProvider", $"Provider {provider.GetType().Name} is not the configured screen provider.");
                PrintResult(false);
                return;
            }

            if (provider.Data.Length < loadedFrames.Length)
            {
                Fail("AddonDataProvider",
                    $"Provider returned {provider.Data.Length} data cells for {loadedFrames.Length} frames.");
                PrintResult(false);
                return;
            }

            Pass("AddonDataProvider", provider.GetType().Name);

            if (!WaitForGlobalTime(screen, addonReader, environment.Cancellation.Token, out string globalTimeReason))
            {
                Fail("Addon GlobalTime", globalTimeReason);
                PrintResult(false);
                return;
            }

            Pass("Addon GlobalTime", $"{addonReader.GlobalTime.Value}");

            bool dataReady = WaitForDataReadyAndPlayerIdentity(
                screen, addonReader, playerReader, environment.Cancellation.Token);

            if (!dataReady)
            {
                Fail("Addon Data Updating",
                    $"DataReady={addonReader.DataReady.IsSet}, GlobalTime={addonReader.GlobalTime.Value} after {PreflightTimeoutMs} ms.");
                if (!IsValidPlayerClass(playerReader.Class))
                    Fail("Player Class", $"Invalid value {playerReader.Class}; addon data is not ready.");
                if (!IsValidPlayerRace(playerReader.Race))
                    Fail("Player Race", $"Invalid value {playerReader.Race}; addon data is not ready.");
                PrintResult(false);
                return;
            }

            Pass("Addon Data Updating", $"DataReady; GlobalTime={addonReader.GlobalTime.Value}");

            bool classValid = IsValidPlayerClass(playerReader.Class);
            if (classValid)
                Pass("Player Class", playerReader.Class.ToString());
            else
                Fail("Player Class", $"Invalid value {playerReader.Class}.");

            bool raceValid = IsValidPlayerRace(playerReader.Race);
            if (raceValid)
                Pass("Player Race", playerReader.Race.ToString());
            else
                Fail("Player Race", $"Invalid value {playerReader.Race}.");

            if (!TryValidatePlayerState(playerReader, out string playerStateReason))
                Fail("Player Position", playerStateReason);
            else
                Pass("Player Position", "Map coordinates, direction, health, and UIMapId are valid");

            bool preflightPassed = classValid && raceValid &&
                TryValidatePlayerState(playerReader, out _);

            PrintResult(preflightPassed);
            if (!preflightPassed)
                return;

            if (watch)
                WatchState(screen, addonReader, playerReader, bits, environment.Cancellation);
            else
                DumpState(process, addonReader, playerReader, bits);

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Fail("State Initialization", ex.Message);
            PrintResult(false);
        }
        finally
        {
            environment?.Dispose();
        }
    }

    private static bool WaitForGlobalTime(
        IWowScreen screen,
        AddonReader addonReader,
        CancellationToken token,
        out string reason)
    {
        int initial = addonReader.GlobalTime.Value;
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (timer.ElapsedMilliseconds < PreflightTimeoutMs && !token.IsCancellationRequested)
            {
                screen.Update();
                addonReader.Update();

                if (addonReader.GlobalTime.Value != initial)
                {
                    reason = string.Empty;
                    return true;
                }

                token.WaitHandle.WaitOne(50);
            }
        }
        catch (Exception ex)
        {
            reason = $"screen.Update/addonReader.Update failed: {ex.Message}";
            return false;
        }

        reason =
            $"GlobalTime did not change during {PreflightTimeoutMs} ms. " +
            "Possible causes: DataToColor addon is not running, frame coordinates are wrong, " +
            "screen capture cannot read addon data, WoW UI scale/resolution differs, or the addon is not initialized.";
        return false;
    }

    private static bool WaitForDataReadyAndPlayerIdentity(
        IWowScreen screen,
        AddonReader addonReader,
        PlayerReader playerReader,
        CancellationToken token)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < PreflightTimeoutMs && !token.IsCancellationRequested)
        {
            try
            {
                screen.Update();
                addonReader.Update();
            }
            catch
            {
                return false;
            }

            if (addonReader.DataReady.IsSet &&
                IsValidPlayerClass(playerReader.Class) &&
                IsValidPlayerRace(playerReader.Race))
            {
                return true;
            }

            token.WaitHandle.WaitOne(50);
        }

        return false;
    }

    private static void DumpState(
        WowProcess process,
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits)
    {
        Vector3 world = playerReader.WorldPos;
        bool hasTarget = bits.Target();

        Console.WriteLine("=== WoW State ===");
        Console.WriteLine($"Game Connected: {process.IsRunning}");
        Console.WriteLine($"Client Version: {playerReader.Version}");
        Console.WriteLine($"Class: {playerReader.Class}");
        Console.WriteLine($"Race: {playerReader.Race}");
        Console.WriteLine($"Faction: {playerReader.Faction}");
        Console.WriteLine($"Level: {playerReader.Level.Value}");
        Console.WriteLine($"UIMapId: {playerReader.UIMapId.Value}");
        Console.WriteLine($"MapX: {playerReader.MapX:F4}");
        Console.WriteLine($"MapY: {playerReader.MapY:F4}");
        Console.WriteLine($"WorldPos X: {world.X:F4}");
        Console.WriteLine($"WorldPos Y: {world.Y:F4}");
        Console.WriteLine($"WorldPos Z: {world.Z:F4}");
        Console.WriteLine($"Direction: {playerReader.Direction:F4}");
        Console.WriteLine($"Health Current: {playerReader.HealthCurrent()}");
        Console.WriteLine($"Health Max: {playerReader.HealthMax()}");
        Console.WriteLine($"Health Percent: {playerReader.HealthPercent()}%");
        Console.WriteLine($"HasTarget: {hasTarget}");
        Console.WriteLine($"TargetGuid: {(hasTarget ? playerReader.TargetGuid : 0)}");
        Console.WriteLine($"TargetId: {(hasTarget ? playerReader.TargetId : 0)}");
        Console.WriteLine($"TargetHealthPercent: {(hasTarget ? $"{playerReader.TargetHealthPercent()}%" : "N/A")}");
        Console.WriteLine($"TargetHostile: {bits.Target_Hostile()}");
        Console.WriteLine($"TargetDead: {bits.Target_Dead()}");
        Console.WriteLine($"Combat: {bits.Combat()}");
        Console.WriteLine($"Moving: {bits.Moving()}");
        Console.WriteLine($"Mounted: {bits.Mounted()}");
        Console.WriteLine($"Falling: {bits.Falling()}");
        Console.WriteLine($"Dead: {bits.Dead()}");
        Console.WriteLine($"Addon GlobalTime: {addonReader.GlobalTime.Value}");
    }

    private static void WatchState(
        IWowScreen screen,
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits,
        CancellationTokenSource cts)
    {
        Console.WriteLine("=== WoW State Watch ===");
        Console.WriteLine("Watching live state every 200ms. Press Ctrl+C to exit.");

        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += cancel;
        bool cursorHidden = false;
        try
        {
            if (!Console.IsOutputRedirected)
            {
                try
                {
                    Console.CursorVisible = false;
                    cursorHidden = true;
                }
                catch
                {
                    // Some hosts do not expose cursor state; line refresh still works.
                }
            }

            int previousLineLength = 0;
            int watchRow = Console.CursorTop;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Keep the same refresh order as BotController.AddonThread().
                    screen.Update();
                    addonReader.Update();
                    previousLineLength = WriteWatchLine(
                        playerReader, bits, previousLineLength, watchRow);
                }
                catch (Exception ex)
                {
                    if (!Console.IsOutputRedirected)
                    {
                        TrySetCursorPosition(0, watchRow);
                        Console.WriteLine();
                    }
                    Console.WriteLine($"Watch update failed: {ex.Message}");
                    cts.Cancel();
                    break;
                }

                cts.Token.WaitHandle.WaitOne(WatchIntervalMs);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (cursorHidden)
                Console.CursorVisible = true;
            Console.WriteLine("State watch stopped.");
        }
    }

    private static int WriteWatchLine(
        PlayerReader playerReader,
        AddonBits bits,
        int previousLineLength,
        int watchRow)
    {
        bool hasTarget = bits.Target();
        string target = hasTarget
            ? $"Target=True TargetId={playerReader.TargetId} TargetHP={playerReader.TargetHealthPercent()}%"
            : "Target=False";

        string line =
            $"X={playerReader.MapX:F4} Y={playerReader.MapY:F4} Z={playerReader.WorldPosZ:F1} " +
            $"Dir={playerReader.Direction:F2} HP={playerReader.HealthPercent()}% {target} " +
            $"Combat={bits.Combat()} Moving={bits.Moving()} Mounted={bits.Mounted()} " +
            $"Falling={bits.Falling()} Dead={bits.Dead()}";

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(line);
            return line.Length;
        }

        TrySetCursorPosition(0, watchRow);

        // Never write into the final console column: doing so makes Windows
        // terminals wrap and scroll when a value changes length.
        int width = GetConsoleWidth();
        string display = width > 1 && line.Length >= width
            ? line[..(width - 1)]
            : line;
        int clearLength = width > 1
            ? width - 1
            : Math.Max(previousLineLength, display.Length);

        Console.Write($"\r{display.PadRight(clearLength)}");
        Console.Out.Flush();
        return display.Length;
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 0;
        }
    }

    private static void TrySetCursorPosition(int left, int top)
    {
        try
        {
            Console.SetCursorPosition(left, top);
        }
        catch
        {
            // The terminal may have been resized or may not support cursor APIs.
        }
    }

    private static bool TryValidatePlayerState(PlayerReader playerReader, out string reason)
    {
        int healthMax = playerReader.HealthMax();
        int health = playerReader.HealthCurrent();
        int uiMapId = playerReader.UIMapId.Value;
        float mapX = playerReader.MapX;
        float mapY = playerReader.MapY;
        float worldZ = playerReader.WorldPosZ;
        float direction = playerReader.Direction;

        bool finite = float.IsFinite(mapX) && float.IsFinite(mapY) &&
            float.IsFinite(worldZ) && float.IsFinite(direction);
        bool positionHasData = mapX != 0 || mapY != 0 || worldZ != 0 || direction != 0;

        Vector3 world;
        try
        {
            world = playerReader.WorldPos;
        }
        catch (Exception ex)
        {
            reason = $"WorldPos conversion failed: {ex.Message}";
            return false;
        }

        if (healthMax <= 0)
        {
            reason = $"HealthMax={healthMax}; player state is not initialized.";
            return false;
        }

        if (health < 0 || health > healthMax)
        {
            reason = $"HealthCurrent={health}, HealthMax={healthMax}; values are inconsistent.";
            return false;
        }

        if (uiMapId <= 0)
        {
            reason = $"UIMapId={uiMapId}; player map data is not initialized.";
            return false;
        }

        if (!finite || !positionHasData ||
            !float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
        {
            reason = $"MapX={mapX}, MapY={mapY}, WorldPosZ={worldZ}, Direction={direction}, " +
                $"WorldPos=({world.X},{world.Y},{world.Z}); values are empty or invalid.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsValidPlayerClass(UnitClass value) =>
        value != UnitClass.None && Enum.IsDefined(value);

    private static bool IsValidPlayerRace(UnitRace value) =>
        value != UnitRace.None && Enum.IsDefined(value);

    private static void PrintUsage()
    {
        Console.WriteLine("State commands:");
        Console.WriteLine("  state              Read the current WoW state once");
        Console.WriteLine("  state watch        Watch the current WoW state");
        Console.WriteLine("  state bindings     Diagnose DataToColor slot 106 binding transport");
    }

    private static void Pass(string item, string details) =>
        Console.WriteLine($"{item}: PASS ({details})");

    private static void Fail(string item, string reason)
    {
        Console.WriteLine($"{item}: FAIL");
        Console.WriteLine($"Reason: {reason}");
    }

    private static void PrintResult(bool passed)
    {
        Console.WriteLine($"Preflight Result: {(passed ? "PASS" : "FAIL")}");
    }
}
