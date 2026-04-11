[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]$Port = 5103,
    [switch]$SkipHwInfo,
    [switch]$SkipFirewall
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Install-HwInfo {
    if ($SkipHwInfo) {
        Write-Host "Skipping HWiNFO install."
        return
    }

    $existing = Get-Command HWiNFO64.exe -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "HWiNFO already appears to be installed."
        return
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "winget is required to install HWiNFO automatically. Install App Installer from Microsoft Store or install HWiNFO manually."
    }

    $installed = & $winget.Path list --id REALiX.HWiNFO --exact --accept-source-agreements 2>$null
    if ($LASTEXITCODE -eq 0 -and ($installed | Out-String) -match "REALiX\.HWiNFO") {
        Write-Host "HWiNFO is already installed."
        return
    }

    if ($PSCmdlet.ShouldProcess("REALiX.HWiNFO", "Install HWiNFO")) {
        & $winget.Path install --id REALiX.HWiNFO --exact --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget failed to install HWiNFO."
        }
    }

    Write-Host "HWiNFO install step completed."
    Write-Host "Open HWiNFO once and enable Shared Memory Support for temperature telemetry."
}

function Ensure-FirewallRule {
    if ($SkipFirewall) {
        Write-Host "Skipping firewall rule."
        return
    }

    $ruleName = "GamingDashboard-$Port"
    $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existingRule) {
        Write-Host "Firewall rule already exists: $ruleName"
        return
    }

    if (-not (Test-Admin)) {
        throw "Administrator privileges are required to create the Windows Firewall rule."
    }

    if ($PSCmdlet.ShouldProcess("Windows Firewall", "Create inbound TCP rule for port $Port")) {
        New-NetFirewallRule `
            -DisplayName $ruleName `
            -Direction Inbound `
            -Action Allow `
            -Protocol TCP `
            -LocalPort $Port `
            -Profile Private | Out-Null
    }

    Write-Host "Firewall rule ensured for TCP port $Port."
}

Install-HwInfo
Ensure-FirewallRule

Write-Host ""
Write-Host "Prerequisite setup complete."
Write-Host "Next:"
Write-Host "  1. Start HWiNFO and enable Shared Memory Support."
Write-Host "  2. Run Monitor.Server.exe."
Write-Host "  3. Open http://<pc-lan-ip>:$Port from your LAN device."
