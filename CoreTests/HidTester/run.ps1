param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HidTester.csproj'
$dotnetArgs = @('run', '--project', $project)
if ($NoBuild) { $dotnetArgs += '--no-build' }
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
