param(
    [Parameter(Mandatory=$true)][string]$ClientRoot,
    [Parameter(Mandatory=$true)][ValidateRange(0,2147483647)][int]$MapId,
    [Parameter(Mandatory=$true)][ValidateRange(0,63)][int]$AdtX,
    [Parameter(Mandatory=$true)][ValidateRange(0,63)][int]$AdtY,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ToolDirectory,
    [switch]$ExportMapMetadata,
    [ValidateSet('zhCN','enUS','zhTW','koKR','frFR','deDE','esES','esMX','ruRU','ptBR','itIT','ptPT')][string]$Locale='zhCN'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$tempRoot = Join-Path $repoRoot 'tmp'
if (-not $ToolDirectory) { $ToolDirectory = Join-Path $tempRoot 'p45-trinitycore-build\bin\Release' }
$ClientRoot = (Resolve-Path $ClientRoot).Path
$buildInfoPath = Join-Path $ClientRoot '.build.info'
if (-not (Test-Path $buildInfoPath)) { throw "Client identity file is missing: $buildInfoPath" }

$rows = @(Get-Content $buildInfoPath)
if ($rows.Count -lt 2) { throw 'Client .build.info has no active product row.' }
$headers = @($rows[0].Split('|') | ForEach-Object { ($_ -split '!')[0] })
$indices = @{}
for ($i=0; $i -lt $headers.Count; $i++) { $indices[$headers[$i]] = $i }
foreach ($required in 'Active','Product','Version') {
    if (-not $indices.ContainsKey($required)) { throw "Client .build.info is missing the $required column." }
}
$activeRows = @($rows | Select-Object -Skip 1 | Where-Object { $_ } | ForEach-Object {
    $fields = $_.Split('|')
    if ($fields.Count -gt $indices['Version'] -and $fields[$indices['Active']] -eq '1') {
        [pscustomobject]@{
            Product = $fields[$indices['Product']]
            Version = $fields[$indices['Version']]
        }
    }
} | Where-Object { $_.Product -eq 'wow_cn_beta' })
if ($activeRows.Count -ne 1) { throw "Expected exactly one active wow_cn_beta product; found $($activeRows.Count)." }
$clientProduct = $activeRows[0].Product
$clientVersion = $activeRows[0].Version
$versionParts = $clientVersion.Split('.')
$clientBuild = 0
if ($versionParts.Count -lt 2 -or -not [int]::TryParse($versionParts[-1], [ref]$clientBuild) -or $clientBuild -le 0) {
    throw "Could not parse the build number from client version '$clientVersion'."
}

foreach ($exe in 'mapextractor.exe','vmap4extractor.exe','vmap4assembler.exe','mmaps_generator.exe') {
    if (-not (Test-Path (Join-Path $ToolDirectory $exe))) { throw "Missing $exe under '$ToolDirectory'; run rebuild-casc-tools.ps1 first." }
}
$ToolDirectory = (Resolve-Path $ToolDirectory).Path
if (Test-Path $OutputDirectory) {
    if (@(Get-ChildItem $OutputDirectory -Force).Count -gt 0) {
        throw "Output directory must be new or empty: $OutputDirectory"
    }
} else {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$logDirectory = Join-Path $OutputDirectory 'logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$mapExe = Join-Path $ToolDirectory 'mapextractor.exe'
$vmapExe = Join-Path $ToolDirectory 'vmap4extractor.exe'
$assemblerExe = Join-Path $ToolDirectory 'vmap4assembler.exe'
$mmapExe = Join-Path $ToolDirectory 'mmaps_generator.exe'

# Confirm the requested tile exists in this MapID's WDT before writing map data.
$tileListLog = Join-Path $logDirectory 'list-tiles.log'
$previousMetadataOutput = $env:TC_WORLD_MAP_AREA_EXPORT
try {
    if ($ExportMapMetadata) { $env:TC_WORLD_MAP_AREA_EXPORT = Join-Path $OutputDirectory 'WorldMapArea.json' }
    else { Remove-Item Env:TC_WORLD_MAP_AREA_EXPORT -ErrorAction SilentlyContinue }
    & $mapExe -i $ClientRoot -o $OutputDirectory -p $clientProduct -l $Locale -m $MapId --list-tiles *> $tileListLog
    if ($LASTEXITCODE -ne 0) { throw "WDT tile listing failed; see $tileListLog" }
    if ($ExportMapMetadata -and -not (Test-Path (Join-Path $OutputDirectory 'WorldMapArea.json'))) {
        throw 'The tool did not export the requested client UiMap/UiMapAssignment metadata; rebuild the current patch.'
    }
} finally {
    if ($null -eq $previousMetadataOutput) { Remove-Item Env:TC_WORLD_MAP_AREA_EXPORT -ErrorAction SilentlyContinue }
    else { $env:TC_WORLD_MAP_AREA_EXPORT = $previousMetadataOutput }
}
$tilePattern = "^tile\s+$MapId\s+$AdtX\s+$AdtY\s*$"
if (-not (Select-String -Path $tileListLog -Pattern $tilePattern -Quiet)) {
    throw "MapID $MapId ADT ($AdtX,$AdtY) is not listed in the client's WDT."
}

$mapLog = Join-Path $logDirectory 'map-extract.log'
& $mapExe -i $ClientRoot -o $OutputDirectory -p $clientProduct -l $Locale -e 3 -f 0 -m $MapId -t "$AdtX,$AdtY" *> $mapLog
if ($LASTEXITCODE -ne 0) { throw "Map/DBC extraction failed; see $mapLog" }
$mapFiles = @(Get-ChildItem (Join-Path $OutputDirectory 'maps') -File -Filter '*.map')
if ($mapFiles.Count -ne 1 -or $mapFiles[0].Name -ne ('{0:D4}_{1:D2}_{2:D2}.map' -f $MapId,$AdtY,$AdtX)) {
    throw "MapID/tile filter produced an unexpected .map file set; see $mapLog"
}
$tileListPath = Join-Path $OutputDirectory ('maps\{0:D4}.tilelist' -f $MapId)
$tileListBytes = [IO.File]::ReadAllBytes($tileListPath)
if ($tileListBytes.Length -ne 4108) { throw "Unexpected tilelist size for the selected map: $tileListPath" }
$tileListBuild = [BitConverter]::ToUInt32($tileListBytes, 8)
$tileBits = [Text.Encoding]::ASCII.GetString($tileListBytes, 12, $tileListBytes.Length - 12)
if ($tileListBuild -ne $clientBuild -or $tileBits.ToCharArray().Where({ $_ -eq '1' }).Count -ne 1) {
    throw 'The map tilelist is not restricted to exactly one ADT or carries a different Build.'
}

$vmapLog = Join-Path $logDirectory 'vmap-extract.log'
Push-Location $OutputDirectory
try {
    & $vmapExe -d $ClientRoot -p $clientProduct -dl $Locale -m $MapId -t "$AdtX,$AdtY" --threads 1 *> $vmapLog
    if ($LASTEXITCODE -ne 0) { throw "Restricted VMAP extraction failed; see $vmapLog" }
    if (-not (Select-String -Path $vmapLog -Pattern 'skipping global GameObjectDisplayInfo model sweep' -Quiet)) {
        throw "Restricted VMAP run did not confirm the global model sweep was skipped; see $vmapLog"
    }

    $assemblerLog = Join-Path $logDirectory 'vmap-assemble.log'
    & $assemblerExe Buildings vmaps *> $assemblerLog
    if ($LASTEXITCODE -ne 0) { throw "VMAP assembly failed; see $assemblerLog" }

    $previousProduct = $env:TC_RAW_GEOMETRY_CLIENT_PRODUCT
    $previousBuild = $env:TC_RAW_GEOMETRY_CLIENT_BUILD
    $previousOutput = $env:TC_RAW_GEOMETRY_EXPORT_DIR
    try {
        $env:TC_RAW_GEOMETRY_CLIENT_PRODUCT = $clientProduct
        $env:TC_RAW_GEOMETRY_CLIENT_BUILD = [string]$clientBuild
        $env:TC_RAW_GEOMETRY_EXPORT_DIR = Join-Path $OutputDirectory 'raw-geometry'
        $generatorLog = Join-Path $logDirectory 'raw-geometry-export.log'
        $generatorOutput = Join-Path $OutputDirectory 'raw-generator-output'
        # TrinityCore's mmap tile axes are ADT Y,X; preserve that established mapping.
        & $mmapExe $MapId --tile "$AdtY,$AdtX" --threads 1 --input $OutputDirectory --output $generatorOutput *> $generatorLog
        if ($LASTEXITCODE -ne 0) { throw "Pre-Recast raw geometry export failed; see $generatorLog" }
    } finally {
        if ($null -eq $previousProduct) { Remove-Item Env:TC_RAW_GEOMETRY_CLIENT_PRODUCT -ErrorAction SilentlyContinue } else { $env:TC_RAW_GEOMETRY_CLIENT_PRODUCT = $previousProduct }
        if ($null -eq $previousBuild) { Remove-Item Env:TC_RAW_GEOMETRY_CLIENT_BUILD -ErrorAction SilentlyContinue } else { $env:TC_RAW_GEOMETRY_CLIENT_BUILD = $previousBuild }
        if ($null -eq $previousOutput) { Remove-Item Env:TC_RAW_GEOMETRY_EXPORT_DIR -ErrorAction SilentlyContinue } else { $env:TC_RAW_GEOMETRY_EXPORT_DIR = $previousOutput }
    }
} finally {
    Pop-Location
}

$rawFile = Join-Path $OutputDirectory ('raw-geometry\map{0:D4}_adt{1:D2}_{2:D2}.rawgeo.json' -f $MapId,$AdtX,$AdtY)
if (-not (Test-Path $rawFile) -or (Get-Item $rawFile).Length -eq 0) { throw "Expected raw geometry output is missing: $rawFile" }
if (@(Get-ChildItem (Join-Path $OutputDirectory 'raw-geometry') -File -Filter '*.rawgeo.json').Count -ne 1) {
    throw 'The restricted run emitted an unexpected raw geometry file set.'
}
if (@(Get-ChildItem (Join-Path $OutputDirectory 'raw-generator-output') -Recurse -File -Filter '*.mmtile').Count -ne 0) {
    throw 'Raw geometry mode unexpectedly wrote an .mmtile product.'
}
Write-Host "PASS: $clientProduct $clientVersion MapID $MapId ADT ($AdtX,$AdtY) -> $rawFile"
