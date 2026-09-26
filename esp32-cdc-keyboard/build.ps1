[CmdletBinding()]
param(
    [ValidateSet('build', 'clean', 'fullclean', 'reconfigure', 'menuconfig', 'flash', 'monitor', 'erase-flash')]
    [string]$Action = 'build',
    [string]$Port,
    [switch]$Monitor
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\scripts\set-esp-idf-env.ps1"

$idf = "$env:IDF_TOOLS_PATH\tools\idf-exe\1.0.3\idf.py.exe"

function Invoke-IdfAction([string[]]$Arguments) {
    & $idf @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "idf.py $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Push-Location $PSScriptRoot
try {
    if ($Monitor) {
        if (-not $Port) {
            throw '-Monitor requires -Port, for example: .\build.ps1 -Port COM21 -Monitor'
        }
        if ($Action -ne 'build') {
            throw '-Monitor must be used with the default build action.'
        }

        # One command for the complete workflow: build, flash, then monitor.
        Invoke-IdfAction @('build')
        Invoke-IdfAction @('flash', '-p', $Port)
        Invoke-IdfAction @('monitor', '-p', $Port)
    }
    else {
        $idfArgs = @($Action)
        if ($Port -and $Action -in @('flash', 'monitor', 'erase-flash')) {
            $idfArgs += @('-p', $Port)
        }
        Invoke-IdfAction $idfArgs
    }
    exit 0
}
finally {
    Pop-Location
}
