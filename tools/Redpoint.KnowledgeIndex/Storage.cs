using System.Text.Json;

namespace Redpoint.KnowledgeIndex;

public sealed class JsonKnowledgeStore(string indexPath) : IKnowledgeStore
{
    public const int MaximumIndexBytes = 128 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public string IndexPath { get; } = Path.GetFullPath(indexPath);

    public async Task SaveAsync(KnowledgeIndex index, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(IndexPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(IndexPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (new FileInfo(temporary).Length > MaximumIndexBytes)
                throw new InvalidDataException("Knowledge index exceeds the storage byte limit.");
            File.Move(temporary, IndexPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<KnowledgeIndex?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexPath)) return null;
        try
        {
            await using var stream = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumIndexBytes)
                throw new InvalidDataException("Knowledge index exceeds the storage byte limit.");
            using var bytes = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (bytes.Length + read > MaximumIndexBytes)
                    throw new InvalidDataException("Knowledge index exceeds the storage byte limit.");
                await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            // Check collection bounds before allocating typed chunks and float arrays.
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array
                || sources.GetArrayLength() > KnowledgeCorpus.MaximumFiles
                || !root.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array
                || chunks.GetArrayLength() > KnowledgeIndexService.MaximumChunks)
                throw new InvalidDataException("Knowledge index collections are missing or exceed limits.");
            var index = root.Deserialize<KnowledgeIndex>(JsonOptions);
            if (index is null || index.SchemaVersion <= 0 || string.IsNullOrWhiteSpace(index.RepositoryIdentity)
                || string.IsNullOrWhiteSpace(index.EmbeddingProvider) || string.IsNullOrWhiteSpace(index.EmbeddingModel)
                || index.EmbeddingDimensions <= 0 || index.Sources is null || index.Chunks is null
                || index.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Path)
                    || string.IsNullOrWhiteSpace(source.Sha256) || source.Bytes < 0)
                || index.Chunks.Any(chunk => chunk is null || string.IsNullOrWhiteSpace(chunk.Id)
                    || string.IsNullOrWhiteSpace(chunk.Path) || chunk.StartLine < 1 || chunk.EndLine < chunk.StartLine
                    || chunk.Embedding is null))
                throw new InvalidDataException("Knowledge index is missing required fields.");
            return index;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Knowledge index is corrupt or unreadable.", ex);
        }
    }

    public static string DefaultPath(string repositoryRoot)
    {
        var identity = KnowledgeCorpus.RepositoryIdentity(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Redpoint", "KnowledgeIndex", identity, "index-v2.json");
    }
}
