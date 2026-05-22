# LabInfoSys EdgeGateway

EdgeGateway is the small device acquisition layer for LabInfoSys. It keeps analyzer communication close to the device, stores durable raw metadata, and forwards metadata-only events to a mock or Mirth-compatible HTTP target.

It is intentionally not a Mirth replacement and does not perform clinical mapping, result interpretation, QC rules, signout, billing, or LIS Core workflow.

## Product Suite Shape

EdgeGateway follows a service-centered product shape:

```text
EdgeGateway.Service
  = central background runtime, installed as a Windows Service

EdgeGateway Server Manager
  = local infrastructure console for service status, startup, restart, logs, paths

EdgeGateway Device Manager
  = connection-engineer console for analyzer profiles, connections, queues, replay

EdgeGateway Tray
  = per-user status helper and shortcut launcher

EdgeGateway CLI
  = planned automation entrypoint for install/export/import/support bundles
```

The UI must not be the runtime. Closing Server Manager, Device Manager, or Tray must
not stop device capture. Only `EdgeGateway.Service` owns long-running analyzer work.

## Run The Service

From the repository root:

```powershell
dotnet run --project EdgeGateway.Service\EdgeGateway.Service.csproj --urls http://127.0.0.1:18082
```

The service exposes a local API on the loopback address for Manager/Tray/dev tools.
Operators should not need to see or edit the API URL during normal use.

Default CA270 TCP dev profile listens on `127.0.0.1:9100`.

## Run The Server Manager

The Server Manager is intentionally small. It manages the already-installed
Windows Service named `EdgeGateway.DeviceService`.

```powershell
dotnet run --project EdgeGateway.Manager\EdgeGateway.Manager.csproj
```

It does not own TCP, Serial, Folder Watcher, analyzer sessions, packet parsing, or
device runtimes. It only shows service status, basic ProgramData config, database
test status, service logs, and version/path information.

## Run The Device Manager

The Device Manager is for connection engineers who configure analyzer connections.
It is separate from the Server Manager.

Start the service first, then in another terminal:

```powershell
dotnet run --project EdgeGateway.Manager\EdgeGateway.Manager.csproj -- --device-manager
```

On the first production-style run, the Manager prompts for an initial local `admin`
password. The service stores only a salted PBKDF2 password hash under the configured
EdgeGateway security directory; it does not store the plaintext password. After setup,
operators must log in before changing service, device, config, output replay, or order
settings.

To point the manager at a different local service URL:

```powershell
$env:EDGE_GATEWAY_MANAGER_API_URL = "http://127.0.0.1:18082"
dotnet run --project EdgeGateway.Manager\EdgeGateway.Manager.csproj
```

The Device Manager provides:

- Runtime menu: start runtime, stop runtime, refresh.
- Device menu: connect selected, disconnect selected, connect all, disconnect all.
- Device grid: status indicator, type, profile/protocol, endpoint, mode, output target, packet count, pending count, last message, last error.
- Tabs: Dashboard, Devices, Logs, prepared Orders, packet journal.
- Logs are grouped by operator category: System, Device, and Data, with level and limit filters.
- Output Queue view shows metadata-only pending/retrying/sent/dead-letter output items and lets connection engineers replay failed output.
- Orders are mock/prepared device replies only. They are not LIS Core orders.
- Packets show communication metadata by default. Raw/HEX samples appear only when diagnostic probe is enabled for the selected device.

## Run The Tray Manager

The tray app is a small per-user helper. It is not the background runtime. It sits in the
Windows notification area and talks to the single EdgeGateway service/API.

Start the service first, then run:

```powershell
dotnet run --project EdgeGateway.Tray\EdgeGateway.Tray.csproj
```

Tray behavior, if enabled:

- Double-click the icon to open the Server Manager.
- Context menu: Open Server Manager, Open Device Manager, Refresh Status, Start/Stop/Restart Capture Runtime, Open Logs Folder, Exit Tray.
- `Start/Stop/Restart Capture Runtime` controls the EdgeGateway runtime through `/api/v1/service/*`; it does not install or uninstall a Windows Service.
- Use `EDGE_GATEWAY_MANAGER_API_URL` to point the tray at a different local service URL.
- Use `EDGE_GATEWAY_MANAGER_EXE` if the Manager executable is not beside the tray in a normal build layout.

Recommended production shape:

```text
EdgeGateway.Service      one Windows Service for all device runtimes
EdgeGateway.Manager      Server Manager by default; Device Manager with --device-manager
EdgeGateway.Tray         optional per-user helper, not required for MVP
```

## Operational Design Decision

EdgeGateway is service-first. The device capture runtime belongs in
`EdgeGateway.Service`, registered as one Windows Service named
`EdgeGateway.DeviceService`. It owns long-running analyzer communication, device
auto-connect, local durability, output retry, logs, and the loopback `/api/v1/`
management surface.

`EdgeGateway.Manager` is not the capture runtime. It is the operator and support
console for the already-running service: view service health, inspect device
status, start or stop the capture runtime, connect or disconnect devices, edit safe
configuration fields, replay failed output, and inspect logs. Closing the Manager
must not stop analyzer capture.

The Windows installer in `installer/` ships the service, managers, and tray
together, creates shortcuts, registers the Windows Service, and sets
recovery/startup options without changing the runtime model.

## Windows Service Install

Production should install one `EdgeGateway.DeviceService` Windows Service for the whole
gateway. Do not install one service per analyzer. Each analyzer stays isolated inside
its own device runtime, while Windows only has one service to start, stop, recover,
and monitor.

From an elevated PowerShell session:

```powershell
.\scripts\install-service.ps1
```

The installer script publishes `EdgeGateway.Service`, registers it with delayed automatic
startup, sets Windows recovery actions to restart on failure, publishes the Server Manager
and Tray helper, creates a current-user Startup shortcut for the tray, and binds the local
API to:

```text
http://127.0.0.1:18082
```

To remove the service:

```powershell
.\scripts\uninstall-service.ps1
```

Use installer/service scripts only from Admin/operator setup flows. The Server Manager
does not install or uninstall the service; it only starts, restarts, stops, refreshes,
and inspects an existing Windows Service.

Long-running durability defaults:

- HTTP/Mirth output uses a SQLite outbox at `output/edge-gateway/outbox/output_outbox.db`.
- HTTP/Mirth output is idempotent by `deviceId + targetUrl + payloadHash`, so analyzer
  resend of the same raw payload does not create another outbound post.
- Raw metadata and packet communication journals are partitioned by day.
- File logging defaults to `Information`, rolls by size, and can be tuned with:
- `EDGE_GATEWAY_LOG_MIN_LEVEL`
- `EDGE_GATEWAY_LOG_MAX_FILE_BYTES`
- `EDGE_GATEWAY_OUTPUT_OUTBOX_DB`
- `EDGE_GATEWAY_SECURITY_AUDIT_RETENTION_DAYS`
- `EDGE_GATEWAY_SECURITY_DIR`
- `EDGE_GATEWAY_AUTH_MODE`

Authentication modes:

- `Local` is the default production-style mode. It requires initial admin bootstrap
  and bearer-token login sessions.
- `DevelopmentRoleHeader` is for automated tests/dev only. It trusts
  `X-EdgeGateway-Role` and should not be used for real lab workstations.

## Architecture Map

The human-readable layer map is in:

```text
ARCHITECTURE.md
```

## Config

Hand-editable config lives at:

```text
config/devices.json
```

The file is intentionally a parent-child device settings document:

```text
Device
  identity
  lifecycle
  capture
    tcp | serial | folder
  protocol
    raw | astm | mllp framing
    astm.orderReply
  output
  observability
```

The service still normalizes that document into runtime `DeviceProfile` objects internally,
so transport code does not need to understand UI/config tree details. The local APIs expose
both forms:

- `/api/v1/config/device-settings`: parent-child settings document for config editors.
- `/api/v1/config/devices`: normalized runtime profiles for operational views.

The WPF Manager can edit only safe fields for existing device profiles:

- `enabled`
- `autoConnect`
- `name`
- `transport`
- `protocol`
- `host`
- `port`
- `comPort`
- `baudRate`
- `dataBits`
- `parity`
- `stopBits`
- `flowControl`
- `folder`
- `output.type`
- `output.url`
- `probeEnabled`

Config saves validate before writing and create timestamped backups beside `devices.json`.

Connection changes require runtime restart/reload to apply reliably.

## Protocol Boundary Engine

EdgeGateway is profile-driven at the communication layer. It does not try to
hard-code every analyzer model; a device profile selects transport, boundary
mode, session policy, diagnostics, and output.

For `raw` and `raw_text` protocols, `protocol.raw` can select:

- `raw_chunk`: preserve each transport read as one raw record.
- `delimited_text`: split CR, LF, CRLF, or a custom delimiter.
- `fixed_length`: split records by configured byte count.
- `custom_bytes`: split by configured start/end control bytes such as `<STX>`
  and `<ETX>` or `<VT>` and `<FS><CR>`.

Example nested raw profile:

```json
{
  "protocol": {
    "type": "raw",
    "encoding": "ascii",
    "raw": {
      "mode": "delimited_text",
      "delimiter": "CRLF",
      "maxMessageBytes": 65536
    }
  }
}
```

This boundary layer is shared by TCP and Serial-style streams. It is still only
transport/framing/session support; business mapping and LIS workflow belong in
Mirth/LIS layers.

## Custom Parser Profiles

Parser profiles are optional and disabled by default with `parser.type = none`.
They run after boundary/session handling and before output, so EdgeGateway can
extract lightweight communication data from raw text or report-like messages
without hard-coding analyzer names in transport code.

Implemented parser types in this slice:

- `regex_lines`: labeled text/key-value lines.
- `delimiter_table`: CSV/TSV/pipe/custom delimited tables.
- `fixed_column_report`: printer/report-style fixed-column tables.

Example:

```json
{
  "protocol": {
    "type": "raw",
    "encoding": "ascii",
    "raw": {
      "mode": "delimited_text",
      "delimiter": "CRLF"
    },
    "parser": {
      "type": "regex_lines",
      "profile": "generic/raw-key-value",
      "enabled": true,
      "regexLineRules": [
        {
          "name": "wbc",
          "pattern": "^WBC:(?<value>[0-9.]+)$",
          "testCode": "WBC",
          "unit": "10^9/L"
        }
      ]
    }
  }
}
```

Parser output is an extraction envelope attached to output metadata. It is not
a signed clinical result and does not include the raw body by default.

## RS232 / COM Port Devices

RS232 profiles use the `rs232_serial` transport. The service opens the configured COM port,
reads bytes continuously, applies the selected protocol handler (`raw`, `astm`, or
`hl7_mllp`), writes ASTM ACK/NAK bytes back to the serial stream when needed, saves raw
metadata, and forwards metadata-only output events.

Example profile shape:

```json
{
  "deviceId": "ca270-serial-dev",
  "name": "CA270 Serial Dev",
  "enabled": true,
  "autoConnect": false,
  "transport": "rs232_serial",
  "protocol": "astm",
  "mode": "edge_agent_first",
  "comPort": "COM5",
  "baudRate": 9600,
  "dataBits": 8,
  "parity": "none",
  "stopBits": "one",
  "flowControl": "none",
  "encoding": "ascii",
  "readTimeoutMs": 30000,
  "probeEnabled": false,
  "output": {
    "type": "http",
    "url": "http://localhost:18081/mock-mirth"
  }
}
```

Without a physical or virtual COM pair, automated verification uses a fake serial stream.
Hardware smoke should be done with a real analyzer or virtual COM pair before pilot use.

## Serial ASTM Prepared Orders

The first bidirectional slice is intentionally narrow: Generic ASTM over Serial.
Prepared orders are file-backed mock/test data created from the Manager or API. When
`astm.orderReply.enabled` and `autoReplyMatchedQuery` are true, EdgeGateway detects a
completed ASTM `Q` record, matches the sample key against prepared orders, then sends:

```text
ENQ -> wait ACK -> ASTM H/P/O/L frame -> wait ACK -> EOT
```

Unknown query behavior defaults to `log_only`. Raw sample data is not shown unless
`probeEnabled` is true, and even then packet samples are capped.

Order records are template-driven, not device-code-driven. The default template is a
minimal ASTM H/P/O/L reply, but each device profile can override
`astm.orderReply.template.records` and placeholder rules from the communication manual.
Supported placeholders are `{SAMPLE_ID}`, `{TEST_LIST}`, `{TIMESTAMP}`, and `{HOST}`.

Local APIs:

- `GET /api/v1/devices/{deviceId}/orders`
- `POST /api/v1/devices/{deviceId}/orders`
- `PUT /api/v1/devices/{deviceId}/orders/{orderId}`
- `DELETE /api/v1/devices/{deviceId}/orders/{orderId}`
- `POST /api/v1/devices/{deviceId}/orders/{orderId}/send-test`
- `GET /api/v1/devices/{deviceId}/packets?limit=500`

## Local Output

Default output directories:

- Logs: `output/edge-gateway/logs/`
- Spool metadata and raw payload files: `output/edge-gateway/spool/`
- Prepared mock orders: `output/edge-gateway/orders/`
- Packet communication journal: `output/edge-gateway/packets/`

APIs and UI expose metadata by default: raw id, device id, time, transport, protocol, byte count, hash, status, and last error. Raw payload body is not displayed by default.

For quick bench testing, set device output type to `FileFolder` in the Manager
and enter a local folder path such as:

```text
output\edge-gateway\test-output
```

EdgeGateway writes one JSON metadata envelope per captured raw message. This is
intended for checking whether capture/framing/output works without running a
mock HTTP/Mirth target. The exported file still does not include raw body data.

HTTP/Mirth output has local queue APIs for operations:

- `GET /api/v1/output/summary`
- `GET /api/v1/output/queue?limit=500`
- `GET /api/v1/output/dead-letter?limit=500`
- `POST /api/v1/output/replay-failed`

The output queue API is metadata-only: raw id, device id, target URL, status,
attempt count, last error, timestamps, and payload hash. Terminal HTTP client
errors are marked as `dead_letter` so operators can distinguish target/config
failures from transient retrying output. `replay-failed` only moves failed or
dead-letter HTTP output items back to pending so the worker can send them after
the target/config issue is fixed; it does not expose or edit the raw payload.
Replay attempts are written to the security audit log, including denied attempts
and successful result records with the requeued item count.
Security audit files are retained for 90 days by default and pruned on the next
audit write. Tune this with `EDGE_GATEWAY_SECURITY_AUDIT_RETENTION_DAYS`.

To inspect raw/HEX snippets while debugging, enable diagnostic/probe mode on
the device. Packet samples stay capped and should be disabled again for normal
long-running capture.

## Verification

```powershell
dotnet restore EdgeGateway.slnx
dotnet build EdgeGateway.slnx
dotnet test EdgeGateway.slnx
```

## Soak Test

For long-running CA270-style ASTM TCP soak, start `EdgeGateway.Service` first, then run:

```powershell
.\scripts\soak-ca270-astm-tcp.ps1 -DurationMinutes 60
```

The soak runner repeatedly sends ASTM `ENQ -> frame -> EOT` transactions to the configured
TCP device port and writes metrics to:

```text
output/edge-gateway/soak/
```

Use it to measure:

- output outbox pending/sent behavior while the HTTP/Mirth target is down/up;
- raw metadata and packet journal growth by day;
- log file growth and rolling behavior;
- reconnect behavior if the service or listener is stopped and restarted during the run.

For production gate, run the same pattern for 24-72 hours with CA270/CA400/CA800 simulator
or virtual COM scenarios before connecting a real analyzer.
