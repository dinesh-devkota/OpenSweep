# Builds OpenSweep: dist\OpenSweep.exe (portable) and, if Inno Setup 6 is installed, dist\OpenSweep-Setup.exe
# Usage: .\build.ps1 [-Version 1.1.0] [-NoInstaller]
param([string]$Version, [switch]$NoInstaller)
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

if ($NoInstaller) { return }
$iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { Write-Warning "Inno Setup 6 not found - skipping OpenSweep-Setup.exe (winget install JRSoftware.InnoSetup)"; return }
$appVersion = (Get-Item "$dist\OpenSweep.exe").VersionInfo.ProductVersion
& $iscc /Q "/DAppVersion=$appVersion" (Join-Path $root 'installer\OpenSweep.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Write-Host "Done: $dist\OpenSweep-Setup.exe"
