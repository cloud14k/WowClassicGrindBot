using Core.Database;
using Core.Goals;
using Core.GoalsComponent;
using Core.GOAP;
using Core.Session;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Numerics;

using static Core.Requirement;
using static Core.RequirementFactory;

using static Core.BlacklistSourceType;
using static Newtonsoft.Json.JsonConvert;

using static System.IO.File;
using static System.IO.Path;

namespace Core;

public static class GoalFactory
{
    /// <summary>Optional behavior-module allow-list used by the interactive behavior test page.</summary>
    public sealed record TestModules(bool PathMove, bool Wait, bool Pull, bool Combat, bool Adhoc, bool Loot, bool NPC);

    public static IServiceProvider Create(
        IServiceCollection services,
        IServiceProvider sp, ClassConfiguration classConfig,
        TestModules? testModules = null)
    {
        services.AddStartupIoC(sp);

        // session scoped services

        services.AddScoped<ConfigurableInput>();
        services.AddScoped<GoapAgentState>();

        services.AddScoped<CancellationTokenSource<GoapAgent>>();
        services.AddScoped<IGrindSessionHandler, GrindSessionHandler>();

        // Always on, unlike the bag and coin trackers below: a level-up is one line every
        // few hours, not the per-item chatter LogBagChanges exists to silence.
        services.AddScoped<LevelChangeTracker>();

        if (classConfig.LogBagChanges)
        {
            services.AddScoped<IBagChangeTracker, BagChangeTracker>();
            services.AddScoped<IMoneyChangeTracker, MoneyChangeTracker>();
        }
        else
        {
            services.AddScoped<IBagChangeTracker, NoBagChangeTracker>();
            services.AddScoped<IMoneyChangeTracker, NoMoneyChangeTracker>();
        }


        if (classConfig.Mode != Mode.Grind)
        {
            services.AddScoped<IBlacklist, NoBlacklist>();

            services.AddKeyedScoped<IBlacklist, NoBlacklist>(TARGET);
            services.AddKeyedScoped<IBlacklist, NoBlacklist>(MOUSE_OVER);
        }
        else
        {
            services.AddScoped<IBlacklistSource, BlacklistMouseOver>();
            services.AddScoped<IBlacklistSource, BlacklistTarget>();

            services.AddScoped<BlacklistMouseOver>();
            services.AddScoped<BlacklistTarget>();

            services.AddKeyedScoped<IBlacklist, Blacklist<BlacklistMouseOver>>(MOUSE_OVER);
            services.AddKeyedScoped<IBlacklist, Blacklist<BlacklistTarget>>(TARGET);

            services.AddScoped<IBlacklist>(x => x.GetRequiredKeyedService<IBlacklist>(TARGET));

            if (testModules == null)
                services.AddScoped<GoapGoal, BlacklistTargetGoal>();
        }

        services.AddScoped<NpcNameTargeting>();
        services.AddScoped<CursorScan>();

        // Goals components
        services.AddScoped<PlayerDirection>();
        services.AddScoped<StopMoving>();
        services.AddScoped<ReactCastError>();
        services.AddScoped<CastingHandlerInterruptWatchdog>();
        services.AddScoped<CastingHandler>();
        services.AddScoped<StuckDetector>();
        services.AddScoped<ApproachThrottle>();
        services.AddScoped<CombatTracker>();
        services.AddScoped<SafeSpotCollector>();
        services.AddScoped<ThreatFinder>();
        services.AddScoped<TrainerPlanner>();
        services.AddScoped<ActionBarPopulator>();

        var playerReader = sp.GetRequiredService<PlayerReader>();

        if (playerReader.Class is UnitClass.Druid)
        {
            services.AddScoped<IMountHandler, DruidMountHandler>();
            services.AddScoped<MountHandler>();
        }
        else
        {
            services.AddScoped<IMountHandler, MountHandler>();
        }

        services.AddScoped<TargetFinder>();

        // each GoapGoal gets an individual instance
        services.AddTransient<Navigation>();

        // This branch deliberately happens before the normal mode-specific Goal graph.
        // Behavior tests register only the explicitly selected modules.
        if (testModules != null)
        {
            // A normal Grind route both moves and runs the TargetFinder side
            // activity. Pull/Combat need that acquisition loop; the Path Move
            // checkbox by itself deliberately uses pathOnly to suppress it.
            if (testModules.PathMove || testModules.Pull || testModules.Combat)
                ResolveFollowRouteGoal(services, classConfig,
                    pathOnly: !testModules.Pull && !testModules.Combat);
            if (testModules.Wait) ResolveWaitGoal(services, classConfig);
            // Combat is an encounter-level test: once a target is selected, it
            // must be able to pull it, approach into range, and then run the
            // class rotation. Keep Pull selectable on its own for isolated tests.
            if (testModules.Pull || testModules.Combat)
            {
                services.AddScoped<GoapGoal, PullTargetGoal>();
                services.AddScoped<GoapGoal, ApproachTargetGoal>();
            }
            if (testModules.Combat)
            {
                services.AddScoped<GoapGoal, CombatGoal>();
                services.AddScoped<GoapGoal, FindThreatGoal>();
            }
            if (testModules.Adhoc) ResolveAdhocGoals(services, classConfig);
            bool includeLoot = testModules.Loot || testModules.Combat;
            if (includeLoot && classConfig.Loot)
            {
                // These two state-machine goals are part of Loot's normal
                // lifecycle: consume the produced corpse, enable loot, then
                // clear the consume state after LootGoal finishes. Combat
                // includes this complete post-kill lifecycle by design.
                services.AddScoped<GoapGoal, ConsumeCorpseGoal>();
                services.AddScoped<GoapGoal, LootGoal>();
                if (classConfig.GatherCorpse)
                    services.AddScoped<GoapGoal, SkinningGoal>();
                services.AddScoped<GoapGoal, CorpseConsumedGoal>();
            }
            if (testModules.NPC) ResolveAdhocNPCGoal(services, classConfig, sp.GetRequiredService<DataConfig>());
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        if (classConfig.Mode == Mode.CorpseRun)
        {
            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
        }
        else if (classConfig.GatheringMode)
        {
            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
            services.AddScoped<GoapGoal, CombatGoal>();
            services.AddScoped<GoapGoal, ApproachTargetGoal>();

            if (classConfig.Mode == Mode.AttendedGather)
            {
                services.AddScoped<GoapGoal, WaitForGatheringGoal>();
            }
            else if (classConfig.Mode == Mode.AutoGather)
            {
                ResolveAutoGatherGoal(services);
            }

            ResolveFollowRouteGoal(services, classConfig);

            ResolveLootAndSkin(services, classConfig);

            ResolvePetClass(services, playerReader.Class);

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);

            ResolveAdhocNPCGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveMailGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveWaitGoal(services, classConfig);
        }
        else if (classConfig.Mode == Mode.AssistFocus)
        {
            services.AddScoped<GoapGoal, PullTargetGoal>();
            services.AddScoped<GoapGoal, ApproachTargetGoal>();
            services.AddScoped<GoapGoal, AssistFocusGoal>();
            services.AddScoped<GoapGoal, CombatGoal>();
            services.AddScoped<GoapGoal, FindThreatGoal>();

            ResolveLootAndSkin(services, classConfig);

            services.AddScoped<GoapGoal, TargetFocusTargetGoal>();
            services.AddScoped<GoapGoal, FollowFocusGoal>();

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);
        }
        else if (classConfig.Mode is Mode.Grind or Mode.AttendedGrind)
        {
            if (classConfig.Mode == Mode.AttendedGrind)
            {
                services.AddScoped<GoapGoal, WaitGoal>();
            }
            else
            {
                ResolveFollowRouteGoal(services, classConfig);
            }

            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
            services.AddScoped<GoapGoal, PullTargetGoal>();
            services.AddScoped<GoapGoal, ApproachTargetGoal>();
            AddFleeGoal(services, classConfig);
            services.AddScoped<GoapGoal, CombatGoal>();
            services.AddScoped<GoapGoal, FindThreatGoal>();

            if (classConfig.WrongZone.ZoneId > 0)
            {
                services.AddScoped<GoapGoal, WrongZoneGoal>();
            }

            ResolveLootAndSkin(services, classConfig);

            ResolvePetClass(services, playerReader.Class);

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);

            ResolveAdhocNPCGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveMailGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveWaitGoal(services, classConfig);
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static void ResolveLootAndSkin(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        services.AddScoped<GoapGoal, ConsumeCorpseGoal>();
        services.AddScoped<GoapGoal, CorpseConsumedGoal>();

        if (classConfig.Loot)
        {
            services.AddScoped<GoapGoal, LootGoal>();

            if (classConfig.GatherCorpse)
            {
                services.AddScoped<GoapGoal, SkinningGoal>();
            }
        }
    }

    private static void ResolveAdhocGoals(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        for (int i = 0; i < classConfig.Adhoc.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.Adhoc.Sequence[i];
            services.AddScoped<GoapGoal>(sp =>
                ActivatorUtilities.CreateInstance<AdhocGoal>(sp, keyAction));
        }
    }

    private static void ResolveAdhocNPCGoal(IServiceCollection services,
        ClassConfiguration classConfig, DataConfig dataConfig)
    {
        for (int i = 0; i < classConfig.NPC.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.NPC.Sequence[i];

            // Skip "Mail" actions - they are handled by ResolveMailGoal
            if (classConfig.Mail &&
                keyAction.Name.Contains(MailGoal.KeyActionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            keyAction.Path = GetPath(keyAction, dataConfig);

            services.AddScoped<GoapGoal>(sp =>
                ActivatorUtilities.CreateInstance<AdhocNPCGoal>(sp, keyAction));
        }
    }

    private static void ResolveAutoGatherGoal(IServiceCollection services)
    {
        services.AddKeyedScoped<KeyAction>(AutoGatherGoal.KeyActionName, (sp, key) =>
        {
            var keyAction = new KeyAction
            {
                Name = AutoGatherGoal.KeyActionName
            };

            keyAction.Init(
                sp.GetRequiredService<ILogger>(),
                sp.GetRequiredService<ClassConfiguration>().Log,
                sp.GetRequiredService<PlayerReader>(),
                sp.GetRequiredService<AddonReader>().GlobalTime);

            return keyAction;
        });

        services.AddScoped<FoundNodeListener>();
        services.AddScoped<GoapGoal, AutoGatherGoal>();
    }

    private static void ResolveWaitGoal(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        for (int i = 0; i < classConfig.Wait.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.Wait.Sequence[i];

            services.AddScoped<GoapGoal>(sp =>
                ActivatorUtilities.CreateInstance<ConditionalWaitGoal>(sp, keyAction));
        }
    }

    private static void ResolveMailGoal(IServiceCollection services,
        ClassConfiguration classConfig, DataConfig dataConfig)
    {
        // Mail goal is registered when mail is enabled
        if (!classConfig.Mail)
            return;

        // Look for Mail action in NPC sequence (allows user to configure path and requirements)
        for (int i = 0; i < classConfig.NPC.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.NPC.Sequence[i];

            // Check if this is a Mail action by name
            if (!keyAction.Name.Contains(MailGoal.KeyActionName, StringComparison.OrdinalIgnoreCase))
                continue;

            keyAction.Path = GetPath(keyAction, dataConfig);

            services.AddScoped<GoapGoal>(sp =>
                ActivatorUtilities.CreateInstance<MailGoal>(sp, keyAction));

            // Only register one MailGoal
            return;
        }

        // If no Mail action in NPC sequence, create a default one with auto-navigation
        services.AddScoped<GoapGoal>(sp =>
        {
            KeyAction defaultMailAction = new()
            {
                Cost = 6.5f,
                Name = MailGoal.KeyActionName,
                Requirement = $"{HasMailableItems} {SymbolOr} {HasExcessGold}"
            };

            // Initialize the KeyAction so CanRun() works properly
            ILogger logger = sp.GetRequiredService<ILogger>();
            PlayerReader playerReader = sp.GetRequiredService<PlayerReader>();
            RecordInt globalTime = sp.GetRequiredService<AddonReader>().GlobalTime;

            defaultMailAction.Init(logger, classConfig.Log, playerReader, globalTime);

            return ActivatorUtilities.CreateInstance<MailGoal>(sp, defaultMailAction);
        });
    }

    private static void ResolvePetClass(IServiceCollection services,
        UnitClass @class)
    {
        if (@class is
            UnitClass.Hunter or
            UnitClass.Warlock or
            UnitClass.Mage or
            UnitClass.DeathKnight)
        {
            services.AddScoped<GoapGoal, TargetPetTargetGoal>();
        }
    }


    public static void ResolveFollowRouteGoal(IServiceCollection services,
        ClassConfiguration classConfig, bool pathOnly = false)
    {
        float baseCost = FollowRouteGoal.DEFAULT_COST;

        for (int i = 0; i < classConfig.Paths.Length; i++)
        {
            int index = i;
            float cost = baseCost + (index * FollowRouteGoal.COST_OFFSET);

            services.AddKeyedScoped<PathSettings>(i,
                (IServiceProvider sp, object? key) =>
                {
                    PathSettings setting =
                        sp.GetRequiredService<ClassConfiguration>().Paths[(int)key!];

                    // A generated path builds itself the first time its goal actually runs
                    // - see FollowRouteGoal.EnsureGenerated. Doing it here built every
                    // path in the profile at session start, including the ones whose
                    // requirements can never pass for this character.
                    return setting.Generate != null
                        ? setting
                        : GetPathSettings(
                            sp.GetRequiredService<ILogger>(),
                            setting,
                            sp.GetRequiredService<DataConfig>(),
                            sp.GetRequiredService<WorldMapAreaDB>());
                });

            services.AddScoped<GoapGoal>(sp =>
                ActivatorUtilities.CreateInstance<FollowRouteGoal>(sp,
                    cost,
                    sp.GetRequiredKeyedService<PathSettings>(index),
                    pathOnly));
        }
    }

    public static void AddFleeGoal(IServiceCollection services, ClassConfiguration classConfig)
    {
        if (classConfig.Flee.Sequence.Length == 0)
            return;

        services.AddScoped<GoapGoal, FleeGoal>();
    }

    private static string RelativeFilePath(DataConfig dataConfig, string path)
    {
        return !path.Contains(dataConfig.Path)
            ? Join(dataConfig.Path, path)
            : path;
    }

    private static PathSettings GetPathSettings(ILogger logger, PathSettings setting,
        DataConfig dataConfig, WorldMapAreaDB worldMapAreaDB)
    {
        setting.PathFilename =
            RelativeFilePath(dataConfig, setting.PathFilename);

        setting.Path = DeserializeObject<Vector3[]>(
            ReadAllText(setting.PathFilename))!;

        // TODO: there could be saved user routes where
        //       the Z component not 0
        for (int i = 0; i < setting.Path.Length; i++)
        {
            if (setting.Path[i].Z != 0)
                setting.Path[i].Z = 0;
        }

        if (setting.PathReduceSteps)
        {
            int step = 2;
            int reducedLength = setting.Path.Length % step == 0
                ? setting.Path.Length / step
                : (setting.Path.Length / step) + 1;

            Vector3[] path = new Vector3[reducedLength];
            for (int i = 0; i < path.Length; i++)
            {
                path[i] = setting.Path[i * step];
            }

            setting.Path = path;
        }

        setting.ConvertToWorldCoords(logger, worldMapAreaDB);

        return setting;
    }

    public static Vector3[] GetPath(KeyAction keyAction, DataConfig dataConfig)
    {
        return string.IsNullOrEmpty(keyAction.PathFilename)
            ? Array.Empty<Vector3>()
            : DeserializeObject<Vector3[]>(
            ReadAllText(RelativeFilePath(dataConfig, keyAction.PathFilename)))!;
    }
}
