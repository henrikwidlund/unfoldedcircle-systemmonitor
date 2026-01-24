using System.Text.Json;
using System.Text.Json.Serialization;
using UnfoldedCircle.SystemMonitor.Http;

namespace UnfoldedCircle.SystemMonitor.Json;

[JsonSerializable(typeof(SystemMonitorResponse))]
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