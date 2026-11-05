using System.Collections.Frozen;

namespace Redpoint.Mcp.Services;

/// <summary>Immutable built-in DungeonEscape content policy shared by bounded inspection and approved writes.</summary>
public sealed class RepositoryContentPolicy
{
    public static RepositoryContentPolicy DungeonEscape { get; } = new();

    private RepositoryContentPolicy() { }

    public IReadOnlyList<string> ApprovedRoots { get; } = Array.AsReadOnly<string>(
    [
        "DungeonEscape.Core",
        "DungeonEscape.Unity/Assets/DungeonEscape/Scripts",
        "DungeonEscape.Tools.GameEditor",
        "tools",
        ".ai",
        "memory-bank"
    ]);

    private readonly FrozenSet<string> excludedDirectories = new[]
    {
        ".git", "Library", "Temp", "Obj", "obj", "bin", "Build", "Builds", "Logs",
        "UserSettings", "TestResults", "artifacts"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenSet<string> textExtensions = new[]
    {
        ".cs", ".csproj", ".sln", ".props", ".targets", ".razor", ".json", ".md", ".txt",
        ".xml", ".yml", ".yaml", ".ps1", ".sh", ".asmdef", ".uxml", ".uss"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenSet<string> writeExtensions = new[]
        { ".cs", ".json", ".md", ".txt", ".xml", ".yml", ".yaml" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool IsApprovedWriteFile(string path) => writeExtensions.Contains(Path.GetExtension(path));

    public bool IsExcludedDirectory(string name) => excludedDirectories.Contains(name);

    public bool IsApprovedTextFile(string path) => textExtensions.Contains(Path.GetExtension(path));

    public string? NormalizeRequestedPath(string? path, bool required)
    {
        if (path is null)
        {
            if (required) throw new ArgumentException("path is required.", nameof(path));
            return null;
        }
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path must not be empty.", nameof(path));
        if (Path.IsPathRooted(path) || Path.IsPathFullyQualified(path))
            throw new ArgumentException("path must be repository-relative.", nameof(path));
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
            throw new ArgumentException("path must not contain '..'.", nameof(path));
        if (segments.Any(IsExcludedDirectory))
            throw new ArgumentException("path contains an excluded directory.", nameof(path));
        var normalized = string.Join('/', segments.Where(segment => segment != "."));
        if (required && normalized.Length == 0) throw new ArgumentException("path must identify a file.", nameof(path));
        return normalized;
    }

    public IReadOnlyList<string> ResolveSearchTargets(string repositoryRoot, string? normalizedPath)
    {
        var approved = GetApprovedRoots(repositoryRoot);
        if (normalizedPath is null)
            return approved.Where(path => Directory.Exists(path) && RepositoryPathSecurity.IsOrdinaryPath(path)).ToArray();
        return [ResolveExistingApprovedPath(repositoryRoot, approved, normalizedPath)];
    }

    public string ResolveApprovedFile(string repositoryRoot, string normalizedPath)
    {
        var candidate = ResolveExistingApprovedPath(repositoryRoot, GetApprovedRoots(repositoryRoot), normalizedPath);
        if (!File.Exists(candidate)) throw new ArgumentException("path must identify an existing file.", nameof(normalizedPath));
        if (!IsApprovedTextFile(candidate)) throw new ArgumentException("path has an unsupported file extension.", nameof(normalizedPath));
        return candidate;
    }

    public static string Relative(string repositoryRoot, string path) =>
        RepositoryPathSecurity.Relative(repositoryRoot, path);

    private string[] GetApprovedRoots(string repositoryRoot) => ApprovedRoots.Select(root =>
        Path.GetFullPath(Path.Combine(repositoryRoot, root.Replace('/', Path.DirectorySeparatorChar)))).ToArray();

    private static string ResolveExistingApprovedPath(string repositoryRoot, IReadOnlyList<string> approved,
        string normalizedPath)
    {
        if (!RepositoryPathSecurity.TryResolveContainedRelativePath(repositoryRoot, normalizedPath,
                out var candidate))
            throw new ArgumentException("path escapes the repository root.", nameof(normalizedPath));
        if (!approved.Any(root => RepositoryPathSecurity.IsContained(root, candidate)))
            throw new ArgumentException("path is outside the approved content roots.", nameof(normalizedPath));
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            throw new ArgumentException("path does not exist.", nameof(normalizedPath));
        if (!RepositoryPathSecurity.IsOrdinaryPath(candidate))
            throw new ArgumentException("linked or reparse-point paths are not allowed.", nameof(normalizedPath));
        return candidate;
    }
}
