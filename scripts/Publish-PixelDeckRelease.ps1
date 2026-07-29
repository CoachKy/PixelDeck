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
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' 'artifacts' 'releases')
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProject = Join-Path $repositoryRoot 'src' 'PixelDeck.App' 'PixelDeck.App.csproj'
$updaterProject = Join-Path $repositoryRoot 'src' 'PixelDeck.Updater' 'PixelDeck.Updater.csproj'

# The package version comes from the app assembly so a release can never be
# tagged with a version the running build does not report.
[xml] $appXml = Get-Content $appProject
$version = ($appXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
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
        dotnet publish $project `
            --configuration $Configuration `
            --runtime $rid `
            --self-contained true `
            --output $stage `
            -p:DebugType=None `
            -p:GenerateDocumentationFile=false `
            --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $name / $rid" }
    }

    # The updater looks for the executable at the archive root, so verify it is
    # there before packaging rather than discovering it during an update.
    $executable = if ($rid -like 'win-*') { 'PixelDeck.App.exe' } else { 'PixelDeck.App' }
    if (-not (Test-Path (Join-Path $stage $executable))) {
        throw "published output for $rid has no $executable at its root"
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
