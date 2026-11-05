using System.Text.Json;
using System.Security.Cryptography;
using Redpoint.AgentOrchestrator;

var provider = Value(args, "--provider") ?? (args.Contains("--demo", StringComparer.Ordinal) ? "scripted" : null);
var mode = Value(args, "--mode") ?? "analysis";
var knowledgeEnabled = args.Contains("--knowledge", StringComparer.Ordinal);
if (provider is null)
{
    Console.WriteLine("Redpoint agent orchestrator. Use --demo or --provider openai --task \"<task>\".");
    return 0;
}
if (provider is not ("scripted" or "openai"))
{
    Console.Error.WriteLine("Provider must be 'scripted' or 'openai'.");
    return 2;
}
if (mode is not ("analysis" or "approved-write"))
{
    Console.Error.WriteLine("Mode must be 'analysis' or 'approved-write'.");
    return 2;
}

IAgentExecutor? executor = null;
if (provider == "openai")
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var model = Environment.GetEnvironmentVariable("REDPOINT_OPENAI_MODEL");
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
    {
        Console.Error.WriteLine("OpenAI mode requires OPENAI_API_KEY and REDPOINT_OPENAI_MODEL.");
        return 2;
    }
    if (!TryEnvironmentInt("REDPOINT_LLM_TIMEOUT_SECONDS", 120, out var timeoutSeconds)
        || !TryEnvironmentInt("REDPOINT_LLM_MAX_TOOL_CALLS", 8, out var maximumToolCalls))
    {
        Console.Error.WriteLine("LLM timeout/tool-call configuration must be valid integers.");
        return 2;
    }
    try
    {
        executor = new LlmAgentExecutor(new OpenAiLlmClient(apiKey),
            LlmExecutionOptions.Create(model, timeoutSeconds, maximumToolCalls));
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine("Invalid LLM configuration: " + exception.Message);
        return 2;
    }
}
else
{
    // Scripted construction is deferred until the target repository has been resolved.
}

var toolsDirectory = FindToolsDirectory(AppContext.BaseDirectory);
var configuredRepositoryRoot = Value(args, "--repository-root")
    ?? Environment.GetEnvironmentVariable("REDPOINT_TARGET_REPOSITORY_ROOT");
var repositoryRoot = configuredRepositoryRoot is not null
    ? Path.GetFullPath(configuredRepositoryRoot)
    : FindSiblingDungeonEscapeRepository(toolsDirectory);
if (provider == "scripted")
{
    var scriptedPath = Value(args, "--scripted-write-path");
    if (mode == "approved-write" && string.IsNullOrWhiteSpace(scriptedPath))
    {
        Console.Error.WriteLine("Scripted approved-write mode requires --scripted-write-path.");
        return 2;
    }
    executor = Scripted(repositoryRoot, mode == "approved-write", scriptedPath, knowledgeEnabled);
}
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var serverDll = Path.Combine(toolsDirectory, "Redpoint.Mcp", "bin", configuration, "net10.0",
    "Redpoint.Mcp.dll");
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine("Build tools/Redpoint.Mcp before running the orchestrator.");
    return 2;
}

var task = Value(args, "--task") ??
    "Inspect the inventory and interaction architecture and propose how a locked chest requiring a key should be implemented.";
var workflowId = "local-" + Guid.NewGuid().ToString("N");
var trace = new ConsoleWorkflowTraceSink();
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var mcp = await McpStdioToolClient.StartAsync(serverDll, repositoryRoot, startupTimeout.Token);
IAgentToolGateway gateway = new RoleBasedToolGateway(mcp, trace, workflowId);
if (knowledgeEnabled)
{
    try
    {
        gateway = new ContextToolGateway(gateway,
            ContextConfiguration.Create(repositoryRoot, Environment.GetEnvironmentVariable), trace, workflowId);
    }
    catch (ArgumentException)
    {
        Console.Error.WriteLine("Invalid knowledge configuration. Check REDPOINT_KNOWLEDGE_PROVIDER, DIMENSIONS, MODEL, INDEX_PATH and required OPENAI_API_KEY. Values are not logged.");
        return 2;
    }
}

var request = new WorkflowRequest(task,
    [mode == "approved-write" ? "Provide exactly one safe existing-file replacement"
        : "Provide an evidence-based, concrete technical analysis"],
    [mode == "approved-write" ? "Only trusted application code may prepare or apply the change"
        : "Do not modify project files", "Only QA may run bounded approved tests"],
    provider == "scripted"
        ? ["Inventory rules live in Core; Unity GameState coordinates interactions and persistence."]
        : []);
var workflow = mode == "approved-write"
    ? new OrchestrationWorkflow(workflowId, executor!, gateway, trace,
        new TrustedApprovedWriteExecutor(mcp, mcp.WriteApprovalIssuer, repositoryRoot),
        ConsoleHumanApprovalProvider.Create())
    : new OrchestrationWorkflow(workflowId, executor!, gateway, trace);
var result = mode == "approved-write"
    ? await workflow.RunApprovedWriteAsync(request)
    : await workflow.RunAsync(request);
Console.WriteLine($"RESULT status={result.Status} engineerAttempts={result.EngineerAttempts} " +
    $"reviewerAttempts={result.ReviewerAttempts} qaAttempts={result.QaAttempts}");
if (result.Proposal is not null) Console.WriteLine("PROPOSAL " + result.Proposal.ProposedSolution);
if (result.Review is not null) Console.WriteLine("REVIEW " + result.Review.Verdict);
if (result.QA is not null)
    Console.WriteLine($"QA {result.QA.Outcome} suites={string.Join(',', result.QA.TestSuitesRun)}");
if (result.AppliedChange is not null)
    Console.WriteLine($"APPLIED path={result.AppliedChange.Path} hash={result.AppliedChange.ResultingSha256}");
return result.Status == WorkflowStatus.Completed ? 0 : 1;

static IAgentExecutor Scripted(string repositoryRoot, bool writeEnabled, string? scriptedPath, bool knowledgeEnabled) =>
    new ScriptedAgentExecutor(
    async (assignment, context, cancellationToken) =>
    {
        var metadata = await context.Tools.CallAsync(AgentRole.UnityEngineer, "get_project_info",
            JsonSerializer.SerializeToElement(new { }), cancellationToken);
        var citations = knowledgeEnabled
            ? await DemoEvidence(AgentRole.UnityEngineer, context, cancellationToken) : [];
        if (knowledgeEnabled && citations.Count == 0)
            return AgentExecutionResult<EngineerToReviewer>.Failure(AgentRole.UnityEngineer,
                "The retrieval demo requires current non-empty source evidence. Build the index and retry.");
        ChangeProposal? change = null;
        if (writeEnabled)
        {
            var relative = scriptedPath!.Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(repositoryRoot, relative));
            var originalBytes = await File.ReadAllBytesAsync(full, cancellationToken);
            var original = await File.ReadAllTextAsync(full, cancellationToken);
            var marker = Path.GetExtension(full).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? "// Redpoint scripted approved-write demonstration."
                : "<!-- Redpoint scripted approved-write demonstration. -->";
            change = new(relative, Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant(),
                original.TrimEnd('\r', '\n') + Environment.NewLine + marker + Environment.NewLine,
                "Demonstrate an exact bounded human-approved replacement.");
        }
        return AgentExecutionResult<EngineerToReviewer>.Success(AgentRole.UnityEngineer,
            new(assignment.WorkflowId, assignment.Attempt, "Analyze the supplied architecture context.",
                knowledgeEnabled
                    ? "Retrieved inventory/persistence evidence: " + string.Join(", ", citations)
                    : "Add a key requirement to the existing interaction path and preserve opened-object persistence.",
                ["Inventory rules", "Unity interaction path", "chest persistence"],
                ["The supplied context describes the relevant boundaries."],
                ["Core key-matching tests", "Unity locked/unlocked interaction test"],
                ["Exact source locations require bounded source inspection."],
                knowledgeEnabled
                    ? [new("get_project_info", metadata.Succeeded), new(ContextToolGateway.ToolName, true)]
                    : [new("get_project_info", metadata.Succeeded)], change));
    },
    async (assignment, context, cancellationToken) =>
    {
        var citations = knowledgeEnabled
            ? await DemoEvidence(AgentRole.Reviewer, context, cancellationToken) : [];
        return AgentExecutionResult<ReviewerToManager>.Success(AgentRole.Reviewer,
            new(assignment.WorkflowId, assignment.Attempt,
                knowledgeEnabled && citations.Count == 0 ? ReviewVerdict.Inconclusive : ReviewVerdict.Approve,
                knowledgeEnabled ? ["Independently retrieved evidence: " + string.Join(", ", citations)]
                    : ["Proposal respects the supplied boundary and identifies validation."], [],
                ["Scripted demonstration validates the retrieval path, not independent reasoning."]));
    },
    async (assignment, context, cancellationToken) =>
    {
        var tests = await context.Tools.CallAsync(AgentRole.QA, "run_tests",
            JsonSerializer.SerializeToElement(new { suite = "orchestrator", timeoutSeconds = 120 }),
            cancellationToken);
        using var testEvidence = tests.Succeeded ? JsonDocument.Parse(tests.Content) : null;
        var passed = testEvidence is not null
            && testEvidence.RootElement.GetProperty("status").GetString() == "passed"
            && testEvidence.RootElement.GetProperty("exitCode").GetInt32() == 0;
        return AgentExecutionResult<QaToManager>.Success(AgentRole.QA,
            new(assignment.WorkflowId, assignment.Attempt,
                passed ? QaOutcome.Pass : QaOutcome.Fail,
                ["orchestrator"], [new("orchestrator", passed ? "passed" : "failed",
                    passed ? 0 : 1, 0, [], false,
                    "Existing orchestrator baseline was executed through bounded run_tests.")],
                ["This validates the current baseline, not the unimplemented proposal."],
                ["Post-implementation validation remains required."],
                ["The proposal has not been implemented in this read-only workflow."],
                [new("run_tests", tests.Succeeded)]));
    });

static async Task<IReadOnlyList<string>> DemoEvidence(AgentRole role, AgentExecutionContext context,
    CancellationToken cancellationToken)
{
    var result = await context.Tools.CallAsync(role, ContextToolGateway.ToolName,
        JsonSerializer.SerializeToElement(new { query = "inventory persistence", maxResults = 3, maximumCharacters = 8000 }),
        cancellationToken);
    if (!result.Succeeded) return [];
    using var json = JsonDocument.Parse(result.Content);
    var status = json.RootElement.GetProperty("status").GetString();
    Console.WriteLine($"CONTEXT role={role} status={status}");
    if (status != "Available") return [];
    var citations = json.RootElement.GetProperty("evidence").EnumerateArray()
        .Select(item => item.GetProperty("citation").GetString()!).ToArray();
    // These user-facing demo citations are not stored in operational traces; source text is not printed.
    foreach (var citation in citations) Console.WriteLine($"EVIDENCE role={role} citation={citation}");
    return citations;
}

static string? Value(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static bool TryEnvironmentInt(string name, int fallback, out int value)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw)) { value = fallback; return true; }
    return int.TryParse(raw, out value);
}

static string FindToolsDirectory(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (directory.Name == "Redpoint.AgentOrchestrator" && directory.Parent is not null)
            return directory.Parent.FullName;
    throw new InvalidOperationException("Could not locate the Redpoint tools directory.");
}

static string FindSiblingDungeonEscapeRepository(string toolsDirectory)
{
    var toolsRepository = Directory.GetParent(toolsDirectory)
        ?? throw new InvalidOperationException("Could not locate the Redpoint tools repository root.");
    var parent = toolsRepository.Parent
        ?? throw new InvalidOperationException("Could not locate a sibling DungeonEscape checkout.");
    var candidate = Path.Combine(parent.FullName, "DungeonEscape");
    if (File.Exists(Path.Combine(candidate, "DungeonEscape.sln"))) return candidate;
    throw new InvalidOperationException(
        "Pass --repository-root or set REDPOINT_TARGET_REPOSITORY_ROOT to a DungeonEscape checkout.");
}
