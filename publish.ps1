# Готовая сборка WinTV: self-contained win-x64 (не требует установленного .NET),
# только 64-битная LibVLC. Результат — publish\WinTV и publish\WinTV-<версия>-win-x64.zip.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish\WinTV'
$project = Join-Path $root 'WinTV\WinTV.csproj'

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '0.0.0' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $out `
    -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -p:DebugType=none -p:SatelliteResourceLanguages=ru
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

$zip = Join-Path $root "publish\WinTV-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal

$size = '{0:N1}' -f ((Get-Item $zip).Length / 1MB)
Write-Host "Готово: $zip ($size МБ)"
