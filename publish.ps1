# Готовая сборка WinTV: self-contained win-x64 (не требует установленного .NET),
# только 64-битная LibVLC. Результат:
#   - publish\WinTV
#   - publish\WinTV-<версия>-win-x64.zip (портативный архив)
#   - publish\WinTV-Setup-<версия>-win-x64.exe (установщик одним файлом)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish\WinTV'
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root 'publish')).TrimEnd('\') + '\'
$setupOut = Join-Path $root 'publish\setup_build'
foreach ($candidate in @($out, $setupOut)) {
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (-not $resolved.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path escapes publish directory: $resolved"
    }
    if ((Test-Path -LiteralPath $resolved) -and ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Output directory must not be a junction or symbolic link: $resolved"
    }
}
$project = Join-Path $root 'WinTV\WinTV.csproj'
$setupProject = Join-Path $root 'Setup\WinTV.Setup.csproj'

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = '1.0.0' }

if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $out `
    -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -p:DebugType=none -p:SatelliteResourceLanguages=ru
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

$zip = Join-Path $root "publish\WinTV-$version-win-x64.zip"
$payloadZip = Join-Path $root "publish\payload.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
if (Test-Path $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }

Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $zip $payloadZip -Force

$setupOut = Join-Path $root 'publish\setup_build'
if (Test-Path $setupOut) { Remove-Item -LiteralPath $setupOut -Recurse -Force }
dotnet publish $setupProject -c Release -r win-x64 --self-contained true -o $setupOut `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$version -p:DebugType=none -p:SatelliteResourceLanguages=ru
if ($LASTEXITCODE -ne 0) { throw "Сборка установщика завершилась с кодом $LASTEXITCODE" }

$setupExe = Join-Path $root "publish\WinTV-Setup-$version-win-x64.exe"
Copy-Item (Join-Path $setupOut 'WinTV-Setup.exe') $setupExe -Force
Remove-Item -LiteralPath $setupOut -Recurse -Force
Remove-Item -LiteralPath $payloadZip -Force

$zipSize = '{0:N1}' -f ((Get-Item $zip).Length / 1MB)
$exeSize = '{0:N1}' -f ((Get-Item $setupExe).Length / 1MB)
Write-Host "Готово:"
Write-Host "  Архив:      $zip ($zipSize МБ)"
Write-Host "  Установщик: $setupExe ($exeSize МБ)"
