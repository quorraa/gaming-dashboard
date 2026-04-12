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
$zipPath = Join-Path $OutputRoot "gaming-dashboard-$Runtime-$Configuration.zip"

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

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $publishDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

Write-Host "Publishing self-contained package to $publishDir"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($localOnlyFile in @("dashboard.user.json", "appsettings.Development.json")) {
    $candidate = Join-Path $publishDir $localOnlyFile
    if (Test-Path $candidate) {
        Remove-Item $candidate -Force
    }
}

$portableReadme = @"
Gaming Dashboard portable package
================================

Run:
  .\Monitor.Server.exe

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

Set-Content -Path (Join-Path $publishDir "PORTABLE.txt") -Value $portableReadme -Encoding ASCII

if (-not $NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Host "Created archive: $zipPath"
}

Write-Host "Portable package ready:"
Write-Host "  $publishDir"
