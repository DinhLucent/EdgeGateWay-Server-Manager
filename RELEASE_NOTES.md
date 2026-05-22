# EdgeGateway v0.1.0

Initial private release for EdgeGateway Server Manager.

## Package

- `EdgeGatewaySetup-0.1.0.exe`
- Lightweight Windows x64 installer.
- Size: 3.14 MB.
- SHA256: `54CA45BD714ACFDAEC50FCC98BA404782C1ED6DA6C95C5CD64F4B0A532CCDB96`.
- Installs `EdgeGateway.DeviceService`, Server Manager, Device Manager, and Tray.
- Stores mutable config, SQLite database, logs, spool, orders, packets, and
  security files under `C:\ProgramData\EdgeGateway`.

## Runtime Prerequisites

The installer checks for Microsoft .NET 10 x64 runtime components:

- .NET Desktop Runtime for Server Manager, Device Manager, and Tray.
- ASP.NET Core Runtime for the Windows Service.

If a runtime is missing, setup asks to download and install it from Microsoft.

## Operational Notes

- Windows Service startup type: Automatic Delayed Start.
- Service recovery: restart on failure.
- Uninstall removes the Windows Service and installed app files, while keeping
  `C:\ProgramData\EdgeGateway` for support and data retention.

## Verification

- `dotnet build` passed.
- `dotnet test` passed: 128 tests.
- `dotnet format --verify-no-changes` passed.
