# Progress

## Completed

- Citation line correction: removed the synthetic final line produced when tracked sources end in LF,
  CRLF or CR; preserved real blank lines, boundaries, overlap, long-line bounds and Unicode safety.
  Knowledge schema 2 rejects version-1 caches and uses `index-v2.json`; migration requires an explicit
  rebuild. Index search and Engineer/Reviewer gateway citations are covered end to end. Windows
  validation: solution build zero warnings/errors; 410 tests passed (MCP 177, orchestrator 191,
  knowledge index 42), with one opt-in live API test skipped. The offline DungeonEscape demo rebuilt
  204 files/740 chunks, validated Current, cited StoreRules.cs:217-277, and completed Reviewer/QA gates.

- AI-031: implemented opt-in specialist retrieval via bounded IContextProvider/ContextToolGateway,
  reusing AI-030 search. Engineer/Reviewer only; Manager/QA and MCP/approval permissions unchanged.
  Added current-snapshot citations/hashes/version metadata, strict query/result/text/call limits,
  structured failure/cancellation outcomes, safe correlated traces, OpenAI executor integration,
  offline demonstration script and PowerShell quick-start guide. Live tests require explicit opt-in.
  Windows validation: solution build zero warnings/errors; full solution tests passed (MCP 177,
  orchestrator 191, knowledge index 36; 404 passed, one opt-in live API test skipped).
  PowerShell demo script built/validated 204 files and 740 chunks, returned three cited results to
  each specialist and completed analysis with Reviewer Approve / QA Pass. Target checkout stayed clean.
  git diff --check passed. Live OpenAI not run (key/model unavailable); Linux unavailable locally.


- RP-01: extracted the fixed DungeonEscape MCP content rules into an immutable, constructor-injected
  policy shared by search, read, and controlled-write services. Approved roots, read/write extensions,
  exclusions, bounds, tool metadata, and approval behavior are unchanged. Startup registers only the
  built-in policy; project selection and external configuration remain future work.
  Windows validation: solution build has zero warnings/errors; MCP 177/177, orchestrator 149 passed
  with the opt-in live API test skipped, knowledge index 36/36 after incorporating AI-030 review fixes;
  git diff --check passed. Linux unavailable (Docker Linux engine stopped; no general-purpose WSL distro).

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
  retrieval integration is now implemented by AI-031. PR hardening restricts discovery to approved
  Git-tracked files and guarantees Unicode-safe 4,000-character chunk bounds for overlong lines.

- AI-021: added an explicit human-approved one-file write workflow with typed Engineer proposals,
  exact Reviewer hand-off, trusted MCP preparation/application, immutable console decisions,
  cross-process signed approvals, safe tracing, post-write QA, and disposable real-stdio coverage.
  Analysis mode and all model-role permissions remain unchanged.
