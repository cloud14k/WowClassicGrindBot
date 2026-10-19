param(
    [string]$SourceDirectory,
    [string]$BuildDirectory,
    [string]$BoostRoot,
    [string]$BoostLibraryDirectory,
    [string]$OpenSslPackageRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$tempRoot = Join-Path $repoRoot 'tmp'
$expectedCommit = '440d365e791baff1cdf555ed2179c71bd6ebd014'
$repositoryUrl = 'https://github.com/advocaite/TrinityCore.git'
$patchPath = Join-Path $repoRoot 'Utilities\ForeverGeometryTool\patches\forever-rawgeo-440d365.patch'

if (-not $SourceDirectory) { $SourceDirectory = Join-Path $tempRoot 'p45-trinitycore-440d365' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $tempRoot 'p45-trinitycore-build' }
if (-not $BoostRoot) { $BoostRoot = Join-Path $tempRoot 'boost-unpack\boost_1_86_0' }
if (-not $BoostLibraryDirectory) { $BoostLibraryDirectory = Join-Path $tempRoot 'boost-stage\lib' }
if (-not $OpenSslPackageRoot) {
    $packageParent = Join-Path $tempRoot 'conan-user\.conan\data\openssl\3.3.2\_\_\package'
    $packages = @(Get-ChildItem $packageParent -Directory -ErrorAction SilentlyContinue)
    if ($packages.Count -ne 1) {
        throw "Expected one cached OpenSSL 3.3.2 package in '$packageParent'. This script does not download dependencies."
    }
    $OpenSslPackageRoot = $packages[0].FullName
}

function Invoke-Git([string[]]$gitArgs) {
    & git @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $patchPath)) { throw "Versioned patch is missing: $patchPath" }
if (-not (Test-Path (Join-Path $BoostRoot 'boost\version.hpp'))) { throw "Cached Boost headers are missing: $BoostRoot" }
foreach ($library in 'filesystem','program_options','regex','locale') {
    if (-not (Test-Path (Join-Path $BoostLibraryDirectory "libboost_${library}-mt-x64.lib"))) {
        throw "Cached Boost $library library is missing under '$BoostLibraryDirectory'."
    }
}
if (-not (Test-Path (Join-Path $OpenSslPackageRoot 'include\openssl\opensslv.h')) -or
    -not (Test-Path (Join-Path $OpenSslPackageRoot 'lib\libcrypto.lib')) -or
    -not (Test-Path (Join-Path $OpenSslPackageRoot 'lib\libssl.lib'))) {
    throw "Cached OpenSSL headers/libraries are incomplete under '$OpenSslPackageRoot'."
}

if (-not (Test-Path $SourceDirectory)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $SourceDirectory -Parent) | Out-Null
    Invoke-Git @('clone','--filter=blob:none','--no-checkout',$repositoryUrl,$SourceDirectory)
    Invoke-Git @('-C',$SourceDirectory,'fetch','--depth','1','origin',$expectedCommit)
    Invoke-Git @('-C',$SourceDirectory,'checkout','--detach',$expectedCommit)
}

$actualOrigin = (& git -C $SourceDirectory remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $actualOrigin -ne $repositoryUrl) {
    throw "Source directory must be a checkout of $repositoryUrl; found '$actualOrigin'."
}
$actualCommit = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $expectedCommit) {
    throw "Source checkout must be exactly $expectedCommit; found '$actualCommit'."
}
$licensePath = Join-Path $SourceDirectory 'COPYING'
if (-not (Test-Path $licensePath)) { throw 'The pinned source checkout has no COPYING license file.' }
$licenseText = Get-Content $licensePath -Raw
if ($licenseText -notmatch 'GNU GENERAL PUBLIC LICENSE' -or $licenseText -notmatch 'Version 2') {
    throw 'The pinned source license was not recognized as GPL version 2.'
}
if (-not (Test-Path (Join-Path $SourceDirectory 'src\genrev\CMakeLists.txt'))) {
    throw 'The source checkout is incomplete (src/genrev/CMakeLists.txt is absent).'
}

$savedErrorActionPreference = $ErrorActionPreference
try {
    # On Windows PowerShell, a native program's expected nonzero stderr can be
    # promoted to a terminating error by the global Stop preference. This
    # reverse-check intentionally fails on a pristine checkout, so suppress
    # that expected diagnostic and use the process exit code as the result.
    $ErrorActionPreference = 'Continue'
    $reverseCheck = & git -C $SourceDirectory apply --reverse --check $patchPath 2>$null
    $patchAlreadyApplied = ($LASTEXITCODE -eq 0)
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
$status = @(& git -C $SourceDirectory status --porcelain=v1 --untracked-files=all)
if ($patchAlreadyApplied) {
    $expectedPaths = @(Select-String -Path $patchPath -Pattern '^diff --git a/.+ b/(.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
    $changedPaths = @(& git -C $SourceDirectory diff --name-only HEAD | Sort-Object -Unique)
    $untracked = @($status | Where-Object { $_.StartsWith('??') })
    if ($untracked.Count -gt 0 -or ($expectedPaths -join "`n") -ne ($changedPaths -join "`n")) {
        throw 'The source tree has changes beyond this patch; refusing to build an ambiguous checkout.'
    }
} else {
    if ($status.Count -gt 0) { throw 'The source checkout is not clean; refusing to overwrite local source changes.' }
    Invoke-Git @('-C',$SourceDirectory,'apply','--check',$patchPath)
    Invoke-Git @('-C',$SourceDirectory,'apply',$patchPath)
}
Invoke-Git @('-C',$SourceDirectory,'diff','--check')

$opensslInclude = Join-Path $OpenSslPackageRoot 'include'
$cmakeArgs = @(
    '-S',$SourceDirectory,
    '-B',$BuildDirectory,
    '-G','Visual Studio 17 2022',
    '-A','x64',
    '-DSERVERS=OFF',
    '-DSCRIPTS=none',
    '-DTOOLS=ON',
    '-DBUILD_TESTING=OFF',
    '-DWITH_DYNAMIC_LINKING=OFF',
    '-DWITH_WARNINGS=OFF',
    '-DBoost_NO_BOOST_CMAKE=ON',
    "-DBOOST_ROOT=$BoostRoot",
    "-DBOOST_INCLUDEDIR=$BoostRoot",
    "-DBOOST_LIBRARYDIR=$BoostLibraryDirectory",
    "-DBoost_INCLUDE_DIR=$BoostRoot",
    "-DBoost_FILESYSTEM_LIBRARY_RELEASE=$(Join-Path $BoostLibraryDirectory 'libboost_filesystem-mt-x64.lib')",
    "-DBoost_PROGRAM_OPTIONS_LIBRARY_RELEASE=$(Join-Path $BoostLibraryDirectory 'libboost_program_options-mt-x64.lib')",
    "-DBoost_REGEX_LIBRARY_RELEASE=$(Join-Path $BoostLibraryDirectory 'libboost_regex-mt-x64.lib')",
    "-DBoost_LOCALE_LIBRARY_RELEASE=$(Join-Path $BoostLibraryDirectory 'libboost_locale-mt-x64.lib')",
    "-DOPENSSL_ROOT_DIR=$OpenSslPackageRoot",
    '-DOPENSSL_USE_STATIC_LIBS=ON',
    "-DOPENSSL_INCLUDE_DIR=$opensslInclude",
    '-DCMAKE_CXX_FLAGS=/EHsc /DBOOST_ALL_NO_LIB'
)
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE" }

foreach ($target in 'mapextractor','vmap4extractor','vmap4assembler','mmaps_generator') {
    & cmake --build $BuildDirectory --config Release --target $target --parallel 1
    if ($LASTEXITCODE -ne 0) { throw "Building $target failed with exit code $LASTEXITCODE" }
}

$binDirectory = Join-Path $BuildDirectory 'bin\Release'
foreach ($exe in 'mapextractor.exe','vmap4extractor.exe','vmap4assembler.exe','mmaps_generator.exe') {
    if (-not (Test-Path (Join-Path $binDirectory $exe))) { throw "Build did not produce $exe." }
}
$mapExtractorPath = Join-Path $binDirectory 'mapextractor.exe'
$provenance = [ordered]@{
    format = 'wow-forever-casc-tools-v1'
    source_repository = $repositoryUrl
    source_commit = $expectedCommit
    patch_sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
    mapextractor_sha256 = (Get-FileHash -LiteralPath $mapExtractorPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$provenancePath = Join-Path $binDirectory 'forever-casc-tools.json'
$provenanceJson = $provenance | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($provenancePath, $provenanceJson,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Built the four scoped extraction tools in $binDirectory"
