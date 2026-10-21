#requires -Version 7.0
<#
  Build script for SB Mod Manager (WinUI 3 / .NET 8).

  - Keeps NuGet packages in a repo-local offline feed (fetch_nuget.py) so the
    build does not depend on the machine-wide NuGet cache.
  - Works around a MakePri.exe access-denied issue by using the Windows SDK copy.
#>
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\SBModManager\SBModManager.csproj"
$outDir = Join-Path $root "dist\SBModManager"

function Find-MakePri {
    $kits = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path $kits)) { return $null }
    return Get-ChildItem $kits -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\makepri.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

Push-Location $root
try {
    if (-not (Test-Path "_nuget\feed\microsoft.windowsappsdk.1.8.260921001.nupkg")) {
        Write-Host "Fetching NuGet packages into _nuget\feed ..."
        python tools\fetch_nuget.py Microsoft.WindowsAppSDK:1.8.260921001 Microsoft.Windows.SDK.NET.Ref:10.0.19041.57 System.Numerics.Tensors:9.0.0 SharpCompress:0.50.7
        if ($LASTEXITCODE -ne 0) { throw "package fetch failed" }
    }

    $makePri = Find-MakePri
    Write-Host "MakePri: $makePri"

    $buildArgs = @(
        "build", $project,
        "-c", $Configuration,
        "-p:Platform=$Platform",
        "-p:WindowsPackageType=None",
        "-p:MSBuildNodeReuse=false",
        "-v:m"
    )
    if ($makePri) { $buildArgs += "-p:MakePriExeFullPath=$makePri" }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $binDir = Join-Path $root "src\SBModManager\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\win-x64"
    if (-not (Test-Path $binDir)) { throw "build output not found: $binDir" }

    # Preserve a portable data folder next to the exe across rebuilds.
    $portableData = Join-Path $outDir "SBMM-Data"
    $stash = Join-Path $env:TEMP ("sbmm-dist-data-" + [guid]::NewGuid().ToString("N"))
    $hasData = (Test-Path $portableData) -and (-not $NoClean)
    if ($hasData) { Move-Item $portableData $stash -Force }

    if (-not $NoClean -and (Test-Path $outDir)) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Copy-Item (Join-Path $binDir "*") $outDir -Recurse -Force -Exclude "SBMM-Data"
    # Never ship a data folder that leaked into the build output directory.
    if (Test-Path (Join-Path $outDir "SBMM-Data")) { Remove-Item (Join-Path $outDir "SBMM-Data") -Recurse -Force }
    # Drop screenshot artifacts accidentally written next to the exe during UI tests.
    foreach ($stray in @("navcol.png", "shot.png", "screenshot.png")) {
        $path = Join-Path $outDir $stray
        if (Test-Path $path) { Remove-Item $path -Force }
    }
    if ($hasData) { Move-Item $stash $portableData -Force }

    # Drop stale empty language folders left behind by earlier self-contained builds.
    Get-ChildItem $outDir -Directory -Recurse |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem $_.FullName -Force | Select-Object -First 1)) {
                Remove-Item $_.FullName -Force
            }
        }

    $exe = Join-Path $outDir "SBModManager.exe"
    Write-Host ""
    Write-Host "Build OK: $exe"
    Write-Host ("Size: {0:N0} MB" -f ((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum / 1MB))
}
finally {
    Pop-Location
}
