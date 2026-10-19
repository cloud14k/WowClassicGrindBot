using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using PPather.Navmesh;

namespace ForeverGeometryTool;

internal sealed record ForeverWdtEnumerationResult(
    int MapId,
    int Build,
    string Product,
    string Locale,
    string ClientRoot,
    string DataRoot,
    string OutputDirectory,
    string[] Arguments,
    string ExecutableSha256,
    string Stdout,
    string Stderr,
    byte[] NativeTileList,
    HashSet<(int X, int Y)> Adts);

internal delegate ProcessStartInfo WdtEnumeratorProcessFactory(string executable,
    IReadOnlyList<string> arguments);

/// <summary>
/// Runs the pinned, locally built mapextractor in its WDT-only mode. A text log
/// supplied by a caller is never accepted as proof of complete WDT coverage.
/// </summary>
internal static class ForeverWdtTileEnumerator
{
    internal const string SourceRepository = ForeverNavmeshCacheManifest.WdtEnumeratorSourceRepository;
    internal const string SourceCommit = ForeverNavmeshCacheManifest.WdtEnumeratorSourceCommit;
    // Updated together with patches/forever-rawgeo-440d365.patch.
    internal const string ExpectedPatchSha256 = ForeverNavmeshCacheManifest.WdtEnumeratorPatchSha256;
    internal const string ProvenanceFileName = "forever-casc-tools.json";
    internal const string ProvenanceFormat = "wow-forever-casc-tools-v1";
    internal const int ExpectedWdtSlots = 64 * 64;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex CompletionMarker = new(
        @"^wdt-enumeration-complete map=(?<map>\d+) build=(?<build>\d+) visited=(?<visited>\d+) tiles=(?<tiles>\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static ForeverWdtEnumerationResult Enumerate(string clientRoot, string toolDirectory,
        string product, int build, int mapId, string locale, string cacheDirectory,
        WdtEnumeratorProcessFactory? processFactory = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (build <= 0 || mapId < 0 || !IsSupportedLocale(locale))
        {
            throw new ArgumentOutOfRangeException(nameof(build),
                "WDT enumeration requires a positive Build, non-negative MapID and a mapextractor locale name.");
        }

        string fullClientRoot = Path.GetFullPath(clientRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string dataRoot = Path.GetFullPath(Path.Combine(fullClientRoot, "Data"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullClientRoot) || !Directory.Exists(dataRoot) ||
            IsReparsePoint(fullClientRoot) || IsReparsePoint(dataRoot))
        {
            throw new InvalidDataException("The client root and its Data directory must exist as ordinary directories.");
        }

        string fullCacheRoot = Path.GetFullPath(cacheDirectory);
        if (!Directory.Exists(fullCacheRoot) || IsReparsePoint(fullCacheRoot))
        {
            throw new InvalidDataException("The cache directory must exist as an ordinary directory before WDT enumeration.");
        }

        string toolRoot = Path.GetFullPath(toolDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string executable = Path.Combine(toolRoot, "mapextractor.exe");
        string provenancePath = Path.Combine(toolRoot, ProvenanceFileName);
        if (!Directory.Exists(toolRoot) || IsReparsePoint(toolRoot) || !File.Exists(executable) ||
            IsReparsePoint(executable) || !File.Exists(provenancePath) || IsReparsePoint(provenancePath))
        {
            throw new FileNotFoundException(
                $"The verified mapextractor executable and {ProvenanceFileName} are required in one ordinary tool directory.",
                executable);
        }

        string executableHash = HashFile(executable);
        ValidateToolProvenance(provenancePath, executableHash);

        string coverageRoot = Path.Combine(fullCacheRoot, "coverage");
        if (Directory.Exists(coverageRoot) && IsReparsePoint(coverageRoot))
        {
            throw new InvalidDataException("The cache coverage directory must not be a reparse point.");
        }
        Directory.CreateDirectory(coverageRoot);
        string scratch = Path.Combine(coverageRoot, ".wdt-listing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string fullScratch = Path.GetFullPath(scratch);
        if (!IsSameOrChild(fullScratch, Path.GetFullPath(coverageRoot)) || IsReparsePoint(scratch))
        {
            throw new InvalidDataException("The isolated WDT listing directory escaped the cache coverage directory.");
        }

        try
        {
            string[] arguments =
            [
                "-i", fullClientRoot,
                "-o", fullScratch,
                "-p", product,
                "-l", locale,
                "-e", "1",
                "-m", mapId.ToString(CultureInfo.InvariantCulture),
                "--list-tiles",
            ];
            ProcessStartInfo startInfo = processFactory?.Invoke(executable, arguments)
                ?? CreateStartInfo(executable, arguments, toolRoot);
            if (startInfo.UseShellExecute)
            {
                throw new InvalidOperationException("The mapextractor process must run without a shell.");
            }
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            startInfo.CreateNoWindow = true;

            string stdout;
            string stderr;
            using (Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                StringBuilder stdoutBuilder = new();
                StringBuilder stderrBuilder = new();
                object outputGate = new();
                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data is null) return;
                    lock (outputGate) stdoutBuilder.AppendLine(eventArgs.Data);
                };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data is null) return;
                    lock (outputGate) stderrBuilder.AppendLine(eventArgs.Data);
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("mapextractor could not be started.");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                TimeSpan wait = timeout ?? DefaultTimeout;
                if (wait <= TimeSpan.Zero || wait.TotalMilliseconds > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeout), "The mapextractor timeout must be positive and bounded.");
                }
                if (!process.WaitForExit((int)wait.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    process.WaitForExit();
                    lock (outputGate)
                    {
                        stdout = stdoutBuilder.ToString();
                        stderr = stderrBuilder.ToString();
                    }
                    throw new TimeoutException(
                        $"mapextractor WDT enumeration for MapID {mapId} exceeded {wait.TotalSeconds:0.###} seconds and was terminated. " +
                        BuildDiagnostic(stdout, stderr));
                }

                process.WaitForExit();
                lock (outputGate)
                {
                    stdout = stdoutBuilder.ToString();
                    stderr = stderrBuilder.ToString();
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        $"mapextractor WDT enumeration exited with code {process.ExitCode}. {BuildDiagnostic(stdout, stderr)}");
                }
            }

            if (!HashFile(executable).Equals(executableHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("mapextractor.exe changed while WDT enumeration was running.");
            }

            HashSet<(int X, int Y)> adts = ParseCompletedOutput(stdout, stderr,
                dataRoot, product, build, locale, mapId);
            string[] outputs = Directory.EnumerateFiles(fullScratch, "*", SearchOption.AllDirectories).ToArray();
            string expectedTileList = Path.GetFullPath(Path.Combine(fullScratch, "maps",
                mapId.ToString("D4", CultureInfo.InvariantCulture) + ".tilelist"));
            if (outputs.Length != 1 || !string.Equals(Path.GetFullPath(outputs[0]), expectedTileList,
                    StringComparison.OrdinalIgnoreCase) || IsReparsePoint(expectedTileList))
            {
                string files = string.Join(", ", outputs.Select(path => Path.GetRelativePath(fullScratch, path)));
                throw new InvalidDataException(
                    $"WDT listing must produce exactly one native .tilelist and no extracted map files; output=[{files}].");
            }

            byte[] nativeTileList = File.ReadAllBytes(expectedTileList);
            ForeverWdtTileList.Parsed parsed = ForeverWdtTileList.Parse(nativeTileList);
            if (parsed.Build != build || !parsed.Adts.SetEquals(adts))
            {
                throw new InvalidDataException(
                    $"The WDT stdout rows and native .tilelist disagree for MapID {mapId}; " +
                    $"stdout={adts.Count} native={parsed.Adts.Count} Build={parsed.Build}.");
            }

            return new ForeverWdtEnumerationResult(mapId, build, product, locale, fullClientRoot,
                dataRoot, fullScratch, arguments, executableHash, stdout, stderr, nativeTileList, adts);
        }
        finally
        {
            TryDeleteScratch(fullScratch, coverageRoot);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static HashSet<(int X, int Y)> ParseCompletedOutput(string stdout, string stderr,
        string dataRoot, string product, int build, string locale, int mapId)
    {
        string[] lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim()).ToArray();
        if (lines.Any(static line => line.StartsWith("FATAL:", StringComparison.OrdinalIgnoreCase)) ||
            stderr.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("error opening casc", StringComparison.OrdinalIgnoreCase) ||
            lines.Any(static line => line.Contains("No locales detected", StringComparison.OrdinalIgnoreCase) ||
                                     line.Contains("not present in the client's Map table", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("mapextractor reported a fatal CASC or WDT enumeration error. " +
                                           BuildDiagnostic(stdout, stderr));
        }

        string[] storageMarkers = lines.Where(static line => line.StartsWith("Opened casc storage '", StringComparison.Ordinal)).ToArray();
        string[] buildMarkers = lines.Where(static line => line.StartsWith("Detected client build:", StringComparison.Ordinal)).ToArray();
        string[] mapMarkers = lines.Where(static line => line.StartsWith("Restricted extraction to MapID ", StringComparison.Ordinal)).ToArray();
        string expectedBuild = $"Detected client build: {build.ToString(CultureInfo.InvariantCulture)}";
        string expectedMap = $"Restricted extraction to MapID {mapId.ToString(CultureInfo.InvariantCulture)}.";
        if (storageMarkers.Length != 2 || storageMarkers.Any(marker => !StorageMarkerMatches(marker, dataRoot, product, locale)) ||
            buildMarkers.Length != 1 || buildMarkers[0] != expectedBuild ||
            mapMarkers.Length != 1 || mapMarkers[0] != expectedMap)
        {
            throw new InvalidDataException(
                "mapextractor output is missing or contradicts the CASC Data root, Product, Locale, Build or MapID identity. " +
                BuildDiagnostic(stdout, stderr));
        }

        string[] completions = lines.Where(static line => line.StartsWith("wdt-enumeration-complete ", StringComparison.Ordinal)).ToArray();
        Match? completion = completions.Length == 1 ? CompletionMarker.Match(completions[0]) : null;
        if (completion is null || !completion.Success || lines.Length == 0 ||
            !string.Equals(lines[^1], completions[0], StringComparison.Ordinal) ||
            !int.TryParse(completion.Groups["map"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int completeMap) ||
            !int.TryParse(completion.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int completeBuild) ||
            !int.TryParse(completion.Groups["visited"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int visited) ||
            !int.TryParse(completion.Groups["tiles"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int expectedTileCount) ||
            completeMap != mapId || completeBuild != build || visited != ExpectedWdtSlots || expectedTileCount <= 0)
        {
            throw new InvalidDataException(
                $"mapextractor did not emit one final completion marker for all {ExpectedWdtSlots} WDT slots. " +
                BuildDiagnostic(stdout, stderr));
        }

        HashSet<(int X, int Y)> adts = [];
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            if (fields.Length == 0 || !fields[0].Equals("tile", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length != 4 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int listedMap) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
                listedMap != mapId || x is < 0 or >= 64 || y is < 0 or >= 64 || !adts.Add((x, y)))
            {
                throw new InvalidDataException($"mapextractor emitted a duplicate, malformed or mismatched WDT ADT row: {line}");
            }
        }

        if (adts.Count == 0 || adts.Count != expectedTileCount)
        {
            throw new InvalidDataException(
                $"mapextractor completion count {expectedTileCount} does not match {adts.Count} unique legal ADT rows.");
        }
        return adts;
    }

    private static bool StorageMarkerMatches(string marker, string expectedDataRoot,
        string expectedProduct, string expectedLocale)
    {
        const string prefix = "Opened casc storage '";
        int pathEnd = marker.IndexOf("' product ", prefix.Length, StringComparison.Ordinal);
        if (!marker.StartsWith(prefix, StringComparison.Ordinal) || pathEnd < prefix.Length) return false;
        string listedPath = marker[prefix.Length..pathEnd];
        string suffix = marker[(pathEnd + "' product ".Length)..];
        return PathEquals(listedPath, expectedDataRoot) &&
               suffix.Equals($"{expectedProduct} locale {expectedLocale}", StringComparison.Ordinal);
    }

    private static void ValidateToolProvenance(string path, string executableHash)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        string format = ReadRequiredString(root, "format", path);
        string repository = ReadRequiredString(root, "source_repository", path);
        string commit = ReadRequiredString(root, "source_commit", path);
        string patchHash = ReadRequiredString(root, "patch_sha256", path);
        string recordedExecutableHash = ReadRequiredString(root, "mapextractor_sha256", path);
        if (format != ProvenanceFormat || repository != SourceRepository || commit != SourceCommit ||
            !patchHash.Equals(ExpectedPatchSha256, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(patchHash) || !recordedExecutableHash.Equals(executableHash, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(recordedExecutableHash))
        {
            throw new InvalidDataException(
                $"mapextractor provenance must identify {SourceRepository}@{SourceCommit}, the checked-in patch and the current executable hash: {path}");
        }
    }

    private static string ReadRequiredString(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Tool provenance field '{name}' is missing or malformed: {path}");

    private static string BuildDiagnostic(string stdout, string stderr)
    {
        string outTail = Tail(stdout);
        string errTail = Tail(stderr);
        return $"stdout tail: {outTail}; stderr tail: {errTail}";
    }

    private static string Tail(string value)
    {
        const int maxChars = 1200;
        string flattened = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flattened.Length <= maxChars ? flattened : "…" + flattened[^maxChars..];
    }

    private static bool IsSupportedLocale(string locale) => locale is
        "enUS" or "enGB" or "koKR" or "frFR" or "deDE" or "zhCN" or "zhTW" or "esES" or "esMX" or
        "ruRU" or "ptBR" or "itIT";

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDeleteScratch(string scratch, string coverageRoot)
    {
        if (!Directory.Exists(scratch) || !IsSameOrChild(Path.GetFullPath(scratch), Path.GetFullPath(coverageRoot)) ||
            !Path.GetFileName(scratch).StartsWith(".wdt-listing-", StringComparison.Ordinal)) return;
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(scratch, "*", SearchOption.AllDirectories))
            {
                if (IsReparsePoint(path)) return;
            }
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
