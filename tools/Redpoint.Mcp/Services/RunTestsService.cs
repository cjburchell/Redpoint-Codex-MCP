using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace Redpoint.Mcp.Services;

public enum ApprovedTestSuite
{
    [JsonStringEnumMemberName("core")] Core,
    [JsonStringEnumMemberName("mcp")] Mcp,
    [JsonStringEnumMemberName("orchestrator")] Orchestrator
}

public sealed record RunTestsResult(string Status, string Suite, string Outcome, int? ExitCode,
    long DurationMilliseconds, bool TimedOut, bool Cancelled, bool OutputTruncated,
    string Stdout, string Stderr, IReadOnlyList<string> FailedTests, IReadOnlyList<string> Warnings);

public sealed record TestSuiteCommandDefinition(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public interface IApprovedTestSuiteExecutor
{
    Task<TestProcessResult> ExecuteAsync(ApprovedTestSuite suite, string repositoryRoot,
        string toolsRepositoryRoot,
        TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record TestProcessResult(string Status, int? ExitCode, long DurationMilliseconds,
    bool TimedOut, bool Cancelled, bool OutputTruncated, string Stdout, string Stderr,
    IReadOnlyList<string> Warnings);

/// <summary>Fixed suite-to-command catalog. No MCP input can alter executable, arguments or working directory.</summary>
public static class ApprovedTestSuiteCatalog
{
    public static TestSuiteCommandDefinition Resolve(ApprovedTestSuite suite, string repositoryRoot,
        string toolsRepositoryRoot)
    {
        var (project, workingDirectory) = suite switch
        {
            ApprovedTestSuite.Core =>
                ("DungeonEscape.Core.Test/DungeonEscape.Core.Test.csproj", repositoryRoot),
            ApprovedTestSuite.Mcp =>
                ("tools/Redpoint.Mcp.Tests/Redpoint.Mcp.Tests.csproj", toolsRepositoryRoot),
            ApprovedTestSuite.Orchestrator =>
                ("tools/Redpoint.AgentOrchestrator.Tests/Redpoint.AgentOrchestrator.Tests.csproj",
                    toolsRepositoryRoot),
            _ => throw new ArgumentOutOfRangeException(nameof(suite), "Unknown test suite.")
        };
        return new("dotnet",
            ["test", project, "--no-restore", "--logger", "console;verbosity=minimal"], workingDirectory);
    }
}

public sealed record ToolsRepositoryRoot(string Path)
{
    public static ToolsRepositoryRoot Discover(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Redpoint.Codex.Mcp.slnx")))
                return new(directory.FullName);
        throw new InvalidOperationException("Could not locate the Redpoint tools repository root.");
    }
}

/// <summary>Process runner dedicated to the fixed approved test suite catalog.</summary>
public sealed class ApprovedTestSuiteExecutor : IApprovedTestSuiteExecutor
{
    public const int MaximumStandardOutputBytes = 128 * 1024;
    public const int MaximumStandardErrorBytes = 64 * 1024;
    private readonly string executable;

    public ApprovedTestSuiteExecutor() : this("dotnet") { }

    internal ApprovedTestSuiteExecutor(string executable)
    {
        this.executable = executable;
    }

    public async Task<TestProcessResult> ExecuteAsync(ApprovedTestSuite suite, string repositoryRoot,
        string toolsRepositoryRoot,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = ApprovedTestSuiteCatalog.Resolve(suite, repositoryRoot, toolsRepositoryRoot);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = command.WorkingDirectory
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["NUGET_XMLDOC_MODE"] = "skip";

        var stopwatch = Stopwatch.StartNew();
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stopwatch.Stop();
            return new("process-error", null, stopwatch.ElapsedMilliseconds, false, false, false,
                "", "", ["The approved test process could not be started."]);
        }
        if (process is null)
            return new("process-error", null, stopwatch.ElapsedMilliseconds, false, false, false,
                "", "", ["The approved test process could not be started."]);

        using (process)
        {
            process.StandardInput.Close();
            var stdout = BoundedTestOutputReader.ReadAsync(process.StandardOutput.BaseStream,
                MaximumStandardOutputBytes);
            var stderr = BoundedTestOutputReader.ReadAsync(process.StandardError.BaseStream,
                MaximumStandardErrorBytes);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(combined.Token);
                var output = await stdout;
                var errors = await stderr;
                stopwatch.Stop();
                return new(process.ExitCode == 0 ? "passed" : "failed", process.ExitCode,
                    stopwatch.ElapsedMilliseconds, false, false, output.Truncated || errors.Truncated,
                    Decode(output.Bytes), Decode(errors.Bytes), []);
            }
            catch (OperationCanceledException)
            {
                var cancelled = cancellationToken.IsCancellationRequested;
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
                var output = await stdout;
                var errors = await stderr;
                stopwatch.Stop();
                return new(cancelled ? "cancelled" : "timed-out", null, stopwatch.ElapsedMilliseconds,
                    !cancelled, cancelled, output.Truncated || errors.Truncated,
                    Decode(output.Bytes), Decode(errors.Bytes), []);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
    }

    private static string Decode(byte[] value) => Encoding.UTF8.GetString(value);
}

internal sealed record BoundedTestOutput(byte[] Bytes, bool Truncated);

internal static class BoundedTestOutputReader
{
    internal static async Task<BoundedTestOutput> ReadAsync(Stream stream, int maximum)
    {
        using var memory = new MemoryStream(Math.Min(maximum, 16 * 1024));
        var buffer = new byte[8192];
        var truncated = false;
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            var accepted = Math.Min(count, maximum - (int)memory.Length);
            if (accepted > 0) memory.Write(buffer, 0, accepted);
            if (accepted < count || memory.Length == maximum) truncated = true;
            // Continue draining after the retained prefix reaches its bound to prevent child-process deadlock.
        }
        return new(memory.ToArray(), truncated);
    }
}

public sealed class RunTestsService(ProjectInfoService projectInfo, IApprovedTestSuiteExecutor executor,
    ToolsRepositoryRoot toolsRepositoryRoot)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MinimumTimeoutSeconds = 10;
    public const int MaximumTimeoutSeconds = 300;

    public async Task<RunTestsResult> RunAsync(ApprovedTestSuite suite, int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(suite)) throw new ArgumentOutOfRangeException(nameof(suite), "Unknown test suite.");
        var seconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        if (seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                $"timeoutSeconds must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds}.");
        var root = projectInfo.DiscoverRepositoryRoot();
        if (root is null)
            return new("unavailable", SuiteName(suite), "unavailable", null, 0, false, false, false,
                "", "", [], ["The configured DungeonEscape repository is unavailable."]);

        var result = await executor.ExecuteAsync(suite, root, toolsRepositoryRoot.Path,
            TimeSpan.FromSeconds(seconds), cancellationToken);
        return new(result.Status, SuiteName(suite), result.Status, result.ExitCode,
            result.DurationMilliseconds, result.TimedOut, result.Cancelled, result.OutputTruncated,
            result.Stdout, result.Stderr, [], result.Warnings);
    }

    private static string SuiteName(ApprovedTestSuite suite) => suite switch
    {
        ApprovedTestSuite.Core => "core",
        ApprovedTestSuite.Mcp => "mcp",
        ApprovedTestSuite.Orchestrator => "orchestrator",
        _ => throw new ArgumentOutOfRangeException(nameof(suite))
    };
}
