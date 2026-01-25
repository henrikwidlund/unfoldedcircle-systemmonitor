using System.Text.Json.Serialization.Metadata;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.SystemMonitor.Json;

namespace UnfoldedCircle.SystemMonitor.Configuration;

public class SystemMonitorConfigurationService(IConfiguration configuration) : ConfigurationService<SystemMonitorConfigurationItem>(configuration)
{
    protected override JsonTypeInfo<UnfoldedCircleConfiguration<SystemMonitorConfigurationItem>> GetSerializer()
        => SystemMonitorSerializerContext.Default.UnfoldedCircleConfigurationSystemMonitorConfigurationItem;
}