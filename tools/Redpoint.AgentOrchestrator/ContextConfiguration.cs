using Redpoint.KnowledgeIndex;

namespace Redpoint.AgentOrchestrator;

/// <summary>Trusted startup-only configuration. No model/tool arguments can select roots or providers.</summary>
public static class ContextConfiguration
{
    public static IContextProvider Create(string repositoryRoot, Func<string, string?> environment)
    {
        var name = environment("REDPOINT_KNOWLEDGE_PROVIDER") ?? "offline";
        var rawDimensions = environment("REDPOINT_KNOWLEDGE_DIMENSIONS");
        var dimensions = name == "offline" ? 4096 : 1536;
        if (rawDimensions is not null && (!int.TryParse(rawDimensions, out dimensions) || dimensions is < 1 or > 4096))
            throw new ArgumentException("REDPOINT_KNOWLEDGE_DIMENSIONS must be between 1 and 4096.");
        var path = environment("REDPOINT_KNOWLEDGE_INDEX_PATH") ?? JsonKnowledgeStore.DefaultPath(repositoryRoot);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("REDPOINT_KNOWLEDGE_INDEX_PATH must be absolute.");
        IEmbeddingProvider embeddings = name switch
        {
            "offline" => new DeterministicEmbeddingProvider(dimensions),
            "openai" => new OpenAiEmbeddingProvider(environment("OPENAI_API_KEY") ?? "",
                environment("REDPOINT_KNOWLEDGE_MODEL") ?? "text-embedding-3-small", dimensions),
            _ => throw new ArgumentException("REDPOINT_KNOWLEDGE_PROVIDER must be offline or openai.")
        };
        return new KnowledgeContextProvider(repositoryRoot,
            new KnowledgeIndexService(new KnowledgeCorpus(), new JsonKnowledgeStore(path), embeddings));
    }
}
