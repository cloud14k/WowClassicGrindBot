using Core;
using Core.GOAP;
using Core.Goals;

using Game;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using SharedLib;
using SharedLib.NpcFinder;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CoreTests;

/// <summary>
/// The live production reader/profile/session setup shared by integration tests.
/// It intentionally does not start the BotController: callers decide whether to
/// drive a specific production goal or the complete production GoapAgent.
/// </summary>
internal sealed class ProductionTestSession : IDisposable
{
    private const int PreflightTimeoutMs = 5000;
    private const int UpdateIntervalMs = 50;

    private readonly ILogger logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly bool useDxgi;
    private CancellationTokenSource? updateCancellation;
    private Thread? updateThread;
    private int disposed;

    private ProductionTestSession(
        GameTestEnvironment environment,
        ServiceProvider services,
        IServiceScope scope,
        ClassConfiguration classConfig,
        string profilePath,
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi)
    {
        Environment = environment;
        Services = services;
        Scope = scope;
        Config = classConfig;
        ProfilePath = profilePath;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        this.useDxgi = useDxgi;

        Screen = Root.GetRequiredService<IWowScreen>();
        AddonReader = Root.GetRequiredService<AddonReader>();
        KeyBindingsReader = Root.GetRequiredService<KeyBindingsReader>();
        NpcNameFinder = Root.GetRequiredService<NpcNameFinder>();
        Bits = Root.GetRequiredService<AddonBits>();
        PlayerReader = Root.GetRequiredService<PlayerReader>();

        TargetFinder = Session.GetRequiredService<TargetFinder>();
        NpcNameTargeting = Session.GetRequiredService<NpcNameTargeting>();
        Input = Session.GetRequiredService<ConfigurableInput>();
        StopMoving = Session.GetRequiredService<StopMoving>();
        CombatLog = Session.GetRequiredService<CombatLog>();
        GoalCancellation = Session.GetRequiredService<CancellationTokenSource<GoapAgent>>();
        Agent = Session.GetRequiredService<GoapAgent>();
    }

    public GameTestEnvironment Environment { get; }
    public ServiceProvider Services { get; }
    public IServiceScope Scope { get; }
    public IServiceProvider Root => Environment.Services;
    public IServiceProvider Session => Scope.ServiceProvider;
    public ClassConfiguration Config { get; }
    public string ProfilePath { get; }

    public IWowScreen Screen { get; }
    public AddonReader AddonReader { get; }
    public KeyBindingsReader KeyBindingsReader { get; }
    public NpcNameFinder NpcNameFinder { get; }
    public AddonBits Bits { get; }
    public PlayerReader PlayerReader { get; }
    public TargetFinder TargetFinder { get; }
    public NpcNameTargeting NpcNameTargeting { get; }
    public ConfigurableInput Input { get; }
    public StopMoving StopMoving { get; }
    public CombatLog CombatLog { get; }
    public CancellationTokenSource<GoapAgent> GoalCancellation { get; }
    public GoapAgent Agent { get; }

    public static bool TryCreate(
        ILogger logger,
        ILoggerFactory loggerFactory,
        bool useDxgi,
        out ProductionTestSession? session,
        out string reason)
    {
        session = null;
        GameTestEnvironment? environment = null;
        ServiceProvider? services = null;
        IServiceScope? scope = null;

        try
        {
            if (!GameTestEnvironment.TryCreate(
                    logger,
                    loggerFactory,
                    useDxgi,
                    out environment,
                    out reason))
            {
                return false;
            }

            IServiceProvider root = environment.Services;
            IWowScreen screen = root.GetRequiredService<IWowScreen>();
            AddonReader addonReader = root.GetRequiredService<AddonReader>();
            KeyBindingsReader keyBindingsReader = root.GetRequiredService<KeyBindingsReader>();
            WowProcessInput flushInput = root.GetRequiredService<WowProcessInput>();
            PlayerReader playerReader = root.GetRequiredService<PlayerReader>();
            environment.MarkReaderGraphInitialized();

            bool readersReady = AddonRefreshHelper.RefreshAddonAndWaitForReaders(
                screen,
                addonReader,
                flushInput,
                keyBindingsReader,
                environment.Cancellation.Token,
                AddonRefreshHelper.DefaultTimeoutMs,
                null,
                out AddonRefreshStats refreshStats,
                out string keyBindingsError);

            Console.WriteLine($"Binding {keyBindingsReader.ReceivedCount}/{keyBindingsReader.ExpectedCount}");
            Console.WriteLine($"DataReady={addonReader.DataReady.IsSet}");
            Console.WriteLine($"KeyBindingsInitialized={keyBindingsReader.IsInitialized}");

            logger.LogInformation(
                "LIVE INITIALIZATION refresh: Ready={Ready}; Updates={UpdateCount}; " +
                "Frequency={UpdatesPerSecond:F1}/s; DataReady={DataReady}; " +
                "KeyBindingsInitialized={KeyBindingsInitialized}; ExpectedCount={ExpectedCount}; " +
                "ReceivedCount={ReceivedCount}; FullResets={FullResets}; " +
                "ExpectedRefreshResets={ExpectedRefreshResets}; LastResetReason={LastResetReason}; " +
                "GlobalTime={GlobalTime}",
                readersReady,
                refreshStats.UpdateCount,
                refreshStats.UpdatesPerSecond,
                addonReader.DataReady.IsSet,
                keyBindingsReader.IsInitialized,
                keyBindingsReader.ExpectedCount,
                keyBindingsReader.ReceivedCount,
                refreshStats.FullResetCount,
                refreshStats.ExpectedRefreshResetCount,
                refreshStats.LastResetReason,
                refreshStats.GlobalTime);

            if (!readersReady)
            {
                reason = keyBindingsError;
                return false;
            }

            if (!WaitForPlayerData(
                    screen,
                    addonReader,
                    playerReader,
                    environment.Cancellation.Token,
                    out reason))
            {
                return false;
            }

            if (!TryLoadClassConfiguration(
                    root,
                    playerReader,
                    out ClassConfiguration classConfig,
                    out string profilePath,
                    out reason))
            {
                return false;
            }

            ServiceCollection registrations = new();
            registrations.AddScoped<ClassConfiguration>(_ => classConfig);
            GoalFactory.Create(registrations, root, classConfig);

            // Match BotController.CreateSession: RouteInfo and GoapAgent are
            // production session services, not test replacements.
            registrations.AddScoped<IEnumerable<IRouteProvider>>(sp =>
                sp.GetServices<GoapGoal>().OfType<IRouteProvider>());
            registrations.AddScoped<RouteInfo>();
            registrations.AddScoped<GoapAgent>();

            services = registrations.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });
            scope = services.CreateScope();

            session = new ProductionTestSession(
                environment,
                services,
                scope,
                classConfig,
                profilePath,
                logger,
                loggerFactory,
                useDxgi);

            session.StartUpdates();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            scope?.Dispose();
            services?.Dispose();
            environment?.Dispose();
            reason = ex.ToString();
            return false;
        }
    }

    public void StartUpdates()
    {
        if (updateThread is not null)
            return;

        updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            Environment.Cancellation.Token,
            GoalCancellation.Token);
        CancellationToken token = updateCancellation.Token;

        updateThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                Screen.Update();
                AddonReader.Update();
                if (Screen.Enabled)
                    NpcNameFinder.Update();

                token.WaitHandle.WaitOne(UpdateIntervalMs);
            }
        })
        {
            IsBackground = true,
            Name = "CoreTests.LiveProductionUpdates"
        };

        Screen.Enabled = true;
        updateThread.Start();
    }

    public static bool HasValidTarget(AddonBits bits) =>
        bits.Target() && bits.Target_NotDead() && bits.Target_Hostile();

    public static bool IsProductionPullComplete(
        AddonBits bits,
        CombatLog combatLog,
        PlayerReader playerReader) =>
        combatLog.PlayerOrPetCombat() &&
        bits.Target_Combat() &&
        combatLog.ToPull.Contains(playerReader.TargetGuid);

    public static string? ResolveClickedAction(
        ClassConfiguration config,
        int clickedKey,
        IEnumerable<KeyAction>? extraActions = null)
    {
        if (clickedKey == (int)ConsoleKey.NoName)
            return null;

        IEnumerable<KeyAction> actions = config.Combat.Sequence
            .Concat(config.Pull.Sequence)
            .Concat(extraActions ?? [
                config.AutoAttack,
                config.PetAttack,
                config.Approach
            ]);

        foreach (KeyAction action in actions)
        {
            if (action.ConsoleKeyFormHash == clickedKey ||
                (int)action.ConsoleKey == clickedKey % 1000)
                return action.Name;
        }

        return null;
    }

    public static string FormatActions(IEnumerable<KeyAction> actions) =>
        string.Join(" -> ", actions.Select(FormatAction));

    public static string FormatAction(KeyAction action) =>
        $"{action.Name} [{action.BindingID}] {action.Modifier.ToPrefix()}{action.ConsoleKey}";

    public static void PrintTargetAcquired(
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits)
    {
        Console.WriteLine("TARGET ACQUIRED");
        Console.WriteLine($"Target Name: {addonReader.TargetName}");
        Console.WriteLine($"Target ID: {playerReader.TargetId}");
        Console.WriteLine($"Target GUID: {playerReader.TargetGuid}");
        Console.WriteLine($"Level: {playerReader.TargetLevel}");
        Console.WriteLine($"Health: {playerReader.TargetHealth()}/{playerReader.TargetMaxHealth()} ({playerReader.TargetHealthPercent()}%)");
        Console.WriteLine($"Hostile: {bits.Target_Hostile()}");
        Console.WriteLine($"Dead: {bits.Target_Dead()}");
    }

    private static bool WaitForPlayerData(
        IWowScreen screen,
        AddonReader addonReader,
        PlayerReader playerReader,
        CancellationToken token,
        out string error)
    {
        Stopwatch timer = Stopwatch.StartNew();
        screen.Enabled = true;

        while (timer.ElapsedMilliseconds < PreflightTimeoutMs && !token.IsCancellationRequested)
        {
            screen.Update();
            addonReader.Update();

            if (addonReader.DataReady.IsSet &&
                playerReader.UIMapId.Value > 0 &&
                playerReader.HealthMax() > 0)
            {
                error = string.Empty;
                return true;
            }

            token.WaitHandle.WaitOne(UpdateIntervalMs);
        }

        error =
            $"live player data was not ready after {PreflightTimeoutMs} ms " +
            $"(DataReady={addonReader.DataReady.IsSet}, UIMapId={playerReader.UIMapId.Value})";
        return false;
    }

    private static bool TryLoadClassConfiguration(
        IServiceProvider root,
        PlayerReader playerReader,
        out ClassConfiguration config,
        out string profilePath,
        out string error)
    {
        config = null!;
        profilePath = string.Empty;

        DataConfig dataConfig = root.GetRequiredService<DataConfig>();
        if (!Directory.Exists(dataConfig.Class))
        {
            error = $"class profile directory does not exist: {dataConfig.Class}";
            return false;
        }

        string className = playerReader.Class.ToString();
        int level = Math.Max(1, playerReader.Level.Value);
        List<ProfileCandidate> candidates = [];

        foreach (string file in Directory.EnumerateFiles(dataConfig.Class, "*.json*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (!fileName.StartsWith(className + "_", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                ClassConfiguration? candidate = JsonConvert.DeserializeObject<ClassConfiguration>(File.ReadAllText(file));
                if (candidate?.Pull.Sequence.Length > 0)
                    candidates.Add(new(candidate, file, ParseProfileLevel(fileName)));
            }
            catch
            {
                // Malformed profiles are not automatic live-test candidates.
            }
        }

        if (candidates.Count == 0)
        {
            error = $"no Pull profile found for {className}";
            return false;
        }

        ProfileCandidate selected = candidates
            .Where(x => x.StartLevel <= level)
            .OrderByDescending(x => x.StartLevel)
            .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (selected.Config is null)
        {
            selected = candidates
                .OrderBy(x => x.StartLevel)
                .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        try
        {
            // This is the same production initialization performed by BotController.
            selected.Config.FileName = Path.GetRelativePath(dataConfig.Class, selected.File);
            selected.Config.Initialise(root, new Dictionary<int, string>());
            config = selected.Config;
            profilePath = selected.File;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"profile '{selected.File}' failed ClassConfiguration.Initialise: {ex.Message}";
            return false;
        }
    }

    private static int ParseProfileLevel(string fileName)
    {
        int separator = fileName.IndexOf('_');
        if (separator < 0)
            return 0;

        string suffix = fileName[(separator + 1)..];
        int end = 0;
        while (end < suffix.Length && char.IsDigit(suffix[end]))
            end++;

        return end > 0 && int.TryParse(suffix[..end], out int level) ? level : 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            if (Agent.Active)
                Agent.Active = false;
        }
        catch (Exception ex) { logger.LogWarning(ex, "GoapAgent stop failed"); }
        try { StopMoving.Stop(); } catch (Exception ex) { logger.LogWarning(ex, "StopMoving.Stop failed"); }
        try { Input.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "ConfigurableInput.Reset failed"); }
        try { TargetFinder.Reset(); } catch (Exception ex) { logger.LogWarning(ex, "TargetFinder.Reset failed"); }
        try { NpcNameTargeting.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameTargeting reset failed"); }
        try { NpcNameFinder.ChangeNpcType(NpcNames.None); } catch (Exception ex) { logger.LogWarning(ex, "NpcNameFinder reset failed"); }
        try { GoalCancellation.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Goal cancellation failed"); }
        try { updateCancellation?.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Update cancellation failed"); }
        try { Environment.Cancellation.Cancel(); } catch (Exception ex) { logger.LogWarning(ex, "Environment cancellation failed"); }

        if (updateThread is not null && updateThread.IsAlive)
            updateThread.Join(1000);

        try { Screen.Enabled = false; } catch (Exception ex) { logger.LogWarning(ex, "Screen disable failed"); }

        Scope.Dispose();
        Services.Dispose();
        updateCancellation?.Dispose();
        Environment.Dispose();
    }

    private readonly record struct ProfileCandidate(
        ClassConfiguration Config,
        string File,
        int StartLevel);
}
