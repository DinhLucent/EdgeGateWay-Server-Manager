param(
    [string]$ServiceUrl = "http://127.0.0.1:18082",
    [string]$HostName = "127.0.0.1",
    [int]$Port = 9100,
    [int]$DurationMinutes = 60,
    [int]$DelayMilliseconds = 250,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
    $OutputDirectory = Join-Path $repoRoot "output\edge-gateway\soak"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$metricsPath = Join-Path $OutputDirectory "ca270-astm-tcp-soak-$runId.jsonl"

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
    $bodyBytes = $encoding.GetBytes($body)
    $checksum = Get-AstmChecksum $bodyBytes
    $frameText = [char]0x02 + $body + $checksum + [char]0x0D + [char]0x0A
    return $encoding.GetBytes($frameText)
}

function Read-ControlByte {
    param([Net.Sockets.NetworkStream]$Stream)

    $buffer = New-Object byte[] 1
    $read = $Stream.Read($buffer, 0, 1)
    if ($read -ne 1) {
        throw "Expected one control byte from EdgeGateway."
    }

    return $buffer[0]
}

function Send-Ca270Result {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$Sequence
    )

    $client = [Net.Sockets.TcpClient]::new()
    $client.ReceiveTimeout = 5000
    $client.SendTimeout = 5000
    try {
        $client.Connect($HostName, $Port)
        $stream = $client.GetStream()
        $payload = "H|\^&|||CA-270`rP|1`rO|1|SOAK-$Sequence||^^^GLU`rR|1|^^^GLU|$((100 + ($Sequence % 20)))|mg/dL`rL|1|N`r"
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

function Get-ServiceHealth {
    param([string]$ServiceUrl)

    try {
        return Invoke-RestMethod -Uri "$ServiceUrl/api/v1/health" -TimeoutSec 5
    }
    catch {
        return $null
    }
}

$deadline = (Get-Date).AddMinutes($DurationMinutes)
$sequence = 0
$sent = 0
$failed = 0

while ((Get-Date) -lt $deadline) {
    $sequence++
    $errorText = $null
    try {
        Send-Ca270Result -HostName $HostName -Port $Port -Sequence $sequence
        $sent++
    }
    catch {
        $failed++
        $errorText = $_.Exception.Message
    }

    $health = Get-ServiceHealth -ServiceUrl $ServiceUrl
    $metric = [ordered]@{
        ts = (Get-Date).ToUniversalTime().ToString("O")
        sequence = $sequence
        sent = $sent
        failed = $failed
        error = $errorText
        health = $health
    }
    $metric | ConvertTo-Json -Depth 20 -Compress | Add-Content -Path $metricsPath
    Start-Sleep -Milliseconds $DelayMilliseconds
}

Write-Host "CA270 ASTM TCP soak completed."
Write-Host "Metrics: $metricsPath"
