[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}
$projectPath = Join-Path $repoRoot "src\\Monitor.Server\\Monitor.Server.csproj"
$publishRoot = Join-Path $OutputRoot "publish"
$publishDir = Join-Path $publishRoot "$Runtime-$Configuration"
$stagingDir = Join-Path $publishRoot "$Runtime-$Configuration-staging-$PID"
$appDir = Join-Path $stagingDir "app"
$zipPath = Join-Path $OutputRoot "gaming-dashboard-$Runtime-$Configuration.zip"

function Stop-PackagedMonitorServer {
    param(
        [string]$PublishRootPath
    )

    $normalizedPublishRoot = [System.IO.Path]::GetFullPath($PublishRootPath).TrimEnd('\')
    $packagedProcesses = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -eq "Monitor.Server.exe" -and
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            ([System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($normalizedPublishRoot, [System.StringComparison]::OrdinalIgnoreCase))
        }

    if (-not $packagedProcesses) {
        return
    }

    Write-Host "Stopping packaged Monitor.Server instances under $normalizedPublishRoot"
    $packagedProcesses | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
    }
    Start-Sleep -Seconds 1
}

Write-Host "Building Studio frontend"
Push-Location $repoRoot
try {
    & npm run build:studio
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build:studio failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$publishArgs = @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $appDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

Write-Host "Publishing self-contained package to $appDir"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($localOnlyFile in @("dashboard.user.json", "appsettings.Development.json")) {
    $candidate = Join-Path $appDir $localOnlyFile
    if (Test-Path $candidate) {
        Remove-Item $candidate -Force
    }
}

$launcherCmd = @"
@echo off
setlocal
set "APP_DIR=%~dp0app"
if not exist "%APP_DIR%\Monitor.Server.exe" (
  echo Monitor.Server.exe was not found in "%APP_DIR%".
  pause
  exit /b 1
)
pushd "%APP_DIR%"
start "" "%APP_DIR%\Monitor.Server.exe"
popd
"@

Set-Content -Path (Join-Path $stagingDir "Launch Gaming Dashboard.cmd") -Value $launcherCmd -Encoding ASCII

$launcherPs1 = @"
$ErrorActionPreference = 'Stop'
$appDir = Join-Path $PSScriptRoot 'app'
$exePath = Join-Path $appDir 'Monitor.Server.exe'
if (-not (Test-Path $exePath)) {
    throw "Monitor.Server.exe was not found in '$appDir'."
}
Start-Process -FilePath $exePath -WorkingDirectory $appDir
"@

Set-Content -Path (Join-Path $stagingDir "Launch Gaming Dashboard.ps1") -Value $launcherPs1 -Encoding ASCII

$portableReadme = @"
Gaming Dashboard portable package
================================

Run:
  .\Launch Gaming Dashboard.cmd

Layout:
  .\Launch Gaming Dashboard.cmd
  .\Launch Gaming Dashboard.ps1
  .\app\Monitor.Server.exe
  .\app\wwwroot\...

Recommended first-run steps:
  1. Run ..\scripts\install-prereqs.ps1 on the target machine.
  2. Start HWiNFO and enable Shared Memory Support if you want temperature data.
  3. Open http://<pc-lan-ip>:5103 from your tablet/phone.

Notes:
  - This package is self-contained and does not require a separate .NET runtime.
  - Local-only files like dashboard.user.json are intentionally excluded from the package.
  - Settings persist in %LocalAppData%\GamingDashboard\dashboard.user.json after first run.
  - Studio is available at /studio and is the promoted Svelte client.
  - Studio Legacy remains available at /studio-legacy and Vanilla remains available at /vanilla.
  - Studio Next remains available at /studio-next for direct testing/debugging.
"@

Set-Content -Path (Join-Path $stagingDir "PORTABLE.txt") -Value $portableReadme -Encoding ASCII

Stop-PackagedMonitorServer -PublishRootPath $publishRoot

Get-ChildItem -Path $publishRoot -Directory -Filter "$Runtime-$Configuration-staging-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $stagingDir } |
    ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

if (Test-Path $publishDir) {
    Get-ChildItem -Path $publishDir -Force | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
    }
}
else {
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
}

Get-ChildItem -Path $stagingDir -Force | ForEach-Object {
    Move-Item -Path $_.FullName -Destination $publishDir -Force
}

Remove-Item $stagingDir -Recurse -Force

if (-not $NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Host "Created archive: $zipPath"
}

Write-Host "Portable package ready:"
Write-Host "  $publishDir"
