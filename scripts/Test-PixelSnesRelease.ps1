[CmdletBinding()]
param(
    [ValidateRange(3600, 216000)]
    [int]$SoakFrames = 18000,

    [switch]$SkipLocalGames
)

$ErrorActionPreference = 'Stop'
$CpuSuiteCommit = '350b394e86ec5d62f600b5cbf64cdce3721bb6ef'
$CpuSuiteRawRoot = "https://raw.githubusercontent.com/PeterLemon/SNES/$CpuSuiteCommit/CPUTest/CPU"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$TestProject = Join-Path $RepoRoot 'tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj'
$TempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$CertificationRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $TempRoot 'PixelDeck-SnesCertification'))

if (-not $CertificationRoot.StartsWith($TempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The certification directory must remain beneath the operating-system temporary directory.'
}

New-Item -ItemType Directory -Path $CertificationRoot -Force | Out-Null
$RunRoot = Join-Path $CertificationRoot (
    'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $PID)
$CpuRunRoot = Join-Path $RunRoot 'cpu'
New-Item -ItemType Directory -Path $CpuRunRoot -Force | Out-Null

$CpuGroups = @(
    'ADC', 'AND', 'ASL', 'BIT', 'BRA', 'CMP', 'DEC', 'EOR', 'INC', 'JMP',
    'LDR', 'LSR', 'MOV', 'MSC', 'ORA', 'PHL', 'PSR', 'RET', 'ROL', 'ROR',
    'SBC', 'STR', 'TRN')
Write-Host "Downloading 23 pinned SNES CPU ROM/reference pairs from commit $CpuSuiteCommit..."
foreach ($Group in $CpuGroups) {
    $BaseName = "CPU$Group"
    foreach ($Extension in @('sfc', 'png')) {
        $Destination = Join-Path $CpuRunRoot "$BaseName.$Extension"
        $Download = $Destination + '.download'
        $Uri = "$CpuSuiteRawRoot/$Group/$BaseName.$Extension"
        Invoke-WebRequest -Uri $Uri -OutFile $Download
        if ((Get-Item -LiteralPath $Download).Length -eq 0) {
            throw "The pinned asset $BaseName.$Extension was empty."
        }

        Move-Item -LiteralPath $Download -Destination $Destination
    }
}

$CopiedRomCount = (Get-ChildItem -LiteralPath $CpuRunRoot -Filter '*.sfc' -File).Count
$CopiedReferenceCount = (Get-ChildItem -LiteralPath $CpuRunRoot -Filter '*.png' -File).Count
if ($CopiedRomCount -ne 23 -or $CopiedReferenceCount -ne 23) {
    throw "Expected 23 CPU ROMs and references; found $CopiedRomCount ROMs and $CopiedReferenceCount references."
}

Push-Location $RepoRoot
try {
    & dotnet restore 'PixelDeck.sln'
    if ($LASTEXITCODE -ne 0) {
        throw 'Package restore failed.'
    }

    Write-Host 'Running 23 pinned 65C816 hardware-reference screens...'
    $env:PIXELDECK_SNES_TEST_ROMS = $CpuRunRoot
    & dotnet test $TestProject -c Release --no-restore `
        --filter 'FullyQualifiedName~SnesConformanceTests' `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw 'The pinned 65C816 visual conformance suite failed.'
    }

    Remove-Item Env:PIXELDECK_SNES_TEST_ROMS -ErrorAction SilentlyContinue

    Write-Host 'Running the complete PixelDeck regression suite...'
    & dotnet test 'PixelDeck.sln' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'The complete regression suite failed.'
    }

    if (-not $SkipLocalGames) {
        Write-Host "Running the local SNES game soak for $SoakFrames frames per supported game..."
        $env:PIXELDECK_SNES_RELEASE_CERTIFICATION = '1'
        $env:PIXELDECK_SNES_SOAK_FRAMES = $SoakFrames.ToString(
            [System.Globalization.CultureInfo]::InvariantCulture)
        & dotnet test $TestProject -c Release --no-restore `
            --filter 'FullyQualifiedName~SnesReleaseCertificationTests.LocalGameMatrixPassesTheReleaseSoakWhenRequested' `
            --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) {
            throw 'The local SNES game soak failed.'
        }
    }

    Remove-Item Env:PIXELDECK_SNES_RELEASE_CERTIFICATION -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_SNES_SOAK_FRAMES -ErrorAction SilentlyContinue

    Write-Host 'Publishing framework-dependent Linux x64 and Raspberry Pi ARM64 builds...'
    & dotnet publish 'src/PixelDeck.App/PixelDeck.App.csproj' -c Release `
        -r linux-x64 --self-contained false
    if ($LASTEXITCODE -ne 0) {
        throw 'The Linux x64 publish failed.'
    }

    & dotnet publish 'src/PixelDeck.App/PixelDeck.App.csproj' -c Release `
        -r linux-arm64 --self-contained false
    if ($LASTEXITCODE -ne 0) {
        throw 'The Linux ARM64 publish failed.'
    }

    Write-Host 'PixelSNES release certification passed.'
    Write-Host "Pinned CPU suite: https://github.com/PeterLemon/SNES/commit/$CpuSuiteCommit"
    Write-Host "Run evidence: $RunRoot"
}
finally {
    Remove-Item Env:PIXELDECK_SNES_TEST_ROMS -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_SNES_RELEASE_CERTIFICATION -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_SNES_SOAK_FRAMES -ErrorAction SilentlyContinue
    Pop-Location
}
