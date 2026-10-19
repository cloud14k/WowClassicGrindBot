using System;
using System.Numerics;

using PPather.Graph;

namespace PPather.Navmesh;

/// <summary>Stable machine-readable reason for a failed navmesh path query.</summary>
public enum NavmeshPathFailureReason
{
    VerifiedEmptyNavmeshTile,
    MissingOrInvalidNavmeshTile,
    OutsideCertifiedCoverage,
    NoWalkableSurface,
    PathUnreachable,
    DynamicBakeFailed,
}

public readonly record struct NavmeshTileCoordinate(int X, int Z);

/// <summary>
/// Additional failure information that does not change the existing Path/null
/// return contract. AffectedTile is populated when one endpoint has a known
/// tile-level cause.
/// </summary>
public sealed record NavmeshPathDiagnostic(
    NavmeshPathFailureReason Reason,
    NavmeshTileCoordinate StartTile,
    NavmeshTileCoordinate EndTile,
    NavmeshTileCoordinate? AffectedTile);

/// <summary>Immutable options applied atomically to one path query.</summary>
public readonly record struct NavmeshPathQueryOptions(
    float JitterYards,
    int? JitterSeed,
    float EdgeMarginYards);

/// <summary>
/// Result and diagnostics captured from one serialized pathfinder execution.
/// QueryId is stable for the result's lifetime; it is not a server-side lookup
/// token and does not retain requests in a registry.
/// </summary>
public sealed record NavmeshPathQueryResult(
    Guid QueryId,
    Vector3 RequestedStart,
    Vector3 RequestedEnd,
    Path? Path,
    NavmeshPathDiagnostic? Diagnostic,
    NavmeshPathfinder.NavmeshStats Stats);

public enum NavmeshRuntimeTileStatus
{
    Unknown,
    OutsideCertifiedCoverage,
    VerifiedEmptyNavmeshTile,
    ExpectedDnmNotLoaded,
    Loaded,
    MissingOrInvalidNavmeshTile,
    UnverifiedNoNavmesh,
    DynamicBakeFailed,
}
