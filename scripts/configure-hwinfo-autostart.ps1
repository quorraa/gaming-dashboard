[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [switch]$RestartIfRunning
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-HwInfoPath {
    param([string]$ConfiguredPath)

    if ($ConfiguredPath -and (Test-Path -LiteralPath $ConfiguredPath)) {
        return $ConfiguredPath
    }

    $candidates = @(
        "C:\Program Files\HWiNFO64\HWiNFO64.EXE",
        "C:\Program Files (x86)\HWiNFO64\HWiNFO64.EXE"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "HWiNFO64.EXE not found. Install HWiNFO first or pass -ExecutablePath."
}

function Ensure-IniValue {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [int]$SettingsIndex,
        [string]$Key,
        [string]$Value
    )

    $nextSectionIndex = -1
    for ($i = $SettingsIndex + 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].StartsWith("[") -and $Lines[$i].EndsWith("]")) {
            $nextSectionIndex = $i
            break
        }
    }

    $searchEnd = if ($nextSectionIndex -ge 0) { $nextSectionIndex } else { $Lines.Count }
    for ($i = $SettingsIndex + 1; $i -lt $searchEnd; $i++) {
        if ($Lines[$i] -like "$Key=*") {
            $Lines[$i] = "$Key=$Value"
            return
        }
    }

    $Lines.Insert($searchEnd, "$Key=$Value")
}

if (-not (Test-Admin)) {
    $scriptPath = $MyInvocation.MyCommand.Path
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$scriptPath`""
    )

    if ($ExecutablePath) {
        $arguments += @("-ExecutablePath", "`"$ExecutablePath`"")
    }

    if ($RestartIfRunning) {
        $arguments += "-RestartIfRunning"
    }

    Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs | Out-Null
    return
}

$hwInfoPath = Resolve-HwInfoPath -ConfiguredPath $ExecutablePath
$iniPath = [System.IO.Path]::ChangeExtension($hwInfoPath, ".INI")

if (-not (Test-Path -LiteralPath $iniPath)) {
    throw "HWiNFO INI not found at $iniPath"
}

$runningProcesses = @(Get-Process HWiNFO64, HWiNFO32 -ErrorAction SilentlyContinue)
if ($runningProcesses.Count -gt 0) {
    if (-not $RestartIfRunning) {
        throw "HWiNFO is currently running. Re-run with -RestartIfRunning so the updated startup flags take effect cleanly."
    }

    $runningProcesses | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.AddRange([string[]](Get-Content -LiteralPath $iniPath))
$settingsIndex = $lines.FindIndex([Predicate[string]]{
    param($line)
    return $line.Trim().Equals("[Settings]", [System.StringComparison]::OrdinalIgnoreCase)
})

if ($settingsIndex -lt 0) {
    $lines.Insert(0, "[Settings]")
    $settingsIndex = 0
}

Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "SensorsOnly" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "OpenSensors" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "ServerRole" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "SensorsSM" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "MinimalizeSensors" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "MinimalizeMainWnd" -Value "1"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "ShowWelcomeAndProgress" -Value "0"
Ensure-IniValue -Lines $lines -SettingsIndex $settingsIndex -Key "ShowRegDialog" -Value "0"

Set-Content -LiteralPath $iniPath -Value $lines -Encoding ASCII

Write-Host "Updated HWiNFO startup settings in $iniPath"
Write-Host "Applied:"
Write-Host "  SensorsOnly=1"
Write-Host "  OpenSensors=1"
Write-Host "  ServerRole=1"
Write-Host "  SensorsSM=1"
Write-Host "  MinimalizeSensors=1"
Write-Host "  MinimalizeMainWnd=1"
Write-Host "  ShowWelcomeAndProgress=0"
Write-Host "  ShowRegDialog=0"

if ($RestartIfRunning) {
    Start-Process -FilePath $hwInfoPath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($hwInfoPath)) -WindowStyle Minimized | Out-Null
    Write-Host "Restarted HWiNFO."
}
