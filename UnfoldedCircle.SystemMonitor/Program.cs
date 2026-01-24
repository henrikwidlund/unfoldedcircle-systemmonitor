using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.SystemMonitor.Configuration;
using UnfoldedCircle.SystemMonitor.Http;
using UnfoldedCircle.SystemMonitor.WebSocket;

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddUnfoldedCircleServer<SystemMonitorWebSocketHandler, SystemMonitorConfigurationService, UnfoldedCircleConfigurationItem>();
builder.Services.AddHttpClient<SystemMonitorClient>(static (provider, client) =>
{
    client.BaseAddress = new Uri(provider.GetRequiredService<IConfiguration>()["ApiEndpoint"] ?? "http://localhost/api/pub/status");
});
var app = builder.Build();

app.UseUnfoldedCircleServer<SystemMonitorWebSocketHandler, UnfoldedCircleConfigurationItem>();

await app.RunAsync();