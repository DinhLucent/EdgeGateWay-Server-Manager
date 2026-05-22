using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Probe;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Core.Security;
using EdgeGateway.Service.Configuration;
using EdgeGateway.Service.ManagerLite;
using EdgeGateway.Service.Security;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace EdgeGateway.Service.Api.V1;

public static class EdgeGatewayApiEndpoints
{
    public static IEndpointRouteBuilder MapEdgeGatewayApi(
        this IEndpointRouteBuilder endpoints
    )
    {
        endpoints.MapGet("/", () => Results.Redirect("/manager"));

        endpoints.MapGet(
            "/manager",
            () => Results.Content(ManagerLiteHtml.Content, "text/html; charset=utf-8")
        );

        endpoints.MapGet(
            "/api/v1/health",
            (EdgeGatewayRuntime runtime) =>
            {
                EdgeGatewayHealthSummary health = runtime.GetHealthSummary();
                return Results.Ok(ApiResponse<EdgeGatewayHealthSummary>.Ok(health));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices",
            (EdgeGatewayRuntime runtime) =>
            {
                IReadOnlyList<DeviceHealthSummary> devices = runtime
                    .GetHealthSummary()
                    .Devices;
                return Results.Ok(ApiResponse<IReadOnlyList<DeviceHealthSummary>>.Ok(devices));
            }
        );

        endpoints.MapGet(
            "/api/v1/server/info",
            (EdgeGatewayServiceOptions options) =>
            {
                string repoRoot = EdgeGatewayPaths.FindRepositoryRoot(
                    Directory.GetCurrentDirectory()
                );
                EdgeGatewayServerInfo info = new(
                    typeof(EdgeGatewayApiEndpoints).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion
                        ?? "dev",
                    Environment.Version.ToString(),
                    Environment.MachineName,
                    options.DevicesConfigPath,
                    options.LogDirectory,
                    options.SpoolDirectory,
                    options.EffectiveOrdersDirectory,
                    options.EffectivePacketDirectory,
                    options.EffectiveOutputOutboxDatabasePath,
                    options.EffectiveSecurityDirectory,
                    options.AuthenticationMode.ToString(),
                    options.LogMinimumLevel.ToString(),
                    options.LogMaxFileBytes,
                    options.SecurityAuditRetentionDays,
                    options.WindowsServiceName,
                    Path.Combine(
                        repoRoot,
                        "packages",
                        "edge-gateway",
                        "scripts",
                        "install-service.ps1"
                    ),
                    Path.Combine(
                        repoRoot,
                        "packages",
                        "edge-gateway",
                        "scripts",
                        "uninstall-service.ps1"
                    ),
                    Path.Combine(repoRoot, "output", "edge-gateway", "service")
                );
                return Results.Ok(ApiResponse<EdgeGatewayServerInfo>.Ok(info));
            }
        );

        endpoints.MapGet(
            "/api/v1/server/log-files",
            ([FromQuery] int? limit, EdgeGatewayServiceOptions options) =>
            {
                IReadOnlyList<EdgeGatewayLogFileSummary> files =
                    ListServerLogFiles(options, NormalizeLimit(limit));
                return Results.Ok(ApiResponse<IReadOnlyList<EdgeGatewayLogFileSummary>>.Ok(files));
            }
        );

        endpoints.MapGet(
            "/api/v1/server/log-files/{fileName}",
            async (
                string fileName,
                [FromQuery] int? tailBytes,
                EdgeGatewayServiceOptions options,
                CancellationToken cancellationToken
            ) =>
            {
                return await ReadServerLogFileAsync(
                    fileName,
                    tailBytes,
                    options,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        );

        endpoints.MapGet(
            "/api/v1/security/auth-status",
            (EdgeGatewayAuthService auth) =>
            {
                return Results.Ok(ApiResponse<EdgeGatewayAuthStatus>.Ok(auth.GetStatus()));
            }
        );

        endpoints.MapPost(
            "/api/v1/security/bootstrap-admin",
            async (
                [FromBody] EdgeGatewayBootstrapRequest request,
                EdgeGatewayAuthService auth,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    EdgeGatewayLoginResult result =
                        await auth.BootstrapAdminAsync(request, cancellationToken)
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<EdgeGatewayLoginResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(ApiResponse<EdgeGatewayLoginResult>.Fail(ex.Message));
                }
            }
        );

        endpoints.MapPost(
            "/api/v1/security/login",
            async (
                [FromBody] EdgeGatewayLoginRequest request,
                EdgeGatewayAuthService auth,
                CancellationToken cancellationToken
            ) =>
            {
                try
                {
                    EdgeGatewayLoginResult result =
                        await auth.LoginAsync(request, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(ApiResponse<EdgeGatewayLoginResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(
                        ApiResponse<EdgeGatewayLoginResult>.Fail(ex.Message),
                        statusCode: StatusCodes.Status401Unauthorized
                    );
                }
            }
        );

        endpoints.MapGet(
            "/api/v1/security/current-user",
            (HttpContext httpContext, EdgeGatewayAccessService access) =>
            {
                return Results.Ok(ApiResponse<EdgeGatewayAccessContext>.Ok(
                    access.Resolve(httpContext)
                ));
            }
        );

        endpoints.MapPost(
            "/api/v1/service/start",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManageService,
                        "service.start",
                        "runtime",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(ApiResponse<EdgeGatewayHealthSummary>.Ok(runtime.GetHealthSummary()));
            }
        );

        endpoints.MapPost(
            "/api/v1/service/stop",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManageService,
                        "service.stop",
                        "runtime",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                await runtime.StopAsync().ConfigureAwait(false);
                return Results.Ok(ApiResponse<EdgeGatewayHealthSummary>.Ok(runtime.GetHealthSummary()));
            }
        );

        endpoints.MapPost(
            "/api/v1/service/reload",
            async (
                EdgeGatewayRuntime runtime,
                DeviceConfigurationFileService configService,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "service.reload",
                        "runtime",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                DeviceConfiguration config = configService.Load();
                await runtime.ReloadAsync(config, cancellationToken).ConfigureAwait(false);
                return Results.Ok(ApiResponse<EdgeGatewayHealthSummary>.Ok(runtime.GetHealthSummary()));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/connect-all",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "device.connect_all",
                        "all",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                IReadOnlyList<RuntimeCommandResult> result =
                    await runtime.ConnectAllDevicesAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<RuntimeCommandResult>>.Ok(result));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/disconnect-all",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "device.disconnect_all",
                        "all",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                IReadOnlyList<RuntimeCommandResult> result =
                    await runtime.DisconnectAllDevicesAsync().ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<RuntimeCommandResult>>.Ok(result));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/{deviceId}/connect",
            async (
                string deviceId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "device.connect",
                        deviceId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                RuntimeCommandResult result =
                    await runtime.ConnectDeviceAsync(deviceId, cancellationToken)
                        .ConfigureAwait(false);
                return Results.Ok(ApiResponse<RuntimeCommandResult>.Ok(result));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/{deviceId}/disconnect",
            async (
                string deviceId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "device.disconnect",
                        deviceId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                RuntimeCommandResult result =
                    await runtime.DisconnectDeviceAsync(deviceId).ConfigureAwait(false);
                return Results.Ok(ApiResponse<RuntimeCommandResult>.Ok(result));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices/{deviceId}/logs",
            (string deviceId, EdgeGatewayRuntime runtime) =>
            {
                DeviceHealthSummary? device = FindDevice(runtime, deviceId);
                if (device is null)
                {
                    return Results.NotFound(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                IReadOnlyList<CommunicationLogEntry> log = device
                    .CommunicationLog
                    .OrderByDescending(entry => entry.Timestamp)
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Ok(log));
            }
        );

        endpoints.MapGet(
            "/api/v1/logs/{category}",
            async (
                string category,
                [FromQuery] string? level,
                [FromQuery] int? limit,
                EdgeGatewayRuntime runtime,
                IRawMessageJournal journal,
                CancellationToken cancellationToken
            ) =>
            {
                if (!TryParseLogCategory(category, out CommunicationLogCategory parsedCategory))
                {
                    return Results.BadRequest(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Fail(
                        $"Log category '{category}' is not supported."
                    ));
                }

                if (!TryParseLogLevel(level, out CommunicationLogLevel minimumLevel))
                {
                    return Results.BadRequest(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Fail(
                        $"Log level '{level}' is not supported."
                    ));
                }

                IReadOnlyList<CommunicationLogEntry> entries =
                    await GetLogsByCategoryAsync(
                        runtime,
                        journal,
                        parsedCategory,
                        minimumLevel,
                        NormalizeLimit(limit),
                        cancellationToken
                    ).ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Ok(entries));
            }
        );

        endpoints.MapGet(
            "/api/v1/output/summary",
            (IOutputSink outputSink) =>
            {
                return Results.Ok(ApiResponse<OutputSinkSummary>.Ok(outputSink.GetSummary()));
            }
        );

        endpoints.MapGet(
            "/api/v1/output/queue",
            ([FromQuery] int? limit, IOutputSink outputSink) =>
            {
                IReadOnlyList<OutputQueueItemSnapshot> items = outputSink
                    .GetSnapshot()
                    .OrderByDescending(item => item.UpdatedAt)
                    .Take(NormalizeLimit(limit))
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<OutputQueueItemSnapshot>>.Ok(items));
            }
        );

        endpoints.MapGet(
            "/api/v1/output/dead-letter",
            ([FromQuery] int? limit, IOutputSink outputSink) =>
            {
                IReadOnlyList<OutputQueueItemSnapshot> items = outputSink
                    .GetSnapshot()
                    .Where(item => item.Status is OutputStatus.Failed or OutputStatus.DeadLetter)
                    .OrderByDescending(item => item.UpdatedAt)
                    .Take(NormalizeLimit(limit))
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<OutputQueueItemSnapshot>>.Ok(items));
            }
        );

        endpoints.MapPost(
            "/api/v1/output/replay-failed",
            async (
                [FromBody] OutputReplayRequest request,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                IOutputSink outputSink,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "output.replay_failed",
                        request.RawId ?? "all",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                DateTimeOffset replayedAt = DateTimeOffset.UtcNow;
                int count = outputSink.ReplayFailed(request.RawId);
                await access
                    .WriteAuditAsync(
                        httpContext,
                        EdgeGatewayPermission.ControlDevices,
                        "output.replay_failed.result",
                        request.RawId ?? "all",
                        allowed: true,
                        $"Requeued {count} output item(s).",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                OutputReplayResult result = new(count, replayedAt);
                return Results.Ok(ApiResponse<OutputReplayResult>.Ok(result));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices/{deviceId}/exceptions",
            (string deviceId, EdgeGatewayRuntime runtime) =>
            {
                DeviceHealthSummary? device = FindDevice(runtime, deviceId);
                if (device is null)
                {
                    return Results.NotFound(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                IReadOnlyList<CommunicationLogEntry> exceptions = device
                    .CommunicationLog
                    .Where(entry => CommunicationLogClassifier.IsCategory(
                        entry.EventType,
                        CommunicationLogCategory.Exception
                    ))
                    .OrderByDescending(entry => entry.Timestamp)
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<CommunicationLogEntry>>.Ok(exceptions));
            }
        );

        endpoints.MapGet(
            "/api/v1/captured-metadata",
            async (IRawMessageJournal journal, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<RawMessageRecord> records =
                    await journal.ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<RawMessageRecord> latest = records
                    .OrderByDescending(record => record.ReceivedAt)
                    .Take(500)
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<RawMessageRecord>>.Ok(latest));
            }
        );

        endpoints.MapGet(
            "/api/v1/captured-files",
            async (IRawMessageJournal journal, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<CapturedFileSummary> latest =
                    await GetCapturedFilesAsync(journal, cancellationToken)
                        .ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<CapturedFileSummary>>.Ok(latest));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices/{deviceId}/captured-files",
            async (
                string deviceId,
                IRawMessageJournal journal,
                EdgeGatewayRuntime runtime,
                CancellationToken cancellationToken
            ) =>
            {
                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<IReadOnlyList<CapturedFileSummary>>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                IReadOnlyList<CapturedFileSummary> latest =
                    (await GetCapturedFilesAsync(journal, cancellationToken)
                        .ConfigureAwait(false))
                    .Where(file =>
                        string.Equals(file.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToArray();
                return Results.Ok(ApiResponse<IReadOnlyList<CapturedFileSummary>>.Ok(latest));
            }
        );

        endpoints.MapGet(
            "/api/v1/orders/summary",
            async (IPreparedOrderRepository orders, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<PreparedOrderQueueSummary> summary =
                    await orders.ListSummaryAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<PreparedOrderQueueSummary>>.Ok(summary));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices/{deviceId}/orders",
            async (
                string deviceId,
                EdgeGatewayRuntime runtime,
                IPreparedOrderRepository orders,
                CancellationToken cancellationToken
            ) =>
            {
                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<IReadOnlyList<PreparedOrder>>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                IReadOnlyList<PreparedOrder> result =
                    await orders.ListByDeviceAsync(deviceId, cancellationToken)
                        .ConfigureAwait(false);
                return Results.Ok(ApiResponse<IReadOnlyList<PreparedOrder>>.Ok(result));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/{deviceId}/orders",
            async (
                string deviceId,
                [FromBody] PreparedOrderDraft draft,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                IPreparedOrderRepository orders,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManagePreparedOrders,
                        "order.create",
                        deviceId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<PreparedOrder>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                try
                {
                    PreparedOrder result = await orders
                        .UpsertAsync(draft with { OrderId = null, DeviceId = deviceId }, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<PreparedOrder>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(ApiResponse<PreparedOrder>.Fail(ex.Message));
                }
            }
        );

        endpoints.MapPut(
            "/api/v1/devices/{deviceId}/orders/{orderId}",
            async (
                string deviceId,
                string orderId,
                [FromBody] PreparedOrderDraft draft,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                IPreparedOrderRepository orders,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManagePreparedOrders,
                        "order.update",
                        $"{deviceId}/{orderId}",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<PreparedOrder>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                try
                {
                    PreparedOrder result = await orders
                        .UpsertAsync(
                            draft with { OrderId = orderId, DeviceId = deviceId },
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<PreparedOrder>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(ApiResponse<PreparedOrder>.Fail(ex.Message));
                }
            }
        );

        endpoints.MapDelete(
            "/api/v1/devices/{deviceId}/orders/{orderId}",
            async (
                string deviceId,
                string orderId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                IPreparedOrderRepository orders,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManagePreparedOrders,
                        "order.delete",
                        $"{deviceId}/{orderId}",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<string>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                bool deleted = await orders.DeleteAsync(deviceId, orderId, cancellationToken)
                    .ConfigureAwait(false);
                return deleted
                    ? Results.Ok(ApiResponse<string>.Ok(orderId))
                    : Results.NotFound(ApiResponse<string>.Fail(
                        $"Order '{orderId}' was not found."
                    ));
            }
        );

        endpoints.MapPost(
            "/api/v1/devices/{deviceId}/orders/{orderId}/send-test",
            async (
                string deviceId,
                string orderId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                IPreparedOrderRepository orders,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.ManagePreparedOrders,
                        "order.send_test",
                        $"{deviceId}/{orderId}",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<RuntimeCommandResult>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                PreparedOrder? order = await orders.GetAsync(deviceId, orderId, cancellationToken)
                    .ConfigureAwait(false);
                if (order is null)
                {
                    return Results.NotFound(ApiResponse<RuntimeCommandResult>.Fail(
                        $"Order '{orderId}' was not found."
                    ));
                }

                RuntimeCommandResult result =
                    await runtime.SendPreparedOrderAsync(deviceId, order, cancellationToken)
                        .ConfigureAwait(false);
                if (result.Success)
                {
                    await orders
                        .MarkSentAsync(deviceId, orderId, DateTimeOffset.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await orders
                        .MarkFailedAsync(
                            deviceId,
                            orderId,
                            result.Error ?? result.Message,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                return Results.Ok(ApiResponse<RuntimeCommandResult>.Ok(result));
            }
        );

        endpoints.MapGet(
            "/api/v1/devices/{deviceId}/packets",
            async (
                string deviceId,
                [FromQuery] int? limit,
                EdgeGatewayRuntime runtime,
                IPacketCommunicationJournal packets,
                CancellationToken cancellationToken
            ) =>
            {
                if (FindDevice(runtime, deviceId) is null)
                {
                    return Results.NotFound(ApiResponse<IReadOnlyList<PacketCommunicationRecord>>.Fail(
                        $"Device '{deviceId}' is not configured."
                    ));
                }

                IReadOnlyList<PacketCommunicationRecord> records =
                    await packets.ReadByDeviceAsync(
                        deviceId,
                        NormalizeLimit(limit),
                        cancellationToken
                    ).ConfigureAwait(false);
                return Results.Ok(
                    ApiResponse<IReadOnlyList<PacketCommunicationRecord>>.Ok(records)
                );
            }
        );

        endpoints.MapDelete(
            "/api/v1/captured-files/{rawId}",
            async (
                string rawId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                IRawMessageJournal journal,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.HideCapturedMetadata,
                        "captured_metadata.hide",
                        rawId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                bool hidden = await journal.HideAsync(rawId, cancellationToken)
                    .ConfigureAwait(false);
                if (!hidden)
                {
                    return Results.NotFound(ApiResponse<CapturedFileDeleteResult>.Fail(
                        $"Captured file '{rawId}' was not found."
                    ));
                }

                CapturedFileDeleteResult result = new(
                    rawId,
                    HiddenFromOperationalView: true,
                    RawPayloadRetained: true
                );
                return Results.Ok(ApiResponse<CapturedFileDeleteResult>.Ok(result));
            }
        );

        endpoints.MapGet(
            "/api/v1/config/devices",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.read",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                DeviceConfiguration config = configService.Load();
                return Results.Ok(ApiResponse<DeviceConfiguration>.Ok(config));
            }
        );

        endpoints.MapGet(
            "/api/v1/config/device-settings",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.read_settings",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                DeviceConfigurationDocument document = configService.LoadDocument();
                return Results.Ok(ApiResponse<DeviceConfigurationDocument>.Ok(document));
            }
        );

        endpoints.MapGet(
            "/api/v1/config/profile-presets",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.read_profile_presets",
                        "profiles",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                IReadOnlyList<DeviceProfilePreset> presets = configService.LoadProfilePresets();
                return Results.Ok(ApiResponse<IReadOnlyList<DeviceProfilePreset>>.Ok(presets));
            }
        );

        endpoints.MapGet(
            "/api/v1/config/backups",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.list_backups",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                IReadOnlyList<ConfigBackupSummary> backups = configService.ListBackups();
                return Results.Ok(ApiResponse<IReadOnlyList<ConfigBackupSummary>>.Ok(backups));
            }
        );

        endpoints.MapPost(
            "/api/v1/config/restore-backup",
            async (
                [FromBody] ConfigRestoreBackupRequest request,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.RestoreConfigBackup,
                        "config.restore_backup",
                        request.BackupFileName,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    DeviceConfigurationSaveResult result =
                        await configService.RestoreBackupAsync(
                                request.BackupFileName,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        endpoints.MapGet(
            "/api/v1/config/export",
            async (
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.export",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                ConfigExportBundle bundle = configService.ExportBundle();
                return Results.Ok(ApiResponse<ConfigExportBundle>.Ok(bundle));
            }
        );

        endpoints.MapPost(
            "/api/v1/config/import/validate",
            async (
                [FromBody] ConfigExportBundle bundle,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.import_validate",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                ConfigImportPreview preview = configService.ValidateImportBundle(bundle);
                return preview.Valid
                    ? Results.Ok(ApiResponse<ConfigImportPreview>.Ok(preview))
                    : Results.BadRequest(ApiResponse<ConfigImportPreview>.Fail(preview.Error ?? "Invalid config bundle."));
            }
        );

        endpoints.MapPost(
            "/api/v1/config/import",
            async (
                [FromBody] ConfigExportBundle bundle,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.import",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    DeviceConfigurationSaveResult result =
                        await configService.ImportBundleAsync(bundle, cancellationToken)
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        endpoints.MapPost(
            "/api/v1/config/devices/from-profile-preset",
            async (
                [FromBody] ProfilePresetCreateRequest request,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.create_from_profile_preset",
                        request.PresetId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    DeviceConfigurationSaveResult result =
                        await configService.AddProfileFromPresetAsync(
                                request.PresetId,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        endpoints.MapDelete(
            "/api/v1/config/devices/{deviceId}",
            async (
                string deviceId,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                EdgeGatewayRuntime runtime,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.delete_device",
                        deviceId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    await runtime.DisconnectDeviceAsync(deviceId).ConfigureAwait(false);
                    DeviceConfigurationSaveResult result =
                        await configService.DeleteDeviceAsync(deviceId, cancellationToken)
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        endpoints.MapPut(
            "/api/v1/config/devices",
            async (
                [FromBody] DeviceConfiguration config,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.save",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    DeviceConfigurationSaveResult result =
                        await configService.SaveAsync(config, cancellationToken)
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        endpoints.MapPut(
            "/api/v1/config/device-settings",
            async (
                [FromBody] DeviceConfigurationDocument document,
                HttpContext httpContext,
                EdgeGatewayAccessService access,
                DeviceConfigurationFileService configService,
                CancellationToken cancellationToken
            ) =>
            {
                IResult? denied = await access
                    .RequireAsync(
                        httpContext,
                        EdgeGatewayPermission.EditDeviceConfig,
                        "config.save_device_settings",
                        "devices",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (denied is not null)
                {
                    return denied;
                }

                try
                {
                    DeviceConfigurationSaveResult result =
                        await configService.SaveDocumentAsync(document, cancellationToken)
                            .ConfigureAwait(false);
                    return Results.Ok(ApiResponse<DeviceConfigurationSaveResult>.Ok(result));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(
                        ApiResponse<DeviceConfigurationSaveResult>.Fail(ex.Message)
                    );
                }
            }
        );

        return endpoints;
    }

    private static IReadOnlyList<EdgeGatewayLogFileSummary> ListServerLogFiles(
        EdgeGatewayServiceOptions options,
        int limit
    )
    {
        if (!Directory.Exists(options.LogDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(options.LogDirectory, "edge-gateway-*.log")
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && IsSafeServerLogFileName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(limit)
            .Select(file => new EdgeGatewayLogFileSummary(
                file.Name,
                file.LastWriteTimeUtc,
                file.Length
            ))
            .ToArray();
    }

    private static async Task<IResult> ReadServerLogFileAsync(
        string fileName,
        int? tailBytes,
        EdgeGatewayServiceOptions options,
        CancellationToken cancellationToken
    )
    {
        if (!IsSafeServerLogFileName(fileName))
        {
            return Results.BadRequest(
                ApiResponse<EdgeGatewayLogFileContent>.Fail("Log file name is not allowed.")
            );
        }

        string logDirectory = Path.GetFullPath(options.LogDirectory);
        string path = Path.GetFullPath(Path.Combine(logDirectory, fileName));
        string directoryPrefix = logDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        ) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(
                ApiResponse<EdgeGatewayLogFileContent>.Fail("Log file path is not allowed.")
            );
        }

        if (!File.Exists(path))
        {
            return Results.NotFound(
                ApiResponse<EdgeGatewayLogFileContent>.Fail($"Log file '{fileName}' was not found.")
            );
        }

        FileInfo file = new(path);
        int normalizedTailBytes = Math.Clamp(tailBytes ?? 64 * 1024, 1, 256 * 1024);
        long start = Math.Max(0, file.Length - normalizedTailBytes);
        int count = checked((int)(file.Length - start));
        byte[] buffer = new byte[count];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        stream.Seek(start, SeekOrigin.Begin);
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        string content = Encoding.UTF8.GetString(buffer.AsSpan(0, offset));
        EdgeGatewayLogFileContent result = new(
            file.Name,
            file.LastWriteTimeUtc,
            file.Length,
            content,
            Truncated: start > 0
        );
        return Results.Ok(ApiResponse<EdgeGatewayLogFileContent>.Ok(result));
    }

    private static bool IsSafeServerLogFileName(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && fileName.StartsWith("edge-gateway-", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static DeviceHealthSummary? FindDevice(EdgeGatewayRuntime runtime, string deviceId)
    {
        return runtime
            .GetHealthSummary()
            .Devices
            .FirstOrDefault(device =>
                string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static bool TryParseLogCategory(
        string category,
        out CommunicationLogCategory parsedCategory
    )
    {
        return Enum.TryParse(
            category.Replace("-", "", StringComparison.Ordinal),
            ignoreCase: true,
            out parsedCategory
        );
    }

    private static bool TryParseLogLevel(
        string? level,
        out CommunicationLogLevel parsedLevel
    )
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            parsedLevel = CommunicationLogLevel.Info;
            return true;
        }

        return Enum.TryParse(
            level.Replace("-", "", StringComparison.Ordinal),
            ignoreCase: true,
            out parsedLevel
        );
    }

    private static int NormalizeLimit(int? limit)
    {
        return Math.Clamp(limit ?? 500, 1, 2_000);
    }

    private static async Task<IReadOnlyList<CommunicationLogEntry>> GetLogsByCategoryAsync(
        EdgeGatewayRuntime runtime,
        IRawMessageJournal journal,
        CommunicationLogCategory category,
        CommunicationLogLevel minimumLevel,
        int limit,
        CancellationToken cancellationToken
    )
    {
        EdgeGatewayHealthSummary health = runtime.GetHealthSummary();
        List<CommunicationLogEntry> entries = [];

        if (category == CommunicationLogCategory.System)
        {
            entries.Add(new CommunicationLogEntry(
                health.CheckedAt,
                "system",
                DeviceRuntimeEventType.ServiceStatus,
                $"Service {health.ServiceStatus.ToLowerInvariant()} with {health.DeviceCount} configured device(s).",
                ByteCount: null,
                RawId: null,
                PayloadHash: null,
                Error: null,
                ProbeSampleHex: null
            ));
        }

        foreach (DeviceHealthSummary device in health.Devices)
        {
            foreach (CommunicationLogEntry entry in device.CommunicationLog)
            {
                if (CommunicationLogClassifier.IsCategory(entry.EventType, category))
                {
                    entries.Add(entry);
                }
            }
        }

        if (category == CommunicationLogCategory.Data)
        {
            IReadOnlyList<RawMessageRecord> records =
                await journal.ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
            foreach (RawMessageRecord record in records)
            {
                entries.Add(new CommunicationLogEntry(
                    record.ReceivedAt,
                    record.DeviceId,
                    DeviceRuntimeEventType.RawMessageCompleted,
                    $"Raw message {record.RawId} captured ({record.ByteCount} byte(s), status {record.Status}).",
                    record.ByteCount,
                    record.RawId,
                    record.PayloadHash,
                    record.LastError,
                    ProbeSampleHex: null
                ));
            }
        }

        return entries
            .Where(entry => CommunicationLogClassifier.IsAtOrAbove(entry.EventType, minimumLevel))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(limit)
            .ToArray();
    }

    private static async Task<IReadOnlyList<CapturedFileSummary>> GetCapturedFilesAsync(
        IRawMessageJournal journal,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<RawMessageRecord> records =
            await journal.ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        return records
            .OrderByDescending(record => record.ReceivedAt)
            .Take(500)
            .Select(CapturedFileSummary.FromRecord)
            .ToArray();
    }
}
