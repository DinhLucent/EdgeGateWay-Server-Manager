param(
    [string]$Version = "0.1.0",
    [string]$Runtime = "win-x64",
    [string]$PrerequisiteVersion = "10.0.8",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$edgeGatewayRoot = $repoRoot
$stageRoot = Join-Path $repoRoot "output\edge-gateway\installer\stage"
$installerOutput = Join-Path $repoRoot "output\edge-gateway\installer"
$innoScript = Join-Path $edgeGatewayRoot "installer\inno\EdgeGateway.iss"

if (Test-Path $stageRoot) {
    Remove-Item $stageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path `
    (Join-Path $stageRoot "service"), `
    (Join-Path $stageRoot "manager"), `
    (Join-Path $stageRoot "tray"), `
    (Join-Path $stageRoot "defaults"), `
    $installerOutput | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
$publishCommon = @(
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $selfContainedValue,
    "/p:PublishSingleFile=false"
)

dotnet publish `
    (Join-Path $edgeGatewayRoot "EdgeGateway.Service\EdgeGateway.Service.csproj") `
    @publishCommon `
    -o (Join-Path $stageRoot "service")

dotnet publish `
    (Join-Path $edgeGatewayRoot "EdgeGateway.Manager\EdgeGateway.Manager.csproj") `
    @publishCommon `
    -o (Join-Path $stageRoot "manager")

dotnet publish `
    (Join-Path $edgeGatewayRoot "EdgeGateway.Tray\EdgeGateway.Tray.csproj") `
    @publishCommon `
    -o (Join-Path $stageRoot "tray")

Copy-Item `
    (Join-Path $edgeGatewayRoot "config\devices.json") `
    (Join-Path $stageRoot "defaults\devices.json") `
    -Force

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidate = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) {
        $iscc = [pscustomobject]@{ Source = $candidate }
    }
}

if (-not $iscc) {
    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6, then rerun this script."
}

$outputBaseFilename = if ($SelfContained) {
    "EdgeGatewaySetup-$Version-self-contained"
} else {
    "EdgeGatewaySetup-$Version"
}

& $iscc.Source `
    "/DAppVersion=$Version" `
    "/DStageDir=$stageRoot" `
    "/DOutputDir=$installerOutput" `
    "/DOutputBaseFilename=$outputBaseFilename" `
    "/DSelfContained=$selfContainedValue" `
    "/DPrerequisiteVersion=$PrerequisiteVersion" `
    $innoScript

$setupPath = Join-Path $installerOutput "$outputBaseFilename.exe"
if (-not (Test-Path $setupPath)) {
    throw "Installer build finished without producing expected setup file: $setupPath"
}

Get-Item $setupPath
