namespace PPather.Navmesh;

/// <summary>Stable machine-readable reason for a failed navmesh path query.</summary>
public enum NavmeshPathFailureReason
{
    VerifiedEmptyNavmeshTile,
    MissingOrInvalidNavmeshTile,
    OutsideCertifiedCoverage,
    NoWalkableSurface,
    PathUnreachable,
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

public enum NavmeshRuntimeTileStatus
{
    Unknown,
    OutsideCertifiedCoverage,
    VerifiedEmptyNavmeshTile,
    ExpectedDnmNotLoaded,
    Loaded,
    MissingOrInvalidNavmeshTile,
    UnverifiedNoNavmesh,
}
