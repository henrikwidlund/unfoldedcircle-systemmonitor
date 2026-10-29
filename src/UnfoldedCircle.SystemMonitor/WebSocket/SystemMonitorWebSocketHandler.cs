using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
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
using UnfoldedCircle.SystemMonitor.Json;
using UnfoldedCircle.SystemMonitor.Logging;

namespace UnfoldedCircle.SystemMonitor.WebSocket;

internal sealed class SystemMonitorWebSocketHandler(
    IConfigurationService<UnfoldedCircleGlobalConfiguration, SystemMonitorConfigurationItem> configurationService,
    IOptions<UnfoldedCircleOptions> options,
    IServiceProvider serviceProvider,
    ILogger<SystemMonitorWebSocketHandler> logger)
    : UnfoldedCircleWebSocketHandler<MediaPlayerCommandId, UnfoldedCircleGlobalConfiguration, SystemMonitorConfigurationItem>(configurationService, options, logger)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private static readonly TimeSpan MinPollBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxPollBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WakeUpRetryWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WakeUpRetryDelay = TimeSpan.FromSeconds(1);
    private volatile bool _isInStandby;
    private long _exitStandbyTimestamp;
    // Released to start a poll before the poll interval has passed
    private readonly SemaphoreSlim _pollRequest = new(0, 1);

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
        => ValueTask.FromResult<SelectCommandResult>(new SelectFailed());

    protected override ValueTask<SelectCommandResult> OnSelectFirstLastCommandAsync(System.Net.WebSockets.WebSocket socket,
        SelectEntityCommandMsgData payload,
        bool first,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult<SelectCommandResult>(new SelectFailed());

    protected override ValueTask<SelectCommandResult> OnSelectNextPreviousCommandAsync(System.Net.WebSockets.WebSocket socket,
        SelectEntityCommandMsgData payload,
        bool next,
        bool cycle,
        string wsId,
        CancellationTokenWrapper cancellationTokenWrapper,
        CancellationToken commandCancellationToken)
        => ValueTask.FromResult<SelectCommandResult>(new SelectFailed());

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
    {
        _isInStandby = true;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnExitStandbyAsync(ExitStandbyEvent payload, string wsId, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _exitStandbyTimestamp, Stopwatch.GetTimestamp());
        _isInStandby = false;
        return ValueTask.CompletedTask;
    }

    private void RequestPoll()
    {
        if (_pollRequest.CurrentCount > 0)
            return;

        try
        {
            _pollRequest.Release();
        }
        catch (SemaphoreFullException)
        {
            // A poll is already requested
        }
    }

    protected override async Task HandleEventUpdatesAsync(System.Net.WebSockets.WebSocket socket, string wsId, SubscribedEntitiesHolder subscribedEntitiesHolder, CancellationToken cancellationToken)
    {
        (string? apiKey, sbyte? intervalSeconds) = await WaitForValidConfig(cancellationToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.NoConfigurationsFound(wsId);
            return;
        }

        // Clear an earlier poll request, the loop polls at start anyway
        // ReSharper disable once MethodHasAsyncOverloadWithCancellation
        _pollRequest.Wait(0);
        var pollInterval = TimeSpan.FromSeconds(intervalSeconds ?? 10);
        TimeSpan nextPollDelay;
        var pollBackoff = TimeSpan.Zero;
        long lastFailureTimestamp = 0;
        do
        {
            nextPollDelay = pollInterval;
            if (subscribedEntitiesHolder.SubscribedEntities.Count == 0)
                continue;

            // Do not poll the remote's API while it is in standby.
            if (_isInStandby)
                continue;

            if (pollBackoff > TimeSpan.Zero && Stopwatch.GetElapsedTime(lastFailureTimestamp) < pollBackoff)
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
                pollBackoff = TimeSpan.Zero;
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                    _logger.FailureDuringEvent(wsId, e);

                // The API can be slow right after wake-up: try again soon without back-off.
                if (!cancellationToken.IsCancellationRequested && Stopwatch.GetElapsedTime(Interlocked.Read(ref _exitStandbyTimestamp)) < WakeUpRetryWindow)
                {
                    nextPollDelay = WakeUpRetryDelay;
                }
                // Unreachable API: wait longer after each failure.
                else if (!cancellationToken.IsCancellationRequested)
                {
                    pollBackoff = pollBackoff == TimeSpan.Zero
                        ? MinPollBackoff
                        : TimeSpan.FromTicks(Math.Min(pollBackoff.Ticks * 2, MaxPollBackoff.Ticks));
                    lastFailureTimestamp = Stopwatch.GetTimestamp();
                }
            }
        } while (!cancellationToken.IsCancellationRequested && await WaitForNextPollAsync(nextPollDelay, cancellationToken));
    }

    // Returns early when a poll is requested
    private async Task<bool> WaitForNextPollAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await _pollRequest.WaitAsync(delay, cancellationToken);
        return true;
    }

    private async Task ProcessMonitors(System.Net.WebSockets.WebSocket socket, string wsId, bool useSystemEndpoint, SystemMonitorClient systemMonitorClient, bool useBatteryEndpoint, string apiKey,
        HashSet<string> flattenedSubscribedEntityIds, CancellationToken cancellationToken)
    {
        var monitorResponse = useSystemEndpoint ? await systemMonitorClient.GetSystemStatusAsync(wsId, cancellationToken) : null;
        var batteryLevel = useBatteryEndpoint ? await systemMonitorClient.GetBatteryLevelAsync(wsId, apiKey, cancellationToken) : null;
        if (monitorResponse is null && batteryLevel is null)
            return;

        foreach (var sensorType in SensorType.GetValues())
        {
            var suffix = sensorType.ToStringFast();
            var entityId = EntityId.GetIdentifier(EntityType.Sensor, suffix);
            if (!flattenedSubscribedEntityIds.Contains(entityId)
                || CreateSensorState(sensorType, entityId, monitorResponse, batteryLevel) is not { } state
                || (SentStates.TryGetValue(sensorType, out var previousState) && previousState.Equals(state)))
                continue;

            SentStates[sensorType] = state;
            await SendMessageAsync(socket, CreateSensorStateChangedPayload(state, suffix), wsId, cancellationToken);
        }
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

    // Last sent state per sensor, also used for get_entity_states
    private static readonly ConcurrentDictionary<SensorType, EntityStateChanged> SentStates = new();

    private static EntityStateChanged? CreateSensorState(SensorType sensorType,
        string entityId,
        SystemMonitorResponse? systemMonitorResponse,
        int? batteryPercentage)
    {
        if (sensorType == SensorType.BatteryPercentage)
            return batteryPercentage is { } battery ? CreateSensorState(entityId, battery) : null;

        if (systemMonitorResponse is null)
            return null;

        return sensorType switch
        {
            SensorType.MemoryPercentage => CreateSensorState(entityId, systemMonitorResponse.Memory.GetMemoryUsagePercentage()),
            SensorType.MemoryDetails => CreateSensorState(entityId, systemMonitorResponse.Memory.GetMemoryUsageDetails()),
            SensorType.SwapPercentage => CreateSensorState(entityId, systemMonitorResponse.Memory.GetSwapUsagePercentage()),
            SensorType.SwapDetails => CreateSensorState(entityId, systemMonitorResponse.Memory.GetSwapUsageDetails()),
            SensorType.CpuUsagePercentLast1Minute => CreateSensorState(entityId, systemMonitorResponse.LoadAvg.GetLoadLastMinute()),
            SensorType.CpuUsagePercentLast5Minutes => CreateSensorState(entityId, systemMonitorResponse.LoadAvg.GetLoadLastFiveMinutes()),
            SensorType.CpuUsagePercentLast15Minutes => CreateSensorState(entityId, systemMonitorResponse.LoadAvg.GetLoadLastFifteenMinutes()),
            SensorType.FileSystemPercentage => CreateSensorState(entityId, systemMonitorResponse.Filesystem.UserData.GetPercentage()),
            SensorType.FileSystemDetails => CreateSensorState(entityId, systemMonitorResponse.Filesystem.UserData.GetDetails()),
            _ => null
        };
    }

    private static SensorEntityStateChanged<TValue> CreateSensorState<TValue>(string entityId, TValue value) => new()
    {
        EntityId = entityId,
        EntityType = EntityType.Sensor,
        Attributes = new SensorStateChangedEventMessageDataAttributes<TValue> { State = SensorState.On, Value = value }
    };

    private static byte[] CreateSensorStateChangedPayload(EntityStateChanged state, string suffix) =>
        state switch
        {
            SensorEntityStateChanged<double> doubleState => ResponsePayloadHelpers.CreateSensorStateChangedPayload(doubleState.Attributes, doubleState.EntityId, suffix),
            SensorEntityStateChanged<string> stringState => ResponsePayloadHelpers.CreateSensorStateChangedPayload(stringState.Attributes, stringState.EntityId, suffix),
            SensorEntityStateChanged<int> intState => ResponsePayloadHelpers.CreateSensorStateChangedPayload(intState.Attributes, intState.EntityId, suffix),
            _ => throw new UnreachableException()
        };

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

        // New subscribers need the current values, send them all with the next poll
        SentStates.Clear();
        RequestPoll();
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

    protected override async ValueTask<EntityStateChanged[]> OnGetEntityStatesAsync(GetEntityStatesMsg payload, string wsId, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await _configurationService.GetConfigurationAsync(cancellationToken);
            var apiKey = configuration.Entities.FirstOrDefault()?.ApiKey;
            await using var scope = _serviceProvider.CreateAsyncScope();
            var systemMonitorClient = scope.ServiceProvider.GetRequiredService<SystemMonitorClient>();
            var monitorResponse = await systemMonitorClient.GetSystemStatusAsync(wsId, cancellationToken);
            var batteryLevel = string.IsNullOrEmpty(apiKey) ? null : await systemMonitorClient.GetBatteryLevelAsync(wsId, apiKey, cancellationToken);
            foreach (var sensorType in SensorType.GetValues())
            {
                if (CreateSensorState(sensorType, EntityId.GetIdentifier(EntityType.Sensor, sensorType.ToStringFast()), monitorResponse, batteryLevel) is { } state)
                    SentStates[sensorType] = state;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Last sent states are used instead
            _logger.FailureDuringEvent(wsId, e);
        }

        return [.. SentStates.Values];
    }

    protected override MediaPlayerEntityCommandMsgData<MediaPlayerCommandId>? DeserializeMediaPlayerCommandPayload(JsonDocument jsonDocument)
        => null;

    protected override ValueTask<SetupDriverUserDataResult> OnSetupDriverUserDataConfirmAsync(System.Net.WebSockets.WebSocket socket, SetDriverUserDataMsg payload, string wsId, CancellationToken cancellationToken)
        => ValueTask.FromResult(SetupDriverUserDataResult.Finalized);

    protected override async ValueTask<string> GetJsonBackupDataAsync(CancellationToken cancellationToken)
    {
        var config = await _configurationService.GetConfigurationAsync(cancellationToken);
        return JsonSerializer.Serialize(config, SystemMonitorSerializerContext.Default.UnfoldedCircleConfigurationUnfoldedCircleGlobalConfigurationSystemMonitorConfigurationItem);
    }

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

    protected override async ValueTask<RestoreResult> HandleRestoreFromBackupAsync(string wsId, string jsonRestoreData, CancellationToken cancellationToken)
    {
        try
        {
            var config = JsonSerializer.Deserialize(jsonRestoreData, SystemMonitorSerializerContext.Default.UnfoldedCircleConfigurationUnfoldedCircleGlobalConfigurationSystemMonitorConfigurationItem);
            if (config is null)
            {
                _logger.BackupDataNullDuringRestore(wsId);
                return RestoreResult.Failure;
            }

            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // source generated deserialize doesn't care about default values
            var restoredConfig = config.GlobalConfiguration is null
                ? config with { GlobalConfiguration = new UnfoldedCircleGlobalConfiguration() }
                : config;
            await _configurationService.UpdateConfigurationAsync(restoredConfig, cancellationToken);
            return RestoreResult.Success;
        }
        catch (Exception e)
        {
            _logger.ExceptionDuringRestore(wsId, e);
            return RestoreResult.Failure;
        }
    }

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

    private static IEnumerable<AvailableEntity> GetAvailableEntities(
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

        static SensorAvailableEntity GetSensorEntity(SystemMonitorConfigurationItem configurationItem, SensorType sensorType)
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

        static SensorOptions GetSensorOptions(SensorType sensorType) =>
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
