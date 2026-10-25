using System.Text;

namespace Redpoint.KnowledgeIndex;

public sealed class KnowledgeIndexService(KnowledgeCorpus corpus, IKnowledgeStore store,
    IEmbeddingProvider embeddings) : IKnowledgeIndexer, IKnowledgeSearch
{
    public const int SchemaVersion = 1;
    public const int MaximumChunks = 25_000;
    public const int MaximumEmbeddingBatch = 64;
    public const int MaximumQueryCharacters = 512;
    public const int MaximumSearchResults = 50;
    public const int MaximumReturnedCharacters = 20_000;

    public async Task<IndexBuildResult> BuildAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var snapshot = await corpus.ReadAsync(repositoryRoot, cancellationToken);
        var prior = await TryLoadAsync(cancellationToken);
        var reusable = prior is not null && Compatible(prior)
            ? prior.Chunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal)
            : new Dictionary<string, KnowledgeChunk>(StringComparer.Ordinal);
        var pending = new List<(CorpusFile File, int Start, int End, string Text, string Id, string ContentHash)>();
        var chunks = new List<KnowledgeChunk>();
        var now = DateTimeOffset.UtcNow;
        foreach (var file in snapshot.Files)
        foreach (var part in KnowledgeChunker.Chunk(file.Path, KnowledgeCorpus.Decode(file)))
        {
            var contentHash = KnowledgeCorpus.Hash(Encoding.UTF8.GetBytes(part.Text));
            var id = StableChunkId(file.Path, part.Start, part.End, contentHash);
            if (reusable.TryGetValue(id, out var old))
                chunks.Add(old with { SourceFileHash = file.Sha256, SourceCommit = snapshot.Commit, IndexedAtUtc = now });
            else pending.Add((file, part.Start, part.End, part.Text, id, contentHash));
            if (chunks.Count + pending.Count > MaximumChunks)
                throw new InvalidOperationException($"Corpus exceeds the {MaximumChunks} chunk limit.");
        }

        foreach (var batch in pending.Chunk(MaximumEmbeddingBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = await embeddings.EmbedAsync(batch.Select(x => x.Text).ToArray(), cancellationToken);
            EmbeddingValidation.Validate(vectors, batch.Length, embeddings.Dimensions);
            for (var i = 0; i < batch.Length; i++)
            {
                var item = batch[i];
                chunks.Add(new(item.Id, item.File.Path, DocumentType(item.File.Path), item.Start, item.End,
                    item.ContentHash, item.File.Sha256, snapshot.Commit, SchemaVersion, embeddings.ProviderId,
                    embeddings.ModelId, now, item.Text, vectors[i]));
            }
        }

        var index = new KnowledgeIndex(SchemaVersion, snapshot.Identity, snapshot.Commit, embeddings.ProviderId,
            embeddings.ModelId, embeddings.Dimensions, now,
            snapshot.Files.Select(file => new KnowledgeSource(file.Path, file.Sha256, file.Bytes.Length)).ToArray(),
            chunks.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.StartLine).ToArray());
        await store.SaveAsync(index, cancellationToken);
        return new(index, snapshot.Files.Count, pending.Count, chunks.Count - pending.Count, snapshot.Warnings);
    }

    public async Task<IndexFreshness> ValidateFreshnessAsync(string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var index = await TryLoadAsync(cancellationToken);
        if (index is null) return IndexFreshness.Unavailable;
        if (!Compatible(index)) return IndexFreshness.Incompatible;
        var current = await corpus.ReadAsync(repositoryRoot, cancellationToken);
        if (current.Identity != index.RepositoryIdentity) return IndexFreshness.Incompatible;
        var sources = current.Files.Select(x => (x.Path, x.Sha256)).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        var stored = index.Sources.Select(x => (x.Path, x.Sha256)).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        return sources.SequenceEqual(stored) ? IndexFreshness.Current : IndexFreshness.Stale;
    }

    public async Task<KnowledgeSearchResponse> SearchAsync(string repositoryRoot, string query, int maxResults,
        int maximumCharacters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryCharacters || query.Any(char.IsControl))
            throw new ArgumentException($"query must be 1-{MaximumQueryCharacters} printable characters.", nameof(query));
        if (maxResults is < 1 or > MaximumSearchResults) throw new ArgumentOutOfRangeException(nameof(maxResults));
        if (maximumCharacters is < 1 or > MaximumReturnedCharacters)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var index = await TryLoadAsync(cancellationToken);
        if (index is null) return new(IndexFreshness.Unavailable, false, [], "No knowledge index is available.");
        if (!Compatible(index)) return new(IndexFreshness.Incompatible, false, [], "Embedding or schema is incompatible.");
        var freshness = await ValidateFreshnessAsync(repositoryRoot, cancellationToken);
        if (freshness != IndexFreshness.Current)
            return new(freshness, false, [], "The knowledge index is not current and cannot be used for retrieval.");

        var queryVector = (await embeddings.EmbedAsync([query], cancellationToken))[0];
        EmbeddingValidation.Validate([queryVector], 1, embeddings.Dimensions);
        var ranked = index.Chunks.Select(chunk => (Chunk: chunk, Score: Cosine(queryVector, chunk.Embedding)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Chunk.StartLine);
        var results = new List<KnowledgeSearchResult>();
        var characters = 0;
        var truncated = false;
        foreach (var item in ranked)
        {
            if (results.Count == maxResults || characters + item.Chunk.Text.Length > maximumCharacters)
            {
                truncated = true;
                break;
            }
            characters += item.Chunk.Text.Length;
            results.Add(new(item.Chunk.Path, item.Chunk.StartLine, item.Chunk.EndLine, item.Chunk.Text,
                item.Score, $"{item.Chunk.Path}:{item.Chunk.StartLine}-{item.Chunk.EndLine}"));
        }
        return new(freshness, truncated, results);
    }

    private async Task<KnowledgeIndex?> TryLoadAsync(CancellationToken cancellationToken)
    {
        try { return await store.LoadAsync(cancellationToken); }
        catch (InvalidDataException) { return null; }
    }

    private bool Compatible(KnowledgeIndex index) => index.SchemaVersion == SchemaVersion
        && index.EmbeddingProvider == embeddings.ProviderId && index.EmbeddingModel == embeddings.ModelId
        && index.EmbeddingDimensions == embeddings.Dimensions
        && index.Chunks.All(chunk => chunk.Embedding.Length == embeddings.Dimensions
                                     && chunk.Embedding.All(float.IsFinite));

    private static string StableChunkId(string path, int start, int end, string contentHash) =>
        KnowledgeCorpus.Hash(Encoding.UTF8.GetBytes($"v{SchemaVersion}\n{path}\n{start}\n{end}\n{contentHash}"));

    private static string DocumentType(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        ? "documentation" : "csharp";

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) return double.NegativeInfinity;
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftLength += left[i] * left[i];
            rightLength += right[i] * right[i];
        }
        return leftLength == 0 || rightLength == 0 ? 0 : dot / Math.Sqrt(leftLength * rightLength);
    }
}
