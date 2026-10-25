# Builds dist\Kemptly-Setup.msi (self-contained - friends don't need .NET installed).
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1 [-Version 1.3.1]
param([string]$Version = "1.3.1")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "1/3 Publishing self-contained Kemptly.exe..." -ForegroundColor Cyan
dotnet publish Kemptly\Kemptly.csproj -c Release -o build\selfcontained --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -nologo
if ($LASTEXITCODE) { throw "publish failed" }

Write-Host "2/3 Generating installer artwork..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File Installer\make-art.ps1

Write-Host "3/3 Building MSI..." -ForegroundColor Cyan
# WiX's cabinet compressor fails on long paths (error 0x57), so build from a short staging folder.
$stage = Join-Path $env:TEMP "kemptly-msi"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\Installer", "$stage\Kemptly\Assets", "$stage\build\selfcontained" | Out-Null
Copy-Item Installer\* "$stage\Installer" -Recurse -Exclude bin, obj
Copy-Item Kemptly\Assets\Kemptly.ico, Kemptly\Assets\logo.png "$stage\Kemptly\Assets"
Copy-Item build\selfcontained\Kemptly.exe "$stage\build\selfcontained"
dotnet build "$stage\Installer\Kemptly.Installer.wixproj" -c Release -nologo -o "$stage\dist" -p:ProductVersion=$Version
if ($LASTEXITCODE) { throw "MSI build failed" }
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item "$stage\dist\*.msi" dist -Force

Get-ChildItem dist\*.msi | ForEach-Object { "Done: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
