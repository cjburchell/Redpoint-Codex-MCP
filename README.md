# Redpoint Codex MCP

This repository contains the DungeonEscape-focused MCP server, deterministic agent orchestrator,
approval-token component, project knowledge index, and their automated tests. The tooling is maintained
separately from the DungeonEscape game repository and operates on a DungeonEscape checkout supplied at startup.

## Projects

- `tools/Redpoint.Mcp` — bounded MCP capabilities for inspecting and operating on an approved
  DungeonEscape checkout.
- `tools/Redpoint.AgentOrchestrator` — deterministic Manager, Unity Engineer, Reviewer, and QA
  workflow with an optional OpenAI-backed specialist executor.
- `tools/Redpoint.WriteApproval` — shared cross-process approval-token contracts and HMAC
  implementation.
- `tools/Redpoint.KnowledgeIndex` provides standalone, read-only, versioned source/documentation
  indexing with deterministic offline and optional OpenAI embeddings, citations, and freshness checks.
- The matching `.Tests` projects provide unit and real stdio integration coverage.

## Requirements

- .NET 10 SDK
- A separate DungeonEscape checkout for real integration tests and demonstrations

## Build and test

```powershell
dotnet restore tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj
dotnet restore tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj
dotnet restore tools/Redpoint.KnowledgeIndex.Tests/Redpoint.KnowledgeIndex.Tests.csproj
dotnet build tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj --no-restore
dotnet build tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj --no-restore
dotnet build tools/Redpoint.KnowledgeIndex.Tests/Redpoint.KnowledgeIndex.Tests.csproj --no-restore
dotnet test tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj --no-build
$env:REDPOINT_TARGET_REPOSITORY_ROOT = "C:\path\to\DungeonEscape"
dotnet test tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj --no-build
dotnet test tools/Redpoint.KnowledgeIndex.Tests/Redpoint.KnowledgeIndex.Tests.csproj --no-build
```

When the repositories are siblings named `Redpoint-Codex-MCP` and `DungeonEscape`, the orchestrator
integration tests discover the DungeonEscape checkout automatically. Otherwise, set
`REDPOINT_TARGET_REPOSITORY_ROOT` to its absolute path.

## Run the MCP server

Always pass the target checkout explicitly now that the server is external:

```powershell
dotnet run --project tools/Redpoint.Mcp -- --repository-root C:\path\to\DungeonEscape
```

The server remains DungeonEscape-specific: approved roots, Unity layout, test suites, and safety
policies are deliberately fixed rather than caller-configurable. See
`tools/Redpoint.Mcp/README.md` for the complete capability and security model.

## Two-repository layout

```text
Development/
    Redpoint-Codex-MCP/
    DungeonEscape/
```

Redpoint binaries and tests resolve from this repository. Every MCP source operation resolves against
the separately validated target supplied with `--repository-root`; the current working directory is
never treated as implicit write authority.

## Human-approved write mode

The default orchestrator mode remains analysis-only. Approved writes require the explicit mode, an
exact Engineer proposal, Reviewer approval, trusted MCP preparation, and a deliberate console decision:

```powershell
$env:OPENAI_API_KEY = "<api-key>"
$env:REDPOINT_OPENAI_MODEL = "gpt-5.4-mini"

dotnet run --project tools/Redpoint.AgentOrchestrator -- `
  --provider openai `
  --mode approved-write `
  --repository-root "C:\Development\DungeonEscape" `
  --task "Make a small safe improvement to one existing source file."
```

The application refuses a dirty target working tree and truncated preview. Approve with `A`, reject
with `R`, or cancel with `C`; blank, unrecognized, redirected, or unavailable input never approves.
Only after approval does trusted application code sign the authoritative prepared scope and call
`apply_write`. QA runs after confirmed application. The workflow supports one existing UTF-8 text-file
replacement, does not persist approvals, and performs no Git mutation.

## Project knowledge index

AI-030 adds an independent indexer; it does not expose retrieval through MCP or change any agent role.
It reads an explicit Git-tracked corpus from a separate checkout and stores an atomic JSON index in the
user's local application-data directory by default. The index records source hashes, commit metadata,
provider/model identity, stable chunks, vectors, and line citations. Search refuses stale or incompatible
indexes. See `tools/Redpoint.KnowledgeIndex/README.md` for corpus rules, CLI examples, provider setup,
storage/removal instructions, limits, and security exclusions.
