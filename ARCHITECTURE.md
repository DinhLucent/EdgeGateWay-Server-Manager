# EdgeGateway Architecture Map

This package is intentionally kept as a small device-edge layer. The stable flow is:

```text
Capture -> Journal / Probe -> Output
```

EdgeGateway should not perform LIS Core work, clinical interpretation, result mapping, billing, signout, or Mirth channel lifecycle management.

## Layers

1. **Core Contracts & Config**
   - Owns `DeviceProfile`, config loading, validation, and health summaries.
   - Operator-editable config must remain simple JSON and must validate before save.
   - Hand-editable config uses parent-child `DeviceConfigurationDocument` settings:
     `identity -> lifecycle -> capture -> protocol -> output -> observability`.
   - Runtime receives normalized `DeviceProfile` objects after mapping, keeping transport modules independent from config editor layout.

2. **Capture Transports**
   - Owns device byte capture over TCP server/client, RS232 serial COM ports, and folder watcher.
   - Transport code should know how to connect, disconnect, read bytes, send protocol ACK/NAK bytes where needed, and report status.

3. **Protocol Sessions**
   - Owns Raw, ASTM, and HL7 MLLP framing/session behavior.
   - Protocol code emits framed message events; it must not transform clinical/business data.

4. **Journal, Probe & Output**
   - Owns metadata-first local durability, categorized communication logs, bounded diagnostic/probe logs, and output retry.
   - Raw payloads are saved for device survival/debugging, but APIs and UI must not expose raw payload by default.

5. **Runtime Orchestration**
   - Owns device lifecycle, runtime state, transport composition, status transitions, and event fan-out.
   - One device failure must not take down the whole runtime.

6. **Service API Host**
   - Owns the .NET service-style host, local API v1, config save helpers, log-category APIs, logging, and Manager Lite debug surface.
   - API routes are operational/status contracts only, not clinical workflows.

7. **Manager WPF UI**
   - Owns the operator UI tabs: Dashboard, Devices, and Logs.
   - Keeps status monitoring, safe config editing, System/Device/Data logs, exceptions, and captured metadata views separated to avoid long-running UI bottlenecks.
   - New device creation writes a disabled TCP profile to config with backup; runtime reload remains explicit.
   - UI depends on API/client abstractions and should not reach directly into transports or journal files.

8. **Tray Helper**
   - Owns the per-user Windows notification-area icon.
   - Opens the WPF Manager, opens Manager Lite, shows service/runtime status, and calls local API commands for start/stop/restart runtime.
   - Must not host device capture itself and must not be required for the service to keep running.

9. **Verification Tests**
   - Owns executable proof for TCP-first capture, ASTM, MLLP, folder watcher, journal, output retry, API, and manager control contracts.

10. **Configuration & Docs**
   - Owns sample devices, runbook, and architecture notes.

## Key Dependency Rules

- `Tray Helper -> Manager WPF UI / Service API Host`.
- `Manager WPF UI -> Service API Host -> Runtime Orchestration -> Capture / Protocol / Journal / Output`.
- `Capture Transports` can depend on `DeviceProfile` and runtime event contracts, but not on UI.
- `Protocol Sessions` can parse frames and checksums, but not create business-result mappings.
- `Journal, Probe & Output` can persist metadata and forward metadata envelopes, but must keep raw payload exposure guarded.
- `Service API Host` may expose status/config/log/metadata commands only through `/api/v1/`.
- Tests may reference all layers, but production layers must not depend on tests.

## Maintenance

Update this map after structural EdgeGateway changes so implementation work keeps
matching the intended product shape.
