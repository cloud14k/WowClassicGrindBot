using DataConfigNamespace = global::DataConfig;

using System;
using System.Globalization;
using System.IO;

using Microsoft.Extensions.Logging;

using Game;
using ForeverGeometryTool;
using PPather.Navmesh;
using SharedLib;

namespace CoreTests;

internal static class P45Regression
{
    public static void Run(ILogger logger, string[] args)
    {
        CheckVersion(new Version(1, 60, 1, 70094), "wow_cn_beta", 70094,
            ClientVersion.Forever, "forever");
        CheckVersion(new Version(1, 60, 1, 70094), "", null,
            ClientVersion.None, "unknown");
        CheckVersion(new Version(1, 15, 7, 0), "wow_classic", 11507,
            ClientVersion.SoM, "som");
        CheckVersion(new Version(2, 5, 1, 0), "wow_classic_tbc", 20501,
            ClientVersion.TBC, "tbc");
        CheckVersion(new Version(3, 4, 0, 0), "wow_classic_wrath", 30400,
            ClientVersion.Wrath, "wrath");
        CheckVersion(new Version(4, 4, 0, 0), "wow_classic_cata", 40400,
            ClientVersion.Cata, "cata");
        CheckVersion(new Version(5, 5, 0, 0), "wow_classic_mop", 50500,
            ClientVersion.Mop, "mop");
        CheckVersion(new Version(1, 12, 1, 0), "", null,
            ClientVersion.Legacy_Vanilla, "legacy_vanilla");
        CheckVersion(new Version(2, 4, 3, 0), "", null,
            ClientVersion.Legacy_TBC, "legacy_tbc");
        CheckVersion(new Version(3, 3, 5, 0), "", null,
            ClientVersion.Legacy_Wrath, "legacy_wrath");
        CheckVersion(new Version(4, 3, 4, 0), "", null,
            ClientVersion.Legacy_Cata, "legacy_cata");
        CheckVersion(new Version(5, 4, 8, 0), "", null,
            ClientVersion.Legacy_Mop, "legacy_mop");
        CheckVersion(new Version(10, 2, 0, 0), "wow", null,
            ClientVersion.Retail, "retail");

        if (DataConfigNamespace.ClientEra("forever") != "forever" ||
            DataConfigNamespace.ClientEra("som") != "precata")
        {
            throw new InvalidOperationException("Forever and SoM must resolve to separate geometry eras.");
        }

        string build70094 = NavmeshSettings.ComputeSettingsHash("forever", "dotrecast-test",
            0.5f, 1.6f, 48f, dataSetIdentity: "wow_cn_beta-70094");
        string build70095 = NavmeshSettings.ComputeSettingsHash("forever", "dotrecast-test",
            0.5f, 1.6f, 48f, dataSetIdentity: "wow_cn_beta-70095");
        string differentSettings = NavmeshSettings.ComputeSettingsHash("forever", "dotrecast-test",
            0.6f, 1.6f, 48f, dataSetIdentity: "wow_cn_beta-70094");
        if (build70094 == build70095 || build70094 == differentSettings)
        {
            throw new InvalidOperationException("Forever Build and bake settings must both partition the navmesh cache.");
        }

        string dotRecastVersion = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        NavmeshBakeOptions defaults = new();
        ForeverNavmeshBakeProfile profileA = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, defaults);
        ForeverNavmeshBakeProfile profileB = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, new NavmeshBakeOptions());
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        string frenchCultureHash;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            frenchCultureHash = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, new NavmeshBakeOptions()).SettingsHash;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
        NavmeshBakeOptions filteredOptions = new() { MinWorldZ = new() { ["Azeroth"] = -700f } };
        ForeverNavmeshBakeProfile filteredProfile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, filteredOptions);
        ForeverNavmeshBakeProfile nextBuildProfile = ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70095, new NavmeshBakeOptions());
        if (profileA.SettingsHash != profileB.SettingsHash || profileA.SettingsHash != frenchCultureHash ||
            profileA.SettingsHash == filteredProfile.SettingsHash ||
            profileA.SettingsHash == nextBuildProfile.SettingsHash || defaults.ResolveMinWorldZ("Azeroth") is not null)
        {
            throw new InvalidOperationException("The shared Forever bake profile is not deterministic or does not partition MinWorldZ and Build identity.");
        }

        NavmeshBakeOptions explicitNull = new() { MinWorldZ = new() { ["Azeroth"] = null } };
        ExpectFailure(() => ForeverNavmeshBakeProfile.Create("Azeroth", "wow_cn_beta", 70094, explicitNull),
            "An explicitly null MinWorldZ was silently treated as absent.");
        string legacyNoFloor = NavmeshSettings.ComputeSettingsHash("wrath", dotRecastVersion, 0.5f, 1.6f, 48f);
        string legacyNullFloor = NavmeshSettings.ComputeSettingsHash("wrath", dotRecastVersion, 0.5f, 1.6f, 48f, minWorldZ: null);
        if (legacyNoFloor != legacyNullFloor)
        {
            throw new InvalidOperationException("Default MinWorldZ changed the existing Wrath cache hash path.");
        }

        RunBuildInfoResolverRegression();

        if (args.Length > 0)
        {
            WowClientBuildInfo? info = WowClientBuildInfo.Read(args[0], new Version(1, 60, 1, 70094));
            if (info is null || info.Product != StartupClientVersion.ForeverProductId || info.Build != 70094)
            {
                throw new InvalidOperationException("The local CASC .build.info did not resolve to wow_cn_beta Build 70094.");
            }

            WowClientBuildInfo foreverInfo = WowClientBuildInfo.ReadForever(args[0]);
            if (foreverInfo.Product != StartupClientVersion.ForeverProductId || foreverInfo.Build != 70094)
            {
                throw new InvalidOperationException("The PathingAPI Forever identity resolver did not match the local client Product/Build.");
            }
        }

        P45GeometryRegression.Run();

        logger.LogInformation("P4.5 client identity and cache isolation checks passed.");
    }

    private static void RunBuildInfoResolverRegression()
    {
        string outputRoot = Environment.GetEnvironmentVariable("WOW_TEST_OUTPUT_ROOT")
            ?? Path.Combine(Environment.CurrentDirectory, "tmp");
        Directory.CreateDirectory(outputRoot);
        string root = Path.Combine(outputRoot, $"wow-build-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, ".build.info");
            File.WriteAllText(path, "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70094|wow_cn_beta\n");
            WowClientBuildInfo info = WowClientBuildInfo.ReadForever(root);
            WowClientBuildInfo configuredInfo = WowClientBuildInfo.ReadForeverConfigured(root, null);
            if (info.Product != "wow_cn_beta" || info.Build != 70094)
            {
                throw new InvalidOperationException("The Forever client identity resolver parsed the fixture incorrectly.");
            }
            if (configuredInfo != info)
            {
                throw new InvalidOperationException("PathingAPI's configured-root resolver did not use the configured client identity.");
            }

            WowClientBuildInfo environmentInfo = WowClientBuildInfo.ReadForeverConfigured(" ", root);
            if (environmentInfo != info)
            {
                throw new InvalidOperationException("PathingAPI's environment-root fallback did not resolve the configured client identity.");
            }
            ExpectFailure(() => WowClientBuildInfo.ReadForeverConfigured(null, null),
                "PathingAPI accepted exp=forever without a configured client root.");

            File.WriteAllText(path, "Active!DEC:1|Version!STRING:0|Product!STRING:0\n1|1.60.1.70094|wow_classic\n");
            ExpectFailure(() => WowClientBuildInfo.ReadForever(root),
                "PathingAPI's Forever identity resolver accepted a non-Forever Product.");
            File.Delete(path);
            ExpectFailure(() => WowClientBuildInfo.ReadForever(root),
                "PathingAPI's Forever identity resolver accepted a missing .build.info.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void CheckVersion(Version executableVersion, string product, int? build,
        ClientVersion expectedVersion, string expectedPath)
    {
        StartupClientVersion actual = new(executableVersion, product, build);
        if (actual.Version != expectedVersion || actual.Path != expectedPath)
        {
            throw new InvalidOperationException(
                $"Version {executableVersion} / product '{product}' resolved to {actual.Version}/{actual.Path}, " +
                $"expected {expectedVersion}/{expectedPath}.");
        }
    }
}
