using Core;

using Game;

using System;
using System.Diagnostics;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Drives the production addon reader while a TimedQueue batch is being emitted.
/// The queue changes faster than a diagnostic that only sleeps and checks state,
/// so every wait must actively capture a frame and call AddonReader.Update().
/// </summary>
internal static class AddonRefreshHelper
{
    // A full DataToColor rebuild normally takes about 9 seconds on the live
    // client. Do not interrupt that rebuild with an early second flush.
    public const int DefaultTimeoutMs = 30_000;
    public const int UpdateIntervalMs = 2;
    // A refresh is one transaction: one reader reset and one CUSTOM_FLUSH.
    // Missing a queue item is reported as a refresh failure; issuing another
    // flush would clear the batch that may already be in flight.
    private const int MaxRefreshAttempts = 1;

    public static bool RefreshAddonAndWaitForReaders(
        IWowScreen screen,
        AddonReader addonReader,
        WowProcessInput flushInput,
        KeyBindingsReader keyBindingsReader,
        CancellationToken token,
        int timeoutMs,
        Action<int> onUpdate,
        out AddonRefreshStats stats,
        out string error)
    {
        screen.Enabled = true;
        Stopwatch timer = Stopwatch.StartNew();
        int updateCount = 0;
        int refreshAttempts = 0;
        int lastReceivedCount = -1;
        int lastBindingRaw = int.MinValue;

        if (IsReady(addonReader, keyBindingsReader))
        {
            Console.WriteLine("Refresh already complete; skipping CUSTOM_FLUSH");
            stats = CreateStats(timer, updateCount, addonReader);
            error = string.Empty;
            return true;
        }

        while (timer.ElapsedMilliseconds < timeoutMs &&
            !token.IsCancellationRequested &&
            refreshAttempts < MaxRefreshAttempts)
        {
            refreshAttempts++;
            Console.WriteLine("Refresh start");
            // BeginRefresh resets the reader graph and DataReady. The
            // following Shift+PageDown invokes the official CUSTOM_FLUSH.
            StartRefreshTransaction(addonReader, flushInput);
            Console.WriteLine("Flush requested");

            bool flushAcknowledgedLogged = false;
            bool initPhaseFinishedLogged = false;
            bool bindingHeaderLogged = false;
            bool spellBookReadyLogged = false;
            bool textureReadyLogged = false;

            while (timer.ElapsedMilliseconds < timeoutMs && !token.IsCancellationRequested)
            {
                screen.Update();
                addonReader.Update();
                updateCount++;
                onUpdate?.Invoke(updateCount);

                if (!flushAcknowledgedLogged && addonReader.ExpectedRefreshResetCount > 0)
                {
                    flushAcknowledgedLogged = true;
                    Console.WriteLine("Expected GlobalTime reset observed");
                    Console.WriteLine("Flush acknowledged");
                }

                if (!initPhaseFinishedLogged && addonReader.GlobalTime.Value >= AddonTicks.INIT_PHASE)
                {
                    initPhaseFinishedLogged = true;
                    Console.WriteLine("Init phase finished");
                }

                if (!bindingHeaderLogged && keyBindingsReader.ExpectedCount >= 0)
                {
                    bindingHeaderLogged = true;
                    Console.WriteLine($"Binding header received: expected {keyBindingsReader.ExpectedCount}");
                }

                if (keyBindingsReader.ReceivedCount != lastReceivedCount ||
                    addonReader.BindingQueueRaw != lastBindingRaw)
                {
                    if (keyBindingsReader.ExpectedCount >= 0 &&
                        (keyBindingsReader.ReceivedCount == 1 ||
                         keyBindingsReader.ReceivedCount == keyBindingsReader.ExpectedCount))
                    {
                        Console.WriteLine(
                            $"Binding {keyBindingsReader.ReceivedCount}/{keyBindingsReader.ExpectedCount}");
                    }

                    lastReceivedCount = keyBindingsReader.ReceivedCount;
                    lastBindingRaw = addonReader.BindingQueueRaw;
                }

                if (!spellBookReadyLogged && addonReader.SpellBookInitialized)
                {
                    spellBookReadyLogged = true;
                    Console.WriteLine("SpellBook ready");
                }

                if (!textureReadyLogged && addonReader.TextureInitialized)
                {
                    textureReadyLogged = true;
                    Console.WriteLine("Texture ready");
                }

                if (IsReady(addonReader, keyBindingsReader))
                {
                    Console.WriteLine("DataReady=true");
                    Console.WriteLine("Refresh complete");
                    stats = CreateStats(timer, updateCount, addonReader);
                    error = string.Empty;
                    return true;
                }

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }
        }

        stats = CreateStats(timer, updateCount, addonReader);
        error =
            $"Addon readers were not ready after {timeoutMs} ms " +
            $"(DataReady={addonReader.DataReady.IsSet}, " +
            $"KeyBindingsInitialized={keyBindingsReader.IsInitialized}, " +
            $"ExpectedCount={keyBindingsReader.ExpectedCount}, " +
            $"ReceivedCount={keyBindingsReader.ReceivedCount}, " +
            $"AddonUpdates={stats.UpdateCount}, RefreshAttempts={refreshAttempts}, " +
            $"FullResets={stats.FullResetCount}, " +
            $"ExpectedRefreshResets={stats.ExpectedRefreshResetCount}, " +
            $"GlobalTime={stats.GlobalTime}, BindingRaw={stats.BindingQueueRaw}, " +
            $"SpellBookInitialized={stats.SpellBookInitialized}, " +
            $"SpellBookRaw={stats.SpellBookQueueRaw}, " +
            $"TextureInitialized={stats.TextureInitialized}, " +
            $"TextureRaw={stats.TextureQueueRaw}).";
        return false;
    }

    private static void StartRefreshTransaction(
        AddonReader addonReader,
        WowProcessInput flushInput)
    {
        // PostMessage input is delivered to the WoW window, but protected
        // bindings can still be ignored while another window owns focus.
        // Keep the official Shift+PageDown/CUSTOM_FLUSH path; only make its
        // delivery deterministic for this live diagnostic.
        flushInput.SetForegroundWindow();
        addonReader.BeginRefresh();
        flushInput.PressFlushKey();
    }

    public static bool IsReady(
        AddonReader addonReader,
        KeyBindingsReader keyBindingsReader) =>
        addonReader.DataReady.IsSet &&
        keyBindingsReader.IsInitialized &&
        addonReader.SpellBookInitialized &&
        addonReader.TextureInitialized;

    private static AddonRefreshStats CreateStats(
        Stopwatch timer,
        int updateCount,
        AddonReader addonReader) =>
        new(
            updateCount,
            timer.ElapsedMilliseconds,
            addonReader.FullResetCount,
            addonReader.ExpectedRefreshResetCount,
            addonReader.LastResetReason,
            addonReader.GlobalTime.Value,
            addonReader.BindingQueueRaw,
            addonReader.SpellBookInitialized,
            addonReader.SpellBookQueueRaw,
            addonReader.TextureInitialized,
            addonReader.TextureQueueRaw);
}

internal readonly record struct AddonRefreshStats(
    int UpdateCount,
    long ElapsedMilliseconds,
    int FullResetCount,
    int ExpectedRefreshResetCount,
    AddonResetReason? LastResetReason,
    int GlobalTime,
    int BindingQueueRaw,
    bool SpellBookInitialized,
    int SpellBookQueueRaw,
    bool TextureInitialized,
    int TextureQueueRaw)
{
    public double UpdatesPerSecond =>
        UpdateCount * 1000d / Math.Max(1, ElapsedMilliseconds);
}
