using EdgeGateway.Service;
using EdgeGateway.Service.Api.V1;
using Microsoft.AspNetCore.Hosting;

EdgeGatewayServiceOptions options =
    EdgeGatewayServiceOptions.CreateDefault(Directory.GetCurrentDirectory());

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.EffectiveUrls);
builder.Host.UseWindowsService(windowsServiceOptions =>
{
    windowsServiceOptions.ServiceName = options.WindowsServiceName;
});
builder.AddEdgeGatewayService(options);

WebApplication app = builder.Build();
app.MapEdgeGatewayApi();

await app.RunAsync();
