using System.ComponentModel;
using Redpoint.Mcp.Services;
using Redpoint.Mcp.Security;
using ModelContextProtocol.Server;

namespace Redpoint.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectInfoTools(ProjectInfoService service)
{
    [McpServerTool(Name = McpCapabilityNames.GetProjectInfo, ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Returns DungeonEscape repository metadata; unavailable values are explicitly marked.")]
    public ProjectInfo GetProjectInfo() => service.GetProjectInfo();
}
