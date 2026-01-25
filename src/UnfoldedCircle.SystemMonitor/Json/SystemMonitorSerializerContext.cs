using System.Text.Json;
using System.Text.Json.Serialization;

using UnfoldedCircle.Server.Configuration;
using UnfoldedCircle.SystemMonitor.Configuration;
using UnfoldedCircle.SystemMonitor.Http;

namespace UnfoldedCircle.SystemMonitor.Json;

[JsonSerializable(typeof(UnfoldedCircleConfiguration<SystemMonitorConfigurationItem>))]
[JsonSerializable(typeof(SystemMonitorResponse))]
[JsonSerializable(typeof(ApiKeyRequest))]
[JsonSerializable(typeof(ApiKeyResponse))]
internal sealed partial class SystemMonitorSerializerContext : JsonSerializerContext
{
    static SystemMonitorSerializerContext()
    {
        Default = new SystemMonitorSerializerContext(new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}