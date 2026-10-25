# Progress

## Completed

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
