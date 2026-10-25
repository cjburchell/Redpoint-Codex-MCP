# Redpoint Knowledge Index

This .NET 10 command-line tool builds and searches a bounded, source-cited knowledge index for a separate DungeonEscape Git checkout. It is independent of MCP transport and the AgentOrchestrator; AI-030 does not grant agents access to retrieval.

## Corpus and security

The explicit corpus is `README.md`, `AGENTS.md`, `AI_CONTEXT.md`, `memory-bank/**/*.md`, `DungeonEscape.Core/**/*.cs`, and `DungeonEscape.Unity/Assets/DungeonEscape/Scripts/**/*.cs`. Candidate paths come from `git ls-files --cached --others --exclude-standard`, so Git-ignored local files are excluded. Each selected file must remain canonically inside the checkout and every path component must be an ordinary non-reparse path. Binary, malformed UTF-8, files above 1 MiB, corpora above 5,000 files or 25 MiB, and indexes above 25,000 chunks fail or skip as documented by build warnings. The indexer never executes repository content and never writes to the target checkout.

Markdown chunks prefer heading boundaries; C# chunks prefer declaration boundaries. Both use deterministic bounded line windows (120 lines/4,000 characters with at most eight lines of overlap). IDs hash schema version, normalized path, citation range, and exact content hash. Every chunk records its repository-relative path, one-based inclusive line range, source and content hashes, commit SHA when available, provider/model, timestamp, and vector.

## Storage and freshness

The default path is `%LOCALAPPDATA%/Redpoint/KnowledgeIndex/<repository-identity>/index-v1.json` on Windows (the platform local-application-data equivalent elsewhere). Override it with `--index-path`. Generated index files are local artifacts and must not be committed. Writes use a same-directory temporary file followed by atomic replacement, preserving the previous index if building or saving fails. Delete the index file to remove it; `rebuild` constructs a replacement.

Freshness validation compares repository identity, the complete approved source path/hash manifest, schema version, embedding provider/model and dimensions. Stale or incompatible indexes are not searched. Unchanged chunks reuse stored embeddings on rebuild when all compatibility checks pass; changed and deleted files are reflected by the replacement index.

## CLI

```powershell
dotnet run --project tools/Redpoint.KnowledgeIndex -- build --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- stats --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- validate --repository-root C:\Development\DungeonEscape
dotnet run --project tools/Redpoint.KnowledgeIndex -- search --repository-root C:\Development\DungeonEscape --query "inventory persistence"
```

The default `offline-deterministic` 4,096-dimension token-hash provider needs no credentials and supports reproducible tests and local verification; it is lexical verification, not a substitute for production-quality semantic embeddings. For real embeddings use `--provider openai --model text-embedding-3-small --dimensions 1536`; the API key is read only from `OPENAI_API_KEY`. Requests are batched to 64 inputs, retry transient provider failures at most twice, honor cancellation, and validate dimensions and finite vector values. Indexes from different providers or models are never mixed.

Search uses normalized cosine similarity, returns only positive-score evidence, enforces result and character budgets, and includes `path:start-end` citations. It does not provide an MCP tool, RAG agent integration, autonomous memory, or write capability.
