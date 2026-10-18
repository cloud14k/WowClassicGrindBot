using DataConfigNamespace = global::DataConfig;

using System;

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

        if (args.Length > 0)
        {
            WowClientBuildInfo? info = WowClientBuildInfo.Read(args[0], new Version(1, 60, 1, 70094));
            if (info is null || info.Product != StartupClientVersion.ForeverProductId || info.Build != 70094)
            {
                throw new InvalidOperationException("The local CASC .build.info did not resolve to wow_cn_beta Build 70094.");
            }
        }

        P45GeometryRegression.Run();

        logger.LogInformation("P4.5 client identity and cache isolation checks passed.");
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
