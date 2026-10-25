using System.Diagnostics;
using System.Text;
using Redpoint.KnowledgeIndex;
using Xunit;

namespace Redpoint.KnowledgeIndex.Tests;

public sealed class KnowledgeIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RedpointKnowledgeTests", Guid.NewGuid().ToString("N"));
    private readonly string indexPath;

    public KnowledgeIndexTests()
    {
        Directory.CreateDirectory(root);
        indexPath = Path.Combine(Path.GetTempPath(), "RedpointKnowledgeIndexes", $"{Guid.NewGuid():N}.json");
        Git("init");
        Git("config", "user.email", "tests@example.invalid");
        Git("config", "user.name", "Knowledge Tests");
        Write("README.md", "# Dungeon Escape\nInventory overview.\n");
        Git("add", ".");
        Git("commit", "-m", "fixture");
    }

    [Fact]
    public async Task ApprovedDocumentationCoreAndSelectedUnityScriptsAreIndexed()
    {
        Write("AGENTS.md", "# Agents\nTrusted guidance");
        Write("AI_CONTEXT.md", "# Context\nArchitecture");
        Write("memory-bank/progress.md", "# Progress\nInventory complete");
        Write("DungeonEscape.Core/Inventory.cs", "namespace Game;\npublic class Inventory {}\n");
        Write("DungeonEscape.Unity/Assets/DungeonEscape/Scripts/Chest.cs", "public class Chest {}\n");
        Write("DungeonEscape.Unity/Assets/Other/Hidden.cs", "public class Hidden {}\n");

        var result = await Service().BuildAsync(root, default);

        Assert.Equal(6, result.FilesIndexed);
        Assert.Contains(result.Index.Chunks, x => x.Path == "DungeonEscape.Core/Inventory.cs");
        Assert.Contains(result.Index.Chunks, x => x.Path.EndsWith("Chest.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Index.Chunks, x => x.Path.EndsWith("Hidden.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IgnoredExcludedAndUnapprovedFilesAreSkipped()
    {
        Write("memory-bank/tracked-local.md", "tracked but ignored local context");
        Write(".gitignore", "memory-bank/local.md\nmemory-bank/tracked-local.md\n");
        WriteUntracked("memory-bank/local.md", "secret local context");
        Write(".env", "TOKEN=secret");
        Write(".ai/private.md", "private");
        Write("DungeonEscape.Core/bin/Generated.cs", "generated");

        var result = await Service().BuildAsync(root, default);

        Assert.Single(result.Index.Sources);
        Assert.Equal("README.md", result.Index.Sources[0].Path);
    }

    [Fact]
    public async Task UntrackedApprovedSourceAndDocumentationAreExcluded()
    {
        Write("DungeonEscape.Core/Tracked.cs", "public class Tracked {}\n");
        Write("memory-bank/tracked.md", "# Tracked\n");
        WriteUntracked("DungeonEscape.Core/Local.cs", "public class Local {}\n");
        WriteUntracked("memory-bank/local.md", "# Local\n");

        var result = await Service().BuildAsync(root, default);

        Assert.Contains(result.Index.Sources, source => source.Path == "DungeonEscape.Core/Tracked.cs");
        Assert.Contains(result.Index.Sources, source => source.Path == "memory-bank/tracked.md");
        Assert.DoesNotContain(result.Index.Sources, source => source.Path == "DungeonEscape.Core/Local.cs");
        Assert.DoesNotContain(result.Index.Sources, source => source.Path == "memory-bank/local.md");
    }

    [Fact]
    public async Task BinaryMalformedUtf8AndOversizedFilesAreSkippedWithWarnings()
    {
        WriteBytes("DungeonEscape.Core/Binary.cs", [65, 0, 66]);
        WriteBytes("DungeonEscape.Core/Invalid.cs", [0xc3, 0x28]);
        WriteBytes("DungeonEscape.Core/Large.cs", new byte[KnowledgeCorpus.MaximumFileBytes + 1]);
        Git("add", "--", "DungeonEscape.Core/Binary.cs", "DungeonEscape.Core/Invalid.cs",
            "DungeonEscape.Core/Large.cs");

        var result = await Service().BuildAsync(root, default);

        Assert.Equal(3, result.Warnings.Count);
        Assert.DoesNotContain(result.Index.Sources, x => x.Path.Contains("Binary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApprovedCorpusFileDiscoveryIsBounded()
    {
        for (var i = 0; i <= KnowledgeCorpus.MaximumFiles; i++)
            WriteUntracked($"DungeonEscape.Core/Generated/{i:D4}.cs", "public class A {}\n");
        Git("add", "--", "DungeonEscape.Core/Generated");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().BuildAsync(root, default));
        Assert.Contains(KnowledgeCorpus.MaximumFiles.ToString(), error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(indexPath));
    }

    [Fact]
    public async Task SymlinkOrReparseEscapeIsSkipped()
    {
        var outside = Path.Combine(Path.GetTempPath(), "RedpointKnowledgeOutside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escape.cs"), "secret");
        var link = Path.Combine(root, "DungeonEscape.Core", "Linked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(outside, "Escape.cs"));
            Git("add", "--", "DungeonEscape.Core/Linked.cs");
            var result = await Service().BuildAsync(root, default);
            Assert.DoesNotContain(result.Index.Sources, x => x.Path.EndsWith("Linked.cs", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, x => x.Contains("unsafe", StringComparison.Ordinal));
        }
        catch (UnauthorizedAccessException) { return; }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void MarkdownChunkingIsDeterministicAndCitationsAreAccurate()
    {
        var text = "# First\nline 2\n" + new string('x', 600) + "\n# Second\nline 5";
        var first = KnowledgeChunker.Chunk("README.md", text);
        var second = KnowledgeChunker.Chunk("README.md", text);
        Assert.Equal(first, second);
        Assert.Collection(first,
            chunk => { Assert.Equal(1, chunk.Start); Assert.Equal(3, chunk.End); },
            chunk => { Assert.Equal(4, chunk.Start); Assert.Equal(5, chunk.End); });
    }

    [Fact]
    public void CSharpChunkCitationsAreAccurate()
    {
        var text = "namespace Game;\n" + new string('x', 600) + "\npublic class Inventory\n{\n}\n";
        var chunks = KnowledgeChunker.Chunk("Inventory.cs", text);
        Assert.Collection(chunks,
            chunk => { Assert.Equal(1, chunk.Start); Assert.Equal(2, chunk.End); },
            chunk => { Assert.Equal(3, chunk.Start); Assert.Equal(6, chunk.End); });
    }

    [Fact]
    public void OverlongLinesAreSplitDeterministicallyWithinHardBounds()
    {
        var maximum = KnowledgeChunker.MaximumChunkCharacters;
        var exactly = KnowledgeChunker.Chunk("A.cs", new string('a', maximum));
        Assert.Single(exactly);
        Assert.Equal(maximum, exactly[0].Text.Length);

        var overByOne = KnowledgeChunker.Chunk("A.cs", new string('b', maximum + 1));
        Assert.Equal([maximum, 1], overByOne.Select(chunk => chunk.Text.Length));
        Assert.All(overByOne, chunk => { Assert.Equal(1, chunk.Start); Assert.Equal(1, chunk.End); });

        var veryLong = new string('c', 20_000);
        var first = KnowledgeChunker.Chunk("A.cs", veryLong);
        var second = KnowledgeChunker.Chunk("A.cs", veryLong);
        Assert.Equal(first, second);
        Assert.Equal(veryLong, string.Concat(first.Select(chunk => chunk.Text)));
        Assert.All(first, AssertValidChunk);
    }

    [Fact]
    public void SeveralOverlongLinesPreserveTextCitationsAndBoundedOverlap()
    {
        var lines = new[]
        {
            "short before",
            new string('x', 9_000),
            new string('y', 8_001),
            "short after"
        };
        var chunks = KnowledgeChunker.Chunk("A.cs", string.Join('\n', lines));
        Assert.All(chunks, AssertValidChunk);
        Assert.Equal(lines[1], string.Concat(chunks.Where(chunk => chunk.Start == 2).Select(chunk => chunk.Text)));
        Assert.Equal(lines[2], string.Concat(chunks.Where(chunk => chunk.Start == 3).Select(chunk => chunk.Text)));
        Assert.Contains(chunks, chunk => chunk.Start == 1 && chunk.End == 1);
        Assert.Contains(chunks, chunk => chunk.Start == 4 && chunk.End == 4);

        var ordinary = KnowledgeChunker.Chunk("A.cs",
            string.Join('\n', Enumerable.Repeat(new string('z', 1_000), 30)));
        Assert.InRange(ordinary.Count, 2, 30);
        Assert.All(ordinary, AssertValidChunk);
    }

    [Fact]
    public void LongLineSplittingNeverBreaksSurrogatePairs()
    {
        var emoji = char.ConvertFromUtf32(0x1f9ed);
        var text = new string('a', KnowledgeChunker.MaximumChunkCharacters - 1) + emoji
                   + new string('b', KnowledgeChunker.MaximumChunkCharacters) + emoji;
        var chunks = KnowledgeChunker.Chunk("A.cs", text);
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk =>
        {
            AssertValidChunk(chunk);
            AssertWellFormedUtf16(chunk.Text);
            Assert.Equal(1, chunk.Start);
            Assert.Equal(1, chunk.End);
        });
    }

    [Fact]
    public async Task ChunkIdsAreStableAndUnchangedEmbeddingsAreReusedWithoutDuplicates()
    {
        Write("DungeonEscape.Core/A.cs", new string('x', 20_000));
        var provider = new CountingEmbeddingProvider();
        var service = Service(provider);
        var first = await service.BuildAsync(root, default);
        var callsAfterFirst = provider.TextCount;
        var second = await service.BuildAsync(root, default);

        Assert.Equal(first.Index.Chunks.Select(x => x.Id), second.Index.Chunks.Select(x => x.Id));
        Assert.Equal(callsAfterFirst, provider.TextCount);
        Assert.Equal(second.Index.Chunks.Count, second.Index.Chunks.Select(x => x.Id).Distinct().Count());
        Assert.Equal(second.Index.Chunks.Count, second.ChunksReused);
        Assert.All(second.Index.Chunks, chunk => Assert.InRange(chunk.Text.Length, 1,
            KnowledgeChunker.MaximumChunkCharacters));
    }

    [Fact]
    public async Task ChangedSourceInvalidatesFreshnessAndRebuildRemovesDeletedFiles()
    {
        Write("DungeonEscape.Core/A.cs", "public class Old {}\n");
        var service = Service();
        await service.BuildAsync(root, default);
        Write("DungeonEscape.Core/A.cs", "public class Changed {}\n");
        var expectedHash = KnowledgeCorpus.Hash(Encoding.UTF8.GetBytes("public class Changed {}\n"));
        Assert.Equal(IndexFreshness.Stale, await service.ValidateFreshnessAsync(root, default));
        var changed = await service.BuildAsync(root, default);
        Assert.Equal(expectedHash, Assert.Single(changed.Index.Sources,
            source => source.Path.EndsWith("A.cs", StringComparison.Ordinal)).Sha256);
        File.Delete(Path.Combine(root, "DungeonEscape.Core", "A.cs"));
        var rebuilt = await service.BuildAsync(root, default);
        Assert.DoesNotContain(rebuilt.Index.Sources, x => x.Path.EndsWith("A.cs", StringComparison.Ordinal));
        Assert.Equal(IndexFreshness.Current, await service.ValidateFreshnessAsync(root, default));
    }

    [Fact]
    public async Task ProviderOrModelMismatchIsIncompatible()
    {
        await Service().BuildAsync(root, default);
        var changed = Service(new CountingEmbeddingProvider("different-model"));
        Assert.Equal(IndexFreshness.Incompatible, await changed.ValidateFreshnessAsync(root, default));
    }

    [Fact]
    public async Task FailedBuildPreservesLastValidIndex()
    {
        var service = Service();
        var original = await service.BuildAsync(root, default);
        Write("DungeonEscape.Core/New.cs", "public class New {}\n");
        var failing = Service(new ThrowingEmbeddingProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.BuildAsync(root, default));
        var stored = await new JsonKnowledgeStore(indexPath).LoadAsync(default);
        Assert.Equal(original.Index.BuiltAtUtc, stored!.BuiltAtUtc);
    }

    [Fact]
    public async Task SearchRanksRelevantCitedResultsAndEnforcesBudgets()
    {
        Write("DungeonEscape.Core/Inventory.cs", "public class InventoryPersistence { void SaveInventory() {} }\n");
        Write("DungeonEscape.Core/Combat.cs", "public class CombatResolver { void AttackMonster() {} }\n");
        var service = Service();
        await service.BuildAsync(root, default);

        var result = await service.SearchAsync(root, "inventory save", 10, 8_000, default);
        Assert.Equal(IndexFreshness.Current, result.Freshness);
        Assert.Equal("DungeonEscape.Core/Inventory.cs", result.Results[0].Path);
        Assert.Equal("DungeonEscape.Core/Inventory.cs:1-2", result.Results[0].Citation);

        var bounded = await service.SearchAsync(root, "class", 1, 8_000, default);
        Assert.Single(bounded.Results);
        Assert.True(bounded.Truncated);
        var characterBounded = await service.SearchAsync(root, "class", 10, 1, default);
        Assert.Empty(characterBounded.Results);
        Assert.True(characterBounded.Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(root, "class", 51, 8_000, default));
    }

    [Fact]
    public async Task EmptyNoMatchAndStaleSearchFailSafely()
    {
        var service = Service();
        await service.BuildAsync(root, default);
        var noMatch = await service.SearchAsync(root, "zzzznotpresent", 10, 8_000, default);
        Assert.Empty(noMatch.Results);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(root, " ", 10, 8_000, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(root,
            new string('q', KnowledgeIndexService.MaximumQueryCharacters + 1), 10, 8_000, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(root, "inventory", 10, KnowledgeIndexService.MaximumReturnedCharacters + 1, default));
        Write("README.md", "changed");
        var stale = await service.SearchAsync(root, "Dungeon", 10, 8_000, default);
        Assert.Equal(IndexFreshness.Stale, stale.Freshness);
        Assert.Empty(stale.Results);
    }

    [Fact]
    public async Task CorruptIndexFailsSafely()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "not-json");
        var service = Service();
        Assert.Equal(IndexFreshness.Unavailable, await service.ValidateFreshnessAsync(root, default));
        var result = await service.SearchAsync(root, "inventory", 10, 8_000, default);
        Assert.Equal(IndexFreshness.Unavailable, result.Freshness);
    }

    [Fact]
    public async Task InvalidEmbeddingDimensionsAndValuesAreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service(new InvalidEmbeddingProvider([float.NaN])).BuildAsync(root, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service(new InvalidEmbeddingProvider([1, 2])).BuildAsync(root, default));
    }

    [Fact]
    public async Task BuildDoesNotModifyTargetRepositoryFiles()
    {
        Write("DungeonEscape.Core/A.cs", "public class A {}\n");
        var before = Snapshot();
        await Service().BuildAsync(root, default);
        Assert.Equal(before, Snapshot());
        Assert.False(indexPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private KnowledgeIndexService Service(IEmbeddingProvider? provider = null) =>
        new(new KnowledgeCorpus(), new JsonKnowledgeStore(indexPath), provider ?? new DeterministicEmbeddingProvider());

    private IReadOnlyList<string> Snapshot() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => $"{Path.GetRelativePath(root, path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}").ToArray();

    private static void AssertValidChunk((int Start, int End, string Text) chunk)
    {
        Assert.NotEmpty(chunk.Text);
        Assert.InRange(chunk.Text.Length, 1, KnowledgeChunker.MaximumChunkCharacters);
        Assert.InRange(chunk.End - chunk.Start + 1, 1, KnowledgeChunker.MaximumChunkLines);
    }

    private static void AssertWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            Assert.False(char.IsLowSurrogate(text[i]));
            if (!char.IsHighSurrogate(text[i])) continue;
            Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
            i++;
        }
    }

    private void Write(string path, string content)
    {
        WriteBytes(path, Encoding.UTF8.GetBytes(content));
        Git("add", "--", path);
    }

    private void WriteUntracked(string path, string content) =>
        WriteBytes(path, Encoding.UTF8.GetBytes(content));
    private void WriteBytes(string path, byte[] content)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private void Git(params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Directory.Delete(root, true);
        }
        if (File.Exists(indexPath)) File.Delete(indexPath);
    }

    private sealed class CountingEmbeddingProvider(string model = "test-v1") : IEmbeddingProvider
    {
        private readonly DeterministicEmbeddingProvider inner = new(16);
        public int TextCount { get; private set; }
        public string ProviderId => "test";
        public string ModelId => model;
        public int Dimensions => 16;
        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken token)
        {
            TextCount += texts.Count;
            return await inner.EmbedAsync(texts, token);
        }
    }

    private sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderId => "offline-deterministic";
        public string ModelId => "token-hash-v1";
        public int Dimensions => 128;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken token) =>
            throw new InvalidOperationException("Injected embedding failure.");
    }

    private sealed class InvalidEmbeddingProvider(float[] vector) : IEmbeddingProvider
    {
        public string ProviderId => "invalid";
        public string ModelId => "invalid";
        public int Dimensions => 1;
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => vector).ToArray());
    }
}
