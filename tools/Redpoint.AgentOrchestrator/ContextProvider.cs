using Redpoint.KnowledgeIndex;

namespace Redpoint.AgentOrchestrator;

public enum ContextStatus
{
    Available, NoResults, Unavailable, Stale, Incompatible, InvalidRequest, Forbidden,
    LimitExceeded, ProviderFailed, Cancelled, TimedOut
}

public sealed record ContextQuery(string Query, int MaxResults = 5, int MaximumCharacters = 8_000);

public sealed record ProjectContext(ContextStatus Status, IndexFreshness? Freshness,
    KnowledgeVersion? Version, IReadOnlyList<KnowledgeSearchResult> Evidence, bool Truncated = false)
{
    public static ProjectContext Empty(ContextStatus status, IndexFreshness? freshness = null) =>
        new(status, freshness, null, []);
}

/// <summary>Read-only retrieval from a trusted, startup-bound repository/index configuration.</summary>
public interface IContextProvider
{
    Task<ProjectContext> RetrieveAsync(ContextQuery query, CancellationToken cancellationToken);
}

public static class ContextLimits
{
    public const int MaximumQueryCharacters = 512;
    public const int MaximumResults = 5;
    public const int MaximumCharacters = 8_000;
    public const int MaximumCallsPerRole = 3;
    public const int MaximumCallsPerWorkflow = 6;
    public const int MaximumCharactersPerWorkflow = 24_000;
    public const int TimeoutSeconds = 30;

    public static bool Valid(ContextQuery query) => !string.IsNullOrWhiteSpace(query.Query)
        && query.Query.Length <= MaximumQueryCharacters && !query.Query.Any(char.IsControl)
        && query.MaxResults is >= 1 and <= MaximumResults
        && query.MaximumCharacters is >= 1 and <= MaximumCharacters;
}

/// <summary>Reuses AI-030 search and its complete freshness/integrity checks; never builds an index.</summary>
public sealed class KnowledgeContextProvider(string repositoryRoot, IKnowledgeSearch search) : IContextProvider
{
    public async Task<ProjectContext> RetrieveAsync(ContextQuery query, CancellationToken cancellationToken)
    {
        if (!ContextLimits.Valid(query)) return ProjectContext.Empty(ContextStatus.InvalidRequest);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await search.SearchAsync(repositoryRoot, query.Query, query.MaxResults,
                query.MaximumCharacters, cancellationToken);
            return result.Freshness switch
            {
                IndexFreshness.Current => new(result.Results.Count == 0 ? ContextStatus.NoResults : ContextStatus.Available,
                    result.Freshness, result.Version, result.Results, result.Truncated),
                IndexFreshness.Stale => ProjectContext.Empty(ContextStatus.Stale, result.Freshness),
                IndexFreshness.Incompatible => ProjectContext.Empty(ContextStatus.Incompatible, result.Freshness),
                _ => ProjectContext.Empty(ContextStatus.Unavailable, result.Freshness)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return ProjectContext.Empty(ContextStatus.Cancelled); }
        catch (OperationCanceledException) { return ProjectContext.Empty(ContextStatus.TimedOut); }
        catch (InvalidDataException) { return ProjectContext.Empty(ContextStatus.Incompatible); }
        catch (Exception) { return ProjectContext.Empty(ContextStatus.ProviderFailed); }
    }
}
