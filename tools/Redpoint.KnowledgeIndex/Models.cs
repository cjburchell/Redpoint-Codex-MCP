namespace Redpoint.KnowledgeIndex;

public sealed record KnowledgeChunk(
    string Id,
    string Path,
    string DocumentType,
    int StartLine,
    int EndLine,
    string ContentHash,
    string SourceFileHash,
    string? SourceCommit,
    int SchemaVersion,
    string EmbeddingProvider,
    string EmbeddingModel,
    DateTimeOffset IndexedAtUtc,
    string Text,
    float[] Embedding);

public sealed record KnowledgeSource(string Path, string Sha256, long Bytes);

public sealed record KnowledgeIndex(
    int SchemaVersion,
    string RepositoryIdentity,
    string? SourceCommit,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    DateTimeOffset BuiltAtUtc,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeChunk> Chunks);

public enum IndexFreshness { Current, Stale, Incompatible, Unavailable }

public sealed record IndexBuildResult(
    KnowledgeIndex Index,
    int FilesIndexed,
    int ChunksEmbedded,
    int ChunksReused,
    IReadOnlyList<string> Warnings);

public sealed record KnowledgeSearchResult(
    string Path,
    int StartLine,
    int EndLine,
    string Text,
    double Similarity,
    string Citation);

public sealed record KnowledgeSearchResponse(
    IndexFreshness Freshness,
    bool Truncated,
    IReadOnlyList<KnowledgeSearchResult> Results,
    string? Warning = null);

public interface IKnowledgeIndexer
{
    Task<IndexBuildResult> BuildAsync(string repositoryRoot, CancellationToken cancellationToken);
    Task<IndexFreshness> ValidateFreshnessAsync(string repositoryRoot, CancellationToken cancellationToken);
}

public interface IKnowledgeStore
{
    Task SaveAsync(KnowledgeIndex index, CancellationToken cancellationToken);
    Task<KnowledgeIndex?> LoadAsync(CancellationToken cancellationToken);
}

public interface IEmbeddingProvider
{
    string ProviderId { get; }
    string ModelId { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IKnowledgeSearch
{
    Task<KnowledgeSearchResponse> SearchAsync(string repositoryRoot, string query, int maxResults,
        int maximumCharacters, CancellationToken cancellationToken);
}
