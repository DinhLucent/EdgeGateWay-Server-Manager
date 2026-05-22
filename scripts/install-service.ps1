param(
    [string]$ServiceName = "EdgeGateway.DeviceService",
    [string]$DisplayName = "EdgeGateway Device Service",
    [string]$Url = "http://127.0.0.1:18082",
    [string]$PublishDirectory = "",
    [string]$ManagerPublishDirectory = "",
    [string]$TrayPublishDirectory = "",
    [bool]$RegisterTrayStartup = $true
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot "output\edge-gateway\service"
}
if ([string]::IsNullOrWhiteSpace($ManagerPublishDirectory)) {
    $ManagerPublishDirectory = Join-Path $repoRoot "output\edge-gateway\manager"
}
if ([string]::IsNullOrWhiteSpace($TrayPublishDirectory)) {
    $TrayPublishDirectory = Join-Path $repoRoot "output\edge-gateway\tray"
}

$projectPath = Join-Path $repoRoot "EdgeGateway.Service\EdgeGateway.Service.csproj"
$managerProjectPath = Join-Path $repoRoot "EdgeGateway.Manager\EdgeGateway.Manager.csproj"
$trayProjectPath = Join-Path $repoRoot "EdgeGateway.Tray\EdgeGateway.Tray.csproj"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $false
if ($existing) {
    Write-Host "Service '$ServiceName' is already installed. Updating service files and configuration."
    if ($existing.Status -ne "Stopped") {
        $wasRunning = $true
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}

try {
    dotnet publish $projectPath -c Release -o $PublishDirectory
    dotnet publish $managerProjectPath -c Release -o $ManagerPublishDirectory
    dotnet publish $trayProjectPath -c Release -o $TrayPublishDirectory
}
catch {
    if ($wasRunning) {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }

    throw
}

$exePath = Join-Path $PublishDirectory "EdgeGateway.Service.exe"
if (-not (Test-Path $exePath)) {
    throw "Published service executable was not found: $exePath"
}
$managerExePath = Join-Path $ManagerPublishDirectory "EdgeGateway.Manager.exe"
if (-not (Test-Path $managerExePath)) {
    throw "Published manager executable was not found: $managerExePath"
}
$trayExePath = Join-Path $TrayPublishDirectory "EdgeGateway.Tray.exe"
if (-not (Test-Path $trayExePath)) {
    throw "Published tray executable was not found: $trayExePath"
}

$programDataRoot = Join-Path $env:ProgramData "EdgeGateway"
$configDirectory = Join-Path $programDataRoot "config"
$databaseDirectory = Join-Path $programDataRoot "database"
New-Item -ItemType Directory -Force -Path $configDirectory, $databaseDirectory | Out-Null

$devicesConfigPath = Join-Path $configDirectory "devices.json"
if (-not (Test-Path $devicesConfigPath)) {
    Copy-Item (Join-Path $repoRoot "config\devices.json") $devicesConfigPath
}

$managerConfigPath = Join-Path $configDirectory "service-manager.json"
if (-not (Test-Path $managerConfigPath)) {
    $uri = [Uri]$Url
    $httpPort = if ($uri.Scheme -eq "http" -and -not $uri.IsDefaultPort) { $uri.Port } else { 18082 }
    $httpsPort = if ($uri.Scheme -eq "https" -and -not $uri.IsDefaultPort) { $uri.Port } else { 0 }
    $databasePath = Join-Path $databaseDirectory "edgegateway.db"
    @{
        server = @{
            httpPort = $httpPort
            httpsPort = $httpsPort
            mainLogLevel = "Information"
            channelLogLevel = "Information"
        }
        database = @{
            type = "SQLite"
            database = $databasePath
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $managerConfigPath -Encoding UTF8
}

$binaryPath = "`"$exePath`""
if ($existing) {
    sc.exe config $ServiceName binPath= $binaryPath start= delayed-auto DisplayName= $DisplayName | Out-Null
}
else {
    New-Service `
        -Name $ServiceName `
        -BinaryPathName $binaryPath `
        -DisplayName $DisplayName `
        -Description "LabInfoSys analyzer device communication gateway." `
        -StartupType Automatic | Out-Null
    sc.exe config $ServiceName start= delayed-auto | Out-Null
}

sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

if ($RegisterTrayStartup) {
    $startupDirectory = [Environment]::GetFolderPath("Startup")
    New-Item -ItemType Directory -Force -Path $startupDirectory | Out-Null
    $shortcutPath = Join-Path $startupDirectory "EdgeGateway Tray.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $trayExePath
    $shortcut.WorkingDirectory = $TrayPublishDirectory
    $shortcut.IconLocation = "$trayExePath,0"
    $shortcut.Description = "EdgeGateway tray status helper"
    $shortcut.Save()
}

Start-Service -Name $ServiceName
Get-Service -Name $ServiceName
