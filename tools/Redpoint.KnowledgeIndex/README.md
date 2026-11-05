# Redpoint Knowledge Index

This .NET 10 command-line tool builds and searches a bounded, source-cited knowledge index for a separate DungeonEscape Git checkout. It is independent of MCP transport. AI-031 reuses its search interface behind the orchestrator's bounded IContextProvider.

## Corpus and security

The explicit corpus is `README.md`, `AGENTS.md`, `AI_CONTEXT.md`, `memory-bank/**/*.md`, `DungeonEscape.Core/**/*.cs`, and `DungeonEscape.Unity/Assets/DungeonEscape/Scripts/**/*.cs`. Candidate paths come from bounded `git ls-files --cached -z` discovery, minus tracked paths reported by bounded `git ls-files --cached --ignored --exclude-standard -z`: a file must be Git-tracked, not ignored, and allowed by this corpus policy. Untracked and Git-ignored local files are never indexed. Modified tracked files are read from the working tree, while deleted tracked files are skipped. Each selected file must remain canonically inside the checkout and every path component must be an ordinary non-reparse path. Binary, malformed UTF-8, files above 1 MiB, corpora above 5,000 files or 25 MiB, and indexes above 25,000 chunks fail or skip as documented by build warnings. The indexer never executes repository content and never writes to the target checkout.

Markdown chunks prefer heading boundaries; C# chunks prefer declaration boundaries. Both use deterministic bounded line windows (120 lines/4,000 UTF-16 code units with at most eight lines of overlap). LF, CRLF, and CR are normalized for chunk text, while a final line terminator does not create a nonexistent source line; genuine interior blank lines remain part of their physical line ranges. A physical line over 4,000 characters is deterministically split into Unicode-safe segments carrying that same one-based source line citation; no chunk exceeds the hard character bound. IDs hash schema version, normalized path, deterministic chunk ordinal, citation range, and exact content hash. Every chunk records its repository-relative path, one-based inclusive line range, source and content hashes, commit SHA when available, provider/model, timestamp, and vector.

## Storage and freshness

The default path is `%LOCALAPPDATA%/Redpoint/KnowledgeIndex/<repository-identity>/index-v2.json` on Windows (the platform local-application-data equivalent elsewhere). Override it with `--index-path`. Generated index files are local artifacts and must not be committed. Writes use a same-directory temporary file followed by atomic replacement, preserving the previous index if building or saving fails. Delete the index file to remove it; `rebuild` constructs a replacement.

Schema version 2 fixes physical end-line counting for files with a final LF, CRLF, or CR. Version-1
indexes contain old chunk text, IDs, hashes, and citation ranges, so they cannot be migrated safely in
place or reused for embeddings. They fail closed as `Incompatible` when loaded through an explicit old
path. Run `build` or `rebuild` to create a complete version-2 index. The new default path avoids silently
using the old cache; a custom `--index-path` may be retained and atomically replaced by `rebuild`.

Freshness validation compares repository identity, the complete approved source path/hash manifest, schema version, embedding provider/model and dimensions. Stale or incompatible indexes are not searched. Unchanged chunks reuse stored embeddings on rebuild when all compatibility checks pass; changed and deleted files are reflected by the replacement index. Before retrieval, the loaded index's complete chunk set is checked against deterministic reconstruction from the current approved corpus: IDs, text, content/source hashes, paths, line ranges, document types, and chunk provider/schema metadata must match. Duplicate, missing, extra, or inconsistent chunks fail closed. Search validates and searches the same loaded snapshot; rebuilding does not reuse chunks with inconsistent source text or citations.

Persisted JSON is limited to 128 MiB, 5,000 source entries and 25,000 chunks. Loading checks the byte limit before parsing (and while reading), then checks collection counts before typed chunk/vector deserialization. Saving an oversized index preserves the previous file. A corpus within the source limits may still exceed the serialized storage limit; reduce the corpus or vector dimensions and rebuild.

Stored embedding vectors are trusted local ranking-cache state, not authenticated evidence of a provider response. Dimensions and finite values are checked, but a local editor can alter valid vector values and influence ranking. Source reconstruction prevents such edits from fabricating cited source text; it does not authenticate vector provenance, historical commit metadata, or protect against a malicious local user changing the checkout itself. Protect the index using normal user filesystem permissions, and delete/rebuild it if its ranking cache may have been modified. Validation never calls the embedding API to regenerate stored vectors.

## CLI

```powershell
dotnet run --project tools/Redpoint.KnowledgeIndex -- build --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- stats --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- validate --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- search --repository-root C:\Development\DungeonEscape --query "inventory persistence"
```

The default `offline-deterministic` 4,096-dimension token-hash provider needs no credentials and supports reproducible tests and local verification; it is lexical verification, not a substitute for production-quality semantic embeddings. For real embeddings use `--provider openai --model text-embedding-3-small --dimensions 1536`; the API key is read only from `OPENAI_API_KEY`. Requests are batched to 64 inputs, retry transient provider failures at most twice, honor cancellation, and validate dimensions and finite vector values. Indexes from different providers or models are never mixed.

Search uses normalized cosine similarity, returns only positive-score evidence, enforces result and character budgets, and includes `path:start-end` citations. The indexer does not provide an MCP tool, autonomous memory, or source-write capability. AI-031 adds optional specialist access through the orchestrator; see [the quick start](../Redpoint.AgentOrchestrator/QUICKSTART.md). Search responses include source/content hashes and version metadata from the same validated index snapshot; the persisted schema is version 2.
