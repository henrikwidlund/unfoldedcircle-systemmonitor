using System.Text.Json.Serialization.Metadata;
using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.Server.Json;

namespace UnfoldedCircle.SystemMonitor.Configuration;

public class SystemMonitorConfigurationService(IConfiguration configuration) : ConfigurationService<UnfoldedCircleConfigurationItem>(configuration)
{
    protected override JsonTypeInfo<UnfoldedCircleConfiguration<UnfoldedCircleConfigurationItem>> GetSerializer()
        => UnfoldedCircleJsonSerializerContext.Default.UnfoldedCircleConfigurationUnfoldedCircleConfigurationItem;
}