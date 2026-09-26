using SixLabors.ImageSharp;

using System;
using System.Diagnostics;
using System.Threading;

using static WinAPI.NativeMethods;

namespace Game;

public sealed class InputWindowsNative : IInput
{
    private readonly int maxDelay;

    private readonly WowProcess process;
    private readonly CancellationToken token;
    public IInputExecutionObserver? ExecutionObserver { get; set; }

    public InputWindowsNative(WowProcess process, CancellationTokenSource cts, int maxDelay)
    {
        this.process = process;
        token = cts.Token;

        this.maxDelay = maxDelay;
    }

    private int DelayTime(int milliseconds)
    {
        return milliseconds + Random.Shared.Next(maxDelay);
    }

    // Virtual key codes for modifier keys
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt key

    /// <summary>
    /// Translates a virtual key code to the correct key for the current keyboard layout.
    /// For layout-dependent OEM keys (like =, -, etc.), this finds the actual key that
    /// produces that character on the target window's keyboard layout.
    /// </summary>
    /// <returns>Tuple of (actualKey, needsShift, needsCtrl, needsAlt)</returns>
    private (int key, bool shift, bool ctrl, bool alt) TranslateKeyForLayout(int key)
    {
        // Only translate layout-dependent OEM keys
        if (!IsLayoutDependentKey(key))
            return (key, false, false, false);

        // Get the character this key represents on US keyboard
        char? character = GetCharacterForUSKey(key);
        if (!character.HasValue)
            return (key, false, false, false);

        // Find the virtual key code that produces this character on the current layout
        if (GetVirtualKeyForCharacter(character.Value, process.MainWindowHandle,
            out int translatedKey, out bool needsShift, out bool needsCtrl, out bool needsAlt))
        {
            return (translatedKey, needsShift, needsCtrl, needsAlt);
        }

        // If translation failed, fall back to original key
        return (key, false, false, false);
    }

    private void PressModifiersDown(bool shift, bool ctrl, bool alt)
    {
        if (shift)
            PostKey(WM_KEYDOWN, VK_SHIFT, MakeKeyDownLParam(VK_SHIFT), true);
        if (ctrl)
            PostKey(WM_KEYDOWN, VK_CONTROL, MakeKeyDownLParam(VK_CONTROL), true);
        if (alt)
            PostKey(WM_KEYDOWN, VK_MENU, MakeKeyDownLParam(VK_MENU), true);
    }

    private void ReleaseModifiersUp(bool shift, bool ctrl, bool alt)
    {
        if (alt)
            PostKey(WM_KEYUP, VK_MENU, MakeKeyUpLParam(VK_MENU), false);
        if (ctrl)
            PostKey(WM_KEYUP, VK_CONTROL, MakeKeyUpLParam(VK_CONTROL), false);
        if (shift)
            PostKey(WM_KEYUP, VK_SHIFT, MakeKeyUpLParam(VK_SHIFT), false);
    }

    private void PostKey(uint message, int key, int lParam, bool down, int? durationMs = null)
    {
        if (PostMessage(process.MainWindowHandle, message, key, lParam))
            ExecutionObserver?.OnKeyboard((ConsoleKey)key, down, durationMs);
    }

    public void KeyDown(int key)
    {
        var (actualKey, shift, ctrl, alt) = TranslateKeyForLayout(key);
        PressModifiersDown(shift, ctrl, alt);

        bool extended = IsExtendedKey(actualKey);
        int lParam = MakeKeyDownLParam(actualKey, extended);
        PostKey(WM_KEYDOWN, actualKey, lParam, true);
    }

    public void KeyUp(int key)
    {
        var (actualKey, shift, ctrl, alt) = TranslateKeyForLayout(key);
        bool extended = IsExtendedKey(actualKey);
        int lParam = MakeKeyUpLParam(actualKey, extended);
        PostKey(WM_KEYUP, actualKey, lParam, false);

        ReleaseModifiersUp(shift, ctrl, alt);
    }

    public int PressRandom(int key, int milliseconds)
    {
        return PressRandom(key, milliseconds, token);
    }

    public int PressRandom(int key, int milliseconds, CancellationToken token)
    {
        var (actualKey, shift, ctrl, alt) = TranslateKeyForLayout(key);
        bool extended = IsExtendedKey(actualKey);
        int downLParam = MakeKeyDownLParam(actualKey, extended);
        int upLParam = MakeKeyUpLParam(actualKey, extended);

        // Press modifiers first if needed
        PressModifiersDown(shift, ctrl, alt);

        PostKey(WM_KEYDOWN, actualKey, downLParam, true);

        int delay = DelayTime(milliseconds);
        long holdStart = Stopwatch.GetTimestamp();
        token.WaitHandle.WaitOne(delay);

        PostKey(WM_KEYUP, actualKey, upLParam, false,
            (int)Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds);

        // Release modifiers
        ReleaseModifiersUp(shift, ctrl, alt);

        return delay;
    }

    public void PressFixed(int key, int milliseconds, CancellationToken token)
    {
        var (actualKey, shift, ctrl, alt) = TranslateKeyForLayout(key);
        bool extended = IsExtendedKey(actualKey);
        int downLParam = MakeKeyDownLParam(actualKey, extended);
        int upLParam = MakeKeyUpLParam(actualKey, extended);

        // Press modifiers first if needed
        PressModifiersDown(shift, ctrl, alt);

        PostKey(WM_KEYDOWN, actualKey, downLParam, true);
        long holdStart = Stopwatch.GetTimestamp();
        token.WaitHandle.WaitOne(milliseconds);
        PostKey(WM_KEYUP, actualKey, upLParam, false,
            (int)Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds);

        // Release modifiers
        ReleaseModifiersUp(shift, ctrl, alt);
    }

    public void LeftClick(Point p)
    {
        Point screenPoint = p;
        SetCursorPos(p);

        ScreenToClient(process.MainWindowHandle, ref p);
        int lparam = MakeLParam(p.X, p.Y);

        if (PostMessage(process.MainWindowHandle, WM_LBUTTONDOWN, 0, lparam))
            ExecutionObserver?.OnMouse("LeftDown", screenPoint);
        token.WaitHandle.WaitOne(DelayTime(maxDelay));
        if (PostMessage(process.MainWindowHandle, WM_LBUTTONUP, 0, lparam))
            ExecutionObserver?.OnMouse("LeftUp", screenPoint);
    }

    public void RightClick(Point p)
    {
        Point screenPoint = p;
        SetCursorPos(p);

        ScreenToClient(process.MainWindowHandle, ref p);
        int lparam = MakeLParam(p.X, p.Y);

        if (PostMessage(process.MainWindowHandle, WM_RBUTTONDOWN, 0, lparam))
            ExecutionObserver?.OnMouse("RightDown", screenPoint);
        token.WaitHandle.WaitOne(DelayTime(maxDelay));
        if (PostMessage(process.MainWindowHandle, WM_RBUTTONUP, 0, lparam))
            ExecutionObserver?.OnMouse("RightUp", screenPoint);
    }

    public void SetCursorPos(Point p)
    {
        if (WinAPI.NativeMethods.SetCursorPos(p.X, p.Y))
            ExecutionObserver?.OnMouse("Move", p);
    }

    public void SendText(string text)
    {
        foreach (char c in text)
        {
            if (PostMessage(process.MainWindowHandle, WM_CHAR, c, 0))
                ExecutionObserver?.OnText(c);
        }
    }
}
