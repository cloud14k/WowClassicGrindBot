using HidShared;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinAPI;

namespace Game;

/// <summary>Bot HID backend. Physical input is sent only while the selected WoW process is foreground.</summary>
public sealed class Esp32HidInput : IInput, IDisposable
{
    private readonly HidClient client;
    private readonly WowProcess process;
    private readonly CancellationTokenSource stopping;
    private readonly Task focusWatch;
    private readonly object sync = new();
    private readonly HashSet<int> heldKeys = [];
    private CancellationTokenSource focusLost = new();
    private bool wasForeground;
    private bool outputPaused;
    private bool disposed;
    private Exception? focusFailure;

    public IInputExecutionObserver? ExecutionObserver { get; set; }
    public string PortName => client.PortName;
    public bool IsConnected => client.IsConnected;

    public Esp32HidInput(string portName, WowProcess process, CancellationToken stop = default)
    {
        this.process = process;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(stop);
        client = new HidClient(portName, IsOutputAllowed);
        wasForeground = IsForeground();
        outputPaused = !wasForeground;
        if (!wasForeground) focusLost.Cancel();
        focusWatch = Task.Run(WatchFocusAsync);
    }

    public Task<byte> ProbeAsync(CancellationToken token = default) => client.ProbeAsync(token);

    public void CancelPending()
    {
        try { stopping.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool IsForeground()
    {
        nint window = NativeMethods.GetForegroundWindow();
        return window != 0 && NativeMethods.GetWindowThreadProcessId(window, out uint pid) != 0 && pid == process.Id;
    }

    private bool IsOutputAllowed() => !Volatile.Read(ref outputPaused) && IsForeground();

    private void WaitForForeground(CancellationToken token = default)
    {
        while (!IsOutputAllowed() || FocusToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref focusFailure) is Exception failure)
                throw new InvalidOperationException("HID foreground monitor stopped.", failure);
            token.ThrowIfCancellationRequested();
            stopping.Token.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }
        token.ThrowIfCancellationRequested();
        stopping.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref focusFailure) is Exception error)
            throw new InvalidOperationException("HID foreground monitor stopped.", error);
    }

    private CancellationToken FocusToken { get { lock (sync) return focusLost.Token; } }

    private async Task WatchFocusAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                bool foreground = IsForeground();
                Volatile.Write(ref outputPaused, !foreground);
                if (foreground != wasForeground)
                {
                    lock (sync)
                    {
                        if (!foreground)
                        {
                            Volatile.Write(ref outputPaused, true);
                            focusLost.Cancel();
                            // Release is required after focus loss so a held movement key cannot remain latched.
                            ReleaseAllBestEffort();
                        }
                        else
                        {
                            focusLost = new CancellationTokenSource();
                            Volatile.Write(ref outputPaused, false);
                            try
                            {
                                foreach (int key in heldKeys)
                                    client.KeyDownAsync(key).GetAwaiter().GetResult();
                            }
                            catch (OperationCanceledException) when (!IsForeground())
                            {
                                Volatile.Write(ref outputPaused, true);
                                focusLost.Cancel();
                                ReleaseAllBestEffort();
                                foreground = false;
                            }
                        }
                        wasForeground = foreground;
                    }
                }
                await Task.Delay(40, stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Volatile.Write(ref focusFailure, ex);
            lock (sync) focusLost.Cancel();
        }
    }

    private void ReleaseAllBestEffort()
    {
        try { client.ReleaseAllAsync().GetAwaiter().GetResult(); }
        catch { /* The port may already be unavailable; fail closed. */ }
    }

    public void KeyDown(int key) => KeyDown(key, CancellationToken.None);

    private void KeyDown(int key, CancellationToken token)
    {
        while (true)
        {
            WaitForForeground(token);
            lock (sync)
            {
                if (!IsOutputAllowed()) continue;
                try
                {
                    client.KeyDownAsync(key, token).GetAwaiter().GetResult();
                    heldKeys.Add(key);
                    ExecutionObserver?.OnKeyboard((ConsoleKey)key, true);
                    return;
                }
                catch (OperationCanceledException) when (!IsForeground()) { }
            }
        }
    }

    public void KeyUp(int key)
    {
        lock (sync)
        {
            heldKeys.Remove(key);
            try
            {
                if (IsOutputAllowed()) client.KeyUpAsync(key).GetAwaiter().GetResult();
                else ReleaseAllBestEffort();
            }
            catch (OperationCanceledException) when (!IsForeground())
            {
                ReleaseAllBestEffort();
            }
            catch (Exception) when (!stopping.IsCancellationRequested && !IsOutputAllowed())
            {
                ReleaseAllBestEffort();
            }
            ExecutionObserver?.OnKeyboard((ConsoleKey)key, false);
        }
    }

    public int PressRandom(int key, int milliseconds) => PressRandom(key, milliseconds, CancellationToken.None);

    public int PressRandom(int key, int milliseconds, CancellationToken token)
    {
        int duration = milliseconds + Random.Shared.Next(35);
        PressFixed(key, duration, token);
        return duration;
    }

    public void PressFixed(int key, int milliseconds, CancellationToken token)
    {
        if (milliseconds < 1) return;
        try
        {
            KeyDown(key, token);
            int remaining = milliseconds;
            while (remaining > 0)
            {
                WaitForForeground(token);
                int slice = Math.Min(remaining, 20);
                long start = Stopwatch.GetTimestamp();
                if (token.WaitHandle.WaitOne(slice)) token.ThrowIfCancellationRequested();
                if (IsForeground()) remaining -= Math.Max(1, Math.Min(slice, (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            }
        }
        finally { KeyUp(key); }
    }

    public void SetCursorPos(Point p)
    {
        TrySetCursorPos(p);
    }

    private bool TrySetCursorPos(Point p)
    {
        while (true)
        {
            WaitForForeground();
            try
            {
                client.MoveCursorAsync(p.X, p.Y, FocusToken).GetAwaiter().GetResult();
                ExecutionObserver?.OnMouse("Move", p);
                return true;
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
            catch (Exception) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
            catch (IOException ex) when (!stopping.IsCancellationRequested &&
                                          ex.Message.StartsWith("Cursor did not converge", StringComparison.Ordinal))
            {
                // Relative HID motion can miss a target if the physical mouse is moved
                // at the same time. Treat this as a failed targeting attempt; callers
                // must not crash the bot or click at an unknown location.
                return false;
            }
        }
    }

    public void RightClick(Point p) => Click(p, 2);
    public void LeftClick(Point p) => Click(p, 1);

    private void Click(Point p, byte button)
    {
        while (true)
        {
            if (!TrySetCursorPos(p))
                return;
            WaitForForeground();
            try
            {
                client.ClickAsync(button, FocusToken).GetAwaiter().GetResult();
                ExecutionObserver?.OnMouse(button == 1 ? "LeftDown" : "RightDown", p);
                ExecutionObserver?.OnMouse(button == 1 ? "LeftUp" : "RightUp", p);
                return;
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
            catch (Exception) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
        }
    }

    public void SendText(string text)
    {
        foreach (char character in text)
        {
            while (true)
            {
                WaitForForeground();
                try
                {
                    client.SendTextAsync(character.ToString(), FocusToken).GetAwaiter().GetResult();
                    ExecutionObserver?.OnText(character);
                    break;
                }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
            catch (Exception) when (!stopping.IsCancellationRequested && !IsOutputAllowed()) { }
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelPending();
        try { focusWatch.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        try { client.ReleaseAllAsync().GetAwaiter().GetResult(); }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            focusLost.Dispose();
            stopping.Dispose();
        }
    }
}
