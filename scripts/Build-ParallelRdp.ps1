[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $BuildDirectory,

    [string] $StandaloneSourceDirectory,

    [string] $RuntimeIdentifier,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path $repositoryRoot 'artifacts/validation/parallel-rdp'
}

if (-not $RuntimeIdentifier) {
    $platform = if ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { 'win' }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $RuntimeIdentifier = "$platform-$architecture"
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/native/$RuntimeIdentifier"
}

$arguments = @(
    '-S', (Join-Path $repositoryRoot 'native/PixelDeck.ParallelRdp'),
    '-B', $BuildDirectory,
    "-DCMAKE_BUILD_TYPE=$Configuration"
)

if ($StandaloneSourceDirectory) {
    $source = (Resolve-Path $StandaloneSourceDirectory).Path
    $arguments += "-DPIXELDECK_PARALLEL_RDP_SOURCE_DIR=$source"
}

Write-Host 'Configuring the pinned PixelDeck paraLLEl-RDP bridge.' -ForegroundColor Cyan
& cmake @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'paraLLEl-RDP CMake configuration failed.'
}

Write-Host 'Compiling with two workers to cap native compiler memory.' -ForegroundColor Cyan
& cmake --build $BuildDirectory --config $Configuration --parallel 2
if ($LASTEXITCODE -ne 0) {
    throw 'paraLLEl-RDP native compilation failed.'
}

& ctest --test-dir $BuildDirectory -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) {
    throw 'paraLLEl-RDP native ABI smoke test failed.'
}

& cmake --install $BuildDirectory --config $Configuration --prefix $OutputDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'paraLLEl-RDP native install staging failed.'
}

$library = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File |
    Where-Object {
        $_.Name -in @(
            'PixelDeck.ParallelRdp.dll',
            'libPixelDeck.ParallelRdp.so',
            'libPixelDeck.ParallelRdp.dylib')
    } |
    Select-Object -First 1

if (-not $library) {
    throw "The build passed but no PixelDeck.ParallelRdp native library was found in $OutputDirectory."
}

Write-Host "Native bridge: $($library.FullName)" -ForegroundColor Green
$library
