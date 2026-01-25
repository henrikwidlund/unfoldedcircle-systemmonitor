using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnfoldedCircle.SystemMonitor.Json;
using UnfoldedCircle.SystemMonitor.Logging;

namespace UnfoldedCircle.SystemMonitor.Http;

public class SystemMonitorClient(HttpClient httpClient, ILogger<SystemMonitorClient> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<SystemMonitorClient> _logger = logger;

    public async ValueTask<SystemMonitorResponse?> GetSystemStatusAsync(string wsId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("pub/status", cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync(
                SystemMonitorSerializerContext.Default.SystemMonitorResponse,
                cancellationToken
            );

        _logger.SystemStatusEndpointFail(wsId, response.StatusCode);
        return null;
    }

    public async ValueTask<int?> GetBatteryLevelAsync(string wsId, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "system/power/battery");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        using JsonDocument jsonDocument = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (jsonDocument.RootElement.TryGetProperty("capacity", out var batteryLevelElement) &&
            batteryLevelElement.ValueKind == JsonValueKind.Number &&
            batteryLevelElement.TryGetInt32(out var batteryLevel))
            return batteryLevel;

        _logger.BatteryLevelParseFail(wsId);
        return null;
    }

    public async ValueTask<string?> GetApiKeyAsync(string wsId, string pin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth/api_keys");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"web-configurator:{pin}")));
        request.Content = JsonContent.Create(new ApiKeyRequest($"System Monitor Client {(DateTime.UtcNow.Ticks % 1000).ToString(NumberFormatInfo.InvariantInfo)}", ["admin"]),
            SystemMonitorSerializerContext.Default.ApiKeyRequest);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.ApiKeyRequestFail(wsId, response.StatusCode);
            return null;
        }

        var apiKeyResponse = await response.Content.ReadFromJsonAsync(
            SystemMonitorSerializerContext.Default.ApiKeyResponse,
            cancellationToken
        );

        if (apiKeyResponse == null)
        {
            _logger.ApiKeyParseFail(wsId);
            return null;
        }

        return apiKeyResponse.ApiKey;
    }
}

/// <summary>
/// Status information about the system.
/// </summary>
/// <param name="Memory">Memory status</param>
/// <param name="LoadAvg">System load average</param>
/// <param name="Filesystem">Filesystem status</param>
public sealed record SystemMonitorResponse(
    [property: JsonPropertyName("memory")] MemoryInfo Memory,
    [property: JsonPropertyName("load_avg")] CpuInfo LoadAvg,
    [property: JsonPropertyName("filesystem")] FileSystemInfo Filesystem
);

/// <summary>
/// Memory status
/// </summary>
/// <param name="TotalMemory">Amount of available RAM in bytes</param>
/// <param name="AvailableMemory">Amount of available RAM in bytes for (re)use</param>
/// <param name="UsedMemory">Amount of used RAM in bytes</param>
/// <param name="TotalSwap">SWAP size in bytes</param>
/// <param name="UsedSwap">Free SWAP in bytes</param>
public sealed record MemoryInfo(
    [property: JsonPropertyName("total_memory")] ulong TotalMemory,
    [property: JsonPropertyName("available_memory")] ulong AvailableMemory,
    [property: JsonPropertyName("used_memory")] ulong UsedMemory,
    [property: JsonPropertyName("total_swap")] ulong TotalSwap,
    [property: JsonPropertyName("used_swap")] ulong UsedSwap
)
{
    public double GetMemoryUsagePercentage() => (int)((double)UsedMemory / TotalMemory * 100);
    public string GetMemoryUsageDetails()
        => $"{UsedMemory.ToMegabytes().ToString(NumberFormatInfo.InvariantInfo)} MB / {TotalMemory.ToMegabytes().ToString(NumberFormatInfo.InvariantInfo)} MB";
    public double GetSwapUsagePercentage() => TotalSwap == 0 ? 0 : (double)UsedSwap / TotalSwap * 100;
    public string GetSwapUsageDetails()
        => $"{UsedSwap.ToMegabytes().ToString(NumberFormatInfo.InvariantInfo)} MB / {TotalSwap.ToMegabytes().ToString(NumberFormatInfo.InvariantInfo)} MB";
}

/// <summary>
/// System load average
/// </summary>
/// <param name="One">Average load within one minute</param>
/// <param name="Five">Average load within five minutes</param>
/// <param name="Fifteen">Average load within fifteen minutes</param>
public sealed record CpuInfo(
    [property: JsonPropertyName("one")] double One,
    [property: JsonPropertyName("five")] double Five,
    [property: JsonPropertyName("fifteen")] double Fifteen
)
{
    public string GetLoadLastMinute() => $"{Math.Round(One * 100, 1).ToString(NumberFormatInfo.InvariantInfo)}";
    public string GetLoadLastFiveMinutes() => $"{Math.Round(Five * 100, 1).ToString(NumberFormatInfo.InvariantInfo)}";
    public string GetLoadLastFifteenMinutes() => $"{Math.Round(Fifteen * 100, 1).ToString(NumberFormatInfo.InvariantInfo)}";
}

/// <summary>
/// Filesystem status
/// </summary>
public sealed record FileSystemInfo(
    [property:JsonPropertyName("user_data")] UserData UserData
);

/// <param name="Available">Amount of available disk space in bytes</param>
/// <param name="Used">Amount of used disk space in bytes</param>
public sealed record UserData(
    [property: JsonPropertyName("available")] ulong Available,
    [property: JsonPropertyName("used")] ulong Used
)
{
    public double GetPercentage() => Math.Round((double)Used / (Available + Used) * 100, 2);
    public string GetDetails()
        => $"{Math.Round(Used.ToMegabytes(), 1).ToString(NumberFormatInfo.InvariantInfo)} MB / {Math.Round((Available + Used).ToMegabytes(), 1).ToString(NumberFormatInfo.InvariantInfo)} MB";
}

public record ApiKeyRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] string[] Scopes);

public record ApiKeyResponse([property: JsonPropertyName("api_key")] string ApiKey);

public static class NumberExtensions
{
    extension(ulong number)
    {
        public double ToMegabytes()
        {
            return number == 0 ? 0d : Math.Round(number / 1048576d, 1); // 1024*1024
        }
    }
}