using UnfoldedCircle.Server.Configuration;

namespace UnfoldedCircle.SystemMonitor.Configuration;

public record SystemMonitorConfigurationItem : UnfoldedCircleConfigurationItem
{
    public string? ApiKey { get; init; }
    public sbyte? IntervalSeconds { get; init; }
}