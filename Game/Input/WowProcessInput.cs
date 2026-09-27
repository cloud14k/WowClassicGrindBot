using Microsoft.Extensions.Logging;

using SharedLib;

using SixLabors.ImageSharp;

using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;

using WinAPI;

namespace Game;

public sealed partial class WowProcessInput : IMouseInput, IDisposable
{
    // Virtual key codes for modifier keys
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;  // Alt key

    private readonly ILogger<WowProcessInput> logger;

    private readonly WowProcess process;
    private readonly InputBackendRouter nativeInput;

    private readonly BitArray keysDown;

    public IInputExecutionObserver? ExecutionObserver
    {
        get => nativeInput.ExecutionObserver;
        set => nativeInput.ExecutionObserver = value;
    }

    public bool IsHidBackend => nativeInput.IsHid;
    public bool IsHidConnected => nativeInput.IsHidConnected;
    public string? HidPort => nativeInput.HidPort;
    public Task<byte> ProbeHidAsync(CancellationToken token = default) => nativeInput.ProbeHidAsync(token);

    public ConsoleKey ForwardKey { get; set; }
    public ConsoleKey BackwardKey { get; set; }
    public ConsoleKey TurnLeftKey { get; set; }
    public ConsoleKey TurnRightKey { get; set; }
    public ConsoleKey InteractMouseover { get; set; }
    public ModifierKey InteractMouseoverModifier { get; set; }
    public int InteractMouseoverPress { get; set; }

    public WowProcessInput(ILogger<WowProcessInput> logger, CancellationTokenSource cts, WowProcess process, InputBackendSettings? inputSettings = null)
    {
        this.logger = logger;
        this.process = process;

        keysDown = new((int)ConsoleKey.OemClear);

        InputBackendSettings settings = inputSettings ?? new InputBackendSettings();
        nativeInput = new InputBackendRouter(process, cts, settings);
        if (settings.Backend == "Hid")
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Input backend: ESP32 HID on {Port}; output gated by WoW foreground state", settings.Port);
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Input backend: Windows messages");
        }
    }

    public void Dispose()
    {
        if (nativeInput is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>
    /// Releases everything before forgetting it. Clearing the belief on its own
    /// leaves the game holding whatever was down - a held movement key runs the
    /// character on forever - and because <see cref="KeyUp"/> ignores a release
    /// for a key it does not believe is down, the release can never be issued
    /// afterwards. The movement keys are released unconditionally: a key the
    /// game holds without this class knowing (the user pressing it themselves)
    /// is exactly the case the belief cannot describe.
    /// </summary>
    public void Reset()
    {
        lock (keysDown)
        {
            for (int i = 0; i < keysDown.Length; i++)
            {
                if (keysDown[i])
                {
                    if (TryReleaseKey(i)) keysDown[i] = false;
                }
            }
        }

        ReleaseIfConfigured(ForwardKey);
        ReleaseIfConfigured(BackwardKey);
        ReleaseIfConfigured(TurnLeftKey);
        ReleaseIfConfigured(TurnRightKey);
    }

    private void ReleaseIfConfigured(ConsoleKey key)
    {
        if (key != default)
        {
            if (TryReleaseKey((int)key)) keysDown[(int)key] = false;
        }
    }

    private bool TryReleaseKey(int key)
    {
        try { nativeInput.KeyUp(key); return true; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release key {Key}", (ConsoleKey)key);
            return false;
        }
    }

    private void RecoverFailedPress(int key)
    {
        try
        {
            nativeInput.KeyUp(key);
            keysDown[key] = false;
        }
        catch (Exception ex)
        {
            // Keep the belief set so Reset can retry the release later.
            logger.LogWarning(ex, "Failed to recover key {Key} after input error", (ConsoleKey)key);
        }
    }

    private void ReleaseTrackedKey(int key)
    {
        nativeInput.KeyUp(key);
        keysDown[key] = false;
    }

    public void KeyDown(ConsoleKey key, bool forced)
    {
        if (IsKeyDown(key))
        {
            if (!forced)
                return;
        }

        //if (IsMovementKey(key))
        //    LogMoveKeyDown(logger, key);
        //else
        //    LogKeyDown(logger, key);

        keysDown[(int)key] = true;
        nativeInput.KeyDown((int)key);
    }

    public void KeyUp(ConsoleKey key, bool forced)
    {
        if (!IsKeyDown(key))
        {
            if (!forced)
                return;
        }

        //if (IsMovementKey(key))
        //    LogMoveKeyUp(logger, key);
        //else
        //    LogKeyUp(logger, key);

        nativeInput.KeyUp((int)key);
        keysDown[(int)key] = false;
    }

    public bool IsKeyDown(ConsoleKey key)
    {
        return keysDown[(int)key];
    }

    public void SendText(string text)
    {
        nativeInput.SendText(text);
    }

    public void SetForegroundWindow()
    {
        if (!nativeInput.IsHid)
            NativeMethods.SetForegroundWindow(process.MainWindowHandle);
    }

    public int PressRandom(ConsoleKey key, int milliseconds = InputDuration.DefaultPress, CancellationToken token = default)
    {
        keysDown[(int)key] = true;
        int elapsedMs;
        try
        {
            elapsedMs = nativeInput.PressRandom((int)key, milliseconds, token);
            keysDown[(int)key] = false;
        }
        catch
        {
            RecoverFailedPress((int)key);
            throw;
        }

        LogKeyPressRandom(logger, key, elapsedMs);

        return elapsedMs;
    }

    public int PressRandomWithModifier(ConsoleKey key, ModifierKey modifier, int milliseconds = InputDuration.DefaultPress, CancellationToken token = default)
    {
        // If no modifier, use the simple path
        if (modifier == ModifierKey.None)
        {
            return PressRandom(key, milliseconds, token);
        }

        // Note: PostMessage sends WM_KEYDOWN to the window's message queue.
        // WoW processes messages in FIFO order, so modifier keys are "pressed"
        // before the main key. No delay needed between messages.
        // If WoW uses GetKeyState() instead of tracking WM_KEYDOWN messages,
        // modifiers may not work - would need SendInput (foreground only).

        bool shiftDown = false, ctrlDown = false, altDown = false;
        int elapsedMs;
        try
        {
            if ((modifier & ModifierKey.Shift) != 0)
            {
                shiftDown = true;
                keysDown[VK_SHIFT] = true;
                nativeInput.KeyDown(VK_SHIFT);
            }
            if ((modifier & ModifierKey.Ctrl) != 0)
            {
                ctrlDown = true;
                keysDown[VK_CONTROL] = true;
                nativeInput.KeyDown(VK_CONTROL);
            }
            if ((modifier & ModifierKey.Alt) != 0)
            {
                altDown = true;
                keysDown[VK_MENU] = true;
                nativeInput.KeyDown(VK_MENU);
            }

            keysDown[(int)key] = true;
            try
            {
                elapsedMs = nativeInput.PressRandom((int)key, milliseconds, token);
                keysDown[(int)key] = false;
            }
            catch
            {
                RecoverFailedPress((int)key);
                throw;
            }
        }
        finally
        {
            try { if (altDown) ReleaseTrackedKey(VK_MENU); }
            finally
            {
                try { if (ctrlDown) ReleaseTrackedKey(VK_CONTROL); }
                finally { if (shiftDown) ReleaseTrackedKey(VK_SHIFT); }
            }
        }

        LogKeyPressRandomWithModifier(logger, key, modifier, elapsedMs);

        return elapsedMs;
    }

    public void PressFixed(ConsoleKey key, int milliseconds, CancellationToken token = default)
    {
        if (milliseconds < 1)
            return;

        if (IsMovementKey(key))
            LogMoveKeyPress(logger, key, milliseconds);
        else
            LogKeyPressFixed(logger, key, milliseconds);

        keysDown[(int)key] = true;
        try
        {
            nativeInput.PressFixed((int)key, milliseconds, token);
            keysDown[(int)key] = false;
        }
        catch
        {
            RecoverFailedPress((int)key);
            throw;
        }
    }

    public void SetKeyState(ConsoleKey key, bool pressDown, bool forced)
    {
        if (pressDown)
            KeyDown(key, forced);
        else
            KeyUp(key, forced);
    }

    public void SetCursorPos(Point p)
    {
        nativeInput.SetCursorPos(p);
    }

    public void RightClick(Point p)
    {
        nativeInput.RightClick(p);
    }

    public void LeftClick(Point p)
    {
        nativeInput.LeftClick(p);
    }

    public void InteractMouseOver(CancellationToken token)
    {
        if (InteractMouseoverModifier != ModifierKey.None)
        {
            PressRandomWithModifier(InteractMouseover, InteractMouseoverModifier, InteractMouseoverPress, token);
        }
        else
        {
            PressFixed(InteractMouseover, InteractMouseoverPress, token);
        }
    }

    /// <summary>
    /// Presses SHIFT-PAGEDOWN to trigger CUSTOM_FLUSH (/tw14kflush) in the addon.
    /// </summary>
    public void PressFlushKey()
    {
        PressRandomWithModifier(ConsoleKey.PageDown, ModifierKey.Shift, 50);
    }

    private bool IsMovementKey(ConsoleKey key) =>
        key == ForwardKey ||
        key == BackwardKey ||
        key == TurnLeftKey ||
        key == TurnRightKey;

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Debug,
        Message = @"[{key}] KeyDown")]
    static partial void LogKeyDown(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = @"[{key}] KeyUp")]
    static partial void LogKeyUp(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = @"[{key}] press fix {milliseconds}ms")]
    static partial void LogKeyPressFixed(ILogger logger, ConsoleKey key, int milliseconds);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = @"[{key}] press random {milliseconds}ms")]
    static partial void LogKeyPressRandom(ILogger logger, ConsoleKey key, int milliseconds);

    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Information,
        Message = @"[{modifier}-{key}] press random {milliseconds}ms")]
    static partial void LogKeyPressRandomWithModifier(ILogger logger, ConsoleKey key, ModifierKey modifier, int milliseconds);

    #region Movement Trance

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Trace,
        Message = @"[{key}] move KeyDown")]
    static partial void LogMoveKeyDown(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Trace,
        Message = @"[{key}] move KeyUp")]
    static partial void LogMoveKeyUp(ILogger logger, ConsoleKey key);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Trace,
        Message = @"[{key}] move Pressed {milliseconds}ms")]
    static partial void LogMoveKeyPress(ILogger logger, ConsoleKey key, int milliseconds);

    #endregion
}
