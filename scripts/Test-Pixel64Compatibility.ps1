[CmdletBinding()]
param(
    [ValidateRange(2, 3600)]
    [int]$FieldsPerGame = 600,

    [ValidateRange(1, 64)]
    [int]$Parallelism = [Math]::Min(4, [Math]::Max(1, [Environment]::ProcessorCount / 2)),

    [string]$Filter = '',

    [switch]$NoCaptures,

    [switch]$GraphicsCaptures,

    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$GamesFolder = Join-Path $RepoRoot 'Games/Nintendo64'
$RunName = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$OutputFolder = Join-Path $RepoRoot "artifacts/n64-compatibility/$RunName"
$Arguments = @(
    'run',
    '--project',
    (Join-Path $RepoRoot 'tools/PixelDeck.N64Compatibility/PixelDeck.N64Compatibility.csproj'),
    '-c',
    'Release',
    '--',
    '--games',
    $GamesFolder,
    '--output',
    $OutputFolder,
    '--fields',
    $FieldsPerGame.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '--parallel',
    $Parallelism.ToString([System.Globalization.CultureInfo]::InvariantCulture)
)

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $Arguments += @('--filter', $Filter)
}

if ($NoCaptures) {
    $Arguments += '--no-captures'
}

if ($GraphicsCaptures) {
    $Arguments += '--graphics-captures'
}

if ($Strict) {
    $Arguments += '--strict'
}

Push-Location $RepoRoot
try {
    & dotnet @Arguments
    $ExitCode = $LASTEXITCODE
    Write-Host "Pixel64 compatibility evidence: $OutputFolder"
    exit $ExitCode
}
finally {
    Pop-Location
}
