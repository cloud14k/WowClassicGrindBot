#nullable enable
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using ForeverGeometryTool;
using PPather.Navmesh;
using SharedLib;

namespace ForeverGeometryTool;

public sealed record ForeverGeometryBatchRequest(
    int MapId,
    int AdtMinX,
    int AdtMinY,
    int AdtMaxX,
    int AdtMaxY,
    string InputDirectory,
    string OutputDirectory,
    string DnmOutputDirectory,
    string Continent,
    IReadOnlyCollection<(int X, int Z)>? TargetTiles = null,
    float AgentRadius = 0.5f,
    float AgentMaxClimb = 1.6f,
    float WalkableSlope = 48f,
    float PaddingYards = TileGeometryExtractor.PaddingYd,
    float? MinWorldZ = null,
    bool Resume = false,
    IReadOnlyCollection<AdtCoordinate>? AdtList = null);

public sealed record ForeverGeometryBatchResult(string StateFile, int SourceCount, int TileCount,
    int ReusedSourceCount, int ReusedTileCount, int FailedTaskCount, long ElapsedMilliseconds,
    long PeakSampledWorkingSetBytes, long OutputFileCount, long DiskBytes, DnmSetVerification Verification);

public sealed record ForeverGeometryPublicationResult(string StateFile, string ManifestFile,
    string WdtTileListSha256, int SourceCount, int TileCount);

/// <summary>
/// Resumable explicit-range processor for pre-extracted rawgeo inputs. It never
/// opens a CASC client, never discovers WDT contents, and always emits a
/// selected-scope non-publishable manifest. Geometry is aggregated and baked
/// one target Detour tile at a time from only that tile's contributing ADTs.
/// </summary>
public static class ForeverGeometryBatchRunner
{
    public const string StateFormat = "wow-forever-geometry-batch-v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ForeverGeometryBatchResult Run(ForeverGeometryBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        string inputRoot = Path.GetFullPath(request.InputDirectory);
        string outputRoot = Path.GetFullPath(request.OutputDirectory);
        string dnmRoot = Path.GetFullPath(request.DnmOutputDirectory);
        EnsureDisjointDirectories(inputRoot, outputRoot, dnmRoot);
        string statePath = Path.Combine(outputRoot, "batch-state.json");
        AdtCoordinate[] expectedAdts = EnumerateAdts(request);
        string signature = ComputeRequestSignature(request, inputRoot, dnmRoot, expectedAdts);
        Stopwatch totalTimer = Stopwatch.StartNew();
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(dnmRoot);

        BatchState state;
        if (File.Exists(statePath))
        {
            if (!request.Resume)
            {
                throw new IOException($"Batch state already exists; pass --resume to continue it: {statePath}");
            }

            state = JsonSerializer.Deserialize<BatchState>(File.ReadAllText(statePath), JsonOptions)
                ?? throw new InvalidDataException($"Batch state is empty or malformed: {statePath}");
            if (state.Format != StateFormat)
            {
                throw new InvalidDataException($"Unsupported batch state format in {statePath}.");
            }

            if (!string.Equals(state.RequestSignature, signature, StringComparison.Ordinal))
            {
                RemoveOwnedOutputs(state, outputRoot, dnmRoot);
                state = CreateState(request, inputRoot, outputRoot, dnmRoot, signature, expectedAdts);
                EnsureOnlyOwnedDnmFiles(state, dnmRoot);
            }
            else
            {
                EnsureOnlyOwnedDnmFiles(state, dnmRoot);
            }
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(outputRoot).Any() || Directory.EnumerateFileSystemEntries(dnmRoot).Any())
            {
                throw new IOException("A new batch requires empty state/output and DNM directories. Use --resume only with a matching batch-state.json.");
            }

            state = CreateState(request, inputRoot, outputRoot, dnmRoot, signature, expectedAdts);
        }

        state.Status = "running";
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        SaveState(statePath, state);

        int reusedSources = 0;
        long peakWorkingSet = ReadWorkingSet();
        foreach (AdtCoordinate adt in expectedAdts)
        {
            BatchSourceTask task = state.SourceTasks.Single(item => item.AdtX == adt.X && item.AdtY == adt.Y);
            string inputPath;
            string sha;
            try
            {
                inputPath = ResolveInputPath(inputRoot, request.MapId, adt);
                sha = HashFile(inputPath);
            }
            catch (Exception ex)
            {
                task.Status = "failed";
                task.FailureReason = ex.Message;
                task.UpdatedUtc = DateTimeOffset.UtcNow;
                SaveState(statePath, state);
                continue;
            }
            if (task.Status == "succeeded" && task.InputSha256 == sha &&
                string.Equals(task.InputPath, inputPath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(task.ClientProduct) && task.ClientBuild > 0 && task.Counts is not null &&
                task.IndependentlyExpectedTiles.Length > 0)
            {
                task.Status = "succeeded";
                reusedSources++;
                continue;
            }

            task.Status = task.AttemptCount > 0 ? "retry" : "pending";
            task.AttemptCount++;
            task.InputPath = inputPath;
            task.InputSha256 = sha;
            task.ConfigHash = "";
            task.FailureReason = null;
            task.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveState(statePath, state);
            task.Status = "running";
            SaveState(statePath, state);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                RawGeometryTileExportResult exportResult = RawGeometryTileExporter.ExportWithIndependentCoverage(inputPath,
                    new GeometryExportRequest(request.MapId, adt.X, adt.Y, request.AgentRadius,
                        request.AgentMaxClimb, request.PaddingYards, request.MinWorldZ));
                IReadOnlyList<ForeverGeometryTile> exported = exportResult.Tiles;
                ForeverGeometryTile first = exported[0];
                if (state.ClientProduct is not null &&
                    (!state.ClientProduct.Equals(first.ClientProduct, StringComparison.OrdinalIgnoreCase) || state.ClientBuild != first.ClientBuild))
                {
                    throw new InvalidDataException(
                        $"Mixed CASC identities in the batch: expected {state.ClientProduct} Build {state.ClientBuild}, received {first.ClientProduct} Build {first.ClientBuild} at ADT ({adt.X},{adt.Y}).");
                }

                ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create(request.Continent,
                    first.ClientProduct, first.ClientBuild, CreateOptions(request), request.PaddingYards);
                state.ClientProduct = first.ClientProduct;
                state.ClientBuild = first.ClientBuild;
                state.SettingsHash = profile.SettingsHash;
                task.ClientProduct = first.ClientProduct;
                task.ClientBuild = first.ClientBuild;
                task.ConfigHash = profile.SettingsHash;
                task.Counts = first.SourceCounts;
                task.ContributingTiles = exported.Select(static tile => new[] { tile.DtTileX, tile.DtTileZ })
                    .Distinct(IntPairComparer.Instance)
                    .OrderBy(static pair => pair[0]).ThenBy(static pair => pair[1]).ToArray();
                task.IndependentlyExpectedTiles = exportResult.IndependentlyExpectedTiles
                    .OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z)
                    .Select(static tile => new[] { tile.X, tile.Z }).ToArray();
                task.OutputFiles = [];
                task.Status = "succeeded";
            }
            catch (Exception ex)
            {
                task.Status = "failed";
                task.FailureReason = ex.Message;
            }
            finally
            {
                timer.Stop();
                task.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                task.PeakSampledWorkingSetBytes = ReadWorkingSet();
                peakWorkingSet = Math.Max(peakWorkingSet, task.PeakSampledWorkingSetBytes);
                task.UpdatedUtc = DateTimeOffset.UtcNow;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                SaveState(statePath, state);
            }
        }

        peakWorkingSet = Math.Max(peakWorkingSet, ReadWorkingSet());
        if (state.SourceTasks.Any(static task => task.Status != "succeeded"))
        {
            const string failure = "One or more source ADT tasks failed; see batch-state.json.";
            RecordFailedBatch(statePath, state, outputRoot, dnmRoot, totalTimer, peakWorkingSet, failure);
            throw new InvalidDataException(failure);
        }

        try
        {
            state.ExpectedTiles = ValidateRecordedSourceCoverage(state, request);
        }
        catch (Exception ex)
        {
            RecordFailedBatch(statePath, state, outputRoot, dnmRoot, totalTimer, peakWorkingSet,
                $"Independent source geometry coverage validation failed: {ex.Message}");
            throw;
        }

        ForeverNavmeshBakeProfile resolvedProfile = ForeverNavmeshBakeProfile.Create(request.Continent,
            state.ClientProduct!, state.ClientBuild, CreateOptions(request), request.PaddingYards);
        if (!string.Equals(state.SettingsHash, resolvedProfile.SettingsHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Resolved source identity/settings differ from the batch's recorded profile.");
        }

        HashSet<(int X, int Z)> availableTiles = state.ExpectedTiles
            .Select(static pair => (pair[0], pair[1])).ToHashSet();
        (int X, int Z)[] targetTiles = (request.TargetTiles is { Count: > 0 }
                ? request.TargetTiles
                : availableTiles)
            .Distinct().OrderBy(static item => item.X).ThenBy(static item => item.Z).ToArray();
        if (targetTiles.Length == 0 || targetTiles.Any(tile => !NavmeshCoords.IsValidTile(tile.X, tile.Z)))
        {
            throw new InvalidDataException("Batch target tiles are empty or outside the Detour tile grid.");
        }

        state.TargetTiles = targetTiles.Select(static tile => new[] { tile.X, tile.Z }).ToArray();
        ReconcileTileTasks(state, targetTiles, outputRoot, dnmRoot);
        SaveState(statePath, state);
        int reusedTiles = 0;

        foreach ((int tileX, int tileZ) in targetTiles)
        {
            BatchTileTask task = state.TileTasks.Single(item => item.TileX == tileX && item.TileZ == tileZ);
            BatchSourceTask[] contributors = state.SourceTasks
                .Where(item => item.ContributingTiles.Any(pair => pair[0] == tileX && pair[1] == tileZ))
                .OrderBy(static item => item.AdtY).ThenBy(static item => item.AdtX).ToArray();
            if (contributors.Length == 0)
            {
                task.Status = "failed";
                task.FailureReason = $"No selected source ADT contributes geometry to tile ({tileX},{tileZ}).";
                SaveState(statePath, state);
                continue;
            }

            GeometrySourceRecord[] contributorRecords = contributors.Select(ToSourceRecord).ToArray();
            string contributorHash = ForeverGeometryAggregator.ComputeSourceSetHash(contributorRecords);
            string dnmPath = Path.Combine(dnmRoot, $"tile_{tileX}_{tileZ}.dnm");
            string geometryPath = Path.Combine(outputRoot, "tiles", $"tile_{tileX}_{tileZ}.geometry.json");
            bool canReuse = task.Status == "succeeded" && task.SourceSetHash == contributorHash &&
                task.ConfigHash == resolvedProfile.SettingsHash && File.Exists(dnmPath) && File.Exists(geometryPath) &&
                task.DnmSha256 == HashFile(dnmPath) && task.GeometrySha256 == HashFile(geometryPath);
            task.SourceSetHash = contributorHash;
            task.ConfigHash = resolvedProfile.SettingsHash;
            task.ClientProduct = resolvedProfile.ClientProduct;
            task.ClientBuild = resolvedProfile.ClientBuild;
            task.SourceAdts = contributors.Select(static source => new[] { source.AdtX, source.AdtY }).ToArray();
            task.SourceSha256s = contributors.Select(static source => source.InputSha256).ToArray();
            if (canReuse)
            {
                try
                {
                    DnmSetVerification oneTile = ForeverGeometryBakeVerifier.VerifyDnmFiles(dnmRoot,
                        [(tileX, tileZ)], requireExactFileSet: false);
                    task.DnmReadbackSucceeded = oneTile.TileCount > 0;
                    task.HeightQuerySucceeded = oneTile.HeightQuerySucceeded;
                    task.ConnectedPathSucceeded = oneTile.ConnectedPathSucceeded;
                    if (task.DnmReadbackSucceeded && task.HeightQuerySucceeded && task.ConnectedPathSucceeded)
                    {
                        reusedTiles++;
                        continue;
                    }
                }
                catch
                {
                    // A previously successful task is retried if current output validation fails.
                }
            }

            task.Status = task.AttemptCount > 0 ? "retry" : "pending";
            task.AttemptCount++;
            task.FailureReason = null;
            task.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveState(statePath, state);
            task.Status = "running";
            SaveState(statePath, state);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                GeometryInputFile[] inputs = contributors.Select(static source =>
                    new GeometryInputFile(source.InputPath, source.AdtX, source.AdtY)).ToArray();
                AdtCoordinate[] adts = contributors.Select(static source => new AdtCoordinate(source.AdtX, source.AdtY)).ToArray();
                GeometryAggregate aggregate = ForeverGeometryAggregator.Export(new GeometryAggregateRequest(
                    request.MapId, inputs, adts, request.AgentRadius, request.AgentMaxClimb,
                    request.PaddingYards, request.WalkableSlope, request.MinWorldZ, request.Continent));
                ForeverGeometryTile tile = aggregate.Tiles.SingleOrDefault(item => item.DtTileX == tileX && item.DtTileZ == tileZ)
                    ?? throw new InvalidDataException($"Tile-wise aggregation produced no geometry for ({tileX},{tileZ}).");

                Directory.CreateDirectory(Path.GetDirectoryName(geometryPath)!);
                using (FileStream geometryStream = File.Create(geometryPath))
                {
                    JsonSerializer.Serialize(geometryStream, tile, JsonOptions);
                }

                DtMeshData data = NavmeshTileBuilder.Bake(tile.ToTileGeometry(), tileX, tileZ, CreateOptions(request))
                    ?? throw new InvalidDataException($"NavmeshTileBuilder returned no navmesh polygons for ({tileX},{tileZ}).");
                WriteDnmAtomically(dnmPath, data);
                task.GeometryOutputPath = geometryPath;
                task.GeometrySha256 = HashFile(geometryPath);
                task.DnmOutputPath = dnmPath;
                task.DnmSha256 = HashFile(dnmPath);
                task.TerrainTriangles = tile.TileCounts.TerrainTriangles;
                task.CollisionTriangles = tile.TileCounts.CollisionTriangles;
                task.LiquidTriangles = tile.TileCounts.LiquidTriangles;
                task.DnmReadbackSucceeded = true;
                task.Status = "succeeded";
            }
            catch (Exception ex)
            {
                task.Status = "failed";
                task.FailureReason = ex.Message;
            }
            finally
            {
                timer.Stop();
                task.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                task.PeakSampledWorkingSetBytes = ReadWorkingSet();
                peakWorkingSet = Math.Max(peakWorkingSet, task.PeakSampledWorkingSetBytes);
                task.UpdatedUtc = DateTimeOffset.UtcNow;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                SaveState(statePath, state);
            }
        }

        if (state.TileTasks.Any(static task => task.Status != "succeeded"))
        {
            const string failure = "One or more target tile tasks failed; see batch-state.json.";
            RecordFailedBatch(statePath, state, outputRoot, dnmRoot, totalTimer, peakWorkingSet, failure);
            throw new InvalidDataException(failure);
        }

        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(dnmRoot, targetTiles);
        foreach (BatchTileTask task in state.TileTasks)
        {
            task.DnmReadbackSucceeded = true;
            task.HeightQuerySucceeded = verification.HeightQuerySucceeded;
            task.ConnectedPathSucceeded = verification.ConnectedPathSucceeded;
            task.CrossTileLinkCount = verification.CrossTileLinkCount;
        }

        foreach (BatchSourceTask source in state.SourceTasks)
        {
            source.OutputFiles = state.TileTasks
                .Where(tile => tile.SourceAdts.Any(pair => pair[0] == source.AdtX && pair[1] == source.AdtY))
                .SelectMany(static tile => new[] { tile.GeometryOutputPath, tile.DnmOutputPath })
                .Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        WriteSelectedManifest(dnmRoot, state, resolvedProfile, verification);
        ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(dnmRoot, resolvedProfile.ClientProduct,
            resolvedProfile.ClientBuild, resolvedProfile.SettingsHash, request.MapId);

        state.Status = "succeeded";
        state.FailureReason = null;
        state.Verification = verification;
        state.ElapsedMilliseconds = totalTimer.ElapsedMilliseconds;
        state.PeakSampledWorkingSetBytes = peakWorkingSet;
        state.OutputFileCount = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).LongCount() +
                                Directory.EnumerateFiles(dnmRoot, "*", SearchOption.AllDirectories).LongCount();
        state.DiskBytes = SumFileBytes(outputRoot) + SumFileBytes(dnmRoot);
        state.FailedTaskCount = state.SourceTasks.Count(static task => task.Status == "failed") +
                                state.TileTasks.Count(static task => task.Status == "failed");
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        SaveState(statePath, state);
        return new ForeverGeometryBatchResult(statePath, state.SourceTasks.Count, state.TileTasks.Count,
            reusedSources, reusedTiles, state.FailedTaskCount, state.ElapsedMilliseconds,
            state.PeakSampledWorkingSetBytes, state.OutputFileCount, state.DiskBytes, verification);
    }

    /// <summary>
    /// Reads a normalized ADT coordinate list. It accepts either one X,Y pair
    /// per line or the `tile MapID X Y` records emitted by mapextractor
    /// --list-tiles. Full client identity is independently checked at seal time.
    /// </summary>
    public static AdtCoordinate[] ReadAdtList(string path, int mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (mapId < 0) throw new ArgumentOutOfRangeException(nameof(mapId));
        string[] lines = File.ReadAllLines(path);
        bool extractorLog = lines.Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Equals("tile", StringComparison.OrdinalIgnoreCase) == true);
        HashSet<AdtCoordinate> seen = [];
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            AdtCoordinate adt;
            if (extractorLog)
            {
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0 || !fields[0].Equals("tile", StringComparison.OrdinalIgnoreCase)) continue;
                if (fields.Length != 4 || !int.TryParse(fields[1], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int listedMap) || listedMap != mapId ||
                    !int.TryParse(fields[2], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int x) ||
                    !int.TryParse(fields[3], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int y))
                {
                    throw new InvalidDataException($"ADT list has a malformed or wrong-MapID tile record: {line}");
                }
                adt = new AdtCoordinate(x, y);
            }
            else
            {
                string[] fields = line.Contains(',')
                    ? line.Split(',', StringSplitOptions.TrimEntries)
                    : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 2 ||
                    !int.TryParse(fields[0], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int x) ||
                    !int.TryParse(fields[1], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int y))
                {
                    throw new InvalidDataException($"ADT list line must be X,Y or X Y: {line}");
                }
                adt = new AdtCoordinate(x, y);
            }

            if (adt.X is < 0 or >= 64 || adt.Y is < 0 or >= 64 || !seen.Add(adt))
            {
                throw new InvalidDataException($"ADT list contains an out-of-range or duplicate coordinate ({adt.X},{adt.Y}).");
            }
        }

        if (seen.Count == 0) throw new InvalidDataException($"ADT list contains no coordinates: {path}");
        return seen.OrderBy(static adt => adt.Y).ThenBy(static adt => adt.X).ToArray();
    }

    /// <summary>Read-only independent validation entry for `batch-verify`.</summary>
    public static ForeverGeometryBatchResult VerifyState(string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        string statePath = Path.GetFullPath(stateFile);
        BatchState state = JsonSerializer.Deserialize<BatchState>(File.ReadAllText(statePath), JsonOptions)
            ?? throw new InvalidDataException($"Batch state is empty or malformed: {statePath}");
        if (state.Format != StateFormat || state.Status != "succeeded" ||
            state.SourceTasks.Count == 0 || state.TileTasks.Count == 0)
        {
            throw new InvalidDataException($"Batch state is incomplete or unsupported: {statePath}");
        }

        NavmeshBakeOptions options = new()
        {
            AgentRadius = state.AgentRadius,
            AgentMaxClimb = state.AgentMaxClimb,
            WalkableSlope = state.WalkableSlope,
            MinWorldZ = state.MinWorldZ.HasValue
                ? new Dictionary<string, float?> { [state.Continent] = state.MinWorldZ.Value }
                : [],
        };
        ForeverNavmeshBakeProfile profile = ForeverNavmeshBakeProfile.Create(state.Continent,
            state.ClientProduct!, state.ClientBuild, options, state.PaddingYards);
        if (!string.Equals(profile.SettingsHash, state.SettingsHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Batch settings hash does not match the recorded profile.");
        }

        ValidateStateAdtSelection(state);
        ForeverGeometryBatchRequest validationRequest = new(state.MapId, state.AdtMinX, state.AdtMinY,
            state.AdtMaxX, state.AdtMaxY, state.InputDirectory, state.OutputDirectory,
            state.DnmOutputDirectory, state.Continent,
            state.TargetTiles.Select(static pair => (pair[0], pair[1])).ToArray(),
            state.AgentRadius, state.AgentMaxClimb, state.WalkableSlope, state.PaddingYards,
            state.MinWorldZ, Resume: true,
            AdtList: state.AdtSelectionMode == "explicit_list"
                ? state.RequestedAdts.Select(static pair => new AdtCoordinate(pair[0], pair[1])).ToArray()
                : null);
        int[][] recalculatedExpectedTiles = ValidateSourceCoverage(state, validationRequest);
        if (!PairArraySetEquals(recalculatedExpectedTiles, state.ExpectedTiles, tileCoordinates: true))
        {
            throw new InvalidDataException("Recorded expected tile coverage differs from the independent raw-geometry calculation.");
        }

        foreach (BatchSourceTask task in state.SourceTasks)
        {
            if (task.Status != "succeeded" || !File.Exists(task.InputPath) ||
                task.ConfigHash != profile.SettingsHash)
            {
                throw new InvalidDataException($"Source task for ADT ({task.AdtX},{task.AdtY}) is stale or incomplete.");
            }
        }

        foreach (BatchTileTask task in state.TileTasks)
        {
            if (task.Status != "succeeded" || task.ConfigHash != profile.SettingsHash ||
                !File.Exists(task.DnmOutputPath) || HashFile(task.DnmOutputPath) != task.DnmSha256 ||
                !File.Exists(task.GeometryOutputPath) || HashFile(task.GeometryOutputPath) != task.GeometrySha256)
            {
                throw new InvalidDataException($"Tile task ({task.TileX},{task.TileZ}) is stale or incomplete.");
            }

            BatchSourceTask[] contributors = task.SourceAdts.Select(pair => state.SourceTasks.Single(source =>
                source.AdtX == pair[0] && source.AdtY == pair[1])).ToArray();
            if (!string.Equals(task.ClientProduct, profile.ClientProduct, StringComparison.OrdinalIgnoreCase) ||
                task.ClientBuild != profile.ClientBuild ||
                !task.SourceSha256s.SequenceEqual(contributors.Select(static source => source.InputSha256), StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Tile task ({task.TileX},{task.TileZ}) has stale client/source identity metadata.");
            }
            string sourceSetHash = ForeverGeometryAggregator.ComputeSourceSetHash(contributors.Select(ToSourceRecord));
            if (!string.Equals(sourceSetHash, task.SourceSetHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Tile task ({task.TileX},{task.TileZ}) has a stale contributor set.");
            }
        }

        int[][] targetTilePairs = state.TargetTiles;
        if (targetTilePairs.Length == 0 || HasDuplicatePairs(targetTilePairs) ||
            state.TileTasks.Count != targetTilePairs.Length || HasDuplicatePairs(state.TileTasks.Select(static tile => new[] { tile.TileX, tile.TileZ }).ToArray()) ||
            !PairArraySetEquals(targetTilePairs,
                state.TileTasks.Select(static tile => new[] { tile.TileX, tile.TileZ }).ToArray(), tileCoordinates: true))
        {
            throw new InvalidDataException("Batch tile task set is incomplete, duplicated, or differs from its recorded target set.");
        }

        (int X, int Z)[] tiles = state.TargetTiles.Select(static pair => (pair[0], pair[1])).ToArray();
        string dnmRoot = Path.GetFullPath(state.DnmOutputDirectory);
        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(dnmRoot, tiles);
        string manifestPath = Path.Combine(dnmRoot, "manifest.json");
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            string scope = manifest.RootElement.TryGetProperty("scope", out JsonElement scopeValue) &&
                           scopeValue.ValueKind == JsonValueKind.String
                ? scopeValue.GetString() ?? ""
                : "";
            if (string.Equals(scope, "explicit_adt_and_tile_set", StringComparison.Ordinal))
            {
                ForeverNavmeshCacheManifest.ValidateSelectedPackForTest(dnmRoot, profile.ClientProduct,
                    profile.ClientBuild, profile.SettingsHash, state.MapId);
            }
            else if (string.Equals(scope, "continent", StringComparison.Ordinal))
            {
                (int X, int Y)[] sourceAdts = state.SourceTasks
                    .Select(static source => (source.AdtX, source.AdtY)).ToArray();
                ForeverNavmeshCacheManifest.ValidateForPublication(dnmRoot, profile.ClientProduct,
                    profile.ClientBuild, profile.SettingsHash, state.MapId, state.Continent, sourceAdts, tiles);
                ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, profile.ClientProduct,
                    profile.ClientBuild, profile.SettingsHash, state.MapId, state.Continent);
            }
            else
            {
                throw new InvalidDataException($"Batch manifest has an unsupported scope '{scope}': {manifestPath}");
            }
        }
        return new ForeverGeometryBatchResult(statePath, state.SourceTasks.Count, state.TileTasks.Count,
            0, 0, 0, state.ElapsedMilliseconds, state.PeakSampledWorkingSetBytes,
            state.OutputFileCount, state.DiskBytes, verification);
    }

    private static int[][] ValidateSourceCoverage(BatchState state, ForeverGeometryBatchRequest request)
    {
        AdtCoordinate[] requested = EnumerateAdts(request);
        ValidateStateAdtSelection(state);
        if (requested.Length != state.SourceTasks.Count || HasDuplicatePairs(state.SourceTasks
                .Select(static task => new[] { task.AdtX, task.AdtY }).ToArray()) ||
            !PairArraySetEquals(requested.Select(static adt => new[] { adt.X, adt.Y }).ToArray(),
                state.SourceTasks.Select(static task => new[] { task.AdtX, task.AdtY }).ToArray(), tileCoordinates: false))
        {
            throw new InvalidDataException("Requested ADTs and source tasks are incomplete, duplicated, or inconsistent.");
        }

        HashSet<(int X, int Z)> expectedTiles = [];
        foreach (AdtCoordinate adt in requested)
        {
            BatchSourceTask source = state.SourceTasks.Single(task => task.AdtX == adt.X && task.AdtY == adt.Y);
            string path = ResolveInputPath(Path.GetFullPath(state.InputDirectory), state.MapId, adt);
            if (source.Status != "succeeded" || !string.Equals(Path.GetFullPath(source.InputPath), path, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                throw new InvalidDataException($"Source ADT ({adt.X},{adt.Y}) is failed, missing, relocated, or has changed since extraction.");
            }

            RawGeometryTileExportResult result = RawGeometryTileExporter.ExportWithIndependentCoverage(path,
                new GeometryExportRequest(state.MapId, adt.X, adt.Y, request.AgentRadius,
                    request.AgentMaxClimb, request.PaddingYards, request.MinWorldZ));
            ForeverGeometryTile first = result.Tiles[0];
            if (!string.Equals(first.SourceSha256, source.InputSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(first.ClientProduct, state.ClientProduct, StringComparison.OrdinalIgnoreCase) ||
                first.ClientBuild != state.ClientBuild ||
                !string.Equals(source.ClientProduct, first.ClientProduct, StringComparison.OrdinalIgnoreCase) ||
                source.ClientBuild != first.ClientBuild || !CountsEqual(source.Counts, first.SourceCounts) ||
                !string.Equals(source.ConfigHash, state.SettingsHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Source ADT ({adt.X},{adt.Y}) identity or geometry metadata does not match the batch.");
            }

            int[][] calculated = result.IndependentlyExpectedTiles
                .OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z)
                .Select(static tile => new[] { tile.X, tile.Z }).ToArray();
            int[][] exported = result.Tiles.Select(static tile => new[] { tile.DtTileX, tile.DtTileZ })
                .Distinct(IntPairComparer.Instance).OrderBy(static pair => pair[0]).ThenBy(static pair => pair[1]).ToArray();
            if (!PairArraySetEquals(calculated, exported, tileCoordinates: true) ||
                !PairArraySetEquals(calculated, source.IndependentlyExpectedTiles, tileCoordinates: true) ||
                !PairArraySetEquals(calculated, source.ContributingTiles, tileCoordinates: true))
            {
                throw new InvalidDataException($"Source ADT ({adt.X},{adt.Y}) recorded contribution tiles differ from independent triangle-bound coverage.");
            }

            foreach (int[] tile in calculated) expectedTiles.Add((tile[0], tile[1]));
        }

        return expectedTiles.OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z)
            .Select(static tile => new[] { tile.X, tile.Z }).ToArray();
    }

    private static int[][] ValidateRecordedSourceCoverage(BatchState state, ForeverGeometryBatchRequest request)
    {
        AdtCoordinate[] requested = EnumerateAdts(request);
        ValidateStateAdtSelection(state);
        if (requested.Length != state.SourceTasks.Count || HasDuplicatePairs(state.SourceTasks
                .Select(static task => new[] { task.AdtX, task.AdtY }).ToArray()) ||
            !PairArraySetEquals(requested.Select(static adt => new[] { adt.X, adt.Y }).ToArray(),
                state.SourceTasks.Select(static task => new[] { task.AdtX, task.AdtY }).ToArray(), tileCoordinates: false))
        {
            throw new InvalidDataException("Requested ADTs and source tasks are incomplete, duplicated, or inconsistent.");
        }

        HashSet<(int X, int Z)> expectedTiles = [];
        foreach (BatchSourceTask source in state.SourceTasks)
        {
            int[][] actual = source.ContributingTiles;
            int[][] independentlyExpected = source.IndependentlyExpectedTiles;
            if (source.Status != "succeeded" || source.ClientBuild != state.ClientBuild ||
                !string.Equals(source.ClientProduct, state.ClientProduct, StringComparison.OrdinalIgnoreCase) ||
                source.ConfigHash != state.SettingsHash ||
                !PairArraySetEquals(actual, independentlyExpected, tileCoordinates: true))
            {
                throw new InvalidDataException($"Source ADT ({source.AdtX},{source.AdtY}) has no matching independent tile coverage record.");
            }

            foreach (int[] pair in independentlyExpected) expectedTiles.Add((pair[0], pair[1]));
        }
        if (expectedTiles.Count == 0)
        {
            throw new InvalidDataException("Independent geometry coverage did not identify any expected Detour tiles.");
        }
        return expectedTiles.OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z)
            .Select(static tile => new[] { tile.X, tile.Z }).ToArray();
    }

    private static void ValidateStateAdtSelection(BatchState state)
    {
        if (state.AdtSelectionMode is not ("rectangle" or "explicit_list") ||
            state.RequestedAdts.Length is < 1 or > 4096 || HasDuplicatePairs(state.RequestedAdts) ||
            state.RequestedAdts.Any(static pair => pair is not { Length: 2 } || pair[0] is < 0 or >= 64 || pair[1] is < 0 or >= 64))
        {
            throw new InvalidDataException("Batch state has a missing, duplicated, or invalid requested ADT set.");
        }

        if (state.AdtSelectionMode == "rectangle")
        {
            if (state.AdtMinX < 0 || state.AdtMinY < 0 || state.AdtMaxX >= 64 || state.AdtMaxY >= 64 ||
                state.AdtMinX > state.AdtMaxX || state.AdtMinY > state.AdtMaxY)
            {
                throw new InvalidDataException("Batch state has an invalid rectangle ADT selection.");
            }
            int[][] rectangle = (from y in Enumerable.Range(state.AdtMinY, state.AdtMaxY - state.AdtMinY + 1)
                                 from x in Enumerable.Range(state.AdtMinX, state.AdtMaxX - state.AdtMinX + 1)
                                 select new[] { x, y }).ToArray();
            if (!PairArraySetEquals(rectangle, state.RequestedAdts, tileCoordinates: false))
            {
                throw new InvalidDataException("Batch state requested ADT set differs from its rectangle selection.");
            }
        }
    }

    private static bool CountsEqual(GeometryCounts? left, GeometryCounts right) => left is not null &&
        left.TerrainTriangles == right.TerrainTriangles && left.CollisionTriangles == right.CollisionTriangles &&
        left.WmoCollisionTriangles == right.WmoCollisionTriangles && left.M2CollisionTriangles == right.M2CollisionTriangles &&
        left.LiquidTriangles == right.LiquidTriangles && left.WaterTriangles == right.WaterTriangles &&
        left.MagmaSlimeTriangles == right.MagmaSlimeTriangles;

    private static bool HasDuplicatePairs(int[][] pairs) => pairs.Any(static pair => pair is not { Length: 2 }) ||
        pairs.Where(static pair => pair is { Length: 2 }).Select(static pair => (pair[0], pair[1])).Distinct().Count() != pairs.Length;

    private static bool PairArraySetEquals(int[][] expected, int[][] actual, bool tileCoordinates)
    {
        if (expected.Any(static pair => pair is not { Length: 2 }) ||
            actual.Any(static pair => pair is not { Length: 2 }) ||
            HasDuplicatePairs(expected) || HasDuplicatePairs(actual) || expected.Length != actual.Length)
        {
            return false;
        }
        if (tileCoordinates && (expected.Any(static pair => !NavmeshCoords.IsValidTile(pair[0], pair[1])) ||
                                actual.Any(static pair => !NavmeshCoords.IsValidTile(pair[0], pair[1]))))
        {
            return false;
        }
        return expected.Select(static pair => (pair[0], pair[1])).ToHashSet()
            .SetEquals(actual.Select(static pair => (pair[0], pair[1])));
    }

    /// <summary>
    /// Turns a selected batch into a continent manifest only after an independent
    /// mapextractor --list-tiles log from the matching Forever client proves exact
    /// WDT ADT coverage and every geometry-contributing Detour tile is verified.
    /// This reads only the supplied log and batch artifacts; it does not open CASC.
    /// </summary>
    public static ForeverGeometryPublicationResult SealForPublication(string stateFile,
        string clientRoot, string mapExtractorTileListLog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapExtractorTileListLog);

        _ = VerifyState(stateFile);
        string statePath = Path.GetFullPath(stateFile);
        BatchState state = JsonSerializer.Deserialize<BatchState>(File.ReadAllText(statePath), JsonOptions)
            ?? throw new InvalidDataException($"Batch state is empty or malformed: {statePath}");
        if (state.AdtSelectionMode != "explicit_list")
        {
            throw new InvalidDataException("Publication requires an explicit --adt-list request; rectangle selections are test/compatibility batches only.");
        }
        (string Product, int Build) client = ReadForeverClientIdentity(clientRoot);
        if (!string.Equals(client.Product, state.ClientProduct, StringComparison.OrdinalIgnoreCase) ||
            client.Build != state.ClientBuild)
        {
            throw new InvalidDataException(
                $"WDT coverage client identity {client.Product} Build {client.Build} does not match batch " +
                $"{state.ClientProduct} Build {state.ClientBuild}.");
        }

        string tileListLogPath = Path.GetFullPath(mapExtractorTileListLog);
        HashSet<(int X, int Y)> wdtAdts = ParseMapExtractorTileList(tileListLogPath,
            state.MapId, client.Build, clientRoot);
        (int X, int Y)[] requestedAdts = state.RequestedAdts
            .Select(static pair => (pair[0], pair[1])).ToArray();
        HashSet<(int X, int Y)> requestedSet = requestedAdts.ToHashSet();
        (int X, int Y)[] processedAdts = state.SourceTasks
            .Where(static source => source.Status == "succeeded" && !string.IsNullOrWhiteSpace(source.InputSha256) &&
                                    source.ClientBuild > 0 && !string.IsNullOrWhiteSpace(source.ClientProduct))
            .Select(static source => (source.AdtX, source.AdtY)).ToArray();
        HashSet<(int X, int Y)> processedSet = processedAdts.ToHashSet();
        if (requestedSet.Count != requestedAdts.Length || processedSet.Count != processedAdts.Length ||
            processedAdts.Length != state.SourceTasks.Count || requestedSet.Count != state.SourceTasks.Count ||
            !requestedSet.SetEquals(wdtAdts) || !requestedSet.SetEquals(processedSet))
        {
            (int X, int Y)[] requestedButUnprocessed = requestedSet.Except(processedSet).ToArray();
            (int X, int Y)[] processedButUnrequested = processedSet.Except(requestedSet).ToArray();
            (int X, int Y)[] wdtOnly = wdtAdts.Except(requestedSet).ToArray();
            (int X, int Y)[] requestedMissingFromWdt = requestedSet.Except(wdtAdts).ToArray();
            throw new InvalidDataException(
                $"Publication refused: Requested ADTs={requestedSet.Count}, successfully processed ADTs={processedSet.Count}, " +
                $"WDT ADTs={wdtAdts.Count}; all three unique sets must match exactly. " +
                $"Requested-but-unprocessed={FormatAdtCoordinates(requestedButUnprocessed)}, " +
                $"processed-but-unrequested={FormatAdtCoordinates(processedButUnrequested)}, " +
                $"WDT-only={FormatAdtCoordinates(wdtOnly)}, requested-but-absent-from-WDT={FormatAdtCoordinates(requestedMissingFromWdt)}.");
        }

        HashSet<(int X, int Z)> independentlyExpectedTiles = state.ExpectedTiles
            .Select(static pair => (pair[0], pair[1])).ToHashSet();
        HashSet<(int X, int Z)> contributedTiles = state.SourceTasks
            .SelectMany(static source => source.IndependentlyExpectedTiles)
            .Select(static pair => (pair[0], pair[1])).ToHashSet();
        HashSet<(int X, int Z)> requestedTiles = state.TargetTiles
            .Select(static pair => (pair[0], pair[1])).ToHashSet();
        HashSet<(int X, int Z)> completedTiles = state.TileTasks
            .Select(static tile => (tile.TileX, tile.TileZ)).ToHashSet();
        if (independentlyExpectedTiles.Count == 0 || contributedTiles.Count != state.ExpectedTiles.Length ||
            !independentlyExpectedTiles.SetEquals(contributedTiles) ||
            requestedTiles.Count != state.TargetTiles.Length ||
            completedTiles.Count != state.TileTasks.Count || !contributedTiles.SetEquals(requestedTiles) ||
            !requestedTiles.SetEquals(completedTiles) || state.TileTasks.Any(static tile =>
                tile.Status != "succeeded" || !tile.DnmReadbackSucceeded || !tile.HeightQuerySucceeded ||
                !tile.ConnectedPathSucceeded))
        {
            throw new InvalidDataException(
                "Publication refused: every geometry-contributing target tile must have one successful, read-back, height- and path-verified task.");
        }

        DnmSetVerification verification = ForeverGeometryBakeVerifier.VerifyDnmFiles(
            Path.GetFullPath(state.DnmOutputDirectory), requestedTiles);
        if (verification.TileCount != requestedTiles.Count || !verification.HeightQuerySucceeded ||
            !verification.ConnectedPathSucceeded)
        {
            throw new InvalidDataException("Publication refused: the exact DNM set failed independent read-back or query verification.");
        }

        string dnmRoot = Path.GetFullPath(state.DnmOutputDirectory);
        string manifestPath = Path.Combine(dnmRoot, "manifest.json");
        byte[] originalManifest = File.ReadAllBytes(manifestPath);
        JsonObject manifest = JsonNode.Parse(originalManifest)?.AsObject()
            ?? throw new InvalidDataException($"Selected batch manifest is malformed: {manifestPath}");
        string tileListSha256 = HashFile(tileListLogPath);
        (int X, int Y)[] orderedAdts = wdtAdts.OrderBy(static item => item.Y).ThenBy(static item => item.X).ToArray();
        (int X, int Z)[] orderedTiles = requestedTiles.OrderBy(static item => item.X).ThenBy(static item => item.Z).ToArray();
        string evidencePath = Path.Combine(dnmRoot, ForeverNavmeshCacheManifest.WdtCoverageEvidenceRelativePath
            .Replace('/', Path.DirectorySeparatorChar));
        byte[]? originalEvidence = File.Exists(evidencePath) ? File.ReadAllBytes(evidencePath) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        WriteWdtCoverageEvidence(evidencePath, client.Product, client.Build, state.MapId, orderedAdts,
            Path.GetFileName(tileListLogPath), tileListSha256);
        string evidenceSha256 = HashFile(evidencePath);
        manifest["scope"] = "continent";
        manifest["complete_for_continent"] = true;
        manifest["publishable_as_continent_cache"] = true;
        manifest["coverage_provenance"] = "wdt_tile_list";
        manifest["coverage"] = new JsonObject
        {
            ["wdt_tile_list_file"] = ForeverNavmeshCacheManifest.WdtCoverageEvidenceRelativePath,
            ["wdt_tile_list_sha256"] = evidenceSha256,
            ["wdt_client_product"] = client.Product,
            ["wdt_client_build"] = client.Build,
            ["wdt_map_id"] = state.MapId,
            ["expected_source_adts_xy"] = ToJsonPairs(orderedAdts.Select(static item => (item.X, item.Y))),
            ["processed_source_adts_xy"] = ToJsonPairs(processedSet.OrderBy(static item => item.Y).ThenBy(static item => item.X)
                .Select(static item => (item.X, item.Y))),
            ["expected_tile_indices"] = ToJsonPairs(orderedTiles),
            ["completed_tile_indices"] = ToJsonPairs(orderedTiles),
        };

        WriteTextAtomically(manifestPath, manifest.ToJsonString(JsonOptions));
        try
        {
            ForeverNavmeshCacheManifest.ValidateForPublication(dnmRoot, client.Product, client.Build,
                state.SettingsHash!, state.MapId, state.Continent, orderedAdts, orderedTiles);
            ForeverNavmeshCacheManifest.ValidateForRuntime(dnmRoot, client.Product, client.Build,
                state.SettingsHash!, state.MapId, state.Continent);
        }
        catch
        {
            File.WriteAllBytes(manifestPath + ".restore", originalManifest);
            File.Move(manifestPath + ".restore", manifestPath, overwrite: true);
            if (originalEvidence is null)
            {
                if (File.Exists(evidencePath)) File.Delete(evidencePath);
            }
            else
            {
                File.WriteAllBytes(evidencePath + ".restore", originalEvidence);
                File.Move(evidencePath + ".restore", evidencePath, overwrite: true);
            }
            throw;
        }

        return new ForeverGeometryPublicationResult(statePath, manifestPath, evidenceSha256,
            orderedAdts.Length, orderedTiles.Length);
    }

    private static void WriteWdtCoverageEvidence(string path, string product, int build, int mapId,
        IReadOnlyList<(int X, int Y)> adts, string sourceLogFile, string sourceLogSha256)
    {
        var proof = new
        {
            format = ForeverNavmeshCacheManifest.WdtCoverageEvidenceFormat,
            client_product = product,
            client_build = build,
            map_id = mapId,
            source = "mapextractor --list-tiles",
            source_log_file = sourceLogFile,
            source_log_sha256 = sourceLogSha256,
            adt_xy = adts.Select(static adt => new[] { adt.X, adt.Y }).ToArray(),
        };
        WriteTextAtomically(path, JsonSerializer.Serialize(proof, JsonOptions));
    }

    private static HashSet<(int X, int Y)> ParseMapExtractorTileList(string logPath, int mapId, int build,
        string clientRoot)
    {
        if (!File.Exists(logPath))
        {
            throw new FileNotFoundException("The independent mapextractor --list-tiles log does not exist.", logPath);
        }

        string[] lines = File.ReadAllLines(logPath);
        string[] buildMarkers = lines.Select(static line => line.Trim())
            .Where(static line => line.StartsWith("Detected client build:", StringComparison.Ordinal))
            .ToArray();
        string[] mapMarkers = lines.Select(static line => line.Trim())
            .Where(static line => line.StartsWith("Restricted extraction to MapID ", StringComparison.Ordinal))
            .ToArray();
        string[] storageMarkers = lines.Select(static line => line.Trim())
            .Where(static line => line.StartsWith("Opened casc storage", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string expectedBuildMarker = $"Detected client build: {build}";
        string expectedMapMarker = $"Restricted extraction to MapID {mapId}.";
        string expectedDataPath = Path.GetFullPath(Path.Combine(clientRoot, "Data"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? listedDataPath = storageMarkers.Length == 1
            ? ExtractOpenedStoragePath(storageMarkers[0])
            : null;
        if (buildMarkers.Length != 1 || !string.Equals(buildMarkers[0], expectedBuildMarker, StringComparison.Ordinal) ||
            mapMarkers.Length != 1 || !string.Equals(mapMarkers[0], expectedMapMarker, StringComparison.Ordinal) ||
            listedDataPath is null ||
            !string.Equals(Path.GetFullPath(listedDataPath).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar), expectedDataPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The WDT tile-list log must contain exactly one matching CASC Data root, detected Build, and restricted MapID marker.");
        }

        HashSet<(int X, int Y)> result = [];
        foreach (string line in lines)
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !fields[0].Equals("tile", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length != 4 || !int.TryParse(fields[1], out int listedMap) ||
                !int.TryParse(fields[2], out int x) || !int.TryParse(fields[3], out int y) ||
                listedMap != mapId || x is < 0 or >= 64 || y is < 0 or >= 64 || !result.Add((x, y)))
            {
                throw new InvalidDataException($"The WDT tile-list log has an invalid, duplicate, or unexpected tile line: {line}");
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("The WDT tile-list log contains no tile records.");
        }

        return result;
    }

    private static string? ExtractOpenedStoragePath(string line)
    {
        const string Prefix = "Opened casc storage ";
        string value = line.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        value = value[Prefix.Length..].Trim();
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                  (value[0] == '"' && value[^1] == '"')))
        {
            value = value[1..^1];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FormatAdtCoordinates(IEnumerable<(int X, int Y)> coordinates)
    {
        (int X, int Y)[] sorted = coordinates.OrderBy(static pair => pair.Y)
            .ThenBy(static pair => pair.X).ToArray();
        string shown = string.Join(",", sorted.Take(12).Select(static pair => $"({pair.X},{pair.Y})"));
        return sorted.Length > 12 ? $"[{shown},… +{sorted.Length - 12}]" : $"[{shown}]";
    }

    private static (string Product, int Build) ReadForeverClientIdentity(string clientRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        string buildInfoPath = Path.Combine(Path.GetFullPath(clientRoot), ".build.info");
        if (!File.Exists(buildInfoPath))
        {
            throw new FileNotFoundException("The Forever client .build.info file is required to seal a batch.", buildInfoPath);
        }

        string[] lines = File.ReadAllLines(buildInfoPath);
        if (lines.Length < 2) throw new InvalidDataException("The Forever client .build.info has no active product row.");
        string[] headers = lines[0].Split('|').Select(static header => header.Split('!')[0]).ToArray();
        int activeIndex = Array.IndexOf(headers, "Active");
        int versionIndex = Array.IndexOf(headers, "Version");
        int productIndex = Array.IndexOf(headers, "Product");
        if (activeIndex < 0 || versionIndex < 0 || productIndex < 0)
        {
            throw new InvalidDataException("The Forever client .build.info is missing Active, Version, or Product.");
        }

        string[][] activeRows = lines.Skip(1).Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Split('|'))
            .Where(fields => fields.Length > Math.Max(activeIndex, Math.Max(versionIndex, productIndex)) &&
                             fields[activeIndex] == "1")
            .ToArray();
        if (activeRows.Length != 1)
        {
            throw new InvalidDataException($"The Forever client .build.info has {activeRows.Length} active rows; exactly one is required.");
        }

        string product = activeRows[0][productIndex];
        string version = activeRows[0][versionIndex];
        string[] parts = version.Split('.');
        if (!string.Equals(product, StartupClientVersion.ForeverProductId, StringComparison.OrdinalIgnoreCase) ||
            parts.Length < 2 || !int.TryParse(parts[^1], out int build) || build <= 0)
        {
            throw new InvalidDataException(
                $"Batch sealing requires Product '{StartupClientVersion.ForeverProductId}' and a valid Build in .build.info; found '{product}' Version '{version}'.");
        }

        return (product, build);
    }

    private static JsonArray ToJsonPairs(IEnumerable<(int X, int Y)> pairs)
    {
        JsonArray result = [];
        foreach ((int x, int y) in pairs) result.Add(new JsonArray(x, y));
        return result;
    }

    private static void WriteTextAtomically(string path, string value)
    {
        string temporary = path + ".partial";
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, overwrite: true);
    }

    private static BatchState CreateState(ForeverGeometryBatchRequest request, string inputRoot,
        string outputRoot, string dnmRoot, string signature, IReadOnlyList<AdtCoordinate> adts) => new()
    {
        Format = StateFormat,
        RequestSignature = signature,
        Status = "pending",
        MapId = request.MapId,
        AdtMinX = request.AdtMinX,
        AdtMinY = request.AdtMinY,
        AdtMaxX = request.AdtMaxX,
        AdtMaxY = request.AdtMaxY,
        AdtSelectionMode = request.AdtList is null ? "rectangle" : "explicit_list",
        RequestedAdts = adts.Select(static adt => new[] { adt.X, adt.Y }).ToArray(),
        InputDirectory = inputRoot,
        OutputDirectory = outputRoot,
        DnmOutputDirectory = dnmRoot,
        Continent = request.Continent,
        AgentRadius = request.AgentRadius,
        AgentMaxClimb = request.AgentMaxClimb,
        WalkableSlope = request.WalkableSlope,
        PaddingYards = request.PaddingYards,
        MinWorldZ = request.MinWorldZ,
        SourceTasks = adts.Select(adt => new BatchSourceTask { AdtX = adt.X, AdtY = adt.Y }).ToList(),
        TileTasks = [],
        TargetTiles = [],
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private static void ValidateRequest(ForeverGeometryBatchRequest request)
    {
        if (request.MapId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Batch MapID must be non-negative.");
        }

        if (request.AdtList is null &&
            (request.AdtMinX < 0 || request.AdtMinY < 0 || request.AdtMaxX >= 64 || request.AdtMaxY >= 64 ||
             request.AdtMinX > request.AdtMaxX || request.AdtMinY > request.AdtMaxY ||
             (long)(request.AdtMaxX - request.AdtMinX + 1) * (request.AdtMaxY - request.AdtMinY + 1) > 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Batch ADT range must be an explicit rectangle inside the 0..63 grid.");
        }

        if (request.AdtList is { } adtList &&
            (adtList.Count is < 1 or > 4096 || adtList.Any(static adt => adt.X is < 0 or >= 64 || adt.Y is < 0 or >= 64) ||
             adtList.Distinct().Count() != adtList.Count))
        {
            throw new ArgumentException("Explicit ADT list must contain 1..4096 unique coordinates in the 0..63 grid.", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DnmOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Continent);
        if (!float.IsFinite(request.AgentRadius) || request.AgentRadius < 0 ||
            !float.IsFinite(request.AgentMaxClimb) || request.AgentMaxClimb < 0 ||
            !float.IsFinite(request.PaddingYards) || request.PaddingYards < 0 ||
            !float.IsFinite(request.WalkableSlope) || request.WalkableSlope is < 0 or > 90 ||
            request.MinWorldZ.HasValue && !float.IsFinite(request.MinWorldZ.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Batch bake settings must be finite and within supported ranges.");
        }

        if (request.PaddingYards != TileGeometryExtractor.PaddingYd)
        {
            throw new InvalidOperationException(
                $"Forever runtime supports only fixed padding {TileGeometryExtractor.PaddingYd}; custom --padding-yards values are rejected.");
        }

        if (request.TargetTiles is { Count: > 0 } && request.TargetTiles.Distinct().Count() != request.TargetTiles.Count)
        {
            throw new ArgumentException("Explicit target Detour tiles must be unique.", nameof(request));
        }
    }

    private static AdtCoordinate[] EnumerateAdts(ForeverGeometryBatchRequest request) =>
        request.AdtList is not null
            ? request.AdtList.OrderBy(static adt => adt.Y).ThenBy(static adt => adt.X).ToArray()
            : (from y in Enumerable.Range(request.AdtMinY, request.AdtMaxY - request.AdtMinY + 1)
               from x in Enumerable.Range(request.AdtMinX, request.AdtMaxX - request.AdtMinX + 1)
               select new AdtCoordinate(x, y)).ToArray();

    private static string ResolveInputPath(string root, int mapId, AdtCoordinate adt)
    {
        string fileName = $"map{mapId:D4}_adt{adt.X:D2}_{adt.Y:D2}.rawgeo.json";
        string folder = $"adt{adt.X:D2}_{adt.Y:D2}";
        string[] candidates =
        [
            Path.Combine(root, fileName),
            Path.Combine(root, folder, "raw-geometry", fileName),
            Path.Combine(root, folder, fileName),
            Path.Combine(root, "raw-geometry", fileName),
        ];
        string? match = candidates.FirstOrDefault(File.Exists);
        return match is not null
            ? Path.GetFullPath(match)
            : throw new FileNotFoundException($"Missing explicitly requested rawgeo for MapID {mapId} ADT ({adt.X},{adt.Y}) under {root}.");
    }

    private static void ReconcileTileTasks(BatchState state, IReadOnlyCollection<(int X, int Z)> targetTiles,
        string outputRoot, string dnmRoot)
    {
        HashSet<(int X, int Z)> expected = targetTiles.ToHashSet();
        foreach (BatchTileTask stale in state.TileTasks.Where(item => !expected.Contains((item.TileX, item.TileZ))))
        {
            stale.Status = "failed";
            stale.FailureReason = "Tile no longer belongs to the explicit target set.";
            stale.SourceSetHash = "";
            DeleteOwnedFile(stale.GeometryOutputPath, Path.Combine(outputRoot, "tiles"));
            DeleteOwnedFile(stale.DnmOutputPath, dnmRoot);
        }

        state.TileTasks = state.TileTasks.Where(item => expected.Contains((item.TileX, item.TileZ))).ToList();
        foreach ((int x, int z) in targetTiles)
        {
            if (!state.TileTasks.Any(item => item.TileX == x && item.TileZ == z))
            {
                state.TileTasks.Add(new BatchTileTask { TileX = x, TileZ = z });
            }
        }
    }

    private static void WriteSelectedManifest(string dnmRoot, BatchState state,
        ForeverNavmeshBakeProfile profile, DnmSetVerification verification)
    {
        BakedTileRecord[] records = verification.Tiles.Select(record =>
        {
            BatchTileTask task = state.TileTasks.Single(item => item.TileX == record.DtTileX && item.TileZ == record.DtTileZ);
            return record with
            {
                TerrainTriangles = task.TerrainTriangles,
                CollisionTriangles = task.CollisionTriangles,
                LiquidTriangles = task.LiquidTriangles,
            };
        }).ToArray();
        GeometrySourceRecord[] sources = state.SourceTasks.OrderBy(static task => task.AdtY)
            .ThenBy(static task => task.AdtX).Select(ToSourceRecord).ToArray();
        var manifest = new
        {
            format = ForeverNavmeshCacheManifest.Format,
            client_product = profile.ClientProduct,
            client_build = profile.ClientBuild,
            data_set_id = profile.DataSetIdentity,
            settings_hash = profile.SettingsHash,
            map_id = state.MapId,
            continent = profile.Continent,
            scope = "explicit_adt_and_tile_set",
            complete_for_requested_sources_and_tiles = true,
            complete_for_continent = false,
            publishable_as_continent_cache = false,
            source_adts = sources,
            coverage_provenance = "explicit_request_set",
            batch_state_file = Path.Combine(state.OutputDirectory, "batch-state.json"),
            bake_settings = new
            {
                agent_radius = profile.AgentRadius,
                agent_max_climb = profile.AgentMaxClimb,
                walkable_slope = profile.WalkableSlope,
                min_world_z = profile.MinWorldZ,
                cell_size = NavmeshSettings.CellSize,
                cell_height = NavmeshSettings.CellHeight,
                tile_world_size = NavmeshSettings.TileWorldSize,
                verts_per_poly = NavmeshSettings.VertsPerPoly,
                format_version = NavmeshSettings.FormatVersion,
                dot_recast_version = profile.DotRecastVersion,
                padding_yards = profile.PaddingYards,
                geometry_export_version = profile.GeometryExportVersion,
                raw_geometry_format = profile.RawGeometryFormat,
            },
            query_verification = new
            {
                add_tile_count = verification.AddTileCount,
                cross_tile_link_count = verification.CrossTileLinkCount,
                height_query_succeeded = verification.HeightQuerySucceeded,
                path_succeeded = verification.ConnectedPathSucceeded,
                path_polygon_count = verification.PathPolyCount,
            },
            tiles = records,
        };
        File.WriteAllText(Path.Combine(dnmRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static GeometrySourceRecord ToSourceRecord(BatchSourceTask task) => new(
        [task.AdtX, task.AdtY], Path.GetFileName(task.InputPath), task.InputSha256,
        task.Counts ?? throw new InvalidDataException($"Source ADT ({task.AdtX},{task.AdtY}) has no validated geometry counts."));

    private static NavmeshBakeOptions CreateOptions(ForeverGeometryBatchRequest request) => new()
    {
        AgentRadius = request.AgentRadius,
        AgentMaxClimb = request.AgentMaxClimb,
        WalkableSlope = request.WalkableSlope,
        MinWorldZ = request.MinWorldZ.HasValue
            ? new Dictionary<string, float?> { [request.Continent] = request.MinWorldZ.Value }
            : [],
    };

    private static string ComputeRequestSignature(ForeverGeometryBatchRequest request, string inputRoot, string dnmRoot,
        IReadOnlyList<AdtCoordinate> expectedAdts)
    {
        string tileSignature = request.TargetTiles is null
            ? "inferred"
            : string.Join(';', request.TargetTiles.OrderBy(static tile => tile.X).ThenBy(static tile => tile.Z)
                .Select(static tile => $"{tile.X},{tile.Z}"));
        string adtSignature = string.Join(';', expectedAdts.Select(static adt => $"{adt.X},{adt.Y}"));
        string value = string.Join('|', StateFormat, request.MapId,
            request.AdtList is null ? "rectangle" : "explicit_list", adtSignature,
            request.AdtMinX, request.AdtMinY, request.AdtMaxX, request.AdtMaxY, inputRoot, dnmRoot, request.Continent,
            request.AgentRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            request.AgentMaxClimb.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            request.WalkableSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            request.PaddingYards.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            request.MinWorldZ?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "<none>", tileSignature,
            NavmeshSettings.FormatVersion, typeof(NavmeshPathfinder).Assembly.GetName().Version?.ToString() ?? "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void WriteDnmAtomically(string path, DtMeshData data)
    {
        string temporary = path + ".partial";
        if (File.Exists(temporary)) File.Delete(temporary);
        using (FileStream stream = File.Create(temporary))
        using (BinaryWriter writer = new(stream))
        {
            new DtMeshDataWriter().Write(writer, data, RcByteOrder.LITTLE_ENDIAN, false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void EnsureOnlyOwnedDnmFiles(BatchState state, string dnmRoot)
    {
        HashSet<string> owned = state.TileTasks.Select(task => Path.GetFullPath(
                string.IsNullOrWhiteSpace(task.DnmOutputPath)
                    ? Path.Combine(dnmRoot, $"tile_{task.TileX}_{task.TileZ}.dnm")
                    : task.DnmOutputPath))
            .Where(path => string.Equals(Path.GetDirectoryName(path), dnmRoot, StringComparison.OrdinalIgnoreCase))
            .Select(static path => Path.GetFileName(path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] actual = Directory.EnumerateFiles(dnmRoot, "*.dnm").Select(Path.GetFileName)
            .Where(static name => name is not null).Cast<string>().ToArray();
        if (actual.Any(name => !owned.Contains(name)))
        {
            throw new IOException("DNM output contains files not owned by this batch state; choose a clean directory or remove only the unowned files.");
        }
    }

    private static void RemoveOwnedOutputs(BatchState state, string outputRoot, string dnmRoot)
    {
        foreach (BatchTileTask task in state.TileTasks)
        {
            DeleteOwnedFile(string.IsNullOrWhiteSpace(task.GeometryOutputPath)
                ? Path.Combine(outputRoot, "tiles", $"tile_{task.TileX}_{task.TileZ}.geometry.json")
                : task.GeometryOutputPath, Path.Combine(outputRoot, "tiles"));
            DeleteOwnedFile(string.IsNullOrWhiteSpace(task.DnmOutputPath)
                ? Path.Combine(dnmRoot, $"tile_{task.TileX}_{task.TileZ}.dnm")
                : task.DnmOutputPath, dnmRoot);
        }
        DeleteOwnedFile(Path.Combine(dnmRoot, "manifest.json"), dnmRoot);
        DeleteOwnedFile(Path.Combine(dnmRoot, ForeverNavmeshCacheManifest.WdtCoverageEvidenceRelativePath
            .Replace('/', Path.DirectorySeparatorChar)), Path.Combine(dnmRoot, "coverage"));
    }

    private static void DeleteOwnedFile(string? path, string expectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = Path.GetFullPath(path);
        if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
        {
            File.Delete(full);
        }
    }

    private static void SaveState(string path, BatchState state)
    {
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        string temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void RecordFailedBatch(string statePath, BatchState state, string outputRoot,
        string dnmRoot, Stopwatch totalTimer, long peakWorkingSet, string failure)
    {
        totalTimer.Stop();
        state.Status = "failed";
        state.FailureReason = failure;
        state.FailedTaskCount = state.SourceTasks.Count(static task => task.Status == "failed") +
                                state.TileTasks.Count(static task => task.Status == "failed");
        state.ElapsedMilliseconds = totalTimer.ElapsedMilliseconds;
        state.PeakSampledWorkingSetBytes = Math.Max(peakWorkingSet, ReadWorkingSet());
        state.OutputFileCount = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).LongCount() +
                                Directory.EnumerateFiles(dnmRoot, "*", SearchOption.AllDirectories).LongCount();
        state.DiskBytes = SumFileBytes(outputRoot) + SumFileBytes(dnmRoot);
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        SaveState(statePath, state);
    }

    private static string HashFile(string path) =>
        HashFileStreaming(path);

    private static string HashFileStreaming(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long ReadWorkingSet()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private static long SumFileBytes(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Sum(static file => new FileInfo(file).Length);

    private static void EnsureDisjointDirectories(params string[] directories)
    {
        string[] full = directories.Select(Path.GetFullPath).ToArray();
        for (int i = 0; i < full.Length; i++)
        {
            for (int j = i + 1; j < full.Length; j++)
            {
                if (IsSameOrChild(full[i], full[j]) || IsSameOrChild(full[j], full[i]))
                {
                    throw new ArgumentException("Batch input, state/output, and DNM directories must be distinct and non-nested.");
                }
            }
        }
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed class IntPairComparer : IEqualityComparer<int[]>
    {
        public static readonly IntPairComparer Instance = new();
        public bool Equals(int[]? left, int[]? right) => left is not null && right is not null && left.Length == 2 && right.Length == 2 && left[0] == right[0] && left[1] == right[1];
        public int GetHashCode(int[] value) => HashCode.Combine(value[0], value[1]);
    }

    private sealed class BatchState
    {
        public string Format { get; set; } = "";
        public string RequestSignature { get; set; } = "";
        public string Status { get; set; } = "pending";
        public int MapId { get; set; }
        public int AdtMinX { get; set; }
        public int AdtMinY { get; set; }
        public int AdtMaxX { get; set; }
        public int AdtMaxY { get; set; }
        public string AdtSelectionMode { get; set; } = "rectangle";
        public int[][] RequestedAdts { get; set; } = [];
        public int[][] ExpectedTiles { get; set; } = [];
        public string InputDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string DnmOutputDirectory { get; set; } = "";
        public string Continent { get; set; } = "";
        public string? ClientProduct { get; set; }
        public int ClientBuild { get; set; }
        public string? SettingsHash { get; set; }
        public float AgentRadius { get; set; }
        public float AgentMaxClimb { get; set; }
        public float WalkableSlope { get; set; }
        public float PaddingYards { get; set; }
        public float? MinWorldZ { get; set; }
        public List<BatchSourceTask> SourceTasks { get; set; } = [];
        public List<BatchTileTask> TileTasks { get; set; } = [];
        public int[][] TargetTiles { get; set; } = [];
        public string? FailureReason { get; set; }
        public DnmSetVerification? Verification { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long PeakSampledWorkingSetBytes { get; set; }
        public long OutputFileCount { get; set; }
        public long DiskBytes { get; set; }
        public int FailedTaskCount { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed class BatchSourceTask
    {
        public int AdtX { get; set; }
        public int AdtY { get; set; }
        public string Status { get; set; } = "pending";
        public int AttemptCount { get; set; }
        public string InputPath { get; set; } = "";
        public string InputSha256 { get; set; } = "";
        public string ClientProduct { get; set; } = "";
        public int ClientBuild { get; set; }
        public string ConfigHash { get; set; } = "";
        public GeometryCounts? Counts { get; set; }
        public int[][] ContributingTiles { get; set; } = [];
        public int[][] IndependentlyExpectedTiles { get; set; } = [];
        public string[] OutputFiles { get; set; } = [];
        public string? FailureReason { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long PeakSampledWorkingSetBytes { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed class BatchTileTask
    {
        public int TileX { get; set; }
        public int TileZ { get; set; }
        public string ClientProduct { get; set; } = "";
        public int ClientBuild { get; set; }
        public string Status { get; set; } = "pending";
        public int AttemptCount { get; set; }
        public int[][] SourceAdts { get; set; } = [];
        public string[] SourceSha256s { get; set; } = [];
        public string SourceSetHash { get; set; } = "";
        public string ConfigHash { get; set; } = "";
        public string GeometryOutputPath { get; set; } = "";
        public string GeometrySha256 { get; set; } = "";
        public string DnmOutputPath { get; set; } = "";
        public string DnmSha256 { get; set; } = "";
        public int TerrainTriangles { get; set; }
        public int CollisionTriangles { get; set; }
        public int LiquidTriangles { get; set; }
        public bool DnmReadbackSucceeded { get; set; }
        public bool HeightQuerySucceeded { get; set; }
        public bool ConnectedPathSucceeded { get; set; }
        public int CrossTileLinkCount { get; set; }
        public string? FailureReason { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long PeakSampledWorkingSetBytes { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
