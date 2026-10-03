using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Game;

/// <summary>Switches the bot's input implementation while serializing it with in-flight actions.</summary>
internal sealed class InputBackendRouter : IInput, IDisposable
{
    private readonly object gate = new();
    private readonly Func<string, string, IInput> createBackend;
    private readonly ILogger logger;
    private readonly InputBackendSettings settings;
    private IInput active;
    private IInputExecutionObserver? observer;
    private bool disposed;

    public InputBackendRouter(WowProcess process, CancellationTokenSource stopping, InputBackendSettings settings, ILogger logger)
        : this(settings, (backend, port) => backend == "Hid"
            ? new Esp32HidInput(port, process, stopping.Token)
            : new InputWindowsNative(process, stopping, InputDuration.FastPress), logger)
    {
    }

    internal InputBackendRouter(InputBackendSettings settings, Func<string, string, IInput> createBackend, ILogger logger)
    {
        this.createBackend = createBackend;
        this.logger = logger;
        this.settings = settings;
        active = createBackend(settings.Backend, settings.Port);
        settings.Changed += OnSettingsChanged;
    }

    public bool IsHid
    {
        get { lock (gate) return active is Esp32HidInput; }
    }

    public bool IsHidConnected
    {
        get { lock (gate) return active is Esp32HidInput hid && hid.IsConnected; }
    }

    public string? HidPort
    {
        get { lock (gate) return active is Esp32HidInput hid ? hid.PortName : null; }
    }

    public Task<byte> ProbeHidAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            if (active is not Esp32HidInput hid)
                throw new InvalidOperationException("Bot 当前没有使用 ESP32 HID 后端。");
            return hid.ProbeAsync(token);
        }
    }

    public IInputExecutionObserver? ExecutionObserver
    {
        get { lock (gate) return observer; }
        set
        {
            lock (gate)
            {
                observer = value;
                ApplyObserver(active, value);
            }
        }
    }

    private void OnSettingsChanged()
    {
        // An HID action can be waiting indefinitely for WoW to regain focus.
        // Wake it before taking the gate so changing backends cannot block the UI.
        if (Volatile.Read(ref active) is Esp32HidInput currentHid)
            currentHid.CancelPending();
        lock (gate)
        {
            if (disposed) return;

            IInput next = createBackend(settings.Backend, settings.Port);
            try
            {
                ApplyObserver(next, observer);
            }
            catch
            {
                DisposeBackend(next);
                throw;
            }
            IInput previous = active;
            active = next;
            // The replacement is usable. Failure to clean up an unresponsive
            // old device must not dispose it or roll back the saved selection.
            try { DisposeBackend(previous); }
            catch (Exception ex) { logger.LogWarning(ex, "Previous input backend cleanup failed after switching to {Backend}", settings.Backend); }
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Input backend switched to {Backend} (port {Port})", settings.Backend, settings.Port);
        }
    }

    private static void ApplyObserver(IInput backend, IInputExecutionObserver? value)
    {
        switch (backend)
        {
            case InputWindowsNative windows: windows.ExecutionObserver = value; break;
            case Esp32HidInput hid: hid.ExecutionObserver = value; break;
        }
    }

    private static void DisposeBackend(IInput backend)
    {
        if (backend is IDisposable disposable)
            disposable.Dispose();
    }

    public void KeyDown(int key) { lock (gate) active.KeyDown(key); }
    public void KeyUp(int key) { lock (gate) active.KeyUp(key); }
    public int PressRandom(int key, int milliseconds) { lock (gate) return active.PressRandom(key, milliseconds); }
    public int PressRandom(int key, int milliseconds, CancellationToken token) { lock (gate) return active.PressRandom(key, milliseconds, token); }
    public void PressFixed(int key, int milliseconds, CancellationToken token) { lock (gate) active.PressFixed(key, milliseconds, token); }
    public void SetCursorPos(SixLabors.ImageSharp.Point point) { lock (gate) active.SetCursorPos(point); }
    public void RightClick(SixLabors.ImageSharp.Point point) { lock (gate) active.RightClick(point); }
    public void LeftClick(SixLabors.ImageSharp.Point point) { lock (gate) active.LeftClick(point); }
    public void SendText(string text) { lock (gate) active.SendText(text); }

    public void Dispose()
    {
        if (Volatile.Read(ref active) is Esp32HidInput currentHid)
            currentHid.CancelPending();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            settings.Changed -= OnSettingsChanged;
            DisposeBackend(active);
        }
    }
}
