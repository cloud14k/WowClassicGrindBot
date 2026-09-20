using Core;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Diagnostics for the production DataToColor binding queue:
/// slot 106 -> KeyBindingsReader -> KeyReader.GameBindings.
/// </summary>
internal static class Test_StateBindings
{
    private const int BindingSlot = 106;
    private static readonly BindingID[] RequiredBindings =
    [
        BindingID.MOVEFORWARD,
        BindingID.MOVEBACKWARD,
        BindingID.TURNLEFT,
        BindingID.TURNRIGHT,
        BindingID.JUMP,
        BindingID.TARGETNEARESTENEMY,
        BindingID.TARGETLASTTARGET,
        BindingID.ASSISTTARGET,
        BindingID.INTERACTTARGET,
        BindingID.INTERACTMOUSEOVER,
        BindingID.STARTATTACK,
        BindingID.PETATTACK,
        BindingID.FOLLOWTARGET,
    ];

    public static void Run(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi)
    {
        Environment.ExitCode = 1;
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
                Console.WriteLine($"STATE BINDINGS FAIL ({environmentReason})");
                return;
            }

            IServiceProvider services = environment.Services;
            IWowScreen screen = services.GetRequiredService<IWowScreen>();
            IAddonDataProvider provider = services.GetRequiredService<IAddonDataProvider>();
            AddonReader addonReader = services.GetRequiredService<AddonReader>();
            KeyBindingsReader keyBindingsReader = services.GetRequiredService<KeyBindingsReader>();
            WowProcessInput wowInput = services.GetRequiredService<WowProcessInput>();
            environment.MarkReaderGraphInitialized();

            screen.Enabled = true;
            screen.Update();
            addonReader.Update();

            Console.WriteLine("STATE BINDINGS");
            Console.WriteLine($"Slot: {BindingSlot}");
            PrintStatus(keyBindingsReader, provider.GetInt(BindingSlot), "Initial");

            Console.WriteLine("Requesting official DataToColor refresh: AddonReader.FullReset() + CUSTOM_FLUSH (Shift+PageDown)");
            int previousRaw = provider.GetInt(BindingSlot);
            bool refreshReady = AddonRefreshHelper.RefreshAddonAndWaitForReaders(
                screen,
                addonReader,
                wowInput,
                keyBindingsReader,
                environment.Cancellation.Token,
                AddonRefreshHelper.DefaultTimeoutMs,
                _ =>
                {
                    int raw = provider.GetInt(BindingSlot);
                    if (raw != previousRaw)
                    {
                        PrintSlotValue(raw, keyBindingsReader);
                        previousRaw = raw;
                    }
                },
                out AddonRefreshStats refreshStats,
                out string refreshError);

            Console.WriteLine(
                $"Addon updates during wait: {refreshStats.UpdateCount} " +
                $"({refreshStats.UpdatesPerSecond:F1}/s, {refreshStats.ElapsedMilliseconds} ms)");
            if (!refreshReady)
                Console.WriteLine($"Refresh wait failed: {refreshError}");

            PrintSummary(keyBindingsReader);

            bool requiredBindingsPresent = HasRequiredBindings();
            bool passed;
            if (keyBindingsReader.ExpectedCount < 0)
            {
                Console.WriteLine("FAIL: Binding queue header was not received from slot 106");
                passed = false;
            }
            else if (keyBindingsReader.ReceivedCount < keyBindingsReader.ExpectedCount)
            {
                Console.WriteLine("FAIL: Binding queue started but was not completely received");
                passed = false;
            }
            else if (!refreshReady || !keyBindingsReader.IsInitialized || !requiredBindingsPresent)
            {
                Console.WriteLine("FAIL: Binding queue completed but some bindings failed to decode");
                passed = false;
            }
            else
            {
                Console.WriteLine("PASS: Binding queue fully received");
                passed = true;
            }

            Environment.ExitCode = passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"STATE BINDINGS FAIL ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            environment?.Dispose();
        }
    }

    private static void PrintStatus(
        KeyBindingsReader keyBindingsReader,
        int raw,
        string label)
    {
        Console.WriteLine(
            $"{label}: KeyBindingsReader.IsInitialized={keyBindingsReader.IsInitialized}, " +
            $"ExpectedCount={keyBindingsReader.ExpectedCount}, " +
            $"ReceivedCount={keyBindingsReader.ReceivedCount}, " +
            $"Count={keyBindingsReader.Count}, Slot106 Raw={raw}");
    }

    private static void PrintSlotValue(int raw, KeyBindingsReader keyBindingsReader)
    {
        if (raw >= AddonTicks.QUEUE_COUNT_MARKER)
        {
            int expectedCount = raw - AddonTicks.QUEUE_COUNT_MARKER;
            Console.WriteLine($"Slot106 Raw: {raw} HEADER ExpectedCount={expectedCount}");
            return;
        }

        if (raw == 0)
        {
            Console.WriteLine(
                $"Slot106 Raw: 0 ReceivedCount={keyBindingsReader.ReceivedCount}/{keyBindingsReader.ExpectedCount}");
            return;
        }

        var decoded = KeyReader.DecodeBinding(raw);
        if (!decoded.HasValue)
        {
            Console.WriteLine(
                $"Slot106 Raw: {raw} INVALID ReceivedCount={keyBindingsReader.ReceivedCount}/{keyBindingsReader.ExpectedCount}");
            return;
        }

        string primary = FormatBinding(decoded.Value.key1, decoded.Value.mod1);
        string secondary = decoded.Value.key2 == ConsoleKey.NoName
            ? string.Empty
            : $" secondary={FormatBinding(decoded.Value.key2, decoded.Value.mod2)}";

        Console.WriteLine(
            $"Slot106 Raw: {raw} {decoded.Value.bindingId} -> {primary}{secondary} " +
            $"ReceivedCount={keyBindingsReader.ReceivedCount}/{keyBindingsReader.ExpectedCount}");
    }

    private static void PrintSummary(KeyBindingsReader keyBindingsReader)
    {
        Console.WriteLine();
        Console.WriteLine("STATE BINDINGS");
        Console.WriteLine($"Slot: {BindingSlot}");
        Console.WriteLine($"ExpectedCount: {keyBindingsReader.ExpectedCount}");
        Console.WriteLine($"ReceivedCount: {keyBindingsReader.ReceivedCount}");
        Console.WriteLine($"DecodedCount: {keyBindingsReader.DecodedCount}");
        Console.WriteLine($"DecodeFailureCount: {keyBindingsReader.DecodeFailureCount}");
        Console.WriteLine($"Count: {keyBindingsReader.Count}");
        Console.WriteLine($"IsInitialized: {keyBindingsReader.IsInitialized}");
        Console.WriteLine($"KeyReader.GameBindings.Count: {KeyReader.GameBindings.Count}");
        Console.WriteLine($"KeyReader.GameBindingsSecondary.Count: {KeyReader.GameBindingsSecondary.Count}");

        foreach (BindingID bindingId in RequiredBindings)
        {
            if (KeyReader.GameBindings.TryGetValue(bindingId, out var primary))
            {
                string value = FormatBinding(primary.Key, primary.Modifier);
                if (KeyReader.GameBindingsSecondary.TryGetValue(bindingId, out var secondary))
                    value += $" | secondary={FormatBinding(secondary.Key, secondary.Modifier)}";

                Console.WriteLine($"{bindingId} = {value}");
            }
            else
            {
                Console.WriteLine($"{bindingId} = MISSING");
            }
        }
    }

    private static bool HasRequiredBindings()
    {
        foreach (BindingID bindingId in RequiredBindings)
        {
            if (!KeyReader.GameBindings.ContainsKey(bindingId))
                return false;
        }

        return KeyReader.GameBindings.Count >= RequiredBindings.Length;
    }

    private static string FormatBinding(ConsoleKey key, ModifierKey modifier) =>
        $"{modifier.ToPrefix()}{key}";
}
