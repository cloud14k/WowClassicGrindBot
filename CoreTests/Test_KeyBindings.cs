using Core;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;

namespace CoreTests;

/// <summary>
/// Offline regression checks for default binding modifiers and live addon binding refresh.
/// </summary>
internal static class Test_KeyBindings
{
    public static void Run(ILogger logger, ILoggerFactory loggerFactory)
    {
        KeyReader.GameBindings.Clear();
        KeyReader.GameBindingsSecondary.Clear();

        ClassConfiguration config = new();
        KeyBindingsReader reader = new(loggerFactory.CreateLogger<KeyBindingsReader>());
        NullAddonDataProvider provider = new(120);
        Action<BindingID> refreshActions = bindingId => config.RefreshKeyBindingsFor(bindingId, logger);
        KeyReader.GameBindingChanged += refreshActions;

        try
        {
            Assert(
                KeyReader.ReadKey(logger, config.Interact) &&
                config.Interact.ConsoleKey == ConsoleKey.Home &&
                config.Interact.Modifier == ModifierKey.Alt,
                "Scenario A: INTERACTTARGET falls back to Alt+Home.");

            KeyAction configAction = new() { BindingID = BindingID.CUSTOM_CONFIG };
            Assert(
                KeyReader.ReadKey(logger, configAction) &&
                configAction.ConsoleKey == ConsoleKey.PageUp &&
                configAction.Modifier == ModifierKey.Shift,
                "Scenario B: CUSTOM_CONFIG falls back to Shift+PageUp.");

            AssertFallback(logger, BindingID.STARTATTACK, ConsoleKey.Add, ModifierKey.Alt);
            AssertFallback(logger, BindingID.INTERACTMOUSEOVER, ConsoleKey.End, ModifierKey.Alt);
            AssertFallback(logger, BindingID.FOLLOWTARGET, ConsoleKey.PageDown, ModifierKey.Alt);
            AssertFallback(logger, BindingID.CUSTOM_STOPATTACK, ConsoleKey.Delete, ModifierKey.Alt);
            AssertFallback(logger, BindingID.CUSTOM_CLEARTARGET, ConsoleKey.Insert, ModifierKey.Alt);
            AssertFallback(logger, BindingID.CUSTOM_FLUSH, ConsoleKey.PageDown, ModifierKey.Shift);

            KeyAction moveForward = new() { BindingID = BindingID.MOVEFORWARD };
            Assert(
                KeyReader.ReadKey(logger, moveForward) &&
                moveForward.ConsoleKey == ConsoleKey.W &&
                moveForward.Modifier == ModifierKey.None,
                "Scenario D: MOVEFORWARD remains W with no modifier.");

            for (int slot = 1; slot <= 3; slot++)
            {
                KeyAction action = new() { Slot = slot };
                ConsoleKey expected = (ConsoleKey)((int)ConsoleKey.D1 + slot - 1);
                Assert(
                    KeyReader.ReadKey(logger, action) &&
                    action.ConsoleKey == expected &&
                    action.Modifier == ModifierKey.None,
                    $"Scenario E: action bar slot {slot} keeps its regular number key.");

                KeyAction bindingAction = new()
                {
                    BindingID = BindingID.ACTIONBUTTON1 + slot - 1
                };
                Assert(
                    KeyReader.ReadKey(logger, bindingAction) &&
                    bindingAction.ConsoleKey == expected &&
                    bindingAction.Modifier == ModifierKey.None,
                    $"Scenario E: ACTIONBUTTON{slot} keeps its regular number key.");
            }

            // The active action starts with its fallback, then receives the real game
            // binding through the same slot-106 queue used in production.
            provider.Data[106] = AddonTicks.QUEUE_COUNT_MARKER + 1;
            reader.Update(provider);
            provider.Data[106] = EncodeBinding(18, 71, ModifierKey.Ctrl);
            reader.Update(provider);

            Assert(
                config.Interact.ConsoleKey == ConsoleKey.Home &&
                config.Interact.Modifier == ModifierKey.Ctrl,
                "Scenario C: the live Ctrl+Home binding replaces the Alt+Home fallback.");

            Assert(
                KeyReader.HasCustomBinding(config.Interact),
                "A live modifier difference is recognized as a custom binding.");

            KeyReader.ProcessBindingFromAddon(EncodeBinding(18, 71, ModifierKey.Shift));
            Assert(
                config.Interact.Modifier == ModifierKey.Shift,
                "ProcessBindingFromAddon refreshes an already resolved action when the binding changes.");

            reader.Reset();
            Assert(
                config.Interact.ConsoleKey == ConsoleKey.Home &&
                config.Interact.Modifier == ModifierKey.Alt,
                "Clearing addon bindings returns active actions to their complete defaults.");

            string lua = KeyReader.GenerateSetBindingLua(new()
            {
                BindingID = BindingID.CUSTOM_CONFIG,
                ConsoleKey = ConsoleKey.PageUp,
                Modifier = ModifierKey.Shift
            }) ?? string.Empty;
            Assert(
                lua == "SetBinding(\"SHIFT-PAGEUP\", \"CUSTOM_CONFIG\")",
                "Generated Lua binding commands preserve modifiers.");

            Console.WriteLine("PASS: keybinding modifier fallback and addon refresh checks.");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"KEYBINDINGS FAIL ({ex.GetType().Name}: {ex.Message})");
            Environment.ExitCode = 1;
        }
        finally
        {
            KeyReader.GameBindingChanged -= refreshActions;
            reader.Reset();
            KeyReader.GameBindings.Clear();
            KeyReader.GameBindingsSecondary.Clear();
        }
    }

    private static int EncodeBinding(int index, int keyId, ModifierKey modifier)
    {
        return (modifier.ToEncodedValue() << 22) |
            (index << 14) |
            (keyId << 7);
    }

    private static void AssertFallback(
        ILogger logger,
        BindingID bindingId,
        ConsoleKey expectedKey,
        ModifierKey expectedModifier)
    {
        KeyAction action = new() { BindingID = bindingId };
        Assert(
            KeyReader.ReadKey(logger, action) &&
            action.ConsoleKey == expectedKey &&
            action.Modifier == expectedModifier,
            $"{bindingId} fallback is {expectedModifier.ToPrefix()}{expectedKey}.");
    }

    private static void Assert(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);

        Console.WriteLine($"PASS: {message}");
    }
}
