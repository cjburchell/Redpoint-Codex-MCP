# Architecture

Redpoint-Codex-MCP is a .NET 10 tooling repository that operates against a separately supplied,
validated DungeonEscape checkout. `Redpoint.Mcp` owns bounded repository capabilities and filesystem
security. `Redpoint.AgentOrchestrator` owns deterministic role transitions. `Redpoint.WriteApproval`
owns the shared HMAC token format used across the parent/child process boundary.

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
