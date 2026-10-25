using System.ComponentModel;
using Redpoint.Mcp.Services;
using Redpoint.Mcp.Security;
using ModelContextProtocol.Server;

namespace Redpoint.Mcp.Tools;

[McpServerToolType]
public sealed class ListScenesTools(ListScenesService service)
{
    [McpServerTool(Name = McpCapabilityNames.ListScenes, ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Lists bounded Unity scene paths and their build-settings status without reading scene contents.")]
    public ListScenesResult ListScenes() => service.List();
}
