namespace EdgeGateway.Service.ManagerLite;

internal static class ManagerLiteHtml
{
    public const string Content = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>EdgeGateway Manager Lite</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f7f9;
      --panel: #ffffff;
      --line: #d7dce2;
      --text: #1f2933;
      --muted: #5f6b7a;
      --ok: #18864b;
      --bad: #b42318;
      --warn: #b7791f;
      --idle: #64748b;
    }

    * { box-sizing: border-box; }

    body {
      margin: 0;
      font-family: "Segoe UI", Arial, sans-serif;
      background: var(--bg);
      color: var(--text);
      font-size: 14px;
    }

    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--line);
      background: var(--panel);
    }

    h1 {
      margin: 0;
      font-size: 18px;
      font-weight: 600;
      letter-spacing: 0;
    }

    button {
      border: 1px solid #9aa6b2;
      background: #f8fafc;
      color: var(--text);
      min-height: 34px;
      padding: 0 12px;
      border-radius: 4px;
      cursor: pointer;
    }

    button:disabled {
      opacity: 0.55;
      cursor: default;
    }

    main {
      padding: 16px;
      display: grid;
      gap: 14px;
    }

    .status-line {
      min-height: 22px;
      color: var(--muted);
    }

    .error {
      color: var(--bad);
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      background: var(--panel);
      border: 1px solid var(--line);
    }

    th,
    td {
      text-align: left;
      padding: 9px 10px;
      border-bottom: 1px solid var(--line);
      vertical-align: top;
      word-break: break-word;
    }

    th {
      font-weight: 600;
      background: #eef2f6;
      color: #24313f;
    }

    tr:last-child td { border-bottom: 0; }

    .dot {
      display: inline-block;
      width: 11px;
      height: 11px;
      border-radius: 50%;
      margin-right: 7px;
      background: var(--idle);
      vertical-align: -1px;
    }

    .Connected .dot,
    .Listening .dot { background: var(--ok); }

    .Disconnected .dot { background: var(--warn); }

    .Error .dot { background: var(--bad); }

    .log {
      max-height: 220px;
      overflow: auto;
      font-family: Consolas, "Courier New", monospace;
      font-size: 12px;
      line-height: 1.45;
      background: #111827;
      color: #e5e7eb;
      padding: 10px;
      border: 1px solid #1f2937;
    }

    .empty {
      padding: 18px;
      background: var(--panel);
      border: 1px solid var(--line);
      color: var(--muted);
    }

    @media (max-width: 760px) {
      table,
      thead,
      tbody,
      th,
      td,
      tr {
        display: block;
      }

      thead { display: none; }

      tr {
        border-bottom: 1px solid var(--line);
      }

      td {
        display: grid;
        grid-template-columns: 120px 1fr;
        border-bottom: 0;
      }

      td::before {
        content: attr(data-label);
        color: var(--muted);
      }
    }
  </style>
</head>
<body>
  <header>
    <h1>EdgeGateway Manager Lite</h1>
    <button id="refreshButton" type="button">Refresh</button>
  </header>
  <main>
    <div id="statusLine" class="status-line">Loading devices...</div>
    <div id="deviceTable"></div>
    <div id="logPanel" class="log" hidden></div>
  </main>

  <script>
    const refreshButton = document.getElementById("refreshButton");
    const statusLine = document.getElementById("statusLine");
    const deviceTable = document.getElementById("deviceTable");
    const logPanel = document.getElementById("logPanel");

    refreshButton.addEventListener("click", () => loadDevices());
    loadDevices();

    async function loadDevices() {
      refreshButton.disabled = true;
      statusLine.className = "status-line";
      statusLine.textContent = "Loading devices...";

      try {
        const response = await fetch("/api/v1/devices", { cache: "no-store" });
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const envelope = await response.json();
        if (!envelope.success) {
          throw new Error(envelope.error || "Request failed");
        }

        renderDevices(envelope.data || []);
        statusLine.textContent = `Last refreshed ${new Date().toLocaleTimeString()}`;
      } catch (error) {
        deviceTable.innerHTML = "";
        logPanel.hidden = true;
        statusLine.className = "status-line error";
        statusLine.innerHTML = "";
        const text = document.createElement("span");
        text.textContent = error.message || "Unable to load devices";
        const retry = document.createElement("button");
        retry.type = "button";
        retry.textContent = "Retry";
        retry.addEventListener("click", () => loadDevices());
        statusLine.append(text, retry);
      } finally {
        refreshButton.disabled = false;
      }
    }

    function renderDevices(devices) {
      if (devices.length === 0) {
        deviceTable.innerHTML = `<div class="empty">No devices configured.</div>`;
        logPanel.hidden = true;
        return;
      }

      const rows = devices.map((device) => `
        <tr class="${escapeHtml(device.status)}">
          <td data-label="Device">${escapeHtml(device.name)}<br><small>${escapeHtml(device.deviceId)}</small></td>
          <td data-label="Status"><span class="dot"></span>${escapeHtml(device.status)}</td>
          <td data-label="Endpoint">${escapeHtml(device.endpoint)}</td>
          <td data-label="Mode">${escapeHtml(device.mode)}</td>
          <td data-label="Protocol">${escapeHtml(device.protocol)}</td>
          <td data-label="Packets">${device.packetCount ?? 0}</td>
          <td data-label="Pending">${device.pendingCount ?? 0}</td>
          <td data-label="Last message">${formatDate(device.lastMessageAt)}</td>
          <td data-label="Last error">${escapeHtml(device.lastError || "")}</td>
        </tr>
      `).join("");

      deviceTable.innerHTML = `
        <table>
          <thead>
            <tr>
              <th>Device</th>
              <th>Status</th>
              <th>Endpoint</th>
              <th>Mode</th>
              <th>Protocol</th>
              <th>Packets</th>
              <th>Pending</th>
              <th>Last Message</th>
              <th>Last Error</th>
            </tr>
          </thead>
          <tbody>${rows}</tbody>
        </table>`;

      const logs = devices.flatMap((device) => device.communicationLog || []);
      if (logs.length === 0) {
        logPanel.hidden = true;
        return;
      }

      logPanel.hidden = false;
      logPanel.textContent = logs
        .slice(-80)
        .map((entry) => {
          const byteText = entry.byteCount ? ` bytes=${entry.byteCount}` : "";
          const rawText = entry.rawId ? ` raw_id=${entry.rawId}` : "";
          const errorText = entry.error ? ` error=${entry.error}` : "";
          return `${entry.timestamp} ${entry.deviceId} ${entry.eventType}${byteText}${rawText}${errorText} ${entry.message}`;
        })
        .join("\n");
    }

    function formatDate(value) {
      if (!value) {
        return "";
      }
      return new Date(value).toLocaleString();
    }

    function escapeHtml(value) {
      return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
    }
  </script>
</body>
</html>
""";
}
