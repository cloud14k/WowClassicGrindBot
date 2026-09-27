using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HidShared;

/// <summary>Stateful keyboard and mouse client using the shared framed CDC protocol.</summary>
public sealed class HidClient : IAsyncDisposable
{
    private readonly SerialClient serial;
    private readonly Func<bool>? allowOutput;
    private readonly HashSet<byte> keys = [];
    private byte modifiers, buttons;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    public event Action<Exception>? ConnectionLost { add => serial.ConnectionLost += value; remove => serial.ConnectionLost -= value; }
    public event Action<string>? PcLog;
    public string PortName => serial.PortName;
    public bool IsConnected => serial.IsOpen;

    public HidClient(string portName, Func<bool>? allowOutput = null)
    {
        serial = new SerialClient(portName);
        this.allowOutput = allowOutput;
    }

    /// <summary>Checks that the CDC HID bridge is reachable without producing a visible input action.</summary>
    public async Task<byte> ProbeAsync(CancellationToken token = default)
    {
        await stateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Re-send the current keyboard state. A zero report here would
            // release movement keys if the user probes while the bot is active.
            if ((modifiers != 0 || keys.Count != 0) && allowOutput is not null && !allowOutput())
                throw new OperationCanceledException("HID keyboard output paused while target is in background.");
            byte[] payload = new byte[HidProtocol.KeyboardPayloadLength];
            payload[0] = modifiers;
            int index = 2;
            foreach (byte key in keys.Order()) payload[index++] = key;
            byte id = await serial.SendReportAsync(HidProtocol.KeyboardReportId, payload, token).ConfigureAwait(false);
            PcLog?.Invoke($"ACK [{id}] PROBE");
            return id;
        }
        finally { stateLock.Release(); }
    }

    public async Task KeyDownAsync(int vk, CancellationToken token = default)
    {
        await stateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte previousModifiers = modifiers;
            bool isModifier = Modifier(vk, out byte bit);
            byte usage = isModifier ? (byte)0 : MapKey(vk);
            bool added = false;
            if (isModifier) modifiers |= bit; else added = keys.Add(usage);
            try
            {
                if (keys.Count > 6) throw new InvalidOperationException("HID boot keyboard supports at most six ordinary keys.");
                await SendKeyboardAsync(token).ConfigureAwait(false);
            }
            catch
            {
                modifiers = previousModifiers;
                if (added) keys.Remove(usage);
                throw;
            }
        }
        finally { stateLock.Release(); }
    }

    public async Task KeyUpAsync(int vk, CancellationToken token = default)
    {
        await stateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Modifier(vk, out byte bit)) modifiers &= (byte)~bit; else keys.Remove(MapKey(vk));
            await SendKeyboardAsync(token).ConfigureAwait(false);
        }
        finally { stateLock.Release(); }
    }

    public async Task<int> HoldAsync(int vk, int milliseconds, CancellationToken token = default)
    {
        if (milliseconds < 1) return 0;
        var watch = Stopwatch.StartNew();
        await KeyDownAsync(vk, token).ConfigureAwait(false);
        try { await Task.Delay(milliseconds, token).ConfigureAwait(false); }
        finally { await KeyUpAsync(vk, CancellationToken.None).ConfigureAwait(false); }
        return (int)watch.ElapsedMilliseconds;
    }

    public async Task AltHomeAsync(int milliseconds = 120, CancellationToken token = default)
    {
        await KeyDownAsync(0x12, token).ConfigureAwait(false);
        try { await HoldAsync(0x24, milliseconds, token).ConfigureAwait(false); }
        finally { await KeyUpAsync(0x12, CancellationToken.None).ConfigureAwait(false); }
    }

    public async Task SendTextAsync(string text, CancellationToken token = default)
    {
        foreach (char c in text)
        {
            var (usage, shift) = MapCharacter(c);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte oldMods = modifiers; byte[] oldKeys = keys.ToArray();
                keys.Clear(); modifiers = shift ? (byte)2 : (byte)0; keys.Add(usage);
                try
                {
                    await SendKeyboardAsync(token).ConfigureAwait(false);
                    await Task.Delay(25, token).ConfigureAwait(false);
                    keys.Clear(); modifiers = 0;
                    await SendKeyboardAsync(token).ConfigureAwait(false);
                    await Task.Delay(10, token).ConfigureAwait(false);
                }
                finally
                {
                    keys.Clear();
                    if (!token.IsCancellationRequested)
                    {
                        foreach (byte oldKey in oldKeys) keys.Add(oldKey);
                        modifiers = oldMods;
                    }
                    else modifiers = 0;
                    await SendKeyboardAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally { stateLock.Release(); }
        }
    }

    public async Task MoveRelativeAsync(int x, int y, int wheel = 0, int pan = 0, CancellationToken token = default)
    {
        while (x != 0 || y != 0 || wheel != 0 || pan != 0)
        {
            int dx = Math.Clamp(x, -127, 127), dy = Math.Clamp(y, -127, 127);
            int dw = Math.Clamp(wheel, -127, 127), dp = Math.Clamp(pan, -127, 127);
            await SendMouseAsync(dx, dy, dw, dp, token).ConfigureAwait(false);
            x -= dx; y -= dy; wheel -= dw; pan -= dp;
        }
    }

    public async Task MoveCursorAsync(int x, int y, CancellationToken token = default)
    {
        double gainX = 2, gainY = 2;
        int noMotionX = 0, noMotionY = 0;
        NativePoint point = default;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            if (!GetCursorPos(out point)) throw new InvalidOperationException("GetCursorPos failed.");
            int dx = x - point.X, dy = y - point.Y;
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return;
            int stepX = CursorStep(dx, gainX, noMotionX);
            int stepY = CursorStep(dy, gainY, noMotionY);
            await MoveRelativeAsync(stepX, stepY, token: token).ConfigureAwait(false);
            await Task.Delay(25, token).ConfigureAwait(false);
            if (!GetCursorPos(out NativePoint next)) throw new InvalidOperationException("GetCursorPos failed.");
            UpdateCursorGain(stepX, next.X - point.X, ref gainX, ref noMotionX);
            UpdateCursorGain(stepY, next.Y - point.Y, ref gainY, ref noMotionY);
            point = next;
        }
        throw new IOException($"Cursor did not converge to ({x}, {y}); last Windows position ({point.X}, {point.Y}).");
    }

    private static int CursorStep(int error, double gain, int noMotion)
    {
        if (error == 0) return 0;
        int magnitude = (int)Math.Round(Math.Abs(error) / gain, MidpointRounding.AwayFromZero);
        return Math.Sign(error) * Math.Clamp(Math.Max(magnitude, noMotion >= 2 ? 2 : 1), 1, 16);
    }

    private static void UpdateCursorGain(int sent, int moved, ref double gain, ref int noMotion)
    {
        if (sent == 0) return;
        if (moved == 0) { noMotion++; return; }
        noMotion = 0;
        if (Math.Sign(sent) == Math.Sign(moved))
            gain = Math.Clamp(0.5 * gain + 0.5 * Math.Abs((double)moved / sent), 0.5, 12);
    }

    public async Task MouseButtonAsync(byte mask, bool down, CancellationToken token = default)
    {
        byte previousButtons = buttons;
        if (down) buttons |= mask; else buttons &= (byte)~mask;
        try { await SendMouseAsync(0, 0, 0, 0, token).ConfigureAwait(false); }
        catch
        {
            // A failed mouse-up may still have reached the device. Keep the
            // desired released state so the next report cannot re-press it.
            if (down) buttons = previousButtons;
            throw;
        }
    }

    public async Task ClickAsync(byte mask, CancellationToken token = default)
    {
        await MouseButtonAsync(mask, true, token).ConfigureAwait(false);
        try { await Task.Delay(50, token).ConfigureAwait(false); }
        finally { await MouseButtonAsync(mask, false, CancellationToken.None).ConfigureAwait(false); }
    }

    public async Task ReleaseAllAsync(CancellationToken token = default)
    {
        await stateLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            keys.Clear(); modifiers = 0; buttons = 0;
            await SendKeyboardAsync(token).ConfigureAwait(false);
            await SendMouseAsync(0, 0, 0, 0, token).ConfigureAwait(false);
        }
        finally { stateLock.Release(); }
    }

    private async Task SendKeyboardAsync(CancellationToken token)
    {
        if ((modifiers != 0 || keys.Count != 0) && allowOutput is not null && !allowOutput())
            throw new OperationCanceledException("HID keyboard output paused while target is in background.");
        byte[] payload = new byte[8]; payload[0] = modifiers;
        int index = 2; foreach (byte key in keys.Order()) payload[index++] = key;
        byte id = await serial.SendReportAsync(HidProtocol.KeyboardReportId, payload, token).ConfigureAwait(false);
        PcLog?.Invoke($"ACK [{id}] KEYBOARD mods={modifiers:X2} keys={string.Join(',', keys.Select(KeyName))}");
    }

    private async Task SendMouseAsync(int x, int y, int wheel, int pan, CancellationToken token)
    {
        if ((buttons != 0 || x != 0 || y != 0 || wheel != 0 || pan != 0) && allowOutput is not null && !allowOutput())
            throw new OperationCanceledException("HID mouse output paused while target is in background.");
        byte[] payload = [buttons, unchecked((byte)x), unchecked((byte)y), unchecked((byte)wheel), unchecked((byte)pan)];
        byte id = await serial.SendReportAsync(HidProtocol.MouseReportId, payload, token).ConfigureAwait(false);
        PcLog?.Invoke($"ACK [{id}] MOUSE buttons={buttons:X2} x={x} y={y} wheel={wheel}");
    }

    public static byte MapKey(int key)
    {
        if (key >= 'A' && key <= 'Z') return (byte)(key - 'A' + 4);
        if (key >= '1' && key <= '9') return (byte)(key - '1' + 0x1E);
        if (key == '0') return 0x27;
        if (key >= 0x100 && key <= 0x1FF) return (byte)key;
        return key switch
        {
            0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C,
            0x21 => 0x4B, 0x22 => 0x4E, 0x23 => 0x4D, 0x24 => 0x4A,
            0x25 => 0x50, 0x26 => 0x52, 0x27 => 0x4F, 0x28 => 0x51,
            0x2D => 0x49, 0x2E => 0x4C,
            0x60 => 0x62, 0x61 => 0x59, 0x62 => 0x5A, 0x63 => 0x5B,
            0x64 => 0x5C, 0x65 => 0x5D, 0x66 => 0x5E, 0x67 => 0x5F,
            0x68 => 0x60, 0x69 => 0x61, 0x6A => 0x55, 0x6B => 0x57,
            0x6D => 0x56, 0x6E => 0x63, 0x6F => 0x54,
            0x70 => 0x3A, 0x71 => 0x3B, 0x72 => 0x3C, 0x73 => 0x3D, 0x74 => 0x3E,
            0x75 => 0x3F, 0x76 => 0x40, 0x77 => 0x41, 0x78 => 0x42, 0x79 => 0x43,
            0x7A => 0x44, 0x7B => 0x45,
            0xBD => 0x2D, 0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31,
            0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37,
            0xBF => 0x38, _ => throw new NotSupportedException($"No HID usage for virtual key 0x{key:X}.")
        };
    }

    private static bool Modifier(int vk, out byte bit) { bit = vk switch { 0x10 => 2, 0x11 => 1, 0x12 => 4, _ => 0 }; return bit != 0; }
    private static string KeyName(byte usage) => usage switch { >= 0x04 and <= 0x1D => ((char)('A' + usage - 4)).ToString(), >= 0x1E and <= 0x27 => "D" + (usage == 0x27 ? "0" : (usage - 0x1D).ToString()), 0x2C => "SPACE", 0x4A => "HOME", _ => $"0x{usage:X2}" };

    private static (byte usage, bool shift) MapCharacter(char c)
    {
        if (c is >= 'a' and <= 'z') return ((byte)(c - 'a' + 4), false);
        if (c is >= 'A' and <= 'Z') return ((byte)(c - 'A' + 4), true);
        const string plain = "1234567890 -=[]\\;'`,./", shifted = "!@#$%^&*() _+{}|:\"~<>?";
        int i = plain.IndexOf(c);
        if (i >= 0) return (MapKey(plain[i] switch { '-' => 0xBD, '=' => 0xBB, '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC, ';' => 0xBA, '\'' => 0xDE, '`' => 0xC0, ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, _ => plain[i] }), false);
        i = shifted.IndexOf(c);
        if (i >= 0) return (MapCharacter(plain[i]).usage, true);
        if (c == '\n') return (0x28, false);
        throw new NotSupportedException($"Unsupported text character U+{(int)c:X4}.");
    }

    public ValueTask DisposeAsync() { stateLock.Dispose(); return serial.DisposeAsync(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
}
