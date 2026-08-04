<#
.SYNOPSIS
    Runs the Peter Lemon RSP conformance ROMs and scores them against the
    committed hardware reference screenshots.

.DESCRIPTION
    Each ROM under TestRoms/N64/RSPTest-CP2 renders its own result table and
    writes "PASS" in green or "FAIL" in red. The reference PNG beside it is a
    capture of the same ROM on real hardware, so the red-pixel count in the
    result column is an external pass/fail signal rather than a judgement call.

    Every reference image contains a fixed number of red pixels from the red
    "Test Result" column header. A run is clean when its red-pixel count equals
    the reference's; anything above that is real FAIL text.

    This is Pixel64's first external oracle. Treat its numbers as authoritative
    over any hand-written unit test.

.EXAMPLE
    .\scripts\Test-Pixel64Rsp.ps1
    .\scripts\Test-Pixel64Rsp.ps1 -Fields 600
#>
[CmdletBinding()]
param(
    [int]$Fields = 300,
    [string]$RomFolder = 'tests/PixelDeck.App.Tests/TestRoms/N64/RSPTest-CP2',
    [string]$OutputFolder,
    [switch]$SkipRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$romPath = Join-Path $repoRoot $RomFolder
if (-not (Test-Path $romPath)) {
    Write-Error "RSP conformance ROMs not found at $romPath. See $RomFolder/README.md."
}

if (-not $OutputFolder) {
    $OutputFolder = Join-Path $repoRoot ('artifacts/n64-rsptest/run-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

if (-not $SkipRun) {
    Write-Host "Running RSP conformance ROMs ($Fields fields each)..."
    & dotnet run --project (Join-Path $repoRoot 'tools/PixelDeck.N64Compatibility') -c Release -- `
        --games $romPath --output $OutputFolder --fields $Fields | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Conformance run failed with exit code $LASTEXITCODE." }
}

$captureFolder = Join-Path $OutputFolder 'captures'
if (-not (Test-Path $captureFolder)) { Write-Error "No captures produced in $captureFolder." }

function Measure-ResultColumn([string]$path) {
    $bitmap = New-Object System.Drawing.Bitmap $path
    try {
        $scaleX = $bitmap.Width / 640.0
        $startX = [int](560 * $scaleX)
        $red = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = $startX; $x -lt $bitmap.Width; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -gt 150 -and $pixel.G -lt 90 -and $pixel.B -lt 90) { $red++ }
            }
        }
        return $red
    }
    finally { $bitmap.Dispose() }
}

$rows = @()
foreach ($capture in Get-ChildItem $captureFolder -Filter *.bmp) {
    if ($capture.BaseName -notmatch '^\d{4}-(.+)-[0-9A-F]{8}$') { continue }
    $stem = $Matches[1]
    $reference = Join-Path $romPath "$stem.png"
    if (-not (Test-Path $reference)) { continue }

    # Undocumented-opcode ROMs exercise instructions Pixel64 deliberately does
    # not implement, so their failures are expected and reported separately.
    $isReserved = $stem.StartsWith('RESERVED_')
    $mine = Measure-ResultColumn $capture.FullName
    $expected = Measure-ResultColumn $reference

    $rows += [pscustomobject]@{
        Test     = $stem
        Expected = $expected
        Actual   = $mine
        Status   = if ($mine -eq $expected) { 'PASS' }
                   elseif ($isReserved) { 'unimplemented' }
                   else { 'FAIL' }
    }
}

$rows = $rows | Sort-Object Test
$rows | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

$pass = @($rows | Where-Object Status -eq 'PASS').Count
$fail = @($rows | Where-Object Status -eq 'FAIL').Count
$skip = @($rows | Where-Object Status -eq 'unimplemented').Count

Write-Host ''
Write-Host ("RSP conformance: {0} pass, {1} fail, {2} undocumented-opcode (expected)" -f $pass, $fail, $skip)
Write-Host ("Evidence: {0}" -f $OutputFolder)

if ($fail -gt 0) { exit 1 }
