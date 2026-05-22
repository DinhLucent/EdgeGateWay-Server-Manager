using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Service.Configuration;
using EdgeGateway.Service.Logging;
using EdgeGateway.Service.Security;

namespace EdgeGateway.Service;

public static class EdgeGatewayHostBuilderExtensions
{
    public static IHostApplicationBuilder AddEdgeGatewayService(
        this IHostApplicationBuilder builder,
        EdgeGatewayServiceOptions options
    )
    {
        Directory.CreateDirectory(options.LogDirectory);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EffectiveOrdersDirectory);
        Directory.CreateDirectory(options.EffectivePacketDirectory);
        Directory.CreateDirectory(options.EffectiveSecurityDirectory);
        Directory.CreateDirectory(
            Path.GetDirectoryName(options.EffectiveOutputOutboxDatabasePath) ?? "."
        );

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(
            options.LogDirectory,
            options.LogMinimumLevel,
            options.LogMaxFileBytes
        ));

        builder.Services.AddSingleton(options);
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull;
            jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            jsonOptions.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
            );
        });
        builder.Services.AddSingleton<JsonDeviceProfileLoader>();
        builder.Services.AddSingleton<DeviceConfigurationFileService>();
        builder.Services.AddSingleton<FileSecurityAuditLog>();
        builder.Services.AddSingleton<EdgeGatewayAuthService>();
        builder.Services.AddSingleton<EdgeGatewayAccessService>();
        builder.Services.AddSingleton<IRawMessageJournal>(
            _ => new FileRawMessageJournal(options.SpoolDirectory)
        );
        builder.Services.AddSingleton<IPreparedOrderRepository>(
            _ => new FilePreparedOrderRepository(options.EffectiveOrdersDirectory)
        );
        builder.Services.AddSingleton<IPacketCommunicationJournal>(
            _ => new FilePacketCommunicationJournal(options.EffectivePacketDirectory)
        );
        builder.Services.AddSingleton<IOutputSink>(_ => new RoutingOutputSink(
            httpSink: new HttpOutputSink(
                outboxStore: new SqliteOutputOutboxStore(
                    options.EffectiveOutputOutboxDatabasePath
                )
            ),
            fileFolderSink: new FileFolderOutputSink()
        ));
        builder.Services.AddSingleton(serviceProvider =>
        {
            JsonDeviceProfileLoader loader =
                serviceProvider.GetRequiredService<JsonDeviceProfileLoader>();
            EdgeGatewayServiceOptions serviceOptions =
                serviceProvider.GetRequiredService<EdgeGatewayServiceOptions>();
            return loader.Load(serviceOptions.DevicesConfigPath);
        });
        builder.Services.AddSingleton(serviceProvider => new EdgeGatewayRuntime(
            serviceProvider.GetRequiredService<DeviceConfiguration>(),
            serviceProvider.GetRequiredService<IRawMessageJournal>(),
            serviceProvider.GetRequiredService<IOutputSink>(),
            serviceProvider.GetRequiredService<IPreparedOrderRepository>(),
            serviceProvider.GetRequiredService<IPacketCommunicationJournal>()
        ));
        builder.Services.AddHostedService<Worker>();

        return builder;
    }
}
