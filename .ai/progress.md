# Progress

## Completed

- RP-01: extracted the fixed DungeonEscape MCP content rules into an immutable, constructor-injected
  policy shared by search, read, and controlled-write services. Approved roots, read/write extensions,
  exclusions, bounds, tool metadata, and approval behavior are unchanged. Startup registers only the
  built-in policy; project selection and external configuration remain future work.
  Windows validation: solution build has zero warnings/errors; MCP 177/177, orchestrator 149 passed
  with the opt-in live API test skipped, knowledge index 20/20; git diff --check passed. Linux was not run.


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
