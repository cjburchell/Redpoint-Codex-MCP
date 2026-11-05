# Architecture

Redpoint-Codex-MCP is a .NET 10 tooling repository that operates against a separately supplied,
validated DungeonEscape checkout. `Redpoint.Mcp` owns bounded repository capabilities and filesystem
security. `Redpoint.AgentOrchestrator` owns deterministic role transitions. `Redpoint.WriteApproval`
owns the shared HMAC token format used across the parent/child process boundary.

`Redpoint.KnowledgeIndex` is a standalone read-only component, deliberately independent of MCP and the
orchestrator. It selects a fixed DungeonEscape documentation/C# corpus from Git-tracked files, applies
canonical containment and reparse-point checks, creates deterministic bounded source-cited chunks, and
persists versioned embeddings via atomic JSON replacement outside the target checkout. Its storage
contract binds repository identity, complete file hashes, source commit, schema, provider/model, and
vector dimensions. Retrieval is allowed only when that evidence validates as current and compatible.
AI-031, not AI-030, owns any future agent access to retrieval.

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
