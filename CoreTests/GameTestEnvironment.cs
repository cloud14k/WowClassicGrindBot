using Core;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;

using System;
using System.Diagnostics;
using System.Threading;

namespace CoreTests;

/// <summary>
/// Read-only game reader environment for diagnostics.
///
/// This deliberately does not call the application's normal process-start
/// registration. That startup method also performs recovery actions such as
/// deleting an invalid AddonConfig or FrameConfig. A diagnostic must report
/// those conditions, never repair the user's Bot environment.
/// </summary>
internal sealed class GameTestEnvironment : IDisposable
{
    private int disposed;
    private bool readerGraphInitialized;

    public CancellationTokenSource Cancellation { get; }
    public WowProcess Process { get; }
    public ServiceProvider Services { get; }

    private GameTestEnvironment(
        CancellationTokenSource cancellation,
        WowProcess process,
        ServiceProvider services)
    {
        Cancellation = cancellation;
        Process = process;
        Services = services;
    }

    /// <summary>
    /// Loads the configured frames without validating, deleting, or rewriting them.
    /// </summary>
    public static bool TryLoadFrameConfig(
        out DataFrame[] frames,
        out string reason)
    {
        frames = [];

        try
        {
            if (!FrameConfig.Exists())
            {
                reason = "frame_config.json does not exist.";
                return false;
            }

            DataFrameConfig config = FrameConfig.Load();
            frames = FrameConfig.LoadFrames();

            if (config.Version != FrameConfigMeta.Version)
            {
                reason =
                    $"FrameConfig version {config.Version} is not supported " +
                    $"(expected {FrameConfigMeta.Version}).";
                return false;
            }

            if (frames.Length <= 1)
            {
                reason = $"FrameConfig contains {frames.Length} frames; at least 2 are required.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Attaches to the existing WoW process and builds only the production reader
    /// graph. No BotController, GOAP, input, navigation, or recovery logic is built.
    /// </summary>
    public static bool TryCreate(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        out GameTestEnvironment? environment,
        out string reason)
    {
        environment = null;
        CancellationTokenSource? cancellation = null;
        WowProcess? process = null;
        ServiceProvider? services = null;

        try
        {
            Process? discovered = WowProcess.Get();
            if (discovered == null)
            {
                reason = "No running World of Warcraft process was found.";
                return false;
            }

            cancellation = new CancellationTokenSource();
            process = new WowProcess(
                cancellation,
                Options.Create(new StartupConfigPid { Id = discovered.Id }));

            ServiceCollection registrations = new();
            registrations.AddLogging();
            registrations.AddSingleton(loggerFactory);
            registrations.AddSingleton<Microsoft.Extensions.Logging.ILogger>(logger);
            registrations.AddSingleton(cancellation);
            registrations.AddSingleton(process);
            registrations.AddSingleton<Version>(process.FileVersion);
            registrations.AddSingleton<IOptions<StartupConfigReader>>(
                Options.Create(new StartupConfigReader
                {
                    Type = useDxgi
                        ? nameof(AddonDataProviderType.DXGI)
                        : nameof(AddonDataProviderType.WGC)
                }));

            // The live moveto suite resolves the same production IPPather used by
            // Navigation. Keep its defaults explicit because CoreTests has no host
            // configuration builder to bind these options from appsettings.json.
            registrations.AddSingleton<IOptions<StartupConfigPathing>>(
                Options.Create(new StartupConfigPathing
                {
                    Mode = nameof(StartupConfigPathing.Types.Local),
                    Engine = nameof(PathingEngine.Navmesh)
                }));
            registrations.AddSingleton<IOptions<NavmeshBakeOptions>>(
                Options.Create(new NavmeshBakeOptions()));
            registrations.AddSingleton<IOptions<NavmeshQueryOptions>>(
                Options.Create(new NavmeshQueryOptions()));
            registrations.AddSingleton<IOptions<SplineFollowerOptions>>(
                Options.Create(new SplineFollowerOptions()));
            registrations.AddSingleton<IOptions<StartupConfigDiagnostics>>(
                Options.Create(new StartupConfigDiagnostics()));

            // Shared production registration. This includes AddCoreBase() and the
            // real GetAddonDataProvider() registration, but not BotController.
            registrations.AddCoreNavigationEnvironment(logger);
            services = registrations.BuildServiceProvider();

            environment = new GameTestEnvironment(cancellation, process, services);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            services?.Dispose();
            process?.Dispose();
            cancellation?.Dispose();
            reason = ex.Message;
            return false;
        }
    }

    public void MarkReaderGraphInitialized() => readerGraphInitialized = true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Cancellation.Cancel();

        // If preflight failed before the reader graph was resolved, do not resolve
        // it from cleanup just to find disposable instances. Dispose only objects
        // the container already created.
        if (!readerGraphInitialized)
        {
            Process.Dispose();
            Services.Dispose();
            Cancellation.Dispose();
            return;
        }

        foreach (IReader reader in Services.GetServices<IReader>())
        {
            if (reader is IDisposable disposable)
                disposable.Dispose();
        }

        Services.GetRequiredService<Core.Database.AreaDB>().Dispose();
        Services.GetRequiredService<AddonReader>().DataReady.Dispose();
        Services.GetRequiredService<IWowScreen>().Dispose();
        Process.Dispose();
        Cancellation.Dispose();
    }
}
