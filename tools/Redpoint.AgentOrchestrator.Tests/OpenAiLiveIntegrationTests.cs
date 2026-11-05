using Xunit;
using Redpoint.KnowledgeIndex;

namespace Redpoint.AgentOrchestrator.Tests;

public sealed class OpenAiLiveIntegrationTests
{
    [OpenAiFact]
    [Trait("Category", "LiveOpenAI")]
    public async Task RealProviderProducesTypedSpecialistWorkflowResult()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")!;
        var model = Environment.GetEnvironmentVariable("REDPOINT_OPENAI_MODEL")!;
        var testProject = FindAncestor(AppContext.BaseDirectory, "Redpoint.AgentOrchestrator.Tests");
        var toolsDirectory = testProject.Parent!;
        var root = Environment.GetEnvironmentVariable("REDPOINT_TARGET_REPOSITORY_ROOT")
            ?? Path.Combine(toolsDirectory.Parent!.Parent!.FullName, "DungeonEscape");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(toolsDirectory.FullName, "Redpoint.Mcp", "bin", configuration,
            "net10.0", "Redpoint.Mcp.dll");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var mcp = await McpStdioToolClient.StartAsync(dll, root, timeout.Token);
        var trace = new InMemoryWorkflowTraceSink();
        using var index = new TemporaryKnowledgeIndex();
        var search = new KnowledgeIndexService(new KnowledgeCorpus(), new JsonKnowledgeStore(index.Path),
            new DeterministicEmbeddingProvider());
        await search.BuildAsync(root, timeout.Token);
        var gateway = new ContextToolGateway(new RoleBasedToolGateway(mcp, trace, "live-openai"),
            new KnowledgeContextProvider(root, search), trace, "live-openai");
        var executor = new LlmAgentExecutor(new OpenAiLlmClient(apiKey),
            LlmExecutionOptions.Create(model, 300, 8));
        var workflow = new OrchestrationWorkflow("live-openai", executor, gateway, trace);
        var result = await workflow.RunAsync(new(
            "Analyze DungeonEscape inventory persistence. Engineer and Reviewer must each call retrieve_project_context with query inventory persistence, then cite returned source evidence. Propose one small improvement without implementing it.",
            ["Use repository evidence", "Return a concrete proposal"],
            ["Do not modify files", "Only QA may run bounded approved tests"], []), timeout.Token);

        Assert.NotEqual(WorkflowStatus.Failed, result.Status);
        Assert.NotNull(result.Proposal);
        Assert.NotNull(result.Review);
        foreach (var role in new[] { AgentRole.UnityEngineer, AgentRole.Reviewer })
            Assert.Contains(trace.Events, item => item.Role == role && item.EventType == "ContextRetrievalCompleted"
                && item.Detail.Contains("status=Available", StringComparison.Ordinal));
        Assert.NotNull(result.QA);
        Assert.Contains(trace.Events, item => item.EventType == "ToolCallStarted"
            && item.Role == AgentRole.QA && item.Detail == "run_tests");
        Assert.DoesNotContain(trace.Events, item => item.Detail.Contains(apiKey, StringComparison.Ordinal));
    }

    private sealed class TemporaryKnowledgeIndex : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "redpoint-live-index-" + Guid.NewGuid().ToString("N") + ".json");
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }

    private static DirectoryInfo FindAncestor(string start, string name)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (directory.Name == name) return directory;
        throw new InvalidOperationException($"Could not find {name} from {start}.");
    }

    private sealed class OpenAiFactAttribute : FactAttribute
    {
        public OpenAiFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("REDPOINT_RUN_LIVE_OPENAI_TESTS") != "1"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDPOINT_OPENAI_MODEL")))
                Skip = "Set REDPOINT_RUN_LIVE_OPENAI_TESTS=1, OPENAI_API_KEY and REDPOINT_OPENAI_MODEL to run the live provider test.";
        }
    }
}
