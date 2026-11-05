# Progress

## Completed

- AI-030 review hardening: verify persisted chunk evidence against deterministic current-corpus
  reconstruction, validate the exact searched snapshot, and avoid reuse of inconsistent chunks.
  Bound stored JSON to 128 MiB and preflight source/chunk counts before typed deserialization.
  Added tampering, duplicate/missing chunk, snapshot substitution, rebuild and storage-limit tests.
  Documented that stored vectors are trusted local ranking cache, not authenticated provider output.
  Windows validation: knowledge index 36/36, MCP 167/167, orchestrator 149 passed with one opt-in
  live API test skipped; solution build zero warnings/errors; git diff --check passed. Linux unavailable
  (Docker Linux engine stopped; no general-purpose WSL distribution installed).


- AI-030: added a standalone secure project knowledge index with an explicit Git-tracked DungeonEscape
  corpus, deterministic Markdown/C# chunking, stable source citations, offline and OpenAI embedding
  providers, atomic versioned storage, rebuild/reuse support, freshness validation, bounded semantic
  search, a CLI, and disposable security/integrity tests. MCP and agent permissions are unchanged;
  retrieval integration remains deferred to AI-031. PR hardening restricts discovery to approved
  Git-tracked files and guarantees Unicode-safe 4,000-character chunk bounds for overlong lines.

- AI-021: added an explicit human-approved one-file write workflow with typed Engineer proposals,
  exact Reviewer hand-off, trusted MCP preparation/application, immutable console decisions,
  cross-process signed approvals, safe tracing, post-write QA, and disposable real-stdio coverage.
  Analysis mode and all model-role permissions remain unchanged.
