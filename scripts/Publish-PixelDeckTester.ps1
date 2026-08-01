[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [string]$OutputRoot = "artifacts/releases"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src/PixelDeck.App/PixelDeck.App.csproj"
$quickStartPath = Join-Path $repositoryRoot "docs/TESTER-QUICKSTART.md"
$resolvedOutputRoot = Join-Path $repositoryRoot $OutputRoot

$assemblyVersionText = (
    dotnet msbuild $projectPath -nologo -getProperty:AssemblyVersion
).Trim()

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($assemblyVersionText))
{
    throw "Could not read the PixelDeck version from $projectPath."
}

$assemblyVersion = [Version]::Parse($assemblyVersionText)
$version = "{0}.{1}.{2:D3}" -f `
    $assemblyVersion.Major, `
    $assemblyVersion.Minor, `
    $assemblyVersion.Build
$packageName = "PixelDeck-v$version-$Runtime"
$packageDirectory = Join-Path $resolvedOutputRoot $packageName
$archivePath = Join-Path $resolvedOutputRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

foreach ($path in @($packageDirectory, $archivePath, $checksumPath))
{
    if (Test-Path -LiteralPath $path)
    {
        throw "Release output already exists: $path"
    }
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $packageDirectory `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0)
{
    throw "PixelDeck publish failed."
}

# NuGet native-runtime packages can contribute large vendor PDBs even when
# project symbols are disabled. Tester builds do not need those symbols.
Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter "*.pdb" |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$nintendoDirectory = Join-Path $packageDirectory "Games/Nintendo"
$nintendo64Directory = Join-Path $packageDirectory "Games/Nintendo64"
$superNintendoDirectory = Join-Path $packageDirectory "Games/SuperNintendo"
New-Item -ItemType Directory -Path $nintendoDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $nintendo64Directory -Force | Out-Null
New-Item -ItemType Directory -Path $superNintendoDirectory -Force | Out-Null

Copy-Item -LiteralPath $quickStartPath -Destination (Join-Path $packageDirectory "README.md")

Set-Content `
    -LiteralPath (Join-Path $nintendoDirectory "PLACE_NES_GAMES_HERE.txt") `
    -Value "Place legally obtained homebrew or NES .nes images in this folder."
Set-Content `
    -LiteralPath (Join-Path $nintendo64Directory "PLACE_N64_GAMES_HERE.txt") `
    -Value "Place legally obtained Nintendo 64 .z64/.v64/.n64 images in this folder."
Set-Content `
    -LiteralPath (Join-Path $superNintendoDirectory "PLACE_SNES_GAMES_HERE.txt") `
    -Value "Place legally obtained homebrew or SNES .sfc/.smc images in this folder."

$unexpectedImages = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
    Where-Object { $_.Extension -in @(".nes", ".fds", ".sfc", ".smc", ".z64", ".v64", ".n64") }

if ($unexpectedImages)
{
    throw "Refusing to package game images: $($unexpectedImages.FullName -join ', ')"
}

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $packageName.zip" | Set-Content -LiteralPath $checksumPath

$archive = Get-Item -LiteralPath $archivePath
[pscustomobject]@{
    Package = $archive.FullName
    SizeMiB = [Math]::Round($archive.Length / 1MB, 2)
    Sha256 = $hash
    ChecksumFile = $checksumPath
}
