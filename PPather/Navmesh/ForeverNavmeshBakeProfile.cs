#nullable enable
using System;
using System.Globalization;

using SharedLib;

namespace PPather.Navmesh;

/// <summary>
/// Resolved, identity-bound settings shared by Forever offline baking and
/// runtime cache selection. The continent selects MinWorldZ; Product/Build
/// identify the immutable source dataset.
/// </summary>
public sealed record ForeverNavmeshBakeProfile(
    string Continent,
    string ClientProduct,
    int ClientBuild,
    string DataSetIdentity,
    string DotRecastVersion,
    float AgentRadius,
    float AgentMaxClimb,
    float WalkableSlope,
    float? MinWorldZ,
    string SettingsHash)
{
    public static ForeverNavmeshBakeProfile Create(string continent, string clientProduct,
        int clientBuild, NavmeshBakeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continent);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientProduct);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(clientProduct, StartupClientVersion.ForeverProductId, StringComparison.OrdinalIgnoreCase) ||
            clientBuild <= 0)
        {
            throw new InvalidOperationException(
                $"Forever navmesh data requires Product '{StartupClientVersion.ForeverProductId}' and a positive verified Build; received '{clientProduct}' Build {clientBuild}.");
        }

        if (!float.IsFinite(options.AgentRadius) || options.AgentRadius < 0 ||
            !float.IsFinite(options.AgentMaxClimb) || options.AgentMaxClimb < 0 ||
            !float.IsFinite(options.WalkableSlope) || options.WalkableSlope is < 0 or > 90)
        {
            throw new InvalidOperationException("Forever navmesh radius, climb, and slope settings must be finite and within their valid ranges.");
        }

        float? minWorldZ = options.ResolveMinWorldZ(continent);
        string dotRecastVersion = typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0";
        string dataSetIdentity = $"{clientProduct.ToLowerInvariant()}-{clientBuild}";
        // Forever's offline tool and Bot may run under different OS locales.
        // Canonicalize only this new product profile; legacy eras keep their
        // historical cache-key formatting unchanged.
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        string settingsHash;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            settingsHash = NavmeshSettings.ComputeSettingsHash("forever", dotRecastVersion,
                options.AgentRadius, options.AgentMaxClimb, options.WalkableSlope,
                minWorldZ, dataSetIdentity);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
        return new ForeverNavmeshBakeProfile(continent, clientProduct, clientBuild, dataSetIdentity,
            dotRecastVersion, options.AgentRadius, options.AgentMaxClimb, options.WalkableSlope,
            minWorldZ, settingsHash);
    }
}
