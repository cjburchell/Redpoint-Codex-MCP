using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Redpoint.KnowledgeIndex;

public sealed record CorpusFile(string Path, string FullPath, string Sha256, byte[] Bytes);

public sealed class KnowledgeCorpus
{
    public const int MaximumFiles = 5_000;
    public const int MaximumFileBytes = 1_048_576;
    public const long MaximumTotalBytes = 25L * 1_048_576;
    private const int MaximumEnumeratedGitPaths = 25_000;
    private const int MaximumRelativePathCharacters = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "Library", "Temp", "Logs", "Build", "Builds", "UserSettings",
        "TestResults", "artifacts"
    };

    public async Task<(IReadOnlyList<CorpusFile> Files, string Identity, string? Commit,
        IReadOnlyList<string> Warnings)> ReadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = CanonicalRepositoryRoot(repositoryRoot);
        var paths = (await ReadGitPathsAsync(root, cancellationToken))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var files = new List<CorpusFile>(paths.Length);
        var warnings = new List<string>();
        long totalBytes = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveOrdinaryFile(root, path, out var fullPath))
            {
                warnings.Add($"Skipped unsafe or missing corpus file: {path}");
                continue;
            }
            var bytes = await ReadBoundedFileAsync(fullPath, cancellationToken);
            if (bytes is null)
            {
                warnings.Add($"Skipped oversized corpus file: {path}");
                continue;
            }
            if (totalBytes + bytes.Length > MaximumTotalBytes)
                throw new InvalidOperationException("Approved corpus exceeds the total byte limit.");
            if (bytes.Contains((byte)0) || !IsValidUtf8(bytes))
            {
                warnings.Add($"Skipped non-UTF-8 or binary corpus file: {path}");
                continue;
            }
            totalBytes += bytes.Length;
            files.Add(new(path, fullPath, Hash(bytes), bytes));
        }

        var commitResult = await RunGitAsync(root, ["rev-parse", "HEAD"], cancellationToken);
        var commit = commitResult.ExitCode == 0 ? commitResult.Output.Trim() : null;
        var identity = RepositoryIdentity(root);
        return (files, identity, string.IsNullOrWhiteSpace(commit) ? null : commit, warnings);
    }

    public static string Decode(CorpusFile file) => StrictUtf8.GetString(file.Bytes);
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static string RepositoryIdentity(string canonicalRoot) => Hash(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? canonicalRoot.ToUpperInvariant() : canonicalRoot));

    private static string CanonicalRepositoryRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("repository root is required.", nameof(root));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(canonical) || !Directory.Exists(Path.Combine(canonical, ".git")))
            throw new ArgumentException("repository root must identify a Git checkout.", nameof(root));
        if (!IsOrdinaryPath(canonical)) throw new ArgumentException("linked repository roots are not allowed.", nameof(root));
        return canonical;
    }

    private static bool IsApproved(string path)
    {
        if (path.Split('/').Any(ExcludedSegments.Contains)) return false;
        if (path is "README.md" or "AGENTS.md" or "AI_CONTEXT.md") return true;
        if (path.StartsWith("memory-bank/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("DungeonEscape.Core/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith("DungeonEscape.Unity/Assets/DungeonEscape/Scripts/", StringComparison.Ordinal)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static async Task<IReadOnlyList<string>> ReadGitPathsAsync(string root,
        CancellationToken cancellationToken)
    {
        var tracked = await ReadBoundedGitPathsAsync(root, ["ls-files", "--cached", "-z"], cancellationToken);
        var ignored = await ReadBoundedGitPathsAsync(root,
            ["ls-files", "--cached", "--ignored", "--exclude-standard", "-z"], cancellationToken);
        return tracked.Except(ignored, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadBoundedGitPathsAsync(string root, string[] arguments,
        CancellationToken cancellationToken)
    {
        using var process = StartGit(root, arguments);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var approved = new List<string>();
        var current = new StringBuilder();
        var buffer = new char[4096];
        var enumerated = 0;
        try
        {
            while (true)
            {
                var count = await process.StandardOutput.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\0')
                    {
                        if (current.Length >= MaximumRelativePathCharacters)
                            throw new InvalidOperationException("Git returned an overlong repository path.");
                        current.Append(buffer[i]);
                        continue;
                    }
                    enumerated++;
                    if (enumerated > MaximumEnumeratedGitPaths)
                        throw new InvalidOperationException("Repository file discovery exceeds the bounded path limit.");
                    var normalized = Normalize(current.ToString());
                    current.Clear();
                    if (!IsApproved(normalized)) continue;
                    approved.Add(normalized);
                    if (approved.Count > MaximumFiles)
                        throw new InvalidOperationException($"Approved corpus exceeds the {MaximumFiles} file limit.");
                }
            }
            await process.WaitForExitAsync(cancellationToken);
            _ = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The target must be a readable Git working tree.");
            return approved;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { _ = await errorTask; } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static bool TryResolveOrdinaryFile(string root, string relative, out string fullPath)
    {
        fullPath = "";
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(x => x is ".." or "")) return false;
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison) || !File.Exists(candidate) || !IsOrdinaryPath(candidate)) return false;
        fullPath = candidate;
        return true;
    }

    private static bool IsOrdinaryPath(string path)
    {
        try
        {
            for (FileSystemInfo? item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
                 item is not null;
                 item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try { _ = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static async Task<byte[]?> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes) return null;
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return stream.ReadByte() == -1 ? bytes : null;
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string root, string[] arguments,
        CancellationToken cancellationToken)
    {
        using var process = StartGit(root, arguments);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        _ = await errorTask;
        return (process.ExitCode, output);
    }

    private static Process StartGit(string root, IEnumerable<string> arguments)
    {
        var process = new Process { StartInfo = new("git") { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(root);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        return process;
    }
}

public static class KnowledgeChunker
{
    public const int MaximumChunkCharacters = 4_000;
    public const int MaximumChunkLines = 120;
    public const int OverlapLines = 8;

    public static IReadOnlyList<(int Start, int End, string Text)> Chunk(string path, string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 1 && lines[0].Length == 0) return [];
        var boundaries = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Range(1, lines.Length - 1).Where(i => lines[i].TrimStart().StartsWith('#')).ToHashSet()
            : Enumerable.Range(1, lines.Length - 1).Where(i => LooksLikeDeclaration(lines[i])).ToHashSet();
        var result = new List<(int, int, string)>();
        var ordinaryStart = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lines[lineIndex].Length <= MaximumChunkCharacters) continue;
            AddOrdinaryChunks(lines, ordinaryStart, lineIndex, boundaries, result);
            AddLongLineChunks(lines[lineIndex], lineIndex + 1, result);
            ordinaryStart = lineIndex + 1;
        }
        AddOrdinaryChunks(lines, ordinaryStart, lines.Length, boundaries, result);
        return result;
    }

    private static void AddOrdinaryChunks(string[] lines, int rangeStart, int rangeEnd,
        IReadOnlySet<int> boundaries, ICollection<(int Start, int End, string Text)> result)
    {
        var start = rangeStart;
        while (start < rangeEnd)
        {
            var end = start;
            var characters = 0;
            var stoppedAtBoundary = false;
            while (end < rangeEnd)
            {
                var next = lines[end].Length + (end == start ? 0 : 1);
                if (end > start && (end - start >= MaximumChunkLines
                                    || characters + next > MaximumChunkCharacters)) break;
                if (end > start && boundaries.Contains(end) && characters >= 500)
                {
                    stoppedAtBoundary = true;
                    break;
                }
                characters += next;
                end++;
            }
            if (end == start) end++;
            var text = string.Join('\n', lines[start..end]);
            if (text.Length > 0) result.Add((start + 1, end, text));
            if (end >= rangeEnd) break;
            start = stoppedAtBoundary ? end : Math.Max(start + 1, end - OverlapLines);
        }
    }

    private static void AddLongLineChunks(string line, int lineNumber,
        ICollection<(int Start, int End, string Text)> result)
    {
        var start = 0;
        while (start < line.Length)
        {
            var end = Math.Min(line.Length, start + MaximumChunkCharacters);
            if (end < line.Length && char.IsHighSurrogate(line[end - 1]) && char.IsLowSurrogate(line[end])) end--;
            if (end <= start) throw new InvalidDataException("Unable to split an overlong Unicode line safely.");
            result.Add((lineNumber, lineNumber, line[start..end]));
            start = end;
        }
    }

    private static bool LooksLikeDeclaration(string line)
    {
        var value = line.TrimStart();
        return value.StartsWith("namespace ", StringComparison.Ordinal)
               || value.Contains(" class ", StringComparison.Ordinal)
               || value.Contains(" interface ", StringComparison.Ordinal)
               || value.Contains(" record ", StringComparison.Ordinal)
               || value.Contains(" enum ", StringComparison.Ordinal);
    }
}
