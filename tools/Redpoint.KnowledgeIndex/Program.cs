using System.Text.Json;
using System.Text.Json.Serialization;
using Redpoint.KnowledgeIndex;

return await KnowledgeIndexCli.RunAsync(args, CancellationToken.None);

public static class KnowledgeIndexCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h") return Usage();
            var command = args[0].ToLowerInvariant();
            var options = Parse(args[1..]);
            var root = Required(options, "repository-root");
            var path = options.GetValueOrDefault("index-path") ?? JsonKnowledgeStore.DefaultPath(root);
            var provider = CreateProvider(options);
            var store = new JsonKnowledgeStore(path);
            var service = new KnowledgeIndexService(new KnowledgeCorpus(), store, provider);
            object output = command switch
            {
                "build" or "rebuild" => await BuildAsync(service, store, root, cancellationToken),
                "validate" => new { Freshness = await service.ValidateFreshnessAsync(root, cancellationToken),
                    IndexPath = store.IndexPath },
                "stats" => await StatsAsync(store, service, root, cancellationToken),
                "search" => await service.SearchAsync(root, Required(options, "query"),
                    Int(options, "max-results", 10), Int(options, "max-characters", 8_000), cancellationToken),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            json.Converters.Add(new JsonStringEnumConverter());
            Console.WriteLine(JsonSerializer.Serialize(output, json));
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operation cancelled."); return 2; }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static IEmbeddingProvider CreateProvider(IReadOnlyDictionary<string, string> options)
    {
        var name = options.GetValueOrDefault("provider") ?? "offline";
        if (name.Equals("offline", StringComparison.OrdinalIgnoreCase))
            return new DeterministicEmbeddingProvider(Int(options, "dimensions", 4096));
        if (!name.Equals("openai", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("provider must be 'offline' or 'openai'.");
        return new OpenAiEmbeddingProvider(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
            options.GetValueOrDefault("model") ?? "text-embedding-3-small",
            Int(options, "dimensions", 1536));
    }

    private static async Task<object> StatsAsync(JsonKnowledgeStore store, KnowledgeIndexService service,
        string root, CancellationToken cancellationToken)
    {
        var index = await store.LoadAsync(cancellationToken)
                    ?? throw new InvalidOperationException("No knowledge index is available.");
        return new { index.SchemaVersion, index.EmbeddingProvider, index.EmbeddingModel,
            index.EmbeddingDimensions, index.BuiltAtUtc, Files = index.Sources.Count, Chunks = index.Chunks.Count,
            Freshness = await service.ValidateFreshnessAsync(root, cancellationToken), IndexPath = store.IndexPath };
    }

    private static async Task<object> BuildAsync(KnowledgeIndexService service, JsonKnowledgeStore store,
        string root, CancellationToken cancellationToken)
    {
        var result = await service.BuildAsync(root, cancellationToken);
        return new { result.FilesIndexed, Chunks = result.Index.Chunks.Count, result.ChunksEmbedded,
            result.ChunksReused, result.Warnings, result.Index.BuiltAtUtc, result.Index.SourceCommit,
            result.Index.EmbeddingProvider, result.Index.EmbeddingModel, IndexPath = store.IndexPath };
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("Options must use '--name value'.");
            result[args[i][2..]] = args[i + 1];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"--{name} is required.");

    private static int Int(IReadOnlyDictionary<string, string> options, string name, int fallback) =>
        options.TryGetValue(name, out var value) && int.TryParse(value, out var parsed)
            ? parsed : options.ContainsKey(name) ? throw new ArgumentException($"--{name} must be an integer.") : fallback;

    private static int Usage()
    {
        Console.WriteLine("""
Redpoint project knowledge index
  build|rebuild --repository-root PATH [--index-path PATH] [--provider offline|openai]
  stats          --repository-root PATH [--index-path PATH] [provider options]
  validate       --repository-root PATH [--index-path PATH] [provider options]
  search         --repository-root PATH --query TEXT [--max-results 10] [--max-characters 8000]

Provider options: --model NAME --dimensions N. OpenAI reads OPENAI_API_KEY.
""");
        return 0;
    }
}
