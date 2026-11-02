# Builds the overlay into dist\WshOverlay and zips it as dist\WshOverlay-<version>.zip.
# Needs only the .NET 10 SDK; nothing else is downloaded.
# Usage:  .\build.ps1            (release build + zip)
#         .\build.ps1 -NoZip     (folder only)
# Settings and logs next to an existing build (ui_config.json, aim_config.json, logs\) are kept.
param([switch]$NoZip)
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\WshOverlay\WshOverlay.csproj'
$dist    = Join-Path $PSScriptRoot 'dist'
$out     = Join-Path $dist 'WshOverlay'
$userFiles = 'ui_config.json', 'aim_config.json', 'logs'

# Replace the previous build output only.
if (Test-Path $out) { Get-ChildItem $out | Where-Object { $userFiles -notcontains $_.Name } | Remove-Item -Recurse -Force }
dotnet publish $project -c Release -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'README.md') $out
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') $out

if (-not $NoZip) {
    $version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $zip = Join-Path $dist "WshOverlay-$version.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    $files = Get-ChildItem $out | Where-Object { $userFiles -notcontains $_.Name }
    Compress-Archive -Path $files.FullName -DestinationPath $zip
    Write-Host "Packaged $zip"
}
Write-Host "Built $out"
