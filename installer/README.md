# EdgeGateway Installer

Full repeatable release procedure: [PACKAGING.md](PACKAGING.md).

Build the Windows installer from the repository root:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0
```

The default build is a lightweight `win-x64` framework-dependent installer. It
contains only EdgeGateway files and checks for the required Microsoft .NET 10 x64
runtimes during setup:

- .NET Desktop Runtime, used by Server Manager, Device Manager, and Tray.
- ASP.NET Core Runtime, used by the background Windows Service.

If a runtime is missing, setup asks to download and install it from Microsoft,
then continues. The output is:

```text
output\edge-gateway\installer\EdgeGatewaySetup-0.1.0.exe
```

The installer:

- installs app files under `Program Files\LabInfoSys\EdgeGateway`;
- installs `EdgeGateway.DeviceService` as Automatic Delayed Start;
- keeps config, logs, database, spool, orders, packets, and security files under
  `C:\ProgramData\EdgeGateway`;
- seeds default config only when ProgramData config does not already exist;
- creates Start Menu shortcuts for Server Manager, Device Manager, and Tray;
- optionally creates desktop shortcuts;
- optionally starts Tray at Windows login;
- starts the Windows Service after install;
- removes the Windows Service on uninstall, while keeping ProgramData data.

Use self-contained only for offline environments where runtime download is not
allowed. This package is much larger because it embeds the .NET runtime:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0 -SelfContained
```

To update the runtime download version used by the lightweight installer:

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0 -PrerequisiteVersion 10.0.8
```
