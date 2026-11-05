# Architecture

Redpoint-Codex-MCP is a .NET 10 tooling repository that operates against a separately supplied,
validated DungeonEscape checkout. `Redpoint.Mcp` owns bounded repository capabilities and filesystem
security. `Redpoint.AgentOrchestrator` owns deterministic role transitions. `Redpoint.WriteApproval`
owns the shared HMAC token format used across the parent/child process boundary.

`Redpoint.KnowledgeIndex` is a standalone read-only component, independent of MCP transport. It selects a fixed DungeonEscape documentation/C# corpus from Git-tracked files, applies
canonical containment and reparse-point checks, creates deterministic bounded source-cited chunks, and
persists versioned embeddings via atomic JSON replacement outside the target checkout. Its storage
contract binds repository identity, complete file hashes, source commit, schema, provider/model, and
vector dimensions. Retrieval is allowed only when that evidence validates as current and compatible.
AI-031 exposes bounded specialist retrieval through the orchestrator gateway.

Analysis mode follows Manager → Engineer → Reviewer → QA. Approved-write mode inserts deterministic
`PreparingChange`, `AwaitingHumanApproval`, and `ApplyingChange` stages after Reviewer approval. Models
retain their existing role allowlists; neither `prepare_write` nor `apply_write` is model-facing.

The trusted parent checks that the target worktree is clean, verifies exact MCP metadata, prepares the
reviewed content, and displays the complete preview. Human approval creates an immutable decision bound
to the authoritative proposal scope. The parent issuer signs that scope using the per-child launch key;
MCP validates signature, expiry, replay, path, hashes, and stale state before atomic replacement. Tokens,
keys, source content, diffs, and raw test output are excluded from traces.

QA receives applied metadata only after successful execution. PASS completes; FAIL or INCONCLUSIVE
requires human handling. No write retry, QA repair loop, Git mutation, multi-file transaction, file
creation/deletion, approval persistence, or autonomous approval exists.

Knowledge indexing never interprets repository text as instructions, executes indexed content, writes
to DungeonEscape, or expands MCP/model permissions. Candidate discovery is bounded and uses fixed Git
arguments solely to select tracked paths and capture source version information.

MCP inspection and controlled writes receive the immutable built-in `RepositoryContentPolicy.DungeonEscape`
through constructor injection. Approved roots are exposed as a read-only collection; extension and
exclusion sets are frozen. The write extension subset lives in the same policy, while path security,
size limits, capability metadata, approval verification, and role admission retain their existing
boundaries. No external profile loading or additional project selection is enabled by this extraction.

Knowledge search validates the same loaded index it returns against deterministic reconstruction of
all current approved source chunks, including IDs, text, citation ranges, document types and hashes.
Duplicate/missing/extra or inconsistent chunks fail closed. Persisted JSON reads are capped at 128 MiB;
source/chunk collection counts are checked before typed deserialization. Stored vectors remain trusted
local ranking-cache state: shape/finite checks do not authenticate provider provenance, while source
reconstruction independently prevents fabricated cited evidence. No embedding API calls occur during
freshness validation.

AI-031 adds optional `ContextToolGateway` around `RoleBasedToolGateway`; the local
`retrieve_project_context` capability is offered only to UnityEngineer/Reviewer. Manager/QA cannot
invoke it. All existing MCP calls still use the original authorization and exact descriptor checks;
the MCP server's nine-tool catalog is unchanged. `IContextProvider` is backed by AI-030 search, bound
to the trusted startup repository/index/provider. No tool argument can select another root or index.

Queries are capped at 512 printable characters, five results and 8,000 source-text characters. Per
workflow limits are three calls per authorized role, six total and 24,000 reserved characters across
retries. Reservations are consumed even on failure; role checks and budget reservations precede
provider execution. A 30-second retrieval timeout and caller cancellation produce structured outcomes.
LLM calls also consume the existing specialist tool budget. The returned exact snapshot includes
source/content hashes, relative citations, freshness and version metadata. Missing/corrupt/stale/
incompatible/empty/provider-failed results never manufacture evidence or trigger index rebuilding.

Retrieved text stays in bounded tool-result envelopes as untrusted evidence. It is never interpolated
into role instructions or operational traces. Trace events correlate workflow, role and retrieval ID,
recording status/counts only. The existing Responses executor discovers the local capability through
the same IAgentToolGateway used by scripted demonstrations. No deterministic workflow transitions or
approval-token permissions change. Live API tests require a separate opt-in marker, stripped from MCP
child environments so QA baseline tests cannot recursively invoke live agents.
