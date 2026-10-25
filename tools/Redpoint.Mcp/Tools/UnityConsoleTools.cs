using System.ComponentModel;
using Redpoint.Mcp.Services;
using Redpoint.Mcp.Security;
using ModelContextProtocol.Server;

namespace Redpoint.Mcp.Tools;

[McpServerToolType]
public sealed class UnityConsoleTools(UnityConsoleService service)
{
    [McpServerTool(Name = McpCapabilityNames.GetUnityConsole, ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false)]
    [Description("Returns a bounded, redacted tail of the newest fixed project-scoped Unity script log.")]
    public UnityConsoleResult GetUnityConsole(
        [Description("Optional maximum entries from 1 to 100; defaults to 50.")] int? maxEntries = null,
        [Description("Optional minimum severity: info, warning, error, or exception.")] string? minimumSeverity = null) =>
        service.Get(maxEntries, minimumSeverity);
}
