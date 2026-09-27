using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace HidTester;

internal enum HidEventKind { KeyDown, KeyUp, MouseMove, LeftDown, LeftUp, RightDown, RightUp, Wheel }
internal sealed record HidEvent(HidEventKind Kind, int Value, int X, int Y, long Timestamp, bool Injected);

/// <summary>Observes low-level Windows input events independently of CDC acknowledgements.</summary>
internal sealed class WindowsHidMonitor : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205, WmMouseWheel = 0x020A;
    private readonly ConcurrentQueue<HidEvent> events = new();
    private readonly HookProc keyboardProc, mouseProc;
    private readonly IntPtr keyboardHook, mouseHook;
    public event Action<HidEvent>? EventObserved;
    public static long Now => Stopwatch.GetTimestamp();

    public WindowsHidMonitor()
    {
        keyboardProc = KeyboardCallback; mouseProc = MouseCallback;
        IntPtr module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardProc, module, 0);
        mouseHook = SetWindowsHookEx(WhMouseLl, mouseProc, module, 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"Unable to install Windows HID event hooks (error {Marshal.GetLastWin32Error()}).");
    }

    public IReadOnlyList<HidEvent> Since(long timestamp) => events.Where(e => e.Timestamp >= timestamp).ToArray();

    private IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int msg = unchecked((int)message.ToInt64());
            if (msg is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                var key = Marshal.PtrToStructure<KbdLlHookStruct>(data);
                Publish(new(msg is WmKeyDown or WmSysKeyDown ? HidEventKind.KeyDown : HidEventKind.KeyUp,
                    (int)key.VirtualKey, 0, 0, Now, (key.Flags & 0x10) != 0));
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }

    private IntPtr MouseCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int msg = unchecked((int)message.ToInt64());
            var mouse = Marshal.PtrToStructure<MsLlHookStruct>(data);
            HidEventKind? kind = msg switch
            {
                WmMouseMove => HidEventKind.MouseMove, WmLButtonDown => HidEventKind.LeftDown,
                WmLButtonUp => HidEventKind.LeftUp, WmRButtonDown => HidEventKind.RightDown,
                WmRButtonUp => HidEventKind.RightUp, WmMouseWheel => HidEventKind.Wheel, _ => null
            };
            if (kind.HasValue)
                Publish(new(kind.Value, kind == HidEventKind.Wheel ? unchecked((short)(mouse.MouseData >> 16)) : 0,
                    mouse.Point.X, mouse.Point.Y, Now, (mouse.Flags & 0x01) != 0));
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }

    private void Publish(HidEvent ev)
    {
        events.Enqueue(ev);
        while (events.Count > 5000) events.TryDequeue(out _);
        EventObserved?.Invoke(ev);
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(keyboardHook); UnhookWindowsHookEx(mouseHook);
        GC.KeepAlive(keyboardProc); GC.KeepAlive(mouseProc);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public Point Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
