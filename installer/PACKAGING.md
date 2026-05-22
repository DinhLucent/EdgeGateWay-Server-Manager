# EdgeGateway Packaging Workflow

This document is the repeatable packaging procedure for EdgeGateway Windows
releases.

## Product Shape

The installer packages the EdgeGateway suite as separate runtime and management
parts:

- `EdgeGateway.DeviceService`: Windows Service runtime.
- `EdgeGateway Server Manager`: local infrastructure/service manager.
- `EdgeGateway Device Manager`: analyzer/device configuration UI.
- `EdgeGateway Tray`: status and quick shortcuts.

The installer must not store mutable runtime data inside `Program Files`.
Configuration, database, logs, spool, orders, packets, and security files stay
under:

```text
C:\ProgramData\EdgeGateway
```

## Build Machine Requirements

Use a Windows x64 build machine with:

- .NET SDK matching the project target framework.
- Inno Setup 6, including `ISCC.exe`.
- Internet access for NuGet restore when dependencies are not already cached.

The current build script looks for `ISCC.exe` in `PATH`, then in:

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

## Default Package: Lightweight Installer

Use this for normal production distribution:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0
```

The lightweight package is framework-dependent. It contains EdgeGateway files
only, so it stays small. During setup it checks for:

- `.NET Desktop Runtime` x64 for Server Manager, Device Manager, and Tray.
- `ASP.NET Core Runtime` x64 for the Windows Service.

If either runtime is missing, setup asks the user to download and install the
missing Microsoft runtime component, then continues.

Expected output:

```text
output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe
```

## Runtime Version Pin

The prerequisite runtime version is controlled by `-PrerequisiteVersion`:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0 -PrerequisiteVersion 10.0.8
```

Update this value when the project target/runtime baseline changes. Keep the
Inno script and release notes aligned with the chosen runtime version.

## Offline Package: Self-Contained Installer

Use self-contained only when the target site cannot download or preinstall .NET
runtimes:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0 -SelfContained
```

Expected output:

```text
output\edge-gateway\installer\EdgeGatewaySetup-0.1.0-self-contained.exe
```

This package is much larger because it embeds .NET runtime files.

## What Setup Does

Install:

- Installs app files under `Program Files\LabInfoSys\EdgeGateway`.
- Creates or updates `EdgeGateway.DeviceService`.
- Configures the service as Automatic Delayed Start.
- Configures service recovery to restart after failures.
- Seeds default ProgramData config only when config does not already exist.
- Creates Start Menu shortcuts for Server Manager, Device Manager, Tray, and logs.
- Optionally creates desktop shortcuts.
- Optionally creates a common Startup shortcut for Tray.
- Starts the Windows Service after install.

Update:

- Stops EdgeGateway Manager and Tray if they are running.
- Stops the Windows Service.
- Replaces app files in `Program Files`.
- Repoints the service binary path to the installed service executable.
- Preserves existing ProgramData config, database, and logs.
- Starts the Windows Service again.

Uninstall:

- Stops Tray, Manager, and the Windows Service.
- Deletes `EdgeGateway.DeviceService`.
- Removes installed app files and shortcuts.
- Keeps `C:\ProgramData\EdgeGateway` for diagnostics and data retention.

## Verification Before Release

Run from the repository root:

```powershell
dotnet restore .\EdgeGateway.slnx
dotnet build .\EdgeGateway.slnx --no-restore
dotnet test .\EdgeGateway.Tests\EdgeGateway.Tests.csproj --no-build
dotnet format .\EdgeGateway.slnx --verify-no-changes --no-restore --verbosity minimal
```

Verify the installer file:

```powershell
Get-Item .\output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe |
    Select-Object FullName, Length, LastWriteTime

Get-FileHash .\output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe -Algorithm SHA256
```

Record the file size and SHA256 in the release note.

## Install Smoke Test

Run the installer with Administrator rights on a clean or disposable Windows
machine.

Interactive install:

```powershell
Start-Process .\output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe -Verb RunAs
```

Silent install:

```powershell
Start-Process .\output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe `
    -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="%TEMP%\EdgeGatewaySetup.log"' `
    -Verb RunAs `
    -Wait
```

After install, check:

```powershell
sc.exe query EdgeGateway.DeviceService
sc.exe qc EdgeGateway.DeviceService
Invoke-RestMethod http://127.0.0.1:18082/api/v1/health
```

Expected:

- Service exists.
- Service state is `RUNNING`.
- Startup type is delayed automatic.
- Health endpoint returns `serviceStatus = Running`.
- Server Manager opens.
- Device Manager opens.
- Tray starts or can be started from Start Menu.
- Logs folder opens from Start Menu or Server Manager.

## Update Smoke Test

Install an older build first, then run the newer installer with the same AppId
and service name.

Check:

- Existing ProgramData config remains.
- Existing SQLite database remains.
- Existing logs remain.
- Service binary path points to `Program Files\LabInfoSys\EdgeGateway\service`.
- Service restarts successfully after setup.
- Device Manager still sees existing devices.

## Uninstall Smoke Test

Uninstall from Windows Apps, or run the uninstaller under the install directory.

Check:

```powershell
sc.exe query EdgeGateway.DeviceService
Test-Path 'C:\ProgramData\EdgeGateway'
```

Expected:

- `sc.exe query` reports the service does not exist.
- `C:\ProgramData\EdgeGateway` still exists.

## Release Checklist

Before sharing a package:

- Version number is correct.
- Lightweight installer builds successfully.
- Self-contained installer is built only if the site needs offline install.
- Runtime prerequisite version is correct.
- Build, tests, and format verification pass.
- Installer size and SHA256 are recorded.
- Install smoke test passes.
- Update smoke test passes when this is not the first release.
- Uninstall smoke test confirms ProgramData retention.
- Release note states whether this is lightweight or self-contained.

## Troubleshooting

`ISCC.exe was not found`:

- Install Inno Setup 6.
- Add the Inno Setup directory to `PATH`, or keep the default install path.

Setup cannot start the service:

- Open EdgeGateway Server Manager.
- Check Windows Services.
- Check `C:\ProgramData\EdgeGateway\logs`.
- Confirm port `18082` is not already used.

Target machine cannot download .NET:

- Preinstall the required .NET runtimes manually, or build with `-SelfContained`.

Runtime already installed but setup still downloads it:

- Confirm x64 runtime is installed under `C:\Program Files\dotnet\shared`.
- Confirm the installed major version matches the project target.

Service runs from the wrong path after update:

- Run the installer again as Administrator.
- Check `sc.exe qc EdgeGateway.DeviceService`.
- The binary path should point to the installed `Program Files` service folder.
