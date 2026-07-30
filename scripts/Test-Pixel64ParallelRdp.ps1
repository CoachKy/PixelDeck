[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $NativeLibrary,

    [switch] $BuildNative
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))

if ($BuildNative) {
    & (Join-Path $PSScriptRoot 'Build-ParallelRdp.ps1') `
        -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'Pixel64 native bridge compilation failed.'
    }
}

if ($NativeLibrary) {
    $NativeLibrary = (Resolve-Path -LiteralPath $NativeLibrary).Path
}
else {
    $nativeRoot = Join-Path $repositoryRoot 'artifacts/native'
    $NativeLibrary = Get-ChildItem -LiteralPath $nativeRoot -Recurse -File `
            -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @(
                'PixelDeck.ParallelRdp.dll',
                'libPixelDeck.ParallelRdp.so',
                'libPixelDeck.ParallelRdp.dylib')
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $NativeLibrary) {
    throw @'
No PixelDeck paraLLEl-RDP native library was found. Pass -NativeLibrary with
an ABI-2 CI artifact, or install CMake plus a C++ toolchain and use -BuildNative.
'@
}

$oldLibrary = $env:PIXELDECK_PARALLEL_RDP_LIBRARY
$oldCertification = $env:PIXELDECK_CERTIFY_PARALLEL_RDP_MARIO
try {
    $env:PIXELDECK_PARALLEL_RDP_LIBRARY = $NativeLibrary
    $env:PIXELDECK_CERTIFY_PARALLEL_RDP_MARIO = '1'
    Write-Host "Native bridge: $NativeLibrary" -ForegroundColor Cyan
    Write-Host (
        'Capturing and replaying consecutive owned-local Super Mario 64 ' +
        'graphics tasks.') -ForegroundColor Cyan

    Push-Location $repositoryRoot
    try {
        & dotnet test `
            'tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj' `
            -c $Configuration `
            --filter (
                'FullyQualifiedName~' +
                'LocalSuperMario64PreservesNativeCoverageAcrossGraphicsTasks') `
            --logger 'console;verbosity=detailed'
        if ($LASTEXITCODE -ne 0) {
            throw 'Pixel64 Mario native certification failed.'
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PIXELDECK_PARALLEL_RDP_LIBRARY = $oldLibrary
    $env:PIXELDECK_CERTIFY_PARALLEL_RDP_MARIO = $oldCertification
}

Write-Host 'Pixel64 Mario native sequence certification passed.' `
    -ForegroundColor Green
