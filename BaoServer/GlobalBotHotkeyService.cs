using Core;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using WinAPI;

namespace BlazorServer;

/// <summary>
/// Owns the process-wide Alt+F1 hotkey. RegisterHotKey delivers WM_HOTKEY to
/// the registering thread, so this service keeps a small dedicated message loop
/// instead of depending on the browser having focus.
/// </summary>
public sealed class GlobalBotHotkeyService : IHostedService, IDisposable
{
    private const int HotkeyId = 0x4553;

    private readonly IBotController botController;
    private readonly ILogger<GlobalBotHotkeyService> logger;
    private readonly ManualResetEventSlim ready = new(false);

    private Thread? thread;
    private uint threadId;
    private bool registered;
    private bool disposed;

    public GlobalBotHotkeyService(
        IBotController botController,
        ILogger<GlobalBotHotkeyService> logger)
    {
        this.botController = botController;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Global Alt+F1 bot hotkey is only available on Windows.");
            return Task.CompletedTask;
        }

        thread = new(MessageLoop)
        {
            IsBackground = true,
            Name = "Global Bot Hotkey"
        };
        thread.Start();

        ready.Wait(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (thread == null)
            return Task.CompletedTask;

        if (threadId != 0)
        {
            NativeMethods.PostThreadMessage(
                threadId, NativeMethods.WM_QUIT, 0, nint.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(1)))
            logger.LogWarning("Global bot hotkey message loop did not stop within one second.");

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        ready.Dispose();
    }

    private void MessageLoop()
    {
        threadId = NativeMethods.GetCurrentThreadId();

        try
        {
            // A thread message queue must exist before PostThreadMessage can wake it.
            NativeMethods.PeekMessage(
                out _, nint.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

            registered = NativeMethods.RegisterHotKey(
                nint.Zero,
                HotkeyId,
                NativeMethods.MOD_ALT,
                NativeMethods.VK_F1);

            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();
                logger.LogWarning(
                    "Unable to register global Alt+F1 bot hotkey. Win32 error: {Error}.",
                    error);
                return;
            }

            logger.LogInformation("Global bot hotkey registered: Alt+F1.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to initialize the global bot hotkey.");
            return;
        }
        finally
        {
            ready.Set();
        }

        try
        {
            while (NativeMethods.GetMessage(
                       out NativeMethods.MSG message,
                       nint.Zero,
                       0,
                       0) > 0)
            {
                if (message.message != NativeMethods.WM_HOTKEY ||
                    message.wParam != (nuint)HotkeyId)
                {
                    continue;
                }

                try
                {
                    botController.ToggleBotStatus();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Global Alt+F1 bot toggle failed.");
                }
            }
        }
        finally
        {
            if (registered)
            {
                NativeMethods.UnregisterHotKey(nint.Zero, HotkeyId);
                registered = false;
            }
        }
    }
}
