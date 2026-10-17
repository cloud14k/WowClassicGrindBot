param(
    [string]$PublishDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Twinkle14k\app'),
    [string]$WinScpPath = '',
    [string]$WinScpSession = '',
    [string]$RemoteDirectory = '',
    [switch]$Apply,
    [switch]$IncludeConfigFiles,
    [switch]$DeleteObsoleteFiles,
    [switch]$CloseWinScpWhenDone
)

$ErrorActionPreference = 'Stop'

function Quote-WinScpValue([string]$Value) {
    if ($Value.Contains("`r") -or $Value.Contains("`n")) {
        throw 'WinSCP 参数不能包含换行符。'
    }

    $quote = [string][char]34
    return $quote + $Value.Replace($quote, $quote + $quote) + $quote
}

function Quote-ProcessArgument([string]$Value) {
    $quote = [string][char]34
    $backslash = [string][char]92
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append($quote)
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append($backslash * (($backslashes * 2) + 1))
            [void]$builder.Append($quote)
        }
        else {
            [void]$builder.Append($backslash * $backslashes)
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    [void]$builder.Append($backslash * ($backslashes * 2))
    [void]$builder.Append($quote)
    return $builder.ToString()
}

function Find-WinScp([string]$ConfiguredPath, [string]$SettingsDirectory) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if (-not [System.IO.Path]::IsPathRooted($ConfiguredPath)) {
            $ConfiguredPath = Join-Path $SettingsDirectory $ConfiguredPath
        }
        $resolved = [System.IO.Path]::GetFullPath($ConfiguredPath)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
        throw "找不到 WinSCP 可执行文件：$resolved"
    }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $PSScriptRoot 'WinSCP.com'),
        (Join-Path $PSScriptRoot 'WinSCP.exe'),
        (Join-Path $repoRoot 'artifacts\Twinkle14k\launch\WinSCP.com'),
        (Join-Path $repoRoot 'artifacts\Twinkle14k\launch\WinSCP.exe')
    )

    foreach ($directory in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            $candidates += Join-Path $directory 'WinSCP.com'
            $candidates += Join-Path $directory 'WinSCP.exe'
        }
    }

    $programDirectories = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    )
    foreach ($directory in $programDirectories) {
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            $candidates += Join-Path $directory 'WinSCP\WinSCP.com'
            $candidates += Join-Path $directory 'WinSCP\WinSCP.exe'
        }
    }

    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $found) {
        throw '找不到 WinSCP.com 或 WinSCP.exe。请把 WinSCP.exe 与 WinSCP.ini 放到 launch 目录，或在 updater.json 中设置 WinScpPath。'
    }
    return (Resolve-Path -LiteralPath $found).Path
}

$exitCode = 0
try {
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'espidf.exe') -PathType Leaf)) {
    throw "发布目录中没有 espidf.exe：$publishRoot"
}

$settingsCandidates = @(
    (Join-Path $PSScriptRoot 'updater.json'),
    (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\Twinkle14k\launch\updater.json'),
    (Join-Path $PSScriptRoot 'updater.example.json')
)
$settingsPath = $settingsCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ($null -eq $settingsPath) {
    throw '找不到 updater.json 或 updater.example.json。'
}
$settingsDirectory = Split-Path -Parent $settingsPath
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($WinScpPath)) {
    $WinScpPath = [string]$settings.WinScpPath
    if ([string]::IsNullOrWhiteSpace($WinScpPath)) {
        $WinScpPath = [string]$settings.WinScpComPath
    }
}
if ([string]::IsNullOrWhiteSpace($WinScpSession)) {
    $WinScpSession = [string]$settings.WinScpSession
}
if ([string]::IsNullOrWhiteSpace($RemoteDirectory)) {
    $RemoteDirectory = [string]$settings.RemoteApplicationDirectory
}
if ([string]::IsNullOrWhiteSpace($WinScpSession) -or [string]::IsNullOrWhiteSpace($RemoteDirectory)) {
    throw "配置不完整，请检查 $settingsPath 中的 WinScpSession 和 RemoteApplicationDirectory。"
}

$winScpExecutable = Find-WinScp $WinScpPath $settingsDirectory
$logPath = Join-Path ([System.IO.Path]::GetTempPath()) ("twinkle14k-upload-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
$excludedFiles = @('*.pdb', '*.log', 'training-data/', 'logs/')
if (-not $IncludeConfigFiles) {
    $excludedFiles += @('appsettings.json', 'addon_config.json', 'frame_config.json', 'data_config.json', 'decision-settings.json')
}
$fileMask = '|' + ($excludedFiles -join '; ')
$syncOptions = @('synchronize remote')
if (-not $Apply) {
    $syncOptions += '-preview'
}
if ($DeleteObsoleteFiles) {
    $syncOptions += '-delete'
}
$syncOptions += @(
    '-criteria=time,size',
    '-transfer=binary',
    ('-filemask=' + (Quote-WinScpValue $fileMask)),
    (Quote-WinScpValue $publishRoot),
    (Quote-WinScpValue $RemoteDirectory)
)

$scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("twinkle14k-upload-{0}.txt" -f [guid]::NewGuid().ToString('N'))
$commands = @(
    'option batch abort',
    'option confirm off',
    ('open ' + (Quote-WinScpValue $WinScpSession)),
    ($syncOptions -join ' ')
)
if ($CloseWinScpWhenDone) {
    $commands += 'exit'
}
Set-Content -LiteralPath $scriptPath -Value $commands -Encoding UTF8

try {
    Write-Host "本地程序目录：$publishRoot"
    Write-Host "FTP 程序目录：$RemoteDirectory"
    if ($Apply) {
        Write-Host '开始增量上传……'
    }
    else {
        Write-Host '预览模式：没有文件会被上传。确认清单后加 -Apply 正式上传。'
    }

    $isConsoleExecutable = [System.IO.Path]::GetExtension($winScpExecutable) -ieq '.com'
    $arguments = @()
    if (-not $isConsoleExecutable) {
        $arguments += '/console'
    }
    $arguments += "/script=$scriptPath"
    $arguments += "/log=$logPath"

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $winScpExecutable
    $startInfo.WorkingDirectory = Split-Path -Parent $winScpExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $isConsoleExecutable
    $startInfo.RedirectStandardError = $isConsoleExecutable
    $startInfo.Arguments = (($arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' ')

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw '无法启动 WinSCP。'
        }
        $outputTask = $null
        $errorTask = $null
        if ($isConsoleExecutable) {
            $outputTask = $process.StandardOutput.ReadToEndAsync()
            $errorTask = $process.StandardError.ReadToEndAsync()
        }
        $process.WaitForExit()
        $details = @()
        if ($isConsoleExecutable) {
            $outputText = $outputTask.GetAwaiter().GetResult()
            $errorText = $errorTask.GetAwaiter().GetResult()
            if (-not [string]::IsNullOrWhiteSpace($outputText)) { $details += $outputText.Trim() }
            if (-not [string]::IsNullOrWhiteSpace($errorText)) { $details += $errorText.Trim() }
        }
        if ($process.ExitCode -ne 0) {
            $detailText = $details -join [Environment]::NewLine
            throw "WinSCP 上传失败，退出码 $($process.ExitCode)。$detailText`n日志：$logPath"
        }
        foreach ($line in $details) { Write-Host $line }
    }
    finally {
        $process.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
}

Write-Host "WinSCP 日志：$logPath"
if ($DeleteObsoleteFiles) {
    Write-Warning '启用了 -DeleteObsoleteFiles：FTP 目录中本地不存在的程序文件将被删除（已排除训练数据、日志和 PDB）。'
}
}
catch {
    $exitCode = 1
    Write-Host "上传工具执行失败：$($_.Exception.Message)" -ForegroundColor Red
}

if ($exitCode -ne 0) {
    exit $exitCode
}
