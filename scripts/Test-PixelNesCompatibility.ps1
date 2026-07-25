[CmdletBinding()]
param(
    [ValidateRange(120, 216000)]
    [int]$FramesPerGame = 600,

    [ValidateRange(1, 64)]
    [int]$Parallelism = [Math]::Min(4, [Math]::Max(1, [Environment]::ProcessorCount / 2)),

    [string]$Filter = '',

    [switch]$NoCaptures,

    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$GamesFolder = Join-Path $RepoRoot 'Games/Nintendo'
$RunName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$OutputFolder = Join-Path $RepoRoot "artifacts/nes-compatibility/$RunName"
$Arguments = @(
    'run',
    '--project',
    (Join-Path $RepoRoot 'tools/PixelDeck.NesCompatibility/PixelDeck.NesCompatibility.csproj'),
    '-c',
    'Release',
    '--',
    '--games',
    $GamesFolder,
    '--output',
    $OutputFolder,
    '--frames',
    $FramesPerGame.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '--parallel',
    $Parallelism.ToString([System.Globalization.CultureInfo]::InvariantCulture)
)

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $Arguments += @('--filter', $Filter)
}

if ($NoCaptures) {
    $Arguments += '--no-captures'
}

if ($Strict) {
    $Arguments += '--strict'
}

Push-Location $RepoRoot
try {
    & dotnet @Arguments
    $ExitCode = $LASTEXITCODE
    Write-Host "PixelNES compatibility evidence: $OutputFolder"
    exit $ExitCode
}
finally {
    Pop-Location
}
