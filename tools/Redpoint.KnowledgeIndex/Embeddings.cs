using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenAI.Embeddings;

namespace Redpoint.KnowledgeIndex;

public sealed partial class DeterministicEmbeddingProvider(int dimensions = 4096) : IEmbeddingProvider
{
    public string ProviderId => "offline-deterministic";
    public string ModelId => "token-hash-v1";
    public int Dimensions { get; } = dimensions is >= 8 and <= 4096
        ? dimensions : throw new ArgumentOutOfRangeException(nameof(dimensions));

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var vectors = texts.Select(text =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = new float[Dimensions];
            foreach (var token in Tokens(text))
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
                var slot = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
                vector[slot] += 1;
            }
            Normalize(vector);
            return vector;
        }).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    internal static void Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (magnitude == 0) return;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / magnitude);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        foreach (Match word in TokenPattern().Matches(text.Replace('_', ' ')))
        {
            var parts = SubwordPattern().Matches(word.Value).Select(match => match.Value.ToLowerInvariant()).ToArray();
            if (parts.Length == 0) yield return word.Value.ToLowerInvariant();
            else foreach (var part in parts) yield return part;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SubwordPattern();
}

public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    public const int MaximumBatchSize = 64;
    private readonly EmbeddingClient client;
    private readonly int maximumRetries;

    public OpenAiEmbeddingProvider(string apiKey, string model = "text-embedding-3-small",
        int dimensions = 1536, int maximumRetries = 2)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("OPENAI_API_KEY is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("embedding model is required.", nameof(model));
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (maximumRetries is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(maximumRetries));
        ModelId = model;
        Dimensions = dimensions;
        this.maximumRetries = maximumRetries;
        client = new(model, apiKey);
    }

    public string ProviderId => "openai";
    public string ModelId { get; }
    public int Dimensions { get; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(MaximumBatchSize))
        {
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    var response = await client.GenerateEmbeddingsAsync(batch, cancellationToken: cancellationToken);
                    foreach (var embedding in response.Value)
                        results.Add(embedding.ToFloats().ToArray());
                    break;
                }
                catch (Exception ex) when (attempt < maximumRetries &&
                                           ex is HttpRequestException or System.ClientModel.ClientResultException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                }
            }
        }
        EmbeddingValidation.Validate(results, texts.Count, Dimensions);
        return results;
    }
}

internal static class EmbeddingValidation
{
    public static void Validate(IReadOnlyList<float[]> vectors, int expectedCount, int dimensions)
    {
        if (vectors.Count != expectedCount) throw new InvalidDataException("Embedding provider returned the wrong vector count.");
        foreach (var vector in vectors)
            if (vector.Length != dimensions || vector.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("Embedding provider returned an invalid vector.");
    }
}
