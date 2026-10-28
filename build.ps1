# Builds OpenSweep and copies the single-file exe to .\dist
# Usage: .\build.ps1 [-Version 1.0.1]
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\OpenSweep'

$icon = Join-Path $proj 'Assets\OpenSweep.ico'
if (-not (Test-Path $icon)) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1') }

$buildArgs = @($proj, '-c', 'Release', '-nologo')
if ($Version) { $buildArgs += "-p:Version=$Version" }
dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $proj 'bin\Release\net48\OpenSweep.exe') $dist -Force
Write-Host "Done: $dist\OpenSweep.exe"
