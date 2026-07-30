<#
.SYNOPSIS
    Builds the release packages PixelDeck's launcher expects.

.DESCRIPTION
    PixelDeck ships as a stable launcher plus replaceable components:

        PixelDeck.exe            self-contained: .NET runtime, Avalonia, launcher
        libSkiaSharp.dll  ...    native graphics and audio libraries
        Components\*.dll         dashboard and emulation cores
        Games\ Saves\ Library\   player content, per system

    The launcher is published single-file so the runtime and every third-party
    assembly land inside one executable. The components stay loose, which is what
    lets a release replace the dashboard or an emulation core without rewriting
    PixelDeck.exe - and lets an update download about two megabytes instead of
    seventy.

    Component assemblies are pure IL, so one build of them serves every runtime.
    Only the launcher is built per RID.

    manifest.json describes the components and their hashes. The updater reads it
    to decide between replacing components only and replacing everything.

.EXAMPLE
    ./scripts/Publish-PixelDeckRelease.ps1
#>
[CmdletBinding()]
param(
    [string[]] $Runtime = @('win-x64', 'linux-arm64'),
    [string] $Configuration = 'Release',

    # Resolved in the body, not here: $PSScriptRoot is not populated while
    # param() defaults are evaluated, so building a path from it at this point
    # silently produces one relative to the caller's working directory.
    [string] $OutputRoot,

    # Thumbprint of an Authenticode certificate in the current user's store.
    # Only a certificate from a trusted authority stops SmartScreen warning
    # players; a self-signed one satisfies the mechanics and nothing else.
    # Linux needs no equivalent, so signing applies to Windows packages only.
    [string] $CertificateThumbprint,

    [string] $TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# Multi-segment paths use Path::Combine because Windows PowerShell 5.1's
# Join-Path only accepts two segments, and this script also has to run on a Pi.
$repositoryRoot = (Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..'))).Path

if (-not $OutputRoot) {
    $OutputRoot = [System.IO.Path]::Combine($repositoryRoot, 'artifacts', 'releases')
}

$launcherProject = [System.IO.Path]::Combine($repositoryRoot, 'src', 'PixelDeck.Launcher', 'PixelDeck.Launcher.csproj')
$appProject = [System.IO.Path]::Combine($repositoryRoot, 'src', 'PixelDeck.App', 'PixelDeck.App.csproj')

function Get-ProjectVersion([string] $project) {
    # The @() keeps this an array even when only one PropertyGroup carries a
    # Version, which would otherwise index into the string and yield one character.
    [xml] $xml = Get-Content $project
    $value = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $value) { throw "Could not read <Version> from $project." }
    return $value
}

# The release version comes from the application component: it is the number
# players see. The launcher's own version is what decides whether an update has
# to replace PixelDeck.exe at all.
$version = Get-ProjectVersion $appProject
$launcherVersion = Get-ProjectVersion $launcherProject

# Package and tag names use the dashboard's padded form (1.22.73 -> v1.22.073)
# so a release reads the same as the version PixelDeck displays.
$parts = $version.Split('.')
$tagVersion = 'v{0}.{1}.{2:000}' -f $parts[0], $parts[1], [int]$parts[2]

Write-Host "PixelDeck $version  (tag $tagVersion)  launcher $launcherVersion" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

# --- components, built once ------------------------------------------------
Write-Host "`n=== components ===" -ForegroundColor Cyan
Write-Host "  building PixelDeck.App and the emulation cores"
dotnet build $appProject --configuration $Configuration -p:DebugType=None `
    -p:GenerateDocumentationFile=false --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'component build failed' }

$componentSource = [System.IO.Path]::Combine(
    $repositoryRoot, 'src', 'PixelDeck.App', 'bin', $Configuration, 'net10.0')

# Only PixelDeck's own assemblies are components. Everything else this folder
# holds - Avalonia, NAudio, SDL3-CS - is inside the launcher's bundle already,
# and shipping a second copy loose would be both wasteful and ambiguous.
$componentFiles = Get-ChildItem $componentSource -Filter 'PixelDeck.*.dll' |
    Where-Object { $_.Name -ne 'PixelDeck.Launcher.dll' }

if ($componentFiles.Count -lt 4) {
    throw "expected at least 4 component assemblies, found $($componentFiles.Count)"
}
foreach ($component in $componentFiles) {
    Write-Host ("  {0,-34} {1,7:N0} KB" -f $component.Name, ($component.Length / 1KB))
}

# --- per-runtime packages ---------------------------------------------------
foreach ($rid in $Runtime) {
    Write-Host "`n=== $rid ===" -ForegroundColor Cyan
    $stage = Join-Path $OutputRoot "stage-$rid"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Host '  publishing PixelDeck.exe'
    # Single-file with native libraries left loose: bundling them would make the
    # executable self-extract into TEMP on first run, which costs startup time
    # and is a known antivirus false-positive trigger.
    dotnet publish $launcherProject `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        --output $stage `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:DebugType=None `
        -p:GenerateDocumentationFile=false `
        --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "launcher publish failed for $rid" }

    # paraLLEl-RDP is optional and platform-specific, so it belongs in the full
    # runtime package rather than the architecture-neutral component update.
    # The native CI workflow and Build-ParallelRdp.ps1 both stage this layout.
    $nativeRuntimeRoot = [System.IO.Path]::Combine(
        $repositoryRoot, 'artifacts', 'native', $rid)
    $nativeLibraryName = if ($rid -like 'win-*') {
        'PixelDeck.ParallelRdp.dll'
    } elseif ($rid -like 'linux-*') {
        'libPixelDeck.ParallelRdp.so'
    } else {
        'libPixelDeck.ParallelRdp.dylib'
    }
    $nativeLibrary = Get-ChildItem $nativeRuntimeRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $nativeLibraryName } |
        Select-Object -First 1
    if ($nativeLibrary) {
        $nativeTarget = [System.IO.Path]::Combine($stage, 'Native', $rid)
        New-Item -ItemType Directory -Force -Path $nativeTarget | Out-Null
        Copy-Item $nativeLibrary.FullName -Destination $nativeTarget -Force

        $nativeLicense = Get-ChildItem $nativeRuntimeRoot -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'paraLLEl-RDP-LICENSE.txt' } |
            Select-Object -First 1
        if ($nativeLicense) {
            Copy-Item $nativeLicense.FullName -Destination $nativeTarget -Force
        }
        Write-Host "  included $nativeLibraryName" -ForegroundColor Green
    }
    else {
        Write-Host "  no paraLLEl-RDP bridge staged for $rid; software fallback remains" `
            -ForegroundColor DarkYellow
    }

    # SkiaSharp and HarfBuzzSharp ship native .pdb symbols in their runtime
    # packs - about 100 MB of debug information that DebugType=None does not
    # touch, because it is package content rather than something this build
    # produces. A release has no use for it.
    Get-ChildItem $stage -Filter '*.pdb' -Recurse | Remove-Item -Force

    $componentFolder = Join-Path $stage 'Components'
    New-Item -ItemType Directory -Force -Path $componentFolder | Out-Null
    $componentFiles | Copy-Item -Destination $componentFolder -Force

    # The player-facing folders ship with the package so they are visible on
    # extraction. The application creates them at startup too, but someone who
    # unzips and looks for somewhere to put a cartridge should not have to
    # launch it first to find out where that is.
    foreach ($content in @('Games', 'Saves', 'Library')) {
        foreach ($platform in @('Nintendo', 'SuperNintendo', 'Nintendo64')) {
            New-Item -ItemType Directory -Force `
                -Path ([System.IO.Path]::Combine($stage, $content, $platform)) | Out-Null
        }
    }

    Set-Content -Encoding ascii -Path ([System.IO.Path]::Combine($stage, 'Games', 'README.txt')) -Value @(
        'Place your legally obtained cartridge files in the folder for their system:'
        ''
        '    Games/Nintendo          .nes'
        '    Games/SuperNintendo     .sfc  .smc'
        '    Games/Nintendo64        .z64  .n64  .v64'
        ''
        'PixelDeck picks them up the next time it scans the library.'
        'No ROM images are distributed with PixelDeck.'
        ''
        'Saves/     battery saves and save states, written per system'
        'Library/   cover images; drop in your own PNG named after the game'
        'Components/  application files. Updates replace these; leave them alone.'
    )

    $executable = if ($rid -like 'win-*') { 'PixelDeck.exe' } else { 'PixelDeck' }
    if (-not (Test-Path (Join-Path $stage $executable))) {
        throw "published output for $rid has no $executable at its root"
    }
    if (-not (Test-Path (Join-Path $componentFolder 'PixelDeck.App.dll'))) {
        throw "published output for $rid has no Components/PixelDeck.App.dll"
    }

    # Authenticode applies to the Windows launcher only; it is the sole
    # executable in the package now that the updater has been folded into it.
    if ($CertificateThumbprint -and $rid -like 'win-*') {
        $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) {
            throw "no certificate with thumbprint $CertificateThumbprint in Cert:\CurrentUser\My"
        }

        $result = Set-AuthenticodeSignature -FilePath (Join-Path $stage $executable) `
            -Certificate $certificate -TimestampServer $TimestampServer -HashAlgorithm SHA256
        if ($result.Status -ne 'Valid') {
            throw "signing $executable failed: $($result.Status) - $($result.StatusMessage)"
        }
        Write-Host "  signed $executable" -ForegroundColor Green
    }
    elseif ($rid -like 'win-*') {
        Write-Host '  unsigned - players will see a SmartScreen warning' -ForegroundColor DarkYellow
    }

    if ($rid -like 'win-*') {
        $package = Join-Path $OutputRoot "PixelDeck-$tagVersion-$rid.zip"
        if (Test-Path $package) { Remove-Item $package -Force }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $package
    }
    else {
        $package = Join-Path $OutputRoot "PixelDeck-$tagVersion-$rid.tar.gz"
        if (Test-Path $package) { Remove-Item $package -Force }
        # -C keeps the archive rooted at the payload rather than the stage path.
        tar -czf $package -C $stage .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    }

    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($package))" |
        Set-Content "$package.sha256" -Encoding ascii -NoNewline

    Remove-Item $stage -Recurse -Force
    $size = [Math]::Round((Get-Item $package).Length / 1MB, 1)
    Write-Host "  $([System.IO.Path]::GetFileName($package))  ${size} MB" -ForegroundColor Green
}

# --- component-only update payload ----------------------------------------
# One archive serves every platform because the components are pure IL. A
# release whose launcher is unchanged can be applied from this alone.
Write-Host "`n=== component update payload ===" -ForegroundColor Cyan
# The launcher version is in the name so the updater can tell from the asset list
# alone whether this archive applies to the machine asking, without a second
# request while the splash is waiting on it.
$componentPackage = Join-Path $OutputRoot "PixelDeck-$tagVersion-components-launcher$launcherVersion.zip"
if (Test-Path $componentPackage) { Remove-Item $componentPackage -Force }

$componentStage = Join-Path $OutputRoot 'stage-components'
if (Test-Path $componentStage) { Remove-Item $componentStage -Recurse -Force }
$componentStageInner = Join-Path $componentStage 'Components'
New-Item -ItemType Directory -Force -Path $componentStageInner | Out-Null
$componentFiles | Copy-Item -Destination $componentStageInner -Force

Compress-Archive -Path (Join-Path $componentStage '*') -DestinationPath $componentPackage
$componentHash = (Get-FileHash $componentPackage -Algorithm SHA256).Hash.ToLowerInvariant()
"$componentHash  $([System.IO.Path]::GetFileName($componentPackage))" |
    Set-Content "$componentPackage.sha256" -Encoding ascii -NoNewline

# The manifest is what the updater parses to choose an update path. Per-file
# hashes let the launcher verify every component before overwriting anything.
$manifestFiles = foreach ($component in (Get-ChildItem $componentStageInner -Filter '*.dll')) {
    [ordered] @{
        relativePath = "Components/$($component.Name)"
        sha256       = (Get-FileHash $component.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        length       = $component.Length
    }
}

$manifest = [ordered] @{
    release          = $version
    launcherVersion  = $launcherVersion
    componentArchive = [System.IO.Path]::GetFileName($componentPackage)
    componentSha256  = $componentHash
    files            = @($manifestFiles)
}

$manifestPath = Join-Path $OutputRoot 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding ascii
Remove-Item $componentStage -Recurse -Force

Write-Host "  $([System.IO.Path]::GetFileName($componentPackage))  $([Math]::Round((Get-Item $componentPackage).Length / 1KB, 0)) KB" -ForegroundColor Green
Write-Host '  manifest.json' -ForegroundColor Green

Write-Host "`nArtifacts in $OutputRoot" -ForegroundColor Cyan
Write-Host "Upload every package, every .sha256, and manifest.json to a SINGLE release tagged $tagVersion." -ForegroundColor Yellow
Write-Host 'manifest.json must be present: without it the updater cannot offer a component-only update.' -ForegroundColor Yellow

$notes = Join-Path $OutputRoot "RELEASE-NOTES-$tagVersion.md"
if (-not (Test-Path $notes)) {
    Write-Host "MISSING: $([System.IO.Path]::GetFileName($notes)) - write it before publishing the release." -ForegroundColor Red
}
