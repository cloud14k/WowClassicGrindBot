using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Twinkle14k.Launch;

internal static partial class Program
{
    private const string AppDirectoryName = "app";
    private const string TransactionDirectoryName = "update-transaction";
    private const string StateDirectoryName = ".launch-state";
    private const string SettingsFileName = "updater.json";
    private const string ExampleSettingsFileName = "updater.example.json";
    private const string InstalledManifestName = "installed-manifest.json";
    private const string ApplicationProcessName = "espidf";
    private const string DefaultApplicationPath = "app/espidf.exe";
    private const string DefaultBrowserUrl = "http://localhost:5000";
    private const int MaximumManifestFiles = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        string? launcherLogPath = null;
        try
        {
            string? installRootOverride = ReadOption(args, "--install-root");
            bool noStart = args.Contains("--no-start", StringComparer.OrdinalIgnoreCase);
            if (args.Contains("--install-root", StringComparer.OrdinalIgnoreCase) && installRootOverride is null)
            {
                throw new ArgumentException("--install-root 后面需要提供安装目录。");
            }
            string launcherDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            LauncherSettings settings = LoadSettings(launcherDirectory);
            string installRoot = ResolveInstallRoot(settings, installRootOverride, launcherDirectory);
            string appDirectory = Path.Combine(installRoot, AppDirectoryName);
            string stateDirectory = Path.Combine(installRoot, StateDirectoryName);
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(stateDirectory);
            string launcherLogDirectory = Path.Combine(installRoot, "logs", "launcher");
            Directory.CreateDirectory(launcherLogDirectory);
            launcherLogPath = Path.Combine(launcherLogDirectory, $"launcher-{DateTime.Now:yyyyMMdd-HHmmssfff}.log");
            WriteLauncherLog(launcherLogPath, $"安装目录：{installRoot}");
            Console.WriteLine($"启动器日志：{launcherLogPath}");

            RecoverInterruptedUpdate(stateDirectory, appDirectory);

            string executablePath = ResolveApplicationPath(installRoot, settings);
            if (IsApplicationRunning(executablePath))
            {
                Console.WriteLine("espidf.exe 仍在运行。请先退出程序，再重新启动 Launch 更新。");
                return 2;
            }

            Console.WriteLine($"安装目录：{installRoot}");
            bool updated = await UpdateApplicationAsync(installRoot, appDirectory, stateDirectory, settings, launcherLogPath);

            if (noStart)
            {
                Console.WriteLine(updated ? "更新完成。" : "程序已是最新版本。");
                return 0;
            }

            executablePath = ResolveApplicationPath(installRoot, settings);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException($"找不到主程序：{executablePath}");
            }

            using Process applicationProcess = StartApplication(executablePath, settings.ApplicationArguments);
            if (!string.IsNullOrWhiteSpace(settings.BrowserUrl))
            {
                await OpenBrowserWhenReadyAsync(settings.BrowserUrl, applicationProcess, launcherLogPath);
            }
            else
            {
                await CheckApplicationStartupAsync(applicationProcess, launcherLogPath);
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (launcherLogPath is not null)
            {
                WriteLauncherLog(launcherLogPath, $"失败：{exception}");
            }
            Console.Error.WriteLine($"启动或更新失败：{exception.Message}");
            Console.Error.WriteLine("按 Enter 关闭窗口。");
            Console.ReadLine();
            return 1;
        }
    }

    private static LauncherSettings LoadSettings(string launcherDirectory)
    {
        string settingsPath = Path.Combine(launcherDirectory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            string examplePath = Path.Combine(launcherDirectory, ExampleSettingsFileName);
            if (!File.Exists(examplePath))
            {
                throw new FileNotFoundException($"找不到配置模板：{examplePath}");
            }

            File.Copy(examplePath, settingsPath);
            throw new InvalidOperationException(
                $"已创建配置文件 {settingsPath}。请填写 WinSCP 会话和 FTP 发布目录后重新启动。");
        }

        LauncherSettings? settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(settingsPath), JsonOptions);
        if (settings is null)
        {
            throw new InvalidDataException($"启动器配置无效：{settingsPath}");
        }

        if (string.IsNullOrWhiteSpace(settings.WinScpSession))
        {
            throw new InvalidDataException("updater.json 的 WinScpSession 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(settings.RemoteApplicationDirectory))
        {
            throw new InvalidDataException("updater.json 的 RemoteApplicationDirectory 不能为空。");
        }

        if (settings.PreservedPaths is null || settings.ApplicationArguments is null)
        {
            throw new InvalidDataException("updater.json 的 PreservedPaths 和 ApplicationArguments 不能为 null。");
        }

        return settings;
    }

    private static string ResolveInstallRoot(LauncherSettings settings, string? overridePath, string launcherDirectory)
    {
        string? configuredPath = string.IsNullOrWhiteSpace(overridePath) ? settings.InstallRoot : overridePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath, launcherDirectory);
        }

        DirectoryInfo? launcherDir = Directory.GetParent(launcherDirectory);
        if (launcherDir is null)
        {
            throw new InvalidOperationException("无法根据 launch 目录推断安装目录，请使用 --install-root 指定。");
        }

        return launcherDir.FullName;
    }

    private static string ResolveApplicationPath(string installRoot, LauncherSettings settings)
    {
        string relativePath = string.IsNullOrWhiteSpace(settings.ApplicationRelativePath)
            ? DefaultApplicationPath
            : settings.ApplicationRelativePath;
        ValidateRelativePath(relativePath);
        return Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), installRoot);
    }

    private static async Task<bool> UpdateApplicationAsync(
        string installRoot,
        string appDirectory,
        string stateDirectory,
        LauncherSettings settings,
        string launcherLogPath)
    {
        string workspace = Path.Combine(stateDirectory, "download");
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }

        string[] preservedPaths = settings.PreservedPaths
            .Append("input-backend.json")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string stageDirectory = Path.Combine(workspace, "files");
        Directory.CreateDirectory(workspace);
        string[] existingPreservedPaths = GetExistingPreservedPaths(appDirectory, preservedPaths);
        if (Directory.Exists(appDirectory))
        {
            CopyDirectoryForStaging(appDirectory, stageDirectory, appDirectory, preservedPaths);
        }

        Directory.CreateDirectory(stageDirectory);
        string launcherLogDirectory = Path.Combine(installRoot, "logs", "launcher");
        Directory.CreateDirectory(launcherLogDirectory);
        string logName = $"winscp-{DateTime.Now:yyyyMMdd-HHmmssfff}.log";
        await RunWinScpSyncAsync(
            settings,
            stageDirectory,
            existingPreservedPaths.Concat(preservedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Path.Combine(launcherLogDirectory, logName));
        WriteLauncherLog(launcherLogPath, "WinSCP 同步成功，开始核对暂存文件与 app 目录。");

        List<UpdateOperation> operations = [];
        HashSet<string> stagedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string stagedPath in Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(stageDirectory, stagedPath).Replace('\\', '/'));
            stagedPaths.Add(relativePath);
            if (File.Exists(Path.Combine(appDirectory, relativePath))
                && IsProtectedPath(relativePath, preservedPaths))
            {
                continue;
            }

            string currentPath = Path.Combine(appDirectory, relativePath);
            if (!File.Exists(currentPath) || !await FilesMatchAsync(currentPath, stagedPath))
            {
                operations.Add(new UpdateOperation(relativePath, "replace", File.Exists(currentPath)));
            }
        }

        if (Directory.Exists(appDirectory))
        {
            foreach (string currentPath in Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(appDirectory, currentPath).Replace('\\', '/'));
                if (!stagedPaths.Contains(relativePath) && !IsProtectedPath(relativePath, preservedPaths))
                {
                    operations.Add(new UpdateOperation(relativePath, "delete", HadOriginal: true));
                }
            }
        }

        if (operations.Count == 0)
        {
            WriteLauncherLog(launcherLogPath, "核对完成：没有需要替换或删除的文件。");
            Console.WriteLine("程序已是最新版本。");
            return false;
        }

        string transactionDirectory = Path.Combine(stateDirectory, TransactionDirectoryName);
        string transactionJournalPath = Path.Combine(transactionDirectory, "transaction.json");
        if (Directory.Exists(transactionDirectory))
        {
            throw new InvalidOperationException("發現未完成的更新記錄；請重新啟動 Launch 以進行恢復。");
        }

        Directory.CreateDirectory(Path.Combine(transactionDirectory, "backup"));
        UpdateTransaction transaction = new(false, operations);
        string temporaryJournalPath = transactionJournalPath + ".tmp";
        await File.WriteAllTextAsync(temporaryJournalPath, JsonSerializer.Serialize(transaction, JsonOptions));
        File.Move(temporaryJournalPath, transactionJournalPath);
        Directory.CreateDirectory(appDirectory);
        try
        {
            foreach (UpdateOperation operation in operations)
            {
                string relativePath = NormalizeRelativePath(operation.Path);
                string targetPath = Path.Combine(appDirectory, relativePath);
                string backupPath = Path.Combine(transactionDirectory, "backup", relativePath);
                if (File.Exists(targetPath) != operation.HadOriginal)
                {
                    throw new IOException($"更新期间目标文件状态发生变化：{relativePath}");
                }

                if (operation.HadOriginal)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Move(targetPath, backupPath);
                }

                if (operation.Kind == "replace")
                {
                    string stagedPath = Path.Combine(stageDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(stagedPath, targetPath);
                }
            }

            File.Delete(transactionJournalPath);
            Directory.Delete(transactionDirectory, recursive: true);
        }
        catch
        {
            RecoverInterruptedUpdate(stateDirectory, appDirectory);
            throw;
        }

        Console.WriteLine($"更新完成，共同步 {operations.Count} 个文件。");
        WriteLauncherLog(launcherLogPath, $"更新应用目录完成，共应用 {operations.Count} 个文件操作。");
        return true;
    }

    private static void CopyDirectoryForStaging(
        string sourceDirectory,
        string destinationDirectory,
        string appRoot,
        IReadOnlyCollection<string> preservedPaths)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(appRoot, sourcePath).Replace('\\', '/'));
            if (IsProtectedPath(relativePath, preservedPaths))
            {
                continue;
            }

            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        }
    }

    private static string[] GetExistingPreservedPaths(string appDirectory, IReadOnlyCollection<string> preservedPaths)
    {
        List<string> result = [];
        if (!Directory.Exists(appDirectory))
        {
            return result.ToArray();
        }

        foreach (string path in preservedPaths)
        {
            string relativePath = path.Replace('\\', '/');
            string localPath = Path.Combine(appDirectory, relativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (relativePath.EndsWith('/') ? Directory.Exists(localPath) : File.Exists(localPath))
            {
                result.Add(relativePath);
            }
        }

        if (Directory.EnumerateFiles(appDirectory, "*.log", SearchOption.AllDirectories).Any())
        {
            result.Add("*.log");
        }

        return result.ToArray();
    }

    private static async Task<bool> FilesMatchAsync(string firstPath, string secondPath)
    {
        FileInfo firstInfo = new(firstPath);
        FileInfo secondInfo = new(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        await using FileStream first = File.OpenRead(firstPath);
        await using FileStream second = File.OpenRead(secondPath);
        byte[] firstHash = await SHA256.HashDataAsync(first);
        byte[] secondHash = await SHA256.HashDataAsync(second);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static void RecoverInterruptedUpdate(string stateDirectory, string appDirectory)
    {
        string transactionDirectory = Path.Combine(stateDirectory, TransactionDirectoryName);
        if (!Directory.Exists(transactionDirectory))
        {
            return;
        }

        string journalPath = Path.Combine(transactionDirectory, "transaction.json");
        if (!File.Exists(journalPath))
        {
            Directory.Delete(transactionDirectory, recursive: true);
            return;
        }

        UpdateTransaction transaction = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(journalPath), JsonOptions)
                                        ?? throw new InvalidDataException("更新恢复记录无效。");
        if (transaction.Operations is null)
        {
            throw new InvalidDataException("更新恢复记录没有操作列表。");
        }
        string backupDirectory = Path.Combine(transactionDirectory, "backup");
        foreach (UpdateOperation operation in transaction.Operations.AsEnumerable().Reverse())
        {
            if (operation.Kind is not ("replace" or "delete") || (operation.Kind == "delete" && !operation.HadOriginal))
            {
                throw new InvalidDataException($"更新恢复记录中的操作无效：{operation.Kind}");
            }

            string relativePath = NormalizeRelativePath(operation.Path);
            string targetPath = Path.Combine(appDirectory, relativePath);
            string backupPath = Path.Combine(backupDirectory, relativePath);
            if (operation.HadOriginal)
            {
                if (File.Exists(backupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(backupPath, targetPath, overwrite: true);
                }
            }
            else if (operation.Kind == "replace" && File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }

        string installedManifestPath = Path.Combine(stateDirectory, InstalledManifestName);
        string previousManifestPath = Path.Combine(transactionDirectory, "installed-manifest.previous.json");
        if (transaction.HadPreviousManifest && File.Exists(previousManifestPath))
        {
            File.Copy(previousManifestPath, installedManifestPath, overwrite: true);
        }
        else if (!transaction.HadPreviousManifest && File.Exists(installedManifestPath))
        {
            File.Delete(installedManifestPath);
        }

        Directory.Delete(transactionDirectory, recursive: true);
    }

    private static async Task RunWinScpSyncAsync(
        LauncherSettings settings,
        string localDirectory,
        IReadOnlyCollection<string> preservedPaths,
        string logPath)
    {
        string winScpPath = ResolveWinScpPath(settings.WinScpPath, settings.WinScpComPath);
        bool isConsoleExecutable = string.Equals(Path.GetExtension(winScpPath), ".com", StringComparison.OrdinalIgnoreCase);
        string scriptPath = Path.Combine(Path.GetTempPath(), $"twinkle14k-{Guid.NewGuid():N}.winscp.txt");
        string fileMask = BuildWinScpExcludeMask(preservedPaths);
        List<string> commands =
        [
            "option batch abort",
            "option confirm off",
            // Saved WinSCP sites can contain a local directory from another
            // Windows account. Override it before opening the session so an
            // obsolete path cannot abort login before synchronize runs.
            $"open {QuoteWinScp(settings.WinScpSession)} -rawsettings LocalDirectory={QuoteWinScp(localDirectory)}",
            $"synchronize local -delete -criteria=time,size -transfer=binary -filemask={QuoteWinScp(fileMask)} {QuoteWinScp(localDirectory)} {QuoteWinScp(settings.RemoteApplicationDirectory)}"
        ];
        commands.Add("exit");
        await File.WriteAllTextAsync(scriptPath, string.Join(Environment.NewLine, commands), new UTF8Encoding(true));

        try
        {
            ProcessStartInfo startInfo = new(winScpPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(winScpPath)!,
                RedirectStandardOutput = isConsoleExecutable,
                RedirectStandardError = isConsoleExecutable
            };
            if (!isConsoleExecutable)
            {
                startInfo.ArgumentList.Add("/console");
            }
            startInfo.ArgumentList.Add($"/script={scriptPath}");
            startInfo.ArgumentList.Add($"/log={logPath}");
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 WinSCP.com。");
            }

            Task<string> outputTask = isConsoleExecutable ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            Task<string> errorTask = isConsoleExecutable ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                string details = string.Join(Environment.NewLine, new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
                string logTail = ReadLogTail(logPath);
                throw new InvalidOperationException(
                    $"WinSCP 同步失败（退出码 {process.ExitCode}）。{Environment.NewLine}{details}{Environment.NewLine}日志末尾：{Environment.NewLine}{logTail}{Environment.NewLine}完整日志：{logPath}");
            }
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    private static string ReadLogTail(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return "WinSCP 没有创建日志文件。";
        }

        try
        {
            return string.Join(Environment.NewLine, File.ReadLines(logPath).TakeLast(24));
        }
        catch (IOException exception)
        {
            return $"无法读取日志：{exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"无法读取日志：{exception.Message}";
        }
    }

    private static void WriteLauncherLog(string logPath, string message)
    {
        File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
    }

    private static string BuildWinScpExcludeMask(IReadOnlyCollection<string> preservedPaths)
    {
        List<string> masks = [];
        foreach (string path in preservedPaths)
        {
            string normalized = path.Replace('\\', '/').TrimStart('/');
            bool isDirectory = normalized.EndsWith('/');
            normalized = normalized.TrimEnd('/');
            if (normalized.Length > 0)
            {
                masks.Add(normalized + (isDirectory ? "/" : ""));
            }
        }

        string exclusions = string.Join("; ", masks);
        return exclusions.Length == 0 ? "|" : "|" + exclusions;
    }

    private static string ResolveWinScpPath(string configuredPath, string legacyConfiguredComPath)
    {
        configuredPath = string.IsNullOrWhiteSpace(configuredPath) ? legacyConfiguredComPath : configuredPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            throw new FileNotFoundException($"找不到 WinSCP.com：{fullPath}");
        }

        string[] executableNames = ["WinSCP.com", "WinSCP.exe"];
        string? launcherDirectoryPath = executableNames
            .Select(name => Path.Combine(AppContext.BaseDirectory, name))
            .FirstOrDefault(File.Exists);
        if (launcherDirectoryPath is not null)
        {
            return launcherDirectoryPath;
        }

        string[] pathEntries = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .SelectMany(path => executableNames.Select(name => Path.Combine(path.Trim(), name)))
            .ToArray() ?? [];
        string? pathEntry = pathEntries.FirstOrDefault(File.Exists);
        if (pathEntry is not null)
        {
            return pathEntry;
        }

        string[] commonPaths = [.. executableNames.SelectMany(name => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinSCP", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinSCP", name)
        })];
        return commonPaths.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("找不到 WinSCP.com 或 WinSCP.exe。请安装 WinSCP，或在 updater.json 的 WinScpPath 中填写可执行文件路径。");
    }

    private static string QuoteWinScp(string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidDataException("WinSCP 路径或会话名不能包含换行符。");
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static async Task<T> DeserializeFileAsync<T>(string path)
    {
        string json = await File.ReadAllTextAsync(path);
        T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return value ?? throw new InvalidDataException($"JSON 文件内容为空：{path}");
    }

    private static void ValidateManifest(ReleaseManifest manifest, string expectedVersion)
    {
        if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("版本清单中的版本号和 latest.json 不一致。");
        }

        if (manifest.Files is null)
        {
            throw new InvalidDataException("版本清单缺少 files 列表。");
        }

        if (manifest.Files.Count > MaximumManifestFiles)
        {
            throw new InvalidDataException($"版本清单文件数量超过限制：{manifest.Files.Count}");
        }

        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReleaseFile file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("版本清单包含空文件条目。");
            }

            string normalizedPath = NormalizeRelativePath(file.Path);
            if (!seenPaths.Add(normalizedPath))
            {
                throw new InvalidDataException($"版本清单中有重复路径：{normalizedPath}");
            }

            if (file.Size < 0 || string.IsNullOrWhiteSpace(file.Sha256) || !Sha256Pattern().IsMatch(file.Sha256))
            {
                throw new InvalidDataException($"版本清单中的文件信息无效：{normalizedPath}");
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        ValidateRelativePath(path);
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':')
            || path.Contains('\\')
            || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new InvalidDataException($"不安全的相对文件路径：{path}");
        }
    }

    private static bool IsProtectedPath(string relativePath, IReadOnlyCollection<string> preservedPaths)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        if (string.Equals(Path.GetExtension(normalizedPath), ".log", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string preservedPath in preservedPaths)
        {
            string normalizedPreservedPath = preservedPath.Replace('\\', '/');
            if (normalizedPreservedPath.EndsWith('/'))
            {
                string directoryPath = normalizedPreservedPath.TrimEnd('/');
                if (string.Equals(normalizedPath, directoryPath, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(directoryPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(normalizedPath, normalizedPreservedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> MatchesHashAsync(string path, string expectedHash, long expectedSize)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        FileInfo fileInfo = new(path);
        if (fileInfo.Length != expectedSize)
        {
            return false;
        }

        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicationRunning(string executablePath)
    {
        string normalizedPath = Path.GetFullPath(executablePath);
        foreach (Process process in Process.GetProcessesByName(ApplicationProcessName))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process can exit while its image path is being inspected.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Ignore unrelated processes whose image path cannot be queried.
                }
            }
        }

        return false;
    }

    private static Process StartApplication(string executablePath, IReadOnlyCollection<string> arguments)
    {
        ProcessStartInfo startInfo = new(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("Windows 未能创建 espidf.exe 进程。");
        Console.WriteLine($"已创建 espidf.exe 进程（PID {process.Id}），正在确认服务启动状态。");
        return process;
    }

    private static async Task OpenBrowserWhenReadyAsync(string browserUrl, Process applicationProcess, string launcherLogPath)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        Uri uri = new(browserUrl);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            CheckApplicationStartup(applicationProcess, launcherLogPath);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(uri);
                Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true });
                WriteLauncherLog(launcherLogPath, $"服务已响应 {browserUrl}，已打开浏览器。");
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(500, (deadline - DateTime.UtcNow).TotalMilliseconds))));
            }
            catch (TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(500, (deadline - DateTime.UtcNow).TotalMilliseconds))));
            }
        }

        CheckApplicationStartup(applicationProcess, launcherLogPath);
        string message = $"espidf.exe 仍在运行，但网页在 10 秒内没有响应：{browserUrl}";
        WriteLauncherLog(launcherLogPath, message);
        Console.Error.WriteLine(message);
        Console.WriteLine("按 Enter 关闭窗口。");
        Console.ReadLine();
    }

    private static async Task CheckApplicationStartupAsync(Process applicationProcess, string launcherLogPath)
    {
        await Task.Delay(500);
        CheckApplicationStartup(applicationProcess, launcherLogPath);
    }

    private static void CheckApplicationStartup(Process applicationProcess, string launcherLogPath)
    {
        if (applicationProcess.HasExited)
        {
            string message = $"espidf.exe 启动后退出，退出代码：{applicationProcess.ExitCode}。";
            WriteLauncherLog(launcherLogPath, message);
            throw new InvalidOperationException(message);
        }
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();
}

internal sealed class LauncherSettings
{
    public string InstallRoot { get; init; } = "";
    public string WinScpPath { get; init; } = "";
    public string WinScpComPath { get; init; } = "";
    public string WinScpSession { get; init; } = "";
    public string RemoteApplicationDirectory { get; init; } = "";
    public string ApplicationRelativePath { get; init; } = ProgramDefaults.ApplicationRelativePath;
    public string[] ApplicationArguments { get; init; } = [];
    public string BrowserUrl { get; init; } = ProgramDefaults.BrowserUrl;
    public string[] PreservedPaths { get; init; } = ProgramDefaults.PreservedPaths;
}

internal sealed class LatestRelease
{
    public string Version { get; init; } = "";
}

internal sealed class ReleaseManifest
{
    public string Version { get; init; } = "";
    public List<ReleaseFile> Files { get; init; } = [];
}

internal sealed class ReleaseFile
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

internal sealed record UpdateTransaction(bool HadPreviousManifest, List<UpdateOperation> Operations);

internal sealed record UpdateOperation(string Path, string Kind, bool HadOriginal);

internal static class ProgramDefaults
{
    public const string ApplicationRelativePath = "app/espidf.exe";
    public const string BrowserUrl = "http://localhost:5000";
    public static readonly string[] PreservedPaths =
    [
        "appsettings.json",
        "addon_config.json",
        "frame_config.json",
        "data_config.json",
        "decision-settings.json",
        "training-data/",
        "logs/"
    ];
}
