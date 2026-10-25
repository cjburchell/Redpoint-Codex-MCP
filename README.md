# Redpoint Codex MCP

This repository contains the DungeonEscape-focused MCP server, deterministic agent orchestrator,
approval-token component, and their automated tests. The tooling is maintained separately from the
DungeonEscape game repository and operates on a DungeonEscape checkout supplied at startup.

## Projects

- `tools/Redpoint.Mcp` — bounded MCP capabilities for inspecting and operating on an approved
  DungeonEscape checkout.
- `tools/Redpoint.AgentOrchestrator` — deterministic Manager, Unity Engineer, Reviewer, and QA
  workflow with an optional OpenAI-backed specialist executor.
- `tools/Redpoint.WriteApproval` — shared cross-process approval-token contracts and HMAC
  implementation.
- The matching `.Tests` projects provide unit and real stdio integration coverage.

## Requirements

- .NET 10 SDK
- A separate DungeonEscape checkout for real integration tests and demonstrations

## Build and test

```powershell
dotnet restore tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj
dotnet restore tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj
dotnet build tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj --no-restore
dotnet build tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj --no-restore
dotnet test tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj --no-build
$env:DUNGEONESCAPE_REPOSITORY_ROOT = "C:\path\to\DungeonEscape"
dotnet test tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj --no-build
```

When the repositories are siblings named `Redpoint-Codex-MCP` and `DungeonEscape`, the orchestrator
integration tests discover the DungeonEscape checkout automatically. Otherwise, set
`DUNGEONESCAPE_REPOSITORY_ROOT` to its absolute path.

## Run the MCP server

Always pass the target checkout explicitly now that the server is external:

```powershell
dotnet run --project tools/Redpoint.Mcp -- --repository-root C:\path\to\DungeonEscape
```

The server remains DungeonEscape-specific: approved roots, Unity layout, test suites, and safety
policies are deliberately fixed rather than caller-configurable. See
`tools/Redpoint.Mcp/README.md` for the complete capability and security model.
