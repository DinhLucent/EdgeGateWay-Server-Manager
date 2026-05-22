param(
    [int]$MessageCount = 500,
    [int]$DrainTimeoutSeconds = 90,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Get-AstmChecksum {
    param([byte[]]$Bytes)

    $sum = 0
    foreach ($byte in $Bytes) {
        $sum = ($sum + $byte) -band 0xFF
    }

    return "{0:X2}" -f $sum
}

function New-AstmFrame {
    param(
        [int]$FrameNumber,
        [string]$Payload
    )

    $encoding = [Text.Encoding]::ASCII
    $body = "$FrameNumber$Payload" + [char]0x03
    $checksum = Get-AstmChecksum $encoding.GetBytes($body)
    $frameText = [char]0x02 + $body + $checksum + [char]0x0D + [char]0x0A
    return $encoding.GetBytes($frameText)
}

function Read-ControlByte {
    param([Net.Sockets.NetworkStream]$Stream)

    $buffer = New-Object byte[] 1
    $read = $Stream.Read($buffer, 0, 1)
    if ($read -ne 1) {
        throw "Expected one ASTM control byte."
    }

    return $buffer[0]
}

function Wait-HealthReady {
    param([string]$ApiUrl)

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri "$ApiUrl/api/v1/health" -TimeoutSec 3
            if ($health.data.serviceStatus -eq "Running" -and
                $health.data.devices[0].status -eq "Listening") {
                return $health
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 250
    }

    throw "EdgeGateway service did not become ready."
}

function Send-AstmResult {
    param(
        [int]$Port,
        [int]$Sequence
    )

    $client = [Net.Sockets.TcpClient]::new()
    $client.ReceiveTimeout = 3000
    $client.SendTimeout = 3000
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $payload =
            "H|\^&|||CA-270`rP|1`rO|1|FAST-$Sequence||^^^GLU`rR|1|^^^GLU|$((100 + ($Sequence % 20)))|mg/dL`rL|1|N`r"
        $enq = [byte[]](0x05)
        $eot = [byte[]](0x04)
        $frame = New-AstmFrame -FrameNumber 1 -Payload $payload

        $stream.Write($enq, 0, $enq.Length)
        if ((Read-ControlByte $stream) -ne 0x06) {
            throw "ENQ was not ACKed."
        }

        $stream.Write($frame, 0, $frame.Length)
        if ((Read-ControlByte $stream) -ne 0x06) {
            throw "ASTM frame was not ACKed."
        }

        $stream.Write($eot, 0, $eot.Length)
    }
    finally {
        $client.Dispose()
    }
}

function Count-Lines {
    param([string]$Directory, [string]$Filter)

    $files = @(Get-ChildItem $Directory -Filter $Filter -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        return 0
    }

    return ($files | ForEach-Object { (Get-Content $_.FullName).Count } | Measure-Object -Sum).Sum
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $runId = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "output\edge-gateway\high-speed-smoke\$runId"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$apiPort = Get-FreePort
$devicePort = Get-FreePort
$mockPort = Get-FreePort
$apiUrl = "http://127.0.0.1:$apiPort"
$mockCountPath = Join-Path $OutputDirectory "mock-count.jsonl"
$configPath = Join-Path $OutputDirectory "devices.json"
$mockScriptPath = Join-Path $OutputDirectory "mock_mirth.py"
$serviceStdoutPath = Join-Path $OutputDirectory "service.stdout.log"
$serviceStderrPath = Join-Path $OutputDirectory "service.stderr.log"
$result = $null

$configText = Get-Content -Raw (Join-Path $repoRoot "config\devices.json")
$configText = $configText.Replace('"port": 9100', """port"": $devicePort")
$configText = $configText.Replace(
    "http://localhost:18081/mock-mirth",
    "http://127.0.0.1:$mockPort/mock-mirth"
)
Set-Content -Path $configPath -Value $configText -Encoding UTF8

$mockScript = @"
import json
import sys
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port = int(sys.argv[1])
count_path = sys.argv[2]
lock = threading.Lock()

class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        with lock:
            with open(count_path, "a", encoding="utf-8") as handle:
                handle.write(json.dumps({
                    "ts": datetime.now(timezone.utc).isoformat(),
                    "path": self.path,
                    "bytes": len(body),
                }, separators=(",", ":")) + "\n")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"ok":true}')

    def log_message(self, format, *args):
        return

server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
server.serve_forever()
"@
Set-Content -Path $mockScriptPath -Value $mockScript -Encoding UTF8

$mockProcess = $null
$serviceProcess = $null

try {
    $mockProcess = Start-Process `
        -FilePath "python" `
        -ArgumentList @($mockScriptPath, $mockPort, $mockCountPath) `
        -WindowStyle Hidden `
        -PassThru

    $previousDevicesConfig = $env:EDGE_GATEWAY_DEVICES_CONFIG
    $previousLogDir = $env:EDGE_GATEWAY_LOG_DIR
    $previousSpoolDir = $env:EDGE_GATEWAY_SPOOL_DIR
    $previousOrdersDir = $env:EDGE_GATEWAY_ORDERS_DIR
    $previousPacketsDir = $env:EDGE_GATEWAY_PACKETS_DIR
    $previousOutboxDb = $env:EDGE_GATEWAY_OUTPUT_OUTBOX_DB
    $previousLogMinLevel = $env:EDGE_GATEWAY_LOG_MIN_LEVEL
    $previousLogMaxFileBytes = $env:EDGE_GATEWAY_LOG_MAX_FILE_BYTES

    $env:EDGE_GATEWAY_DEVICES_CONFIG = $configPath
    $env:EDGE_GATEWAY_LOG_DIR = Join-Path $OutputDirectory "logs"
    $env:EDGE_GATEWAY_SPOOL_DIR = Join-Path $OutputDirectory "spool"
    $env:EDGE_GATEWAY_ORDERS_DIR = Join-Path $OutputDirectory "orders"
    $env:EDGE_GATEWAY_PACKETS_DIR = Join-Path $OutputDirectory "packets"
    $env:EDGE_GATEWAY_OUTPUT_OUTBOX_DB = Join-Path $OutputDirectory "outbox\output.db"
    $env:EDGE_GATEWAY_LOG_MIN_LEVEL = "Information"
    $env:EDGE_GATEWAY_LOG_MAX_FILE_BYTES = "1048576"

    $serviceProcess = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--no-build",
            "--project",
            "EdgeGateway.Service\EdgeGateway.Service.csproj",
            "--urls",
            $apiUrl
        ) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $serviceStdoutPath `
        -RedirectStandardError $serviceStderrPath `
        -WindowStyle Hidden `
        -PassThru

    Wait-HealthReady -ApiUrl $apiUrl | Out-Null

    $sendFailures = 0
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    for ($index = 1; $index -le $MessageCount; $index++) {
        try {
            Send-AstmResult -Port $devicePort -Sequence $index
        }
        catch {
            $sendFailures++
        }
    }
    $stopwatch.Stop()

    $deadline = (Get-Date).AddSeconds($DrainTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $mockPosts = if (Test-Path $mockCountPath) {
            @((Get-Content $mockCountPath)).Count
        }
        else {
            0
        }
        $payloadFiles = @(Get-ChildItem (Join-Path $OutputDirectory "spool\payloads") -Filter "*.raw" -ErrorAction SilentlyContinue).Count
    } while ((Get-Date) -lt $deadline -and
        ($mockPosts -lt $MessageCount -or $payloadFiles -lt $MessageCount))

    $health = Invoke-RestMethod -Uri "$apiUrl/api/v1/health" -TimeoutSec 5
    $rawMetadataLines = Count-Lines -Directory (Join-Path $OutputDirectory "spool") -Filter "raw_messages-*.jsonl"
    $packetLines = Count-Lines -Directory (Join-Path $OutputDirectory "packets") -Filter "packets-*.jsonl"
    $logBytes = (
        Get-ChildItem (Join-Path $OutputDirectory "logs") -Filter "*.log" -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum
    ).Sum

    $result = [pscustomobject]@{
        runRoot = $OutputDirectory
        requestedMessages = $MessageCount
        sendFailures = $sendFailures
        elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
        messagesPerSecond = [Math]::Round($MessageCount / [Math]::Max($stopwatch.Elapsed.TotalSeconds, 0.001), 2)
        rawPayloadFiles = $payloadFiles
        rawMetadataLines = $rawMetadataLines
        packetJournalLines = $packetLines
        mockHttpPosts = $mockPosts
        serviceStatus = $health.data.serviceStatus
        deviceStatus = $health.data.devices[0].status
        pendingCount = $health.data.devices[0].pendingCount
        healthPacketCount = $health.data.devices[0].packetCount
        lastError = $health.data.devices[0].lastError
        logBytes = $logBytes
        passed = (
            $sendFailures -eq 0 -and
            $payloadFiles -eq $MessageCount -and
            $rawMetadataLines -eq $MessageCount -and
            $mockPosts -eq $MessageCount -and
            $health.data.devices[0].pendingCount -eq 0
        )
    }
}
catch {
    $result = [pscustomobject]@{
        runRoot = $OutputDirectory
        requestedMessages = $MessageCount
        error = $_.Exception.Message
        passed = $false
    }
}
finally {
    if ($serviceProcess -and -not $serviceProcess.HasExited) {
        Stop-Process -Id $serviceProcess.Id -Force -ErrorAction SilentlyContinue
        $serviceProcess.WaitForExit(10000) | Out-Null
    }

    if ($mockProcess -and -not $mockProcess.HasExited) {
        Stop-Process -Id $mockProcess.Id -Force -ErrorAction SilentlyContinue
        $mockProcess.WaitForExit(10000) | Out-Null
    }

    $env:EDGE_GATEWAY_DEVICES_CONFIG = $previousDevicesConfig
    $env:EDGE_GATEWAY_LOG_DIR = $previousLogDir
    $env:EDGE_GATEWAY_SPOOL_DIR = $previousSpoolDir
    $env:EDGE_GATEWAY_ORDERS_DIR = $previousOrdersDir
    $env:EDGE_GATEWAY_PACKETS_DIR = $previousPacketsDir
    $env:EDGE_GATEWAY_OUTPUT_OUTBOX_DB = $previousOutboxDb
    $env:EDGE_GATEWAY_LOG_MIN_LEVEL = $previousLogMinLevel
    $env:EDGE_GATEWAY_LOG_MAX_FILE_BYTES = $previousLogMaxFileBytes
}

$result | ConvertTo-Json -Depth 5
if ($result.passed -ne $true) {
    exit 1
}
