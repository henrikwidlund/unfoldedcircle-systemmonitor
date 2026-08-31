using System.Text.Json.Serialization.Metadata;

using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.SystemMonitor.Json;

namespace UnfoldedCircle.SystemMonitor.Configuration;

public class SystemMonitorConfigurationService(IConfiguration configuration) : ConfigurationService<UnfoldedCircleGlobalConfiguration, SystemMonitorConfigurationItem>(configuration)
{
    protected override JsonTypeInfo<UnfoldedCircleConfiguration<UnfoldedCircleGlobalConfiguration, SystemMonitorConfigurationItem>> GetSerializer()
        => SystemMonitorSerializerContext.Default.UnfoldedCircleConfigurationUnfoldedCircleGlobalConfigurationSystemMonitorConfigurationItem;
}
