using System.Diagnostics;
using System.Text.Json;
using Redpoint.KnowledgeIndex;
using Xunit;

namespace Redpoint.AgentOrchestrator.Tests;

public sealed class ContextRetrievalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RedpointContextTests", Guid.NewGuid().ToString("N"));
    private readonly string indexPath;
    private const string Source = "# Inventory\nInventory persistence uses SaveInventory.\n";

    public ContextRetrievalTests()
    {
        Directory.CreateDirectory(root);
        indexPath = Path.Combine(root, "index.json");
        Git("init");
        File.WriteAllText(Path.Combine(root, "README.md"), Source);
        Git("add", "README.md");
        Git("-c", "user.name=Context Tests", "-c", "user.email=context@example.invalid", "commit", "-m", "fixture");
    }

    [Theory]
    [InlineData(AgentRole.UnityEngineer)]
    [InlineData(AgentRole.Reviewer)]
    public async Task AuthorizedSpecialistsReceiveCurrentCitationsAndExactSnapshotMetadata(AgentRole role)
    {
        var search = Search();
        var built = await search.BuildAsync(root, default);
        var (gateway, client, _) = Gateway(new KnowledgeContextProvider(root, search));
        Assert.Contains(ContextToolGateway.ToolName, gateway.AllowedToolsFor(role));
        Assert.Contains(await gateway.ListAllowedToolsAsync(role, default), tool => tool.Name == ContextToolGateway.ToolName);

        var call = await gateway.CallAsync(role, ContextToolGateway.ToolName, Arguments(), default);
        Assert.True(call.Succeeded);
        using var result = JsonDocument.Parse(call.Content);
        Assert.Equal("Available", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("Current", result.RootElement.GetProperty("freshness").GetString());
        var evidence = Assert.Single(result.RootElement.GetProperty("evidence").EnumerateArray());
        Assert.Equal("README.md:1-3", evidence.GetProperty("citation").GetString());
        Assert.Equal(Source, evidence.GetProperty("text").GetString());
        Assert.Equal(built.Index.Sources[0].Sha256, evidence.GetProperty("sourceFileHash").GetString());
        var version = result.RootElement.GetProperty("version");
        Assert.Equal(built.Index.SourceCommit, version.GetProperty("sourceCommit").GetString());
        Assert.Equal(built.Index.RepositoryIdentity, version.GetProperty("repositoryIdentity").GetString());
        Assert.Equal(built.Index.SchemaVersion, version.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(AgentRole.Manager)]
    [InlineData(AgentRole.QA)]
    [InlineData((AgentRole)999)]
    public async Task UnauthorizedRolesCannotAdvertiseOrInvokeRetrieval(AgentRole role)
    {
        var provider = new StubProvider();
        var (gateway, client, _) = Gateway(provider);
        Assert.DoesNotContain(ContextToolGateway.ToolName, gateway.AllowedToolsFor(role));
        Assert.DoesNotContain(await gateway.ListAllowedToolsAsync(role, default), tool => tool.Name == ContextToolGateway.ToolName);
        var call = await gateway.CallAsync(role, ContextToolGateway.ToolName, Arguments(), default);
        Assert.False(call.Succeeded);
        Assert.Equal("Forbidden", Status(call));
        Assert.Equal(0, provider.Calls);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("missing", "Unavailable")]
    [InlineData("corrupt", "Unavailable")]
    [InlineData("stale", "Stale")]
    [InlineData("incompatible", "Incompatible")]
    [InlineData("tampered", "Incompatible")]
    [InlineData("empty", "NoResults")]
    public async Task UnusableIndexesReturnStructuredOutcomesWithNoEvidence(string mode, string expected)
    {
        var search = Search();
        var built = await search.BuildAsync(root, default);
        switch (mode)
        {
            case "empty":
                await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "");
                await search.BuildAsync(root, default);
                break;
            case "missing": File.Delete(indexPath); break;
            case "corrupt": await File.WriteAllTextAsync(indexPath, "invalid-json"); break;
            case "stale": await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "changed"); break;
            case "incompatible": search = Search(new DeterministicEmbeddingProvider(32)); break;
            case "tampered":
                await new JsonKnowledgeStore(indexPath).SaveAsync(built.Index with
                { Chunks = [built.Index.Chunks[0] with { Text = "forged evidence" }] }, default);
                break;
        }
        var (gateway, _, _) = Gateway(new KnowledgeContextProvider(root, search));
        var call = await gateway.CallAsync(AgentRole.UnityEngineer, ContextToolGateway.ToolName,
            Arguments(mode == "empty" ? "zzzznomatch" : "inventory"), default);
        Assert.Equal(expected, Status(call));
        using var json = JsonDocument.Parse(call.Content);
        Assert.Empty(json.RootElement.GetProperty("evidence").EnumerateArray());
    }

    [Theory]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{\"query\":\"inventory\",\"maxResults\":6}")]
    [InlineData("{\"query\":\"inventory\",\"maximumCharacters\":8001}")]
    [InlineData("{\"query\":\"inventory\",\"maximumCharacters\":0}")]
    [InlineData("{\"query\":\"inventory\",\"repositoryRoot\":\"C:/other\"}")]
    [InlineData("{\"query\":\"inventory\",\"role\":\"Reviewer\"}")]
    [InlineData("{\"query\":\"inventory\",\"query\":\"duplicate\"}")]
    [InlineData("{\"query\":\"inventory\",\"maxResults\":\"5\"}")]
    [InlineData("[]")]
    public async Task InvalidOrAuthorityChangingArgumentsNeverReachProvider(string json)
    {
        var provider = new StubProvider();
        var (gateway, _, _) = Gateway(provider);
        var result = await gateway.CallAsync(AgentRole.UnityEngineer, ContextToolGateway.ToolName,
            JsonDocument.Parse(json).RootElement.Clone(), default);
        Assert.Equal("InvalidRequest", Status(result));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task QueryAndResultBudgetsAreEnforced()
    {
        var provider = new StubProvider();
        var (gateway, _, _) = Gateway(provider);
        Assert.Equal("InvalidRequest", Status(await gateway.CallAsync(AgentRole.UnityEngineer,
            ContextToolGateway.ToolName, Arguments(new string('a', 513)), default)));
        Assert.Equal("InvalidRequest", Status(await gateway.CallAsync(AgentRole.UnityEngineer,
            ContextToolGateway.ToolName, Arguments("a\nb"), default)));
        Assert.Equal(0, provider.Calls);

        var search = Search();
        await search.BuildAsync(root, default);
        var bounded = new KnowledgeContextProvider(root, search);
        var result = await bounded.RetrieveAsync(new("inventory", 1, 1), default);
        Assert.Empty(result.Evidence);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task CallsAreBoundedAcrossRolesAndRetriesIncludingProviderFailures()
    {
        var provider = new StubProvider { Error = new HttpRequestException("secret detail") };
        var (gateway, _, _) = Gateway(provider);
        for (var i = 0; i < 3; i++)
            Assert.Equal("ProviderFailed", Status(await gateway.CallAsync(AgentRole.UnityEngineer,
                ContextToolGateway.ToolName, Arguments(characters: 100), default)));
        Assert.Equal("LimitExceeded", Status(await gateway.CallAsync(AgentRole.UnityEngineer,
            ContextToolGateway.ToolName, Arguments(characters: 100), default)));
        for (var i = 0; i < 3; i++)
            await gateway.CallAsync(AgentRole.Reviewer, ContextToolGateway.ToolName, Arguments(characters: 100), default);
        Assert.Equal("LimitExceeded", Status(await gateway.CallAsync(AgentRole.Reviewer,
            ContextToolGateway.ToolName, Arguments(characters: 100), default)));
        Assert.Equal(6, provider.Calls);
    }

    [Fact]
    public async Task CharacterReservationsAreSharedAndCannotBeBypassedConcurrently()
    {
        var provider = new StubProvider();
        var (gateway, _, _) = Gateway(provider);
        var calls = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => gateway.CallAsync(
            i % 2 == 0 ? AgentRole.UnityEngineer : AgentRole.Reviewer,
            ContextToolGateway.ToolName, Arguments(characters: 8000), default)));
        Assert.Equal(3, calls.Count(call => Status(call) == "LimitExceeded"));
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task FailureAndCorrelationTracesNeverContainQueriesSourceOrExceptionText()
    {
        var provider = new StubProvider { Error = new Exception("credential-private-error") };
        var (gateway, _, trace) = Gateway(provider);
        var call = await gateway.CallAsync(AgentRole.Reviewer, ContextToolGateway.ToolName,
            Arguments("private-query"), default);
        Assert.Equal("ProviderFailed", Status(call));
        Assert.DoesNotContain("credential-private-error", call.Content);
        Assert.Equal(2, trace.Events.Count);
        Assert.All(trace.Events, item =>
        {
            Assert.Equal("context-workflow", item.WorkflowId);
            Assert.Equal(AgentRole.Reviewer, item.Role);
            Assert.DoesNotContain("private-query", item.Detail);
            Assert.DoesNotContain("credential-private-error", item.Detail);
        });
        Assert.NotNull(trace.Events[0].RunId);
        Assert.Equal(trace.Events[0].RunId, trace.Events[1].RunId);
    }

    [Fact]
    public async Task CallerCancellationReturnsStructuredOutcomeAndCancelsProvider()
    {
        var provider = new StubProvider { Delay = true };
        var (gateway, _, _) = Gateway(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var call = await gateway.CallAsync(AgentRole.UnityEngineer, ContextToolGateway.ToolName,
            Arguments(), cancellation.Token);
        Assert.Equal("Cancelled", Status(call));
        Assert.True(provider.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RetrievalTimeoutReturnsNoEvidenceAndCancelsProvider()
    {
        var provider = new StubProvider { Delay = true };
        var (gateway, _, _) = Gateway(provider);
        var call = await gateway.CallAsync(AgentRole.UnityEngineer, ContextToolGateway.ToolName, Arguments(), default);
        Assert.Equal("TimedOut", Status(call));
        Assert.True(provider.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ProviderAdapterSanitizesFailuresAndCancellation()
    {
        var provider = new KnowledgeContextProvider(root, new FailingSearch());
        var result = await provider.RetrieveAsync(new("inventory"), default);
        Assert.Equal(ContextStatus.ProviderFailed, result.Status);
        Assert.Empty(result.Evidence);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(ContextStatus.Cancelled, (await provider.RetrieveAsync(new("inventory"), cancellation.Token)).Status);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("path")]
    [InlineData("citation")]
    [InlineData("missing-version")]
    [InlineData("stale-evidence")]
    [InlineData("extra-results")]
    public async Task InvalidProviderEvidenceIsDiscarded(string kind)
    {
        var evidence = new KnowledgeSearchResult("README.md", 1, 1, "inventory", 1,
            "README.md:1-1", new string('a', 64), new string('b', 64));
        var result = new ProjectContext(ContextStatus.Available, IndexFreshness.Current,
            new(1, new string('c', 64), null, "offline", "test", 16, DateTimeOffset.UtcNow), [evidence]);
        result = kind switch
        {
            "oversized" => result with { Evidence = [evidence with { Text = new string('x', 8001) }] },
            "path" => result with { Evidence = [evidence with { Path = "../private" }] },
            "citation" => result with { Evidence = [evidence with { Citation = "fake:1-1" }] },
            "missing-version" => result with { Version = null },
            "stale-evidence" => result with { Freshness = IndexFreshness.Stale },
            _ => result with { Evidence = Enumerable.Repeat(evidence, 6).ToArray() }
        };
        var (gateway, _, _) = Gateway(new StubProvider { Result = result });
        var call = await gateway.CallAsync(AgentRole.UnityEngineer, ContextToolGateway.ToolName, Arguments(), default);
        Assert.Equal("ProviderFailed", Status(call));
        using var json = JsonDocument.Parse(call.Content);
        Assert.Empty(json.RootElement.GetProperty("evidence").EnumerateArray());
    }

    [Theory]
    [InlineData(AgentRole.UnityEngineer, "run_tests", false)]
    [InlineData(AgentRole.Reviewer, "apply_write", false)]
    [InlineData(AgentRole.QA, "run_tests", true)]
    [InlineData(AgentRole.Manager, "read_script", false)]
    public async Task ExistingMcpAdmissionRemainsAuthoritative(AgentRole role, string tool, bool allowed)
    {
        var (gateway, client, _) = Gateway(new StubProvider());
        var result = await gateway.CallAsync(role, tool, JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(allowed ? 1 : 0, client.Calls.Count);
    }

    [Fact]
    public void StartupConfigurationFailsClosedWithoutEchoingSecrets()
    {
        Assert.Throws<ArgumentException>(() => ContextConfiguration.Create(root,
            name => name == "REDPOINT_KNOWLEDGE_PROVIDER" ? "unexpected" : null));
        Assert.Throws<ArgumentException>(() => ContextConfiguration.Create(root,
            name => name == "REDPOINT_KNOWLEDGE_INDEX_PATH" ? "relative.json" : null));
        Assert.Throws<ArgumentException>(() => ContextConfiguration.Create(root,
            name => name == "REDPOINT_KNOWLEDGE_PROVIDER" ? "openai" : null));
        Assert.Throws<ArgumentException>(() => ContextConfiguration.Create(root,
            name => name == "REDPOINT_KNOWLEDGE_DIMENSIONS" ? "0" : null));
    }

    private KnowledgeIndexService Search(IEmbeddingProvider? provider = null) =>
        new(new KnowledgeCorpus(), new JsonKnowledgeStore(indexPath), provider ?? new DeterministicEmbeddingProvider(16));

    private static (ContextToolGateway Gateway, FakeClient Client, InMemoryWorkflowTraceSink Trace) Gateway(IContextProvider provider)
    {
        var trace = new InMemoryWorkflowTraceSink();
        var client = new FakeClient();
        return (new(new RoleBasedToolGateway(client, trace, "context-workflow"), provider, trace, "context-workflow"), client, trace);
    }

    private static JsonElement Arguments(string query = "inventory", int characters = 8000) =>
        JsonSerializer.SerializeToElement(new { query, maxResults = 5, maximumCharacters = characters });
    private static string Status(ToolCallResult result)
    {
        using var json = JsonDocument.Parse(result.Succeeded ? result.Content : result.Error!);
        return json.RootElement.GetProperty("status").GetString()!;
    }

    private sealed class StubProvider : IContextProvider
    {
        public int Calls;
        public Exception? Error;
        public ProjectContext? Result;
        public bool Delay;
        public CancellationToken Token;
        public async Task<ProjectContext> RetrieveAsync(ContextQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Token = cancellationToken;
            if (Delay) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Error is not null) throw Error;
            return Result ?? ProjectContext.Empty(ContextStatus.Unavailable);
        }
    }

    private sealed class FailingSearch : IKnowledgeSearch
    {
        public Task<KnowledgeSearchResponse> SearchAsync(string root, string query, int count, int chars, CancellationToken token) =>
            throw new HttpRequestException("private provider credential");
    }

    private sealed class FakeClient : IReadOnlyToolClient
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>([
                new("read_script", JsonSerializer.SerializeToElement(new { type = "object" }), true, false, false),
                new("run_tests", JsonSerializer.SerializeToElement(new { type = "object" }), false, false, false, Idempotent: false)]);
        public Task<ToolCallResult> CallAsync(string name, JsonElement arguments, CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult(new ToolCallResult(true, "{}"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private void Git(params string[] args)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        // This directory is the per-test GUID fixture created by this test class.
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
