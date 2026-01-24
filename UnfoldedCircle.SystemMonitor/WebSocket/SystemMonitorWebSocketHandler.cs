using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UnfoldedCircle.Models.Events;
using UnfoldedCircle.Models.Shared;
using UnfoldedCircle.Models.Sync;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.Server.DependencyInjection;
using UnfoldedCircle.Server.Extensions;
using UnfoldedCircle.Server.Response;
using UnfoldedCircle.Server.WebSocket;
using UnfoldedCircle.SystemMonitor.Configuration;
using UnfoldedCircle.SystemMonitor.Http;
using UnfoldedCircle.SystemMonitor.Logging;

namespace UnfoldedCircle.SystemMonitor.WebSocket;

internal sealed class SystemMonitorWebSocketHandler(
    IConfigurationService<UnfoldedCircleConfigurationItem> configurationService,
    IOptions<UnfoldedCircleOptions> options,
    IServiceProvider serviceProvider,
    ILogger<SystemMonitorWebSocketHandler> logger)
    : UnfoldedCircleWebSocketHandler<MediaPlayerCommandId, UnfoldedCircleConfigurationItem>(configurationService, options, logger)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    protected override ValueTask<EntityCommandResult> OnRemoteCommandAsync(System.Net.WebSockets.WebSocket socket, RemoteEntityCommandMsgData payload, string command, string wsId, CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken) =>
        ValueTask.FromResult(EntityCommandResult.Failure);

    protected override ValueTask<bool> IsEntityReachableAsync(string wsId, string entityId, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    protected override ValueTask<EntityCommandResult> OnMediaPlayerCommandAsync(System.Net.WebSockets.WebSocket socket, MediaPlayerEntityCommandMsgData<MediaPlayerCommandId> payload, string wsId, CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken) =>
        ValueTask.FromResult(EntityCommandResult.Failure);

    protected override async ValueTask OnConnectAsync(ConnectEvent payload, string wsId, CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        var identifier = EntityId.GetIdentifier(EntityType.Sensor);
        var entity = configuration.Entities.FirstOrDefault(x => x.EntityId.Equals(identifier, StringComparison.OrdinalIgnoreCase));
        if (entity is null)
        {
            _logger.AddingConfiguration(identifier);
            entity = new UnfoldedCircleConfigurationItem
            {
                Host = "localhost",
                EntityName = "Remote",
                EntityId = identifier
            };

            configuration.Entities.Add(entity);
            await _configurationService.UpdateConfigurationAsync(configuration, cancellationToken);
        }
    }

    protected override ValueTask<bool> OnDisconnectAsync(DisconnectEvent payload, string wsId, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    protected override ValueTask OnAbortDriverSetupAsync(AbortDriverSetupEvent payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    protected override ValueTask OnEnterStandbyAsync(EnterStandbyEvent payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    protected override ValueTask OnExitStandbyAsync(ExitStandbyEvent payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    protected override async Task HandleEventUpdatesAsync(System.Net.WebSockets.WebSocket socket, string wsId, CancellationTokenWrapper cancellationTokenWrapper)
    {
        if (!IsSocketSubscribedToEvents(wsId))
            return;

        if (!TryAddSocketBroadcastingEvents(wsId))
            return;

        var cancellationTokenSource = cancellationTokenWrapper.GetCurrentBroadcastCancellationTokenSource();
        if (cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested)
        {
            _logger.BroadcastTokenCancelled(wsId, cancellationTokenSource?.IsCancellationRequested);
            return;
        }

        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!cancellationTokenSource.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(cancellationTokenSource.Token))
        {
            var subscribedEntityIds = GetSubscribedEntityIds();
            if (subscribedEntityIds.Count == 0)
                continue;

            await using var scope = _serviceProvider.CreateAsyncScope();
            var systemMonitorClient = scope.ServiceProvider.GetRequiredService<SystemMonitorClient>();
            var result = await systemMonitorClient.GetSystemStatusAsync(cancellationTokenSource.Token);
            if (result is null)
                continue;

            foreach (var sensorType in SensorType.GetValues())
            {
                var entityId = EntityId.GetIdentifier(EntityType.Sensor, sensorType.ToStringFast());
                if (!subscribedEntityIds.Contains(entityId, StringComparer.OrdinalIgnoreCase))
                    continue;

                await (sensorType switch
                {
                    SensorType.MemoryPercentage => SendMemoryPercentageSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.MemoryDetails => SendMemoryDetailsSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.SwapPercentage => SendSwapPercentageSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.SwapDetails => SendSwapDetailsSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.CpuUsagePercentLast1Minute => SendCpuUsagePercentLast1MinuteSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.CpuUsagePercentLast5Minutes => SendCpuUsagePercentLast5MinuteSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.CpuUsagePercentLast15Minutes => SendCpuUsagePercentLast15MinuteSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.FileSystemPercentage => SendFileSystemPercentageSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    SensorType.FileSystemDetails => SendFileSystemDetailsSensor(socket, wsId, entityId, result, cancellationTokenSource.Token),
                    _ => Task.CompletedTask
                });
            }
        }
    }

    private static readonly ConcurrentDictionary<SensorType, int> PreviousSensorValuesMap = new();

    private async Task SendMemoryPercentageSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.MemoryPercentage, out var previousValue) &&
            previousValue == systemMonitorResponse.Memory.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.MemoryPercentage] = systemMonitorResponse.Memory.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<double>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Memory.GetMemoryUsagePercentage()
                },
                entityId,
                nameof(SensorType.MemoryPercentage)),
            wsId,
            cancellationToken);
    }

    private async Task SendMemoryDetailsSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.MemoryDetails, out var previousValue) &&
            previousValue == systemMonitorResponse.Memory.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.MemoryDetails] = systemMonitorResponse.Memory.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Memory.GetMemoryUsageDetails()
                },
                entityId,
                nameof(SensorType.MemoryDetails)),
            wsId,
            cancellationToken);
    }

    private async Task SendSwapPercentageSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.SwapPercentage, out var previousValue) &&
            previousValue == systemMonitorResponse.Memory.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.SwapPercentage] = systemMonitorResponse.Memory.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<double>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Memory.GetSwapUsagePercentage()
                },
                entityId,
                nameof(SensorType.SwapPercentage)),
            wsId,
            cancellationToken);
    }

    private async Task SendSwapDetailsSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.SwapDetails, out var previousValue) &&
            previousValue == systemMonitorResponse.Memory.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.SwapDetails] = systemMonitorResponse.Memory.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Memory.GetSwapUsageDetails()
                },
                entityId,
                nameof(SensorType.SwapDetails)),
            wsId,
            cancellationToken);
    }

    private async Task SendCpuUsagePercentLast1MinuteSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.CpuUsagePercentLast1Minute, out var previousValue) &&
            previousValue == systemMonitorResponse.LoadAvg.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.CpuUsagePercentLast1Minute] = systemMonitorResponse.LoadAvg.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.LoadAvg.GetLoadLastMinute()
                },
                entityId,
                nameof(SensorType.CpuUsagePercentLast1Minute)),
            wsId,
            cancellationToken);
    }

    private async Task SendCpuUsagePercentLast5MinuteSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.CpuUsagePercentLast5Minutes, out var previousValue) &&
            previousValue == systemMonitorResponse.LoadAvg.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.CpuUsagePercentLast5Minutes] = systemMonitorResponse.LoadAvg.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.LoadAvg.GetLoadLastFiveMinutes()
                },
                entityId,
                nameof(SensorType.CpuUsagePercentLast5Minutes)),
            wsId,
            cancellationToken);
    }

    private async Task SendCpuUsagePercentLast15MinuteSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.CpuUsagePercentLast15Minutes, out var previousValue) &&
            previousValue == systemMonitorResponse.LoadAvg.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.CpuUsagePercentLast15Minutes] = systemMonitorResponse.LoadAvg.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.LoadAvg.GetLoadLastFifteenMinutes()
                },
                entityId,
                nameof(SensorType.CpuUsagePercentLast15Minutes)),
            wsId,
            cancellationToken);
    }

    private async Task SendFileSystemPercentageSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.FileSystemPercentage, out var previousValue) &&
            previousValue == systemMonitorResponse.Filesystem.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.FileSystemPercentage] = systemMonitorResponse.Filesystem.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<double>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Filesystem.UserData.GetPercentage()
                },
                entityId,
                nameof(SensorType.FileSystemPercentage)),
            wsId,
            cancellationToken);
    }

    private async Task SendFileSystemDetailsSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (PreviousSensorValuesMap.TryGetValue(SensorType.FileSystemDetails, out var previousValue) &&
            previousValue == systemMonitorResponse.Filesystem.GetHashCode())
            return;

        PreviousSensorValuesMap[SensorType.FileSystemDetails] = systemMonitorResponse.Filesystem.GetHashCode();

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<string>
                {
                    State = SensorState.On,
                    Value = systemMonitorResponse.Filesystem.UserData.GetDetails()
                },
                entityId,
                nameof(SensorType.FileSystemDetails)),
            wsId,
            cancellationToken);
    }

    protected override ValueTask<DeviceState> OnGetDeviceStateAsync(GetDeviceStateMsg payload, string wsId, CancellationToken cancellationToken)
    {
        // not supported
        return ValueTask.FromResult(DeviceState.Error);
    }

    protected override ValueTask<EntityState> GetEntityStateAsync(UnfoldedCircleConfigurationItem entity, string wsId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(EntityState.Connected);
    }

    protected override async ValueTask<IReadOnlyCollection<AvailableEntity>> OnGetAvailableEntitiesAsync(GetAvailableEntitiesMsg payload, string wsId, CancellationToken cancellationToken)
    {
        return GetAvailableEntities(await GetEntitiesAsync(wsId, payload.MsgData.Filter?.EntityType, cancellationToken), payload).ToArray();
    }

    protected override ValueTask OnSubscribeEventsAsync(System.Net.WebSockets.WebSocket socket, SubscribeEventsMsg subscribeEventsMsg, string wsId, CancellationTokenWrapper cancellationTokenWrapper, CancellationToken commandCancellationToken)
    {
        if (subscribeEventsMsg.MsgData?.EntityIds is not { Length: > 0 })
            return ValueTask.CompletedTask;

        foreach (var sensorType in SensorType.GetValues())
        {
            var identifier = EntityId.GetIdentifier(EntityType.Sensor, sensorType.ToStringFast());
            if (subscribeEventsMsg.MsgData.EntityIds.Contains(identifier))
            {
                TryAddEntityIdToBroadcastingEvents(identifier, cancellationTokenWrapper);
                _ = Task.Factory.StartNew(() => HandleEventUpdatesAsync(socket, wsId, cancellationTokenWrapper),
                    TaskCreationOptions.LongRunning);
            }
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnUnsubscribeEventsAsync(UnsubscribeEventsMsg payload, string wsId, CancellationTokenWrapper cancellationTokenWrapper)
    {
        if (payload.MsgData?.EntityIds is { Length: > 0 })
        {
            foreach (var msgDataEntityId in payload.MsgData.EntityIds)
            {
                RemoveEntityIdToBroadcastingEvents(msgDataEntityId, cancellationTokenWrapper);
            }
        }

        // If no specific device or entity was specified, dispose all clients for this websocket ID.
        if (payload.MsgData is { DeviceId: null, EntityIds: null })
        {
            foreach (var sensorType in SensorType.GetValues())
            {
                RemoveEntityIdToBroadcastingEvents(EntityId.GetIdentifier(EntityType.Sensor, sensorType.ToStringFast()), cancellationTokenWrapper);
            }
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask<EntityStateChanged[]> OnGetEntityStatesAsync(GetEntityStatesMsg payload, string wsId, CancellationToken cancellationToken)
    {
        var states = new EntityStateChanged[SensorTypeExtensions.Length];
        var types = SensorType.GetValues();
        for (var index = 0; index < types.Length; index++)
        {
            var sensorType = types[index];
            states[index] = new SensorEntityStateChanged
            {
                Attributes = [SensorEntityAttribute.State, SensorEntityAttribute.Unit, SensorEntityAttribute.Value],
                EntityId = EntityId.GetIdentifier(EntityType.Sensor, sensorType.ToStringFast()),
                EntityType = EntityType.Sensor
            };
        }

        return ValueTask.FromResult(states);
    }

    protected override MediaPlayerEntityCommandMsgData<MediaPlayerCommandId>? DeserializeMediaPlayerCommandPayload(JsonDocument jsonDocument)
        => null;

    protected override ValueTask<OnSetupResult?> OnSetupDriverAsync(SetupDriverMsg payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.FromResult<OnSetupResult?>(new OnSetupResult(SetupDriverResult.Finalized));
    protected override ValueTask<SetupDriverUserDataResult> OnSetupDriverUserDataConfirmAsync(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.FromResult(SetupDriverUserDataResult.Finalized);

    protected override ValueTask<SettingsPage> CreateNewEntitySettingsPageAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SettingsPage);

    private static readonly SettingsPage SettingsPage = new()
    {
        Settings = [],
        Title = []
    };

    private const string EntityId = "local";

    protected override ValueTask<SettingsPage> CreateReconfigureEntitySettingsPageAsync(UnfoldedCircleConfigurationItem configurationItem, CancellationToken cancellationToken)
        => ValueTask.FromResult(SettingsPage);

    protected override ValueTask<SetupDriverUserDataResult> HandleEntityReconfigured(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, UnfoldedCircleConfigurationItem configurationItem,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(SetupDriverUserDataResult.Finalized);
    }

    protected override ValueTask<SetupDriverUserDataResult> HandleCreateNewEntity(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(SetupDriverUserDataResult.Finalized);
    }

    protected override FrozenSet<EntityType> SupportedEntityTypes { get; } = [EntityType.Sensor];

    private async Task<List<UnfoldedCircleConfigurationItem>?> GetEntitiesAsync(
        string wsId,
        EntityType? entityType,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        if (configuration.Entities.Count == 0)
        {
            _logger.NoConfigurationsFound(wsId);
            return null;
        }

        if (entityType is not null and not EntityType.Sensor)
        {
            _logger.NoEntitiesOnlySensorSupported(wsId);
            return null;
        }

        return configuration.Entities;
    }

    private IEnumerable<AvailableEntity> GetAvailableEntities(
        List<UnfoldedCircleConfigurationItem>? entities,
        GetAvailableEntitiesMsg payload)
    {
        if (entities is not { Count: > 0 })
            yield break;

        var hasEntityTypeFilter = payload.MsgData.Filter?.EntityType is not null;
        foreach (var unfoldedCircleConfigurationItem in entities)
        {
            if (hasEntityTypeFilter)
            {
                if (payload.MsgData.Filter?.EntityType is null)
                    continue;

                if (payload.MsgData.Filter.EntityType == EntityType.Sensor)
                {
                    foreach (var sensorType in SensorType.GetValues())
                        yield return GetSensorEntity(unfoldedCircleConfigurationItem, sensorType);
                }
            }
            else
            {
                foreach (var sensorType in SensorType.GetValues())
                    yield return GetSensorEntity(unfoldedCircleConfigurationItem, sensorType);
            }
        }

        yield break;

        SensorAvailableEntity GetSensorEntity(UnfoldedCircleConfigurationItem configurationItem, in SensorType sensorType)
        {
            var sensorSuffix = sensorType.ToStringFast();
            var entityId = configurationItem.EntityId.GetIdentifier(EntityType.Sensor, sensorSuffix);
            RegisterSensor(entityId, sensorSuffix);
            return new SensorAvailableEntity
            {
                EntityId = entityId,
                EntityType = EntityType.Sensor,
                Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = $"{configurationItem.EntityName} {sensorType.ToStringFast(true)}" },
                DeviceId = configurationItem.DeviceId.GetNullableIdentifier(EntityType.Sensor),
                DeviceClass = DeviceClass.Custom,
                Options = GetSensorOptions(sensorType)
            };
        }

        static SensorOptions GetSensorOptions(in SensorType sensorType) =>
            sensorType switch
            {
                SensorType.MemoryPercentage => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.MemoryDetails => new SensorOptions
                {
                    CustomUnit = "MB",
                    Decimals = 1
                },
                SensorType.SwapPercentage => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.SwapDetails => new SensorOptions
                {
                    CustomUnit = "MB",
                    Decimals = 1
                },
                SensorType.CpuUsagePercentLast1Minute => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.CpuUsagePercentLast5Minutes => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.CpuUsagePercentLast15Minutes => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.FileSystemPercentage => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 1
                },
                SensorType.FileSystemDetails => new SensorOptions
                {
                    CustomUnit = "MB",
                    Decimals = 1
                },
                _ => new SensorOptions()
            };
    }
}
