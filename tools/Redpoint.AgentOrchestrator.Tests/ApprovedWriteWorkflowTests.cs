using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Redpoint.WriteApproval;
using Xunit;

namespace Redpoint.AgentOrchestrator.Tests;

public sealed class ApprovedWriteWorkflowTests
{
    private static readonly string Original = new('a', 64);
    private static readonly string Proposed = new('b', 64);

    [Theory]
    [InlineData(ApprovalOutcome.Approved, true)]
    [InlineData(ApprovalOutcome.Rejected, false)]
    public async Task RealStdioWorkflowChangesFixtureOnlyAfterHumanApproval(
        ApprovalOutcome outcome, bool shouldChange)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "RedpointApprovedWrite",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "DungeonEscape.Core"));
        Directory.CreateDirectory(Path.Combine(fixture, "DungeonEscape.Unity"));
        File.WriteAllText(Path.Combine(fixture, "DungeonEscape.sln"), "");
        var relative = "DungeonEscape.Core/Example.cs";
        var full = Path.Combine(fixture, "DungeonEscape.Core", "Example.cs");
        const string original = "public static class Example { public const int Value = 1; }\n";
        const string replacement = "public static class Example { public const int Value = 2; }\n";
        File.WriteAllText(full, original, new UTF8Encoding(false));
        Git(fixture, "init");
        Git(fixture, "config", "user.name", "Redpoint Tests");
        Git(fixture, "config", "user.email", "tests@example.invalid");
        Git(fixture, "add", ".");
        Git(fixture, "commit", "-m", "fixture");
        try
        {
            var testProject = FindAncestor(AppContext.BaseDirectory, "Redpoint.AgentOrchestrator.Tests");
            var tools = testProject.Parent!;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var dll = Path.Combine(tools.FullName, "Redpoint.Mcp", "bin", configuration,
                "net10.0", "Redpoint.Mcp.dll");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var client = await McpStdioToolClient.StartAsync(dll, fixture, timeout.Token);
            Assert.Equal(9, (await client.ListToolsAsync(timeout.Token)).Count);
            var trace = new InMemoryWorkflowTraceSink();
            var gateway = new RoleBasedToolGateway(client, trace, "stdio-write");
            var proposal = new ChangeProposal(relative, Hash(original), replacement, "fixture change");
            var executor = new ScriptedAgentExecutor(
                (assignment, _, _) => Task.FromResult(AgentExecutionResult<EngineerToReviewer>.Success(
                    AgentRole.UnityEngineer, new(assignment.WorkflowId, assignment.Attempt,
                        "fixture", "replace value", [], [], ["fixture QA"], [], [], proposal))),
                (assignment, _, _) => Task.FromResult(AgentExecutionResult<ReviewerToManager>.Success(
                    AgentRole.Reviewer, new(assignment.WorkflowId, assignment.Attempt,
                        ReviewVerdict.Approve, ["exact content reviewed"], [], []))),
                (assignment, _, _) => Task.FromResult(AgentExecutionResult<QaToManager>.Success(
                    AgentRole.QA, new(assignment.WorkflowId, assignment.Attempt, QaOutcome.Pass,
                        ["fixture"], [new("fixture", "passed", 0, 1, [], false,
                            "Applied file metadata was validated in the disposable fixture.")],
                        ["resulting hash received"], [], [], [new("run_tests", true)]))));
            var workflow = new OrchestrationWorkflow("stdio-write", executor, gateway, trace,
                new TrustedApprovedWriteExecutor(client, client.WriteApprovalIssuer, fixture),
                new FakeHuman(outcome));

            var result = await workflow.RunApprovedWriteAsync(Request(), timeout.Token);

            Assert.Equal(shouldChange ? WorkflowStatus.Completed : WorkflowStatus.Incomplete, result.Status);
            Assert.Equal(shouldChange ? replacement : original, File.ReadAllText(full));
            Assert.Equal(shouldChange, result.AppliedChange is not null);
            if (shouldChange) Assert.Equal(Hash(replacement), result.AppliedChange!.ResultingSha256);
        }
        finally { DeleteFixture(fixture); }
    }

    [Theory]
    [InlineData(ApprovalOutcome.Approved, WorkflowStatus.Completed, 1, 1)]
    [InlineData(ApprovalOutcome.Rejected, WorkflowStatus.Incomplete, 0, 0)]
    [InlineData(ApprovalOutcome.Cancelled, WorkflowStatus.Incomplete, 0, 0)]
    public async Task HumanDecisionControlsWriteAndQa(ApprovalOutcome outcome,
        WorkflowStatus expectedStatus, int expectedApplies, int expectedQa)
    {
        var run = Create(outcome);
        var result = await run.Workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedApplies, run.Writer.ApplyCalls);
        Assert.Equal(expectedQa, run.QaCalls());
        Assert.DoesNotContain(run.Trace.Events, e => e.Detail.Contains("approval-token"));
        Assert.DoesNotContain(run.Trace.Events,
            e => e.Detail.Contains(Change().ProposedContent, StringComparison.Ordinal));
        if (outcome == ApprovalOutcome.Approved)
        {
            Assert.NotNull(result.AppliedChange);
            Assert.Contains(run.Trace.Events, e => e.EventType == "WriteApplied");
            Assert.Contains(run.Trace.Events, e => e.EventType == "QADispatched");
        }
    }

    [Fact]
    public async Task ReviewerSeesExactProposalBeforePreparation()
    {
        ChangeProposal? reviewed = null;
        var run = Create(ApprovalOutcome.Rejected, assignment => reviewed = assignment.EngineerResult.Change);
        await run.Workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(Change(), reviewed);
        Assert.Equal(Change(), run.Writer.PreparedProposal);
    }

    [Fact]
    public async Task RequestChangesUsesOneRetryBeforePromptingHuman()
    {
        var decisions = 0;
        var writer = new FakeWriter();
        var executor = Executor(ReviewVerdict.RequestChanges, () => decisions++, writer,
            secondVerdict: ReviewVerdict.Approve);
        var workflow = new OrchestrationWorkflow("write", executor, new NoTools(),
            new InMemoryWorkflowTraceSink(), writer, new FakeHuman(ApprovalOutcome.Rejected));

        var result = await workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(2, result.EngineerAttempts);
        Assert.Equal(1, decisions);
    }

    [Fact]
    public async Task PreparationFailureNeverRequestsHumanApproval()
    {
        var human = new FakeHuman(ApprovalOutcome.Approved);
        var writer = new FakeWriter { FailPrepare = true };
        var workflow = new OrchestrationWorkflow("write", Executor(ReviewVerdict.Approve, null, writer),
            new NoTools(), new InMemoryWorkflowTraceSink(), writer, human);

        var result = await workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(0, human.Calls);
        Assert.Equal(0, writer.ApplyCalls);
    }

    [Fact]
    public async Task TruncatedPreviewCannotReachHumanApproval()
    {
        var human = new FakeHuman(ApprovalOutcome.Approved);
        var writer = new FakeWriter { PreviewTruncated = true };
        var workflow = new OrchestrationWorkflow("write", Executor(ReviewVerdict.Approve, null, writer),
            new NoTools(), new InMemoryWorkflowTraceSink(), writer, human);

        var result = await workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(0, human.Calls);
        Assert.Equal(0, writer.ApplyCalls);
    }

    [Fact]
    public async Task WrongHumanScopeFailsBeforeApply()
    {
        var writer = new FakeWriter();
        var human = new FakeHuman(ApprovalOutcome.Approved, wrongScope: true);
        var workflow = new OrchestrationWorkflow("write", Executor(ReviewVerdict.Approve, null, writer),
            new NoTools(), new InMemoryWorkflowTraceSink(), writer, human);

        var result = await workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(0, writer.ApplyCalls);
    }

    [Theory]
    [InlineData(QaOutcome.Fail)]
    [InlineData(QaOutcome.Inconclusive)]
    public async Task NonPassingQaRequiresHumanWithoutRepair(QaOutcome outcome)
    {
        var run = Create(ApprovalOutcome.Approved, qaOutcome: outcome);
        var result = await run.Workflow.RunApprovedWriteAsync(Request());

        Assert.Equal(WorkflowStatus.NeedsHumanDecision, result.Status);
        Assert.Equal(1, result.EngineerAttempts);
        Assert.Equal(1, run.Writer.ApplyCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unexpected")]
    public async Task MissingOrUnrecognizedConsoleInputCannotApprove(string input)
    {
        var prepared = new FakeWriter().Prepared();
        var provider = new ConsoleHumanApprovalProvider(new StringReader(input), new StringWriter(), true);
        var decision = await provider.DecideAsync(new("target", prepared, ReviewVerdict.Approve), default);
        Assert.Equal(ApprovalOutcome.Cancelled, decision.Outcome);
    }

    [Fact]
    public async Task TrustedExecutorRejectsUnsafeWriteToolMetadata()
    {
        var client = new MetadataClient();
        var executor = new TrustedApprovedWriteExecutor(client,
            new HmacWriteApprovalIssuer(WriteApprovalKey.Generate()), "fixture");

        var error = await Assert.ThrowsAsync<TrustedWriteException>(() =>
            executor.PrepareAsync(Change(), default));

        Assert.Equal("unsafe-tool-metadata", error.Code);
        Assert.DoesNotContain("prepare_write", client.Calls);
    }

    private static Run Create(ApprovalOutcome outcome, Action<ReviewAssignment>? reviewer = null,
        QaOutcome qaOutcome = QaOutcome.Pass)
    {
        var writer = new FakeWriter();
        var trace = new InMemoryWorkflowTraceSink();
        var qaCalls = 0;
        var executor = new ScriptedAgentExecutor(
            (assignment, _, _) => Task.FromResult(AgentExecutionResult<EngineerToReviewer>.Success(
                AgentRole.UnityEngineer, Engineer(assignment))),
            (assignment, _, _) =>
            {
                reviewer?.Invoke(assignment);
                return Task.FromResult(AgentExecutionResult<ReviewerToManager>.Success(AgentRole.Reviewer,
                    new(assignment.WorkflowId, assignment.Attempt, ReviewVerdict.Approve, [], [], [])));
            },
            (assignment, _, _) =>
            {
                qaCalls++;
                Assert.NotNull(assignment.AppliedChange);
                var passed = qaOutcome == QaOutcome.Pass;
                return Task.FromResult(AgentExecutionResult<QaToManager>.Success(AgentRole.QA,
                    new(assignment.WorkflowId, assignment.Attempt, qaOutcome,
                        passed ? ["core"] : [], passed
                            ? [new QaTestEvidence("core", "passed", 0, 1, [], false, "passed")]
                            : [], ["validated applied hash"], [], [],
                        passed ? [new ToolUse("run_tests", true)] : [])));
            });
        var workflow = new OrchestrationWorkflow("write", executor, new NoTools(), trace,
            writer, new FakeHuman(outcome));
        return new(workflow, writer, trace, () => qaCalls);
    }

    private static IAgentExecutor Executor(ReviewVerdict firstVerdict, Action? approved,
        FakeWriter writer, ReviewVerdict? secondVerdict = null) => new ScriptedAgentExecutor(
        (assignment, _, _) => Task.FromResult(AgentExecutionResult<EngineerToReviewer>.Success(
            AgentRole.UnityEngineer, Engineer(assignment))),
        (assignment, _, _) =>
        {
            var verdict = assignment.Attempt == 1 ? firstVerdict : secondVerdict ?? firstVerdict;
            if (verdict == ReviewVerdict.Approve) approved?.Invoke();
            return Task.FromResult(AgentExecutionResult<ReviewerToManager>.Success(AgentRole.Reviewer,
                new(assignment.WorkflowId, assignment.Attempt, verdict, [],
                    verdict == ReviewVerdict.RequestChanges ? ["revise"] : [], [])));
        },
        (assignment, _, _) => Task.FromResult(AgentExecutionResult<QaToManager>.Success(AgentRole.QA,
            new(assignment.WorkflowId, assignment.Attempt, QaOutcome.Pass, ["core"],
                [new("core", "passed", 0, 1, [], false, "passed")], [], [], [],
                [new("run_tests", true)]))));

    private static EngineerToReviewer Engineer(ManagerToEngineer assignment) =>
        new(assignment.WorkflowId, assignment.Attempt, "interpret", "solution", [], [], [], [], [], Change());

    private static ChangeProposal Change() => new("DungeonEscape.Core/Example.cs", Original,
        "new content\n", "safe change");

    private static WorkflowRequest Request() => new("change file", ["safe"], ["one file"], []);

    private sealed record Run(OrchestrationWorkflow Workflow, FakeWriter Writer,
        InMemoryWorkflowTraceSink Trace, Func<int> QaCalls);

    private sealed class FakeWriter : IApprovedWriteExecutor
    {
        public string TargetRepository => "fixture";
        public bool FailPrepare { get; init; }
        public bool PreviewTruncated { get; init; }
        public int ApplyCalls { get; private set; }
        public ChangeProposal? PreparedProposal { get; private set; }
        public Task EnsureTargetCleanAsync(CancellationToken token) => Task.CompletedTask;
        public PreparedWrite Prepared() => new("proposal", Change().Path, Original, Proposed,
            12, "diff", PreviewTruncated, []);
        public Task<PreparedWrite> PrepareAsync(ChangeProposal proposal, CancellationToken token)
        {
            if (FailPrepare) throw new TrustedWriteException("prepare-failed");
            PreparedProposal = proposal;
            return Task.FromResult(Prepared());
        }
        public Task<AppliedWrite> ApplyAsync(PreparedWrite prepared, ApprovalDecision decision,
            CancellationToken token)
        {
            ApplyCalls++;
            return Task.FromResult(new AppliedWrite("applied", prepared.Path, prepared.OriginalSha256,
                prepared.ProposedSha256, prepared.ProposedLength, prepared.ProposalId, "approval", []));
        }
    }

    private sealed class FakeHuman(ApprovalOutcome outcome, bool wrongScope = false) : IHumanApprovalProvider
    {
        public int Calls { get; private set; }
        public Task<ApprovalDecision> DecideAsync(HumanApprovalRequest request, CancellationToken token)
        {
            Calls++;
            var scope = new WriteApprovalScope(request.Prepared.ProposalId,
                wrongScope ? "DungeonEscape.Core/Other.cs" : request.Prepared.Path,
                request.Prepared.OriginalSha256, request.Prepared.ProposedSha256,
                request.Prepared.ProposedLength);
            return Task.FromResult(new ApprovalDecision(request.Prepared.ProposalId, scope,
                outcome, DateTimeOffset.UtcNow, "tester"));
        }
    }

    private sealed class NoTools : IAgentToolGateway
    {
        public IReadOnlyList<string> AllowedTools => [];
        public IReadOnlyList<string> AllowedToolsFor(AgentRole role) => [];
        public Task<IReadOnlyList<McpToolDefinition>> ListAllowedToolsAsync(AgentRole role,
            CancellationToken token) => Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
        public Task<ToolCallResult> CallAsync(AgentRole role, string name,
            System.Text.Json.JsonElement arguments, CancellationToken token) =>
            Task.FromResult(new ToolCallResult(false, ""));
    }

    private sealed class MetadataClient : IReadOnlyToolClient
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>([
                new("get_git_diff", JsonSerializer.SerializeToElement(new { }), true, false, false,
                    Idempotent: true),
                new("prepare_write", JsonSerializer.SerializeToElement(new { }), false, false, false,
                    Idempotent: true),
                new("apply_write", JsonSerializer.SerializeToElement(new { }), false, true, false,
                    Idempotent: false)
            ]);
        public Task<ToolCallResult> CallAsync(string name, JsonElement arguments,
            CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult(new ToolCallResult(true, "{\"clean\":true}"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static DirectoryInfo FindAncestor(string start, string name)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (directory.Name == name) return directory;
        throw new InvalidOperationException($"Could not locate {name}.");
    }

    private static void DeleteFixture(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
