[CmdletBinding()]
param(
    [ValidateRange(3600, 216000)]
    [int]$SoakFrames = 18000,

    [switch]$SkipLocalGames
)

$ErrorActionPreference = 'Stop'
$CatalogCommit = '95d8f621ae55cee0d09b91519a8989ae0e64753b'
$CatalogUrl = "https://codeload.github.com/christopherpow/nes-test-roms/zip/$CatalogCommit"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$TestProject = Join-Path $RepoRoot 'tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj'
$TempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$CertificationRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $TempRoot 'PixelDeck-Certification'))

if (-not $CertificationRoot.StartsWith($TempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The certification directory must remain beneath the operating-system temporary directory.'
}

New-Item -ItemType Directory -Path $CertificationRoot -Force | Out-Null
$ArchivePath = Join-Path $CertificationRoot "nes-test-roms-$CatalogCommit.zip"
$CatalogRoot = Join-Path $CertificationRoot "catalog-$CatalogCommit"
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    Write-Host "Downloading the pinned NES test catalog commit $CatalogCommit..."
    Invoke-WebRequest -Uri $CatalogUrl -OutFile $ArchivePath
}

if (-not (Test-Path -LiteralPath $CatalogRoot)) {
    $ExtractionRoot = Join-Path $CertificationRoot "extract-$CatalogCommit"
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractionRoot -Force
    $ExtractedDirectory = Get-ChildItem -LiteralPath $ExtractionRoot -Directory |
        Select-Object -First 1
    if ($null -eq $ExtractedDirectory) {
        throw 'The NES test catalog archive did not contain a root directory.'
    }

    Move-Item -LiteralPath $ExtractedDirectory.FullName -Destination $CatalogRoot
}

$RunRoot = Join-Path $CertificationRoot (
    'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $PID)
New-Item -ItemType Directory -Path $RunRoot | Out-Null

function Copy-TestGroup {
    param(
        [Parameter(Mandatory)]
        [string]$RelativeSource,

        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $Source = Join-Path $CatalogRoot $RelativeSource
    $Destination = Join-Path $DestinationRoot ($RelativeSource -replace '[/\\]', '-')
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Filter '*.nes' -File |
        Copy-Item -Destination $Destination
}

function Copy-TestFile {
    param(
        [Parameter(Mandatory)]
        [string]$RelativeSource,

        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $Source = Join-Path $CatalogRoot $RelativeSource
    $Suite = ($RelativeSource -split '[/\\]')[0]
    $Destination = Join-Path $DestinationRoot $Suite
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}

function Assert-RomCount {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [int]$Expected
    )

    $Actual = (Get-ChildItem -LiteralPath $Path -Filter '*.nes' -File -Recurse).Count
    if ($Actual -ne $Expected) {
        throw "Expected $Expected test ROMs beneath $Path, but found $Actual."
    }
}

function Invoke-ConformanceSuite {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Path,

        [ValidateSet('Auto', 'Sharp', 'Nec')]
        [string]$Mmc3Revision = 'Auto',

        [string]$VisualResultAddress = ''
    )

    Write-Host "Running PixelNES conformance suite: $Name"
    $env:PIXELDECK_NES_TEST_ROMS = $Path
    $env:PIXELDECK_NES_MMC3_IRQ_REVISION = $Mmc3Revision
    if ([string]::IsNullOrWhiteSpace($VisualResultAddress)) {
        Remove-Item Env:PIXELDECK_NES_VISUAL_RESULT_ADDRESS -ErrorAction SilentlyContinue
    }
    else {
        $env:PIXELDECK_NES_VISUAL_RESULT_ADDRESS = $VisualResultAddress
    }

    & dotnet test $TestProject -c Release --no-restore `
        --filter 'FullyQualifiedName~NesConformanceTests' `
        --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) {
        throw "The $Name conformance suite failed."
    }
}

Push-Location $RepoRoot
try {
    & dotnet restore 'PixelDeck.sln'
    if ($LASTEXITCODE -ne 0) {
        throw 'Package restore failed.'
    }

    $Baseline = Join-Path $RunRoot 'baseline'
    New-Item -ItemType Directory -Path $Baseline | Out-Null
    Copy-TestFile 'instr_test-v5/official_only.nes' $Baseline
    Copy-TestFile 'instr_test-v5/all_instrs.nes' $Baseline
    Copy-TestGroup 'ppu_vbl_nmi/rom_singles' $Baseline
    Copy-TestGroup 'apu_test/rom_singles' $Baseline
    Assert-RomCount $Baseline 20
    Invoke-ConformanceSuite '20-ROM required baseline' $Baseline

    $Deep = Join-Path $RunRoot 'deep'
    New-Item -ItemType Directory -Path $Deep | Out-Null
    foreach ($Group in @(
            'instr_timing/rom_singles',
            'instr_misc/rom_singles',
            'cpu_dummy_writes',
            'cpu_reset',
            'cpu_interrupts_v2/rom_singles',
            'apu_reset')) {
        Copy-TestGroup $Group $Deep
    }
    foreach ($File in @(
            'ppu_open_bus/ppu_open_bus.nes',
            'ppu_read_buffer/test_ppu_read_buffer.nes',
            'oam_read/oam_read.nes')) {
        Copy-TestFile $File $Deep
    }
    Assert-RomCount $Deep 24
    Invoke-ConformanceSuite '24-ROM deep CPU/APU/PPU suite' $Deep

    $Mmc3Sharp = Join-Path $RunRoot 'mmc3-sharp'
    New-Item -ItemType Directory -Path $Mmc3Sharp | Out-Null
    foreach ($Index in 1..5) {
        $Rom = Get-ChildItem -LiteralPath (Join-Path $CatalogRoot 'mmc3_test_2/rom_singles') `
            -Filter "$Index-*.nes" -File
        Copy-Item -LiteralPath $Rom.FullName -Destination $Mmc3Sharp
    }
    Assert-RomCount $Mmc3Sharp 5
    Invoke-ConformanceSuite 'MMC3 Sharp/new IRQ suite' $Mmc3Sharp 'Sharp'

    $Mmc3Nec = Join-Path $RunRoot 'mmc3-nec'
    New-Item -ItemType Directory -Path $Mmc3Nec | Out-Null
    Copy-TestFile 'mmc3_test_2/rom_singles/6-MMC3_alt.nes' $Mmc3Nec
    Assert-RomCount $Mmc3Nec 1
    Invoke-ConformanceSuite 'MMC3 NEC/old IRQ suite' $Mmc3Nec 'Nec'

    $Visual = Join-Path $RunRoot 'visual-ppu'
    New-Item -ItemType Directory -Path $Visual | Out-Null
    Copy-TestGroup 'sprite_overflow_tests' $Visual
    Copy-TestGroup 'sprite_hit_tests_2005.10.05' $Visual
    Assert-RomCount $Visual 16
    Invoke-ConformanceSuite '16-ROM sprite overflow/hit suite' $Visual 'Auto' '00F8'

    Remove-Item Env:PIXELDECK_NES_TEST_ROMS -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_NES_MMC3_IRQ_REVISION -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_NES_VISUAL_RESULT_ADDRESS -ErrorAction SilentlyContinue

    Write-Host 'Running the complete PixelDeck regression suite...'
    & dotnet test 'PixelDeck.sln' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'The complete regression suite failed.'
    }

    if (-not $SkipLocalGames) {
        Write-Host "Running the local-game soak for $SoakFrames frames per game..."
        $env:PIXELDECK_NES_RELEASE_CERTIFICATION = '1'
        $env:PIXELDECK_NES_SOAK_FRAMES = $SoakFrames.ToString(
            [System.Globalization.CultureInfo]::InvariantCulture)
        & dotnet test $TestProject -c Release --no-restore `
            --filter 'FullyQualifiedName~LocalGameMatrixPassesTheReleaseSoakWhenRequested' `
            --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) {
            throw 'The local-game soak failed.'
        }
    }

    Remove-Item Env:PIXELDECK_NES_RELEASE_CERTIFICATION -ErrorAction SilentlyContinue
    Remove-Item Env:PIXELDECK_NES_SOAK_FRAMES -ErrorAction SilentlyContinue

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

    Write-Host 'PixelNES release certification passed.'
    Write-Host "Pinned test catalog: https://github.com/christopherpow/nes-test-roms/commit/$CatalogCommit"
    Write-Host "Run evidence: $RunRoot"
}
finally {
    Pop-Location
}
