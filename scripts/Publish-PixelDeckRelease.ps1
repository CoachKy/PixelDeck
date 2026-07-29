<#
.SYNOPSIS
    Builds the release packages PixelDeck's updater expects.

.DESCRIPTION
    Publishes a self-contained PixelDeck for each target runtime, bundles
    PixelDeck.Updater alongside it, and produces one archive plus a .sha256
    sidecar per runtime.

    Names follow the convention already in artifacts/releases, with the runtime
    identifier appended so UpdatePlatform.Matches can route each machine to its
    own package:

        PixelDeck-v<version>-<rid>.zip       Windows
        PixelDeck-v<version>-<rid>.tar.gz    Linux and macOS
        <package>.sha256                     checksum sidecar

    Changing a name here means changing UpdatePlatform.Matches too.

    Linux ships as a tarball because zip does not carry the Unix execute bit,
    which would leave the extracted binary unrunnable on a Raspberry Pi.

.PARAMETER Runtime
    Runtime identifiers to build. Defaults to the Windows desktop and
    Raspberry Pi targets.

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
    # Windows packages are signed with it when supplied and left unsigned when
    # not. Only a certificate from a trusted authority stops SmartScreen
    # warning players; a self-signed one satisfies the mechanics here but is
    # treated by SmartScreen exactly like no signature at all.
    [string] $CertificateThumbprint,

    # Countersigning proves the signature predates the certificate's expiry, so
    # already-released builds keep verifying after it lapses.
    [string] $TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

# Multi-segment paths use Path::Combine because Windows PowerShell 5.1's
# Join-Path only accepts two segments, and this script also has to run on a Pi.
$repositoryRoot = (Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..'))).Path

if (-not $OutputRoot) {
    $OutputRoot = [System.IO.Path]::Combine($repositoryRoot, 'artifacts', 'releases')
}
$appProject = [System.IO.Path]::Combine($repositoryRoot, 'src', 'PixelDeck.App', 'PixelDeck.App.csproj')
$updaterProject = [System.IO.Path]::Combine($repositoryRoot, 'src', 'PixelDeck.Updater', 'PixelDeck.Updater.csproj')

# The package version comes from the app assembly so a release can never be
# tagged with a version the running build does not report. The @() keeps this
# an array even when only one PropertyGroup carries a Version, which would
# otherwise index into the string and yield a single character.
[xml] $appXml = Get-Content $appProject
$version = @($appXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'Could not read <Version> from PixelDeck.App.csproj.' }

# Package and tag names use the dashboard's padded form (1.22.70 -> v1.22.070)
# so a release reads the same as the version PixelDeck displays.
$parts = $version.Split('.')
$tagVersion = 'v{0}.{1}.{2:000}' -f $parts[0], $parts[1], [int]$parts[2]

Write-Host "PixelDeck $version  (tag $tagVersion)" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

foreach ($rid in $Runtime) {
    Write-Host "`n=== $rid ===" -ForegroundColor Cyan
    $stage = Join-Path $OutputRoot "stage-$rid"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    foreach ($project in @($appProject, $updaterProject)) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
        Write-Host "  publishing $name"
        # Single-file keeps the payload out of the folder the player sees: the
        # runtime and every managed assembly are linked into the executable,
        # which takes the Windows package from 239 loose files to a handful.
        # Third-party native libraries (Skia, HarfBuzz, ANGLE, SDL) are left
        # beside it deliberately - bundling them would make the executable
        # self-extract into %TEMP% on first run, which costs startup time and
        # is a well-known antivirus false-positive trigger.
        dotnet publish $project `
            --configuration $Configuration `
            --runtime $rid `
            --self-contained true `
            --output $stage `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=false `
            -p:DebugType=None `
            -p:GenerateDocumentationFile=false `
            --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $name / $rid" }
    }

    # SkiaSharp and HarfBuzzSharp ship native .pdb symbols in their runtime
    # packs - about 100 MB of debug information that DebugType=None does not
    # touch, because it is package content rather than something this build
    # produces. A release has no use for it.
    Get-ChildItem $stage -Filter '*.pdb' -Recurse | Remove-Item -Force

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
        'Saves\   battery saves and save states, written per system'
        'Library\ cover images; drop in your own PNG named after the game'
    )

    # The updater looks for the executable at the archive root, so verify it is
    # there before packaging rather than discovering it during an update.
    $executable = if ($rid -like 'win-*') { 'PixelDeck.App.exe' } else { 'PixelDeck.App' }
    if (-not (Test-Path (Join-Path $stage $executable))) {
        throw "published output for $rid has no $executable at its root"
    }

    # Authenticode applies to the Windows binaries only. Both are signed: the
    # updater is launched as its own process, so an unsigned one would prompt
    # separately from the application that started it.
    if ($CertificateThumbprint -and $rid -like 'win-*') {
        $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) {
            throw "no certificate with thumbprint $CertificateThumbprint in Cert:\CurrentUser\My"
        }

        foreach ($binary in @('PixelDeck.App.exe', 'PixelDeck.Updater.exe')) {
            $path = Join-Path $stage $binary
            $result = Set-AuthenticodeSignature -FilePath $path -Certificate $certificate `
                -TimestampServer $TimestampServer -HashAlgorithm SHA256
            if ($result.Status -ne 'Valid') {
                throw "signing $binary failed: $($result.Status) - $($result.StatusMessage)"
            }

            Write-Host "  signed $binary" -ForegroundColor Green
        }
    }
    elseif ($rid -like 'win-*') {
        Write-Host "  unsigned - players will see a SmartScreen warning" -ForegroundColor DarkYellow
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

Write-Host "`nArtifacts in $OutputRoot" -ForegroundColor Cyan
Write-Host "Upload every package and its .sha256 to a SINGLE GitHub release tagged $tagVersion." -ForegroundColor Yellow
Write-Host "Both runtimes share one release: the updater reads releases/latest and picks its own asset." -ForegroundColor Yellow

# The notes are the body of the GitHub release, so a missing file means an
# unannotated release. The script cannot write the prose, but it can refuse to
# let the omission go unnoticed.
$notes = Join-Path $OutputRoot "RELEASE-NOTES-$tagVersion.md"
if (-not (Test-Path $notes)) {
    Write-Host "MISSING: $([System.IO.Path]::GetFileName($notes)) - write it before publishing the release." -ForegroundColor Red
}
