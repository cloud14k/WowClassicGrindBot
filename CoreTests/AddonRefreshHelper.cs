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
    // The first attempt is the normal transaction. A second transaction is
    // allowed only after the binding queue has emitted a header and then made
    // no progress for the stall window. This is recovery from a captured-frame
    // / addon queue stall, not an overlapping early retry.
    private const int MaxRefreshAttempts = 2;
    private const int BindingQueueStallTimeoutMs = 2500;
    private const int FlushAckTimeoutMs = 2500;

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
        long lastBindingProgressMs = 0;
        long lastFlushMs = 0;

        while (timer.ElapsedMilliseconds < timeoutMs &&
            !token.IsCancellationRequested &&
            refreshAttempts < MaxRefreshAttempts)
        {
            refreshAttempts++;
            // AddonReader.BeginRefresh() pauses its own reader graph, but the
            // ManualResetEventSlim remains signalled until the next rebuild
            // completes. Reset it here so this helper waits for this refresh,
            // not for a previous session's DataReady state.
            StartRefreshTransaction(addonReader, flushInput, timer, ref lastFlushMs);

            while (timer.ElapsedMilliseconds < timeoutMs && !token.IsCancellationRequested)
            {
                screen.Update();
                addonReader.Update();
                updateCount++;
                onUpdate?.Invoke(updateCount);

                if (keyBindingsReader.ExpectedCount >= 0 &&
                    (keyBindingsReader.ReceivedCount != lastReceivedCount ||
                     addonReader.BindingQueueRaw != lastBindingRaw))
                {
                    lastReceivedCount = keyBindingsReader.ReceivedCount;
                    lastBindingRaw = addonReader.BindingQueueRaw;
                    lastBindingProgressMs = timer.ElapsedMilliseconds;
                }

                if (addonReader.DataReady.IsSet && keyBindingsReader.IsInitialized)
                {
                    stats = CreateStats(timer, updateCount, addonReader);
                    error = string.Empty;
                    return true;
                }

                bool bindingQueueStalled =
                    refreshAttempts < MaxRefreshAttempts &&
                    keyBindingsReader.ExpectedCount > 0 &&
                    keyBindingsReader.ReceivedCount < keyBindingsReader.ExpectedCount &&
                    timer.ElapsedMilliseconds - lastBindingProgressMs >= BindingQueueStallTimeoutMs;

                bool flushWasNotAcknowledged =
                    refreshAttempts < MaxRefreshAttempts &&
                    keyBindingsReader.ExpectedCount < 0 &&
                    addonReader.BindingQueueRaw == 0 &&
                    timer.ElapsedMilliseconds - lastFlushMs >= FlushAckTimeoutMs;

                if (bindingQueueStalled || flushWasNotAcknowledged)
                {
                    // The current transaction has conclusively stopped making
                    // progress. Finish it before beginning one explicit
                    // recovery transaction; this keeps the expected Lua reset
                    // from being mistaken for an unexpected addon reset.
                    StartRefreshTransaction(addonReader, flushInput, timer, ref lastFlushMs);
                    refreshAttempts++;
                    lastReceivedCount = -1;
                    lastBindingRaw = int.MinValue;
                    lastBindingProgressMs = timer.ElapsedMilliseconds;
                    continue;
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
        WowProcessInput flushInput,
        Stopwatch timer,
        ref long lastFlushMs)
    {
        // PostMessage input is delivered to the WoW window, but protected
        // bindings can still be ignored while another window owns focus.
        // Keep the official Shift+PageDown/CUSTOM_FLUSH path; only make its
        // delivery deterministic for this live diagnostic.
        flushInput.SetForegroundWindow();
        addonReader.BeginRefresh();
        flushInput.PressFlushKey();
        lastFlushMs = timer.ElapsedMilliseconds;
    }

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
