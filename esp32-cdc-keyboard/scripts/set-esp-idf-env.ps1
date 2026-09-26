[CmdletBinding()]
param(
    [switch]$Persist
)

$ErrorActionPreference = 'Stop'

# This setup intentionally uses the global Python installation.  Do not set
# IDF_PYTHON_ENV_PATH here: that variable selects ESP-IDF's Python venv.
$script:EspIdfPath = 'C:\Espressif\frameworks\esp-idf-v5.3.1'
$script:EspToolsPath = 'C:\Espressif'
$script:GlobalPythonRoot = 'C:\Users\cloud\AppData\Local\Programs\Python\Python312'

$toolPaths = @(
    "$GlobalPythonRoot",
    "$GlobalPythonRoot\Scripts",
    "$EspToolsPath\tools\idf-exe\1.0.3",
    "$EspToolsPath\tools\xtensa-esp-elf-gdb\14.2_20240403\xtensa-esp-elf-gdb\bin",
    "$EspToolsPath\tools\riscv32-esp-elf-gdb\14.2_20240403\riscv32-esp-elf-gdb\bin",
    "$EspToolsPath\tools\xtensa-esp-elf\esp-13.2.0_20240530\xtensa-esp-elf\bin",
    "$EspToolsPath\tools\esp-clang\16.0.1-fe4f10a809\esp-clang\bin",
    "$EspToolsPath\tools\riscv32-esp-elf\esp-13.2.0_20240530\riscv32-esp-elf\bin",
    "$EspToolsPath\tools\esp32ulp-elf\2.38_20240113\esp32ulp-elf\bin",
    "$EspToolsPath\tools\cmake\3.24.0\bin",
    "$EspToolsPath\tools\openocd-esp32\v0.12.0-esp32-20240318\openocd-esp32\bin",
    "$EspToolsPath\tools\ninja\1.11.1",
    "$EspToolsPath\tools\ccache\4.8\ccache-4.8-windows-x86_64",
    "$EspToolsPath\tools\idf-git\2.44.0\cmd"
)

foreach ($requiredPath in @($EspIdfPath, "$GlobalPythonRoot\python.exe", "$EspToolsPath\tools\idf-exe\1.0.3\idf.py.exe")) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required ESP-IDF path does not exist: $requiredPath"
    }
}

function Join-UniquePath([string[]]$Entries) {
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $Entries) {
        if ([string]::IsNullOrWhiteSpace($entry)) { continue }
        $normalized = $entry.Trim().TrimEnd('\')
        if ($seen.Add($normalized)) { [void]$result.Add($normalized) }
    }
    return ($result -join ';')
}

$currentPathEntries = @($env:Path -split ';')
$newPath = Join-UniquePath (@($toolPaths) + $currentPathEntries)
$userPathEntries = @([Environment]::GetEnvironmentVariable('Path', 'User') -split ';')
$persistentPath = Join-UniquePath (@($toolPaths) + $userPathEntries)

$env:IDF_PATH = $EspIdfPath
$env:IDF_TOOLS_PATH = $EspToolsPath
$env:ESP_IDF_VERSION = '5.3'
$env:IDF_CCACHE_ENABLE = '1'
$env:ESP_ROM_ELF_DIR = "$EspToolsPath\tools\esp-rom-elfs\20240305\"
$env:OPENOCD_SCRIPTS = "$EspToolsPath\tools\openocd-esp32\v0.12.0-esp32-20240318\openocd-esp32\share\openocd\scripts"
$env:PATH = $newPath

# An inherited value would make idf.py select the ESP-IDF venv.  Removing it
# from this process guarantees that the global Python executable is used.
Remove-Item Env:IDF_PYTHON_ENV_PATH -ErrorAction SilentlyContinue

if ($Persist) {
    [Environment]::SetEnvironmentVariable('IDF_PATH', $env:IDF_PATH, 'User')
    [Environment]::SetEnvironmentVariable('IDF_TOOLS_PATH', $env:IDF_TOOLS_PATH, 'User')
    [Environment]::SetEnvironmentVariable('ESP_IDF_VERSION', $env:ESP_IDF_VERSION, 'User')
    [Environment]::SetEnvironmentVariable('IDF_CCACHE_ENABLE', $env:IDF_CCACHE_ENABLE, 'User')
    [Environment]::SetEnvironmentVariable('ESP_ROM_ELF_DIR', $env:ESP_ROM_ELF_DIR, 'User')
    [Environment]::SetEnvironmentVariable('OPENOCD_SCRIPTS', $env:OPENOCD_SCRIPTS, 'User')
    # Persist only the user's existing PATH plus ESP-IDF entries. Do not copy
    # the current process PATH, which may contain temporary IDE/agent paths.
    [Environment]::SetEnvironmentVariable('Path', $persistentPath, 'User')
}

Write-Output "IDF_PATH=$env:IDF_PATH"
Write-Output "Python=$GlobalPythonRoot\python.exe"
Write-Output "IDF_PYTHON_ENV_PATH=<not set; global Python mode>"
if ($Persist) { Write-Output 'User environment variables saved. Open a new terminal to inherit them.' }
