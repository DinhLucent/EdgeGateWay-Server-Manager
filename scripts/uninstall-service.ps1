[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [string]$ServiceName = "EdgeGateway.DeviceService"
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

if (-not $PSCmdlet.ShouldProcess($ServiceName, "Stop and delete the Windows service registration")) {
    Write-Host "Uninstall cancelled."
    return
}

Write-Host "Removing service registration only. ProgramData config, database, and logs are kept."

if ($existing.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

sc.exe delete $ServiceName | Out-Null
Write-Host "Service '$ServiceName' has been removed."
