#requires -version 5.1
<#
    Builds and packages AutoShazam into Velopack release assets (installer + nupkg + releases feed)
    in .\Releases. Run this locally to test packaging before pushing a release tag (which runs the
    same steps via .github\workflows\release.yml and uploads the result to GitHub Releases).
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "AutoShazam\AutoShazam.csproj"
$publishDir = Join-Path $root "publish"
$releasesDir = Join-Path $root "Releases"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Publishing $project ($Configuration, $Runtime)..." -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Ensuring vpk CLI is installed..." -ForegroundColor Cyan
dotnet tool install --global vpk 2>$null | Out-Null

Write-Host "Packing version $Version..." -ForegroundColor Cyan
vpk pack `
    --packId AutoShazam `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe AutoShazam.exe `
    --packTitle "AutoShazam" `
    --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "Done. Packages are in $releasesDir" -ForegroundColor Green
