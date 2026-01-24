using System.Globalization;
using System.Text.Json.Serialization;
using UnfoldedCircle.SystemMonitor.Json;
using UnfoldedCircle.SystemMonitor.Logging;

namespace UnfoldedCircle.SystemMonitor.Http;

public class SystemMonitorClient(HttpClient httpClient, ILogger<SystemMonitorClient> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<SystemMonitorClient> _logger = logger;
    public async Task<SystemMonitorResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync((Uri?)null, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<SystemMonitorResponse>(
                SystemMonitorSerializerContext.Default.SystemMonitorResponse,
                cancellationToken
            );

        _logger.SystemStatusEndpointFail(response.StatusCode);
        return null;
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