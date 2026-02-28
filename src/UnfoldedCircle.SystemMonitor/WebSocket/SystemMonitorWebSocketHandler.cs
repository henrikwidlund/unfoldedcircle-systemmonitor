using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
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
    IConfigurationService<SystemMonitorConfigurationItem> configurationService,
    IOptions<UnfoldedCircleOptions> options,
    IServiceProvider serviceProvider,
    ILogger<SystemMonitorWebSocketHandler> logger)
    : UnfoldedCircleWebSocketHandler<MediaPlayerCommandId, SystemMonitorConfigurationItem>(configurationService, options, logger)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    protected override ValueTask<EntityCommandResult> OnRemoteCommandAsync(System.Net.WebSockets.WebSocket socket, RemoteEntityCommandMsgData payload, string command, string wsId, CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken) =>
        ValueTask.FromResult(EntityCommandResult.Failure);

    protected override ValueTask<EntityCommandResult> OnClimateHvacModeCommandAsync(System.Net.WebSockets.WebSocket socket,
        ClimateEntityCommandMsgData payload,
        HvacMode hvacMode,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(EntityCommandResult.Other);

    protected override ValueTask<EntityCommandResult> OnClimatePowerCommandAsync(System.Net.WebSockets.WebSocket socket,
        ClimateEntityCommandMsgData payload,
        bool powerOn,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(EntityCommandResult.Other);

    protected override ValueTask<EntityCommandResult> OnClimateTargetTemperatureCommandAsync(System.Net.WebSockets.WebSocket socket,
        ClimateEntityCommandMsgData payload,
        float targetTemperature,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(EntityCommandResult.Other);

    protected override ValueTask<SelectCommandResult> OnSelectOptionCommandAsync(System.Net.WebSockets.WebSocket socket,
        SelectEntityCommandMsgData payload,
        string option,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(new SelectCommandResult(EntityCommandResult.Other, string.Empty));

    protected override ValueTask<SelectCommandResult> OnSelectFirstLastCommandAsync(System.Net.WebSockets.WebSocket socket,
        SelectEntityCommandMsgData payload,
        bool first,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(new SelectCommandResult(EntityCommandResult.Other, string.Empty));

    protected override ValueTask<SelectCommandResult> OnSelectNextPreviousCommandAsync(System.Net.WebSockets.WebSocket socket,
        SelectEntityCommandMsgData payload,
        bool next,
        bool cycle,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult(new SelectCommandResult(EntityCommandResult.Other, string.Empty));

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
            entity = new SystemMonitorConfigurationItem
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

    protected override async Task HandleEventUpdatesAsync(System.Net.WebSockets.WebSocket socket, string wsId, SubscribedEntitiesHolder subscribedEntitiesHolder, CancellationToken cancellationToken)
    {
        (string? apiKey, sbyte? intervalSeconds) = await WaitForValidConfig(cancellationToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.NoConfigurationsFound(wsId);
            return;
        }

        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds ?? 10));
        do
        {
            if (subscribedEntitiesHolder.SubscribedEntities.Count == 0)
                continue;

            var flattenedSubscribedEntityIds = subscribedEntitiesHolder.SubscribedEntities
                .SelectMany(static x => x.Value)
                .Select(static x => x.EntityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            (bool useBatteryEndpoint, bool useSystemEndpoint) = GetEndpointTypes(flattenedSubscribedEntityIds);

            await using var scope = _serviceProvider.CreateAsyncScope();
            var systemMonitorClient = scope.ServiceProvider.GetRequiredService<SystemMonitorClient>();
            try
            {
                await ProcessMonitors(socket, wsId, useSystemEndpoint, systemMonitorClient, useBatteryEndpoint, apiKey, flattenedSubscribedEntityIds, cancellationToken);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                    _logger.FailureDuringEvent(e, wsId);
            }
            finally
            {
                PreviousSensorValuesMap.Clear();
            }
        } while (!cancellationToken.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ProcessMonitors(System.Net.WebSockets.WebSocket socket, string wsId, bool useSystemEndpoint, SystemMonitorClient systemMonitorClient, bool useBatteryEndpoint, string apiKey,
        HashSet<string> flattenedSubscribedEntityIds, CancellationToken cancellationToken)
    {
        var monitorResponse = useSystemEndpoint ? await systemMonitorClient.GetSystemStatusAsync(wsId, cancellationToken) : null;
        var batteryLevel = useBatteryEndpoint ? await systemMonitorClient.GetBatteryLevelAsync(wsId, apiKey, cancellationToken) : null;
        if (monitorResponse is null && batteryLevel is null)
            return;

        await Parallel.ForEachAsync(SensorType.GetValues()
            .Select(static x => (sensorType: x, entityId: EntityId.GetIdentifier(EntityType.Sensor, x.ToStringFast())))
            .Where(x => flattenedSubscribedEntityIds.Contains(x.entityId)), cancellationToken, (async (arg, token) =>
        {
            (SensorType sensorType, string entityId) = arg;
            await (sensorType switch
            {
                SensorType.MemoryPercentage => SendMemoryPercentageSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.MemoryDetails => SendMemoryDetailsSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.SwapPercentage => SendSwapPercentageSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.SwapDetails => SendSwapDetailsSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.CpuUsagePercentLast1Minute => SendCpuUsagePercentLast1MinuteSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.CpuUsagePercentLast5Minutes => SendCpuUsagePercentLast5MinuteSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.CpuUsagePercentLast15Minutes => SendCpuUsagePercentLast15MinuteSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.FileSystemPercentage => SendFileSystemPercentageSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.FileSystemDetails => SendFileSystemDetailsSensor(socket, wsId, entityId, monitorResponse, token),
                SensorType.BatteryPercentage => SendBatteryPercentageSensor(socket, wsId, entityId, batteryLevel, token),
                _ => Task.CompletedTask
            });
        }));
    }

    private async Task<(string? apiKey, sbyte? intervalSeconds)> WaitForValidConfig(CancellationToken cancellationToken)
    {
        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
            var configurationItem = configuration.Entities.FirstOrDefault();
            if (!string.IsNullOrEmpty(configurationItem?.ApiKey))
                return (configurationItem.ApiKey, configurationItem.IntervalSeconds);
        } while (!cancellationToken.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(cancellationToken));

        return default;
    }

    private static (bool useBatteryEndpoint, bool useSystemEndpoint) GetEndpointTypes(HashSet<string> flattenedSubscribedEntityIds)
    {
        bool useBatteryEndpoint = false, useSystemEndpoint = false;
        foreach (var entityId in flattenedSubscribedEntityIds)
        {
            if (entityId.EndsWith(nameof(SensorType.BatteryPercentage), StringComparison.OrdinalIgnoreCase))
                useBatteryEndpoint = true;
            else
                useSystemEndpoint = true;

            if (useBatteryEndpoint && useSystemEndpoint)
                break;
        }

        return (useBatteryEndpoint, useSystemEndpoint);
    }

    private static readonly ConcurrentDictionary<SensorType, int> PreviousSensorValuesMap = new();

    private async Task SendMemoryPercentageSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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
        SystemMonitorResponse? systemMonitorResponse,
        CancellationToken cancellationToken)
    {
        if (systemMonitorResponse is null)
            return;

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

    private async Task SendBatteryPercentageSensor(System.Net.WebSockets.WebSocket socket,
        string wsId,
        string entityId,
        int? batteryPercentage,
        CancellationToken cancellationToken)
    {
        if (batteryPercentage is null)
            return;

        if (PreviousSensorValuesMap.TryGetValue(SensorType.BatteryPercentage, out var previousValue) &&
            previousValue == batteryPercentage.Value)
            return;

        PreviousSensorValuesMap[SensorType.BatteryPercentage] = batteryPercentage.Value;

        await SendMessageAsync(socket,
            ResponsePayloadHelpers.CreateSensorStateChangedResponsePayload(
                new SensorStateChangedEventMessageDataAttributes<int>
                {
                    State = SensorState.On,
                    Value = batteryPercentage.Value
                },
                entityId,
                nameof(SensorType.BatteryPercentage)),
            wsId,
            cancellationToken);
    }

    protected override ValueTask<DeviceState> OnGetDeviceStateAsync(GetDeviceStateMsg payload, string wsId, CancellationToken cancellationToken)
    {
        // not supported
        return ValueTask.FromResult(DeviceState.Error);
    }

    protected override async ValueTask<IReadOnlyCollection<AvailableEntity>> OnGetAvailableEntitiesAsync(GetAvailableEntitiesMsg payload, string wsId, CancellationToken cancellationToken)
    {
        return GetAvailableEntities(await GetEntitiesAsync(wsId, payload.MsgData.Filter?.EntityType, cancellationToken), payload).ToArray();
    }

    protected override ValueTask OnSubscribeEventsAsync(System.Net.WebSockets.WebSocket socket, SubscribeEventsMsg payload, string wsId, CancellationTokenWrapper cancellationTokenWrapper, CancellationToken commandCancellationToken)
    {
        if (payload.MsgData?.EntityIds is not { Length: > 0 })
            return ValueTask.CompletedTask;

        foreach (string msgDataEntityId in payload.MsgData.EntityIds)
            cancellationTokenWrapper.AddSubscribedEntity(msgDataEntityId);

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnUnsubscribeEventsAsync(UnsubscribeEventsMsg payload, string wsId, CancellationTokenWrapper cancellationTokenWrapper)
    {
        if (payload.MsgData?.EntityIds is { Length: > 0 })
        {
            foreach (string msgDataEntityId in payload.MsgData.EntityIds)
                cancellationTokenWrapper.RemoveSubscribedEntity(msgDataEntityId);
        }

        // If no specific device or entity was specified, dispose all clients for this websocket ID.
        if (payload.MsgData is { DeviceId: null, EntityIds: null })
            cancellationTokenWrapper.RemoveAllSubscribedEntities();

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

    protected override ValueTask<SetupDriverUserDataResult> OnSetupDriverUserDataConfirmAsync(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.FromResult(SetupDriverUserDataResult.Finalized);

    protected override ValueTask<SettingsPage> CreateNewEntitySettingsPageAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(CreateSettingsPage(null));

    private static SettingsPage CreateSettingsPage(SystemMonitorConfigurationItem? configurationItem) => new()
    {
        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "System Monitor Configuration" },
        Settings =
        [
            new Setting
            {
                Field = new SettingTypeText { Text = new ValueRegex { RegEx = "^\\d{4}$" } },
                Id = "pin",
                Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "en", "Enter the remote's PIN code" } }
            },
            new Setting
            {
                Field = new SettingTypeNumber{ Number = new SettingTypeNumberInner{ Decimals = 0, Min = 1, Max = 120, Steps = 1, Value = configurationItem?.IntervalSeconds ?? 10 } },
                Id = "interval",
                Label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "en", "Enter the stats poll interval (seconds)" } }
            }
        ]
    };

    private const string EntityId = "local";

    protected override ValueTask<SettingsPage> CreateReconfigureEntitySettingsPageAsync(SystemMonitorConfigurationItem configurationItem, CancellationToken cancellationToken)
        => ValueTask.FromResult(CreateSettingsPage(configurationItem));

    protected override ValueTask<SetupDriverUserDataResult> HandleEntityReconfigured(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, SystemMonitorConfigurationItem configurationItem,
        CancellationToken cancellationToken) =>
        HandleSetup(payload, wsId, configurationItem, cancellationToken);

    private async ValueTask<SetupDriverUserDataResult> HandleSetup(SetDriverUserDataMsg payload, string wsId, SystemMonitorConfigurationItem configurationItem, CancellationToken cancellationToken)
    {
        var pin = payload.MsgData.InputValues?.TryGetValue("pin", out var pinValue) == true ? pinValue : null;
        if (string.IsNullOrEmpty(pin))
            return SetupDriverUserDataResult.Error;

        sbyte interval = payload.MsgData.InputValues?.TryGetValue("interval", out var intervalValue) == true
                       && sbyte.TryParse(intervalValue, NumberFormatInfo.InvariantInfo, out var intervalParsed)
            ? intervalParsed
            : (sbyte)10;
        await using var scope = _serviceProvider.CreateAsyncScope();
        var systemMonitorClient = scope.ServiceProvider.GetRequiredService<SystemMonitorClient>();
        var apiKey = await systemMonitorClient.GetApiKeyAsync(wsId, pin, cancellationToken);
        if (apiKey is null)
            return SetupDriverUserDataResult.Error;

        var newConfigurationItem = configurationItem with
        {
            ApiKey = apiKey,
            IntervalSeconds = interval
        };
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        configuration.Entities.Remove(configurationItem);
        configuration.Entities.Add(newConfigurationItem);
        await _configurationService.UpdateConfigurationAsync(configuration, cancellationToken);
        return SetupDriverUserDataResult.Finalized;
    }

    protected override async ValueTask<SetupDriverUserDataResult> HandleCreateNewEntity(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
        var configurationItem = configuration.Entities.FirstOrDefault();
        if (configurationItem is null)
        {
            configurationItem = new SystemMonitorConfigurationItem
            {
                Host = "localhost",
                EntityName = "Remote",
                EntityId = EntityId
            };
            configuration.Entities.Add(configurationItem);
            await _configurationService.UpdateConfigurationAsync(configuration, cancellationToken);
        }

        return await HandleSetup(payload, wsId, configurationItem, cancellationToken);
    }

    protected override FrozenSet<EntityType> SupportedEntityTypes { get; } = [EntityType.Sensor];

    private async Task<List<SystemMonitorConfigurationItem>?> GetEntitiesAsync(
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
        List<SystemMonitorConfigurationItem>? entities,
        GetAvailableEntitiesMsg payload)
    {
        if (entities is not { Count: > 0 })
            yield break;

        var hasEntityTypeFilter = payload.MsgData.Filter?.EntityType is not null;
        foreach (var systemMonitorConfigurationItem in entities)
        {
            if (hasEntityTypeFilter)
            {
                if (payload.MsgData.Filter?.EntityType is null)
                    continue;

                if (payload.MsgData.Filter.EntityType == EntityType.Sensor)
                {
                    foreach (var sensorType in SensorType.GetValues())
                        yield return GetSensorEntity(systemMonitorConfigurationItem, sensorType);
                }
            }
            else
            {
                foreach (var sensorType in SensorType.GetValues())
                    yield return GetSensorEntity(systemMonitorConfigurationItem, sensorType);
            }
        }

        yield break;

        SensorAvailableEntity GetSensorEntity(SystemMonitorConfigurationItem configurationItem, in SensorType sensorType)
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
                SensorType.BatteryPercentage => new SensorOptions
                {
                    CustomUnit = "%",
                    Decimals = 0
                },
                _ => new SensorOptions()
            };
    }
}
