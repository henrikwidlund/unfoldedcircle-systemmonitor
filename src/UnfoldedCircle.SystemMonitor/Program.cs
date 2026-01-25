using System.Net.Http.Headers;

using UnfoldedCircle.SystemMonitor.Configuration;
using UnfoldedCircle.SystemMonitor.Http;
using UnfoldedCircle.SystemMonitor.WebSocket;

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddUnfoldedCircleServer<SystemMonitorWebSocketHandler, SystemMonitorConfigurationService, SystemMonitorConfigurationItem>();
builder.Services.AddHttpClient<SystemMonitorClient>(static (provider, client) =>
{
    client.BaseAddress = new Uri(provider.GetRequiredService<IConfiguration>()["ApiBaseAddress"] ?? "http://localhost/api/");
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
var app = builder.Build();

app.UseUnfoldedCircleServer<SystemMonitorWebSocketHandler, SystemMonitorConfigurationItem>();

await app.RunAsync();