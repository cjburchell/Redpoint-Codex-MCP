# Source-cited specialist retrieval

Run these commands from the Redpoint-Codex-MCP checkout in PowerShell. Requirements: .NET 10 SDK,
Git, and a separate ordinary DungeonEscape checkout. Unity is not required for this example.
Linked Git worktrees remain unsupported by the existing repository/index discovery policy.

## Offline end-to-end demonstration

```powershell
# Change this to your target checkout. It must contain DungeonEscape.sln, Core and Unity directories.
$target = 'E:\dev\cburchell\apps\DungeonEscape'
powershell -ExecutionPolicy Bypass -File .\scripts\run-knowledge-demo.ps1 -RepositoryRoot $target
```

With PowerShell 7 on Linux, use `pwsh -File ./scripts/run-knowledge-demo.ps1 -RepositoryRoot /path/to/DungeonEscape`.
Linux execution has not been validated in the current development environment.

The script restores/builds Redpoint, builds and validates an offline index outside the target checkout,
then starts the analysis orchestrator. Engineer and Reviewer each independently retrieve actual source
evidence. QA runs the existing bounded orchestrator test suite through MCP. The specialists in this
demo are deterministic scripts: this proves orchestration and real retrieval, not model reasoning.
No API credentials are needed; no model/embedding API calls are made. Tests may generate local artifacts.

Expected output (counts and citation ranges depend on the checkout):

```text
"filesIndexed": 204,
"chunks": 740,
"freshness": "Current"
CONTEXT role=UnityEngineer status=Available
EVIDENCE role=UnityEngineer citation=DungeonEscape.Core/Rules/StoreRules.cs:217-277
CONTEXT role=Reviewer status=Available
EVIDENCE role=Reviewer citation=DungeonEscape.Core/Rules/StoreRules.cs:217-277
RESULT status=Completed engineerAttempts=1 reviewerAttempts=1 qaAttempts=1
REVIEW Approve
QA Pass suites=orchestrator
```

The default demo index is in the platform local application-data directory under
`Redpoint/KnowledgeIndex/ai-031-demo/index-v2.json`. `-IndexPath <absolute path>` overrides it.
The script temporarily sets knowledge configuration and restores the previous environment afterward.
It stops on any command failure. Deleting this generated JSON removes the demo cache.

Schema version 2 corrects end-line citations for newline-terminated files. Schema-version-1 indexes
are incompatible and are never searched. Rebuild them explicitly with `build` or `rebuild`; the default
cache filename is now `index-v2.json`. If `REDPOINT_KNOWLEDGE_INDEX_PATH` points to an old custom file,
keep the path and run `rebuild` to replace it atomically, or select a new absolute path and run `build`.

## Individual commands and configuration

```powershell
dotnet restore Redpoint.Codex.Mcp.slnx
dotnet build Redpoint.Codex.Mcp.slnx --no-restore
$target = 'E:\dev\cburchell\apps\DungeonEscape'
$index = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Redpoint/KnowledgeIndex/ai-031-demo/index-v2.json'

dotnet run --no-build --project tools/Redpoint.KnowledgeIndex -- build --repository-root $target --index-path $index --provider offline --dimensions 4096
dotnet run --no-build --project tools/Redpoint.KnowledgeIndex -- validate --repository-root $target --index-path $index --provider offline --dimensions 4096

$env:REDPOINT_KNOWLEDGE_INDEX_PATH = $index
$env:REDPOINT_KNOWLEDGE_PROVIDER = 'offline'
$env:REDPOINT_KNOWLEDGE_DIMENSIONS = '4096'
dotnet run --no-build --project tools/Redpoint.AgentOrchestrator -- --demo --knowledge --mode analysis --repository-root $target
```

`--knowledge` explicitly enables retrieval. Without it, existing workflows keep their original tools.
The repository root is selected once by trusted startup and validated by MCP before retrieval is wired.
Models cannot choose a different root, index file, embedding provider, model, or dimensions.

| Environment variable | Default / purpose |
|---|---|
| `REDPOINT_KNOWLEDGE_PROVIDER` | `offline`; alternatively `openai` |
| `REDPOINT_KNOWLEDGE_INDEX_PATH` | AI-030's per-repository local application-data path; an override must be absolute |
| `REDPOINT_KNOWLEDGE_DIMENSIONS` | `4096` offline, `1536` OpenAI; must match the built index |
| `REDPOINT_KNOWLEDGE_MODEL` | OpenAI embedding model; default `text-embedding-3-small`; ignored for offline |
| `OPENAI_API_KEY` | Required only for the OpenAI agent or embedding provider; process environment only |
| `REDPOINT_OPENAI_MODEL` | Required Responses-capable agent model for `--provider openai` |
| `REDPOINT_LLM_TIMEOUT_SECONDS` | Existing specialist timeout, default `120`, maximum `300` |
| `REDPOINT_LLM_MAX_TOOL_CALLS` | Existing per-execution total tool budget, default `8`, maximum `20` |

## OpenAI-backed specialists

Use the same offline index/configuration above to keep query embedding local. Supply an existing API
key through your session environment or secret manager. To enter one interactively without displaying
it or putting its literal value into command history:

```powershell
$secret = Read-Host 'OpenAI API key' -AsSecureString
$credential = [System.Net.NetworkCredential]::new('', $secret)
$env:OPENAI_API_KEY = $credential.Password
Remove-Variable secret, credential
$env:REDPOINT_OPENAI_MODEL = '<your Responses/tool/structured-output capable model>'
$env:REDPOINT_LLM_TIMEOUT_SECONDS = '300'

dotnet run --no-build --project tools/Redpoint.AgentOrchestrator -- --provider openai --knowledge --mode analysis --repository-root $target --task 'Analyze inventory persistence. Engineer and Reviewer must each retrieve indexed project context, cite the returned path and line ranges, and distinguish evidence from assumptions. Propose one small improvement; do not implement it.'
```

Model decisions can produce `Incomplete` or `NeedsHumanDecision` rather than `Completed`; inspect the
structured verdict and trace statuses. A successful OpenAI run emits `ContextRetrievalCompleted` for
both specialist roles and source-cited findings in the final proposal. Raw retrieval text is delivered
only as a tool result, never inserted into system instructions or operational traces.

Optional semantic embeddings require a separately built matching index:

```powershell
$env:REDPOINT_KNOWLEDGE_PROVIDER = 'openai'
$env:REDPOINT_KNOWLEDGE_MODEL = 'text-embedding-3-small'
$env:REDPOINT_KNOWLEDGE_DIMENSIONS = '1536'
dotnet run --no-build --project tools/Redpoint.KnowledgeIndex -- build --repository-root $target --index-path $index --provider openai --model $env:REDPOINT_KNOWLEDGE_MODEL --dimensions 1536
```

The current OpenAI embedding implementation validates the dimensions returned by the model; choose a
matching model/dimension combination. It does not request arbitrary reduced dimensions from the API.
Agent API calls and optional embedding API calls use API billing, independently of a ChatGPT Codex
subscription. Retrieval never rebuilds automatically. Remove the process key when finished with
`Remove-Item Env:OPENAI_API_KEY` if it was set only for this session. Do not save it in profiles or traces.

## Bounds and failure outcomes

Only Unity Engineer and Reviewer receive `retrieve_project_context`. Manager has no retrieval or
implementation tools; QA retains its existing inspection/test permissions. The MCP server still has
its original nine tools. Retrieval is local to the orchestrator and never forwarded to MCP.

Each query is limited to 512 printable characters, five results, and 8,000 source-text characters.
Each workflow allows three retrieval calls per authorized role, six total, and 24,000 requested
source-text characters across all attempts/retries. Reservations are conservative and remain consumed
after failed/empty calls. Retrieval has a 30-second deadline and also consumes the existing LLM tool
budget. Status, citation/version metadata and fixed envelope overhead are separate from the text budget.

Results contain relative paths, one-based inclusive line ranges, content/source hashes, freshness,
schema, provider/model/dimensions, index build time, repository identity hash and historical commit.
The exact loaded index snapshot undergoes AI-030 integrity/freshness validation before evidence is
returned. Historical commit metadata and stored ranking vectors remain unauthenticated local cache
state; source text/citations are reconstructed from approved files. A checkout can change after a read.

| Status / symptom | Action |
|---|---|
| `Unavailable` | Index is missing, unreadable, corrupt JSON or over the storage bound. Build it explicitly and check the configured path. |
| `Stale` | Approved sources changed. Rebuild explicitly; stale evidence is never returned. |
| `Incompatible` | Schema/provider/dimensions differ, or chunk evidence fails integrity validation. Check configuration and rebuild. |
| `NoResults` | No positive match, empty corpus, or no whole chunk fits the requested text limit. Refine the query/budget. |
| `InvalidRequest` | Query/limits invalid, duplicate fields, or unsupported arguments such as root/path/role. Use the advertised schema. |
| `Forbidden` | Manager, QA or unknown role tried retrieval; no provider call occurs. |
| `LimitExceeded` | Shared workflow call/character reservation exhausted. Finalize with the limitation; retries do not reset it. |
| `ProviderFailed` | Source access, embedding request or result validation failed. Check configuration/connectivity; exceptions and secrets are not echoed. |
| `Cancelled` / `TimedOut` | Cancellation propagated or the retrieval deadline expired. No partial evidence is returned. |
| QA fails | Resolve the reported baseline test failure; the demo does not repair code or claim success from MCP transport alone. |

Retrieved text is untrusted evidence. Prompts explicitly reject embedded commands, role changes and
approval requests. No retrieval operation builds an index, changes configuration, reads arbitrary
paths, writes source, issues approvals or expands MCP permissions.

## Verification

```powershell
$env:REDPOINT_TARGET_REPOSITORY_ROOT = $target
dotnet build Redpoint.Codex.Mcp.slnx --no-restore
dotnet test Redpoint.Codex.Mcp.slnx --no-build --no-restore
git diff --check
```

The live OpenAI test is opt-in, even if credentials are already present:

```powershell
$env:REDPOINT_RUN_LIVE_OPENAI_TESTS = '1'
try {
    dotnet test tools/Redpoint.AgentOrchestrator.Tests --no-build --filter Category=LiveOpenAI
} finally {
    Remove-Item Env:REDPOINT_RUN_LIVE_OPENAI_TESTS
}
```

The MCP child does not inherit this opt-in marker, so QA's bounded baseline suite cannot recursively
launch live model workflows. Operational trace events contain workflow ID, role, retrieval ID, status
and counts, never queries, evidence text, credentials, raw provider errors or hidden reasoning.

## Recorded local validation

On Windows, the full solution built with zero warnings/errors and passed 410 tests (177 MCP, 191
orchestrator, 42 knowledge index), with the opt-in live OpenAI test skipped because key/model
configuration was unavailable. The PowerShell demo script indexed 204 tracked files into 740 chunks,
validated Current freshness, returned three citations to each specialist including the corrected
`StoreRules.cs:217-277`, and finished Completed with Reviewer Approve and QA Pass. The target DungeonEscape
working tree stayed clean. Linux and actual
OpenAI model/embedding requests were not exercised in this validation.
