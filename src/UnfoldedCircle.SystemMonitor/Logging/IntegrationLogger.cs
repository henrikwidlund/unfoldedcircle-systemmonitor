using System.Net;

namespace UnfoldedCircle.SystemMonitor.Logging;

internal static partial class IntegrationLogger
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "[{WSId}] WS: No configurations found")]
    public static partial void NoConfigurationsFound(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "[{WSId}] WS: Only sensor entities are supported, no entities found")]
    public static partial void NoEntitiesOnlySensorSupported(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Adding configuration for entity_id '{EntityId}'")]
    public static partial void AddingConfiguration(this ILogger logger, string entityId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "{WSId} Failed to get system status: {StatusCode}")]
    public static partial void SystemStatusEndpointFail(this ILogger logger, string wsId, HttpStatusCode statusCode);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "{WSId} Failed to parse battery level from response")]
    public static partial void BatteryLevelParseFail(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "{WSId} API key request failed with status code {StatusCode}")]
    public static partial void ApiKeyRequestFail(this ILogger logger, string wsId, HttpStatusCode statusCode);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "{WSId} Failed to parse API key from response")]
    public static partial void ApiKeyParseFail(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "{WSId} Failure during event.")]
    public static partial void FailureDuringEvent(this ILogger logger, string wsId, Exception exception);

    [LoggerMessage(EventId = 10, EventName = nameof(BackupDataNullDuringRestore), Level = LogLevel.Error,
        Message = "[{WSId}] BackupData null during restore.")]
    public static partial void BackupDataNullDuringRestore(this ILogger logger, string wsId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "[{WSId}] Exception during restore.")]
    public static partial void ExceptionDuringRestore(this ILogger logger, string wsId, Exception exception);
}
