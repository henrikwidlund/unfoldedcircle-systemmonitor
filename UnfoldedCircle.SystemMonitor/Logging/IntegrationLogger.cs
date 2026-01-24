using System.Net;

namespace UnfoldedCircle.SystemMonitor.Logging;

internal static partial class IntegrationLogger
{
    [LoggerMessage(EventId = 1, EventName = nameof(NoConfigurationsFound), Level = LogLevel.Information,
        Message = "[{WSId}] WS: No configurations found")]
    public static partial void NoConfigurationsFound(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 2, EventName = nameof(NoEntitiesOnlySensorSupported), Level = LogLevel.Information,
        Message = "[{WSId}] WS: Only sensor entities are supported, no entities found")]
    public static partial void NoEntitiesOnlySensorSupported(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 3, EventName = nameof(AddingConfiguration), Level = LogLevel.Information,
        Message = "Adding configuration for entity_id '{EntityId}'")]
    public static partial void AddingConfiguration(this ILogger logger, string entityId);

    [LoggerMessage(EventId = 4, EventName = nameof(SystemStatusEndpointFail), Level = LogLevel.Information,
        Message = "Failed to get system status: {StatusCode}")]
    public static partial void SystemStatusEndpointFail(this ILogger logger, HttpStatusCode statusCode);

    [LoggerMessage(EventId = 5, EventName = nameof(BroadcastTokenCancelled), Level = LogLevel.Debug,
        Message = "{WSId} Broadcast token is cancelled {IsCancellationRequested}")]
    public static partial void BroadcastTokenCancelled(this ILogger logger, string wsId, bool? isCancellationRequested);
}
