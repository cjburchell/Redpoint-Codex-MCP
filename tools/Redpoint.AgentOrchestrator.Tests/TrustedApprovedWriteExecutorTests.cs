using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Redpoint.WriteApproval;
using Xunit;

namespace Redpoint.AgentOrchestrator.Tests;

public sealed class TrustedApprovedWriteExecutorTests
{
    private const string Path = "DungeonEscape.Core/Example.cs";
    private const string OriginalText = "old\n";
    private const string ProposedText = "new\n";
    private static readonly string OriginalHash = Hash(OriginalText);
    private static readonly string ProposedHash = Hash(ProposedText);
    private static readonly ChangeProposal Proposal = new(Path, OriginalHash, ProposedText, "change");

    [Fact]
    public async Task CompleteCleanStatusAndConsistentPreparationSucceed()
    {
        var client = new FakeClient(CleanStatus(), Preparation());
        var executor = Executor(client, out _);

        var prepared = await executor.PrepareAsync(Proposal, default);

        Assert.Equal("prepared", prepared.Status);
        Assert.Equal(ProposedHash, prepared.ProposedSha256);
        Assert.Equal(["get_git_diff", "prepare_write"], client.Calls);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    [InlineData("warnings")]
    [InlineData("missing-staged")]
    [InlineData("missing-unstaged")]
    [InlineData("missing-untracked")]
    [InlineData("untracked-file")]
    [InlineData("staged-change")]
    [InlineData("unstaged-change")]
    public async Task UncertainOrDirtyStatusFailsClosed(string scenario)
    {
        var status = scenario == "malformed" ? "{not-json" : scenario switch
        {
            "truncated" => CleanStatus(truncated: true),
            "unavailable" => CleanStatus(status: "unavailable"),
            "warnings" => CleanStatus(warnings: ["status information incomplete"]),
            "missing-staged" => CleanStatus(staged: false),
            "missing-unstaged" => CleanStatus(unstaged: false),
            "missing-untracked" => CleanStatus(untracked: false),
            _ => CleanStatus(clean: false)
        };
        var client = new FakeClient(status, Preparation());
        var executor = Executor(client, out var issuer);

        await Assert.ThrowsAsync<TrustedWriteException>(() =>
            executor.PrepareAsync(Proposal, default));

        Assert.DoesNotContain("prepare_write", client.Calls);
        Assert.DoesNotContain("apply_write", client.Calls);
        Assert.Equal(0, issuer.Calls);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("proposal-id")]
    [InlineData("path")]
    [InlineData("original-hash")]
    [InlineData("proposed-hash")]
    [InlineData("original-length")]
    [InlineData("proposed-length")]
    [InlineData("unchanged")]
    [InlineData("preview")]
    [InlineData("preview-too-large")]
    [InlineData("warnings-null")]
    [InlineData("warning-invalid")]
    [InlineData("truncated-without-warning")]
    public async Task InconsistentPreparationResponseFailsBeforeSigningOrApply(string mismatch)
    {
        var client = new FakeClient(CleanStatus(), Preparation(mismatch));
        var executor = Executor(client, out var issuer);

        var error = await Assert.ThrowsAsync<TrustedWriteException>(() =>
            executor.PrepareAsync(Proposal, default));

        Assert.Equal("prepare-result-mismatch", error.Code);
        Assert.DoesNotContain("apply_write", client.Calls);
        Assert.Equal(0, issuer.Calls);
    }

    [Fact]
    public async Task PreparationMismatchPreventsHumanApprovalInWorkflow()
    {
        var client = new FakeClient(CleanStatus(), Preparation("proposed-hash"));
        var executor = Executor(client, out var issuer);
        var human = new CountingHuman();
        var agents = new ScriptedAgentExecutor(
            (assignment, _, _) => Task.FromResult(AgentExecutionResult<EngineerToReviewer>.Success(
                AgentRole.UnityEngineer, new(assignment.WorkflowId, assignment.Attempt,
                    "change", "replace", [], [], [], [], [], Proposal))),
            (assignment, _, _) => Task.FromResult(AgentExecutionResult<ReviewerToManager>.Success(
                AgentRole.Reviewer, new(assignment.WorkflowId, assignment.Attempt,
                    ReviewVerdict.Approve, [], [], []))),
            (_, _, _) => throw new Xunit.Sdk.XunitException("QA must not run."));
        var workflow = new OrchestrationWorkflow("mismatch", agents, new NoTools(),
            new InMemoryWorkflowTraceSink(), executor, human);

        var result = await workflow.RunApprovedWriteAsync(
            new("change", ["safe"], ["one file"], []));

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Equal(0, human.Calls);
        Assert.Equal(0, issuer.Calls);
        Assert.DoesNotContain("apply_write", client.Calls);
    }

    private static TrustedApprovedWriteExecutor Executor(FakeClient client, out CountingIssuer issuer)
    {
        issuer = new();
        return new(client, issuer, "fixture");
    }

    private static string CleanStatus(string status = "available", bool clean = true,
        bool truncated = false, bool staged = true, bool unstaged = true, bool untracked = true,
        string[]? warnings = null) => JsonSerializer.Serialize(new
        {
            status, clean, truncated, stagedIncluded = staged, unstagedIncluded = unstaged,
            untrackedIncluded = untracked, entries = Array.Empty<object>(), warnings = warnings ?? []
        });

    private static string Preparation(string? mismatch = null)
    {
        var length = Encoding.UTF8.GetByteCount(ProposedText);
        var identity = WriteProposalIdentity.Create(Path, OriginalHash, ProposedHash, length);
        object? warnings = Array.Empty<string>();
        var preview = "--- a/file\n+++ b/file\n-old\n+new";
        var truncated = false;
        var status = "prepared";
        var proposalId = identity;
        var path = Path;
        var originalHash = OriginalHash;
        var proposedHash = ProposedHash;
        var originalLength = Encoding.UTF8.GetByteCount(OriginalText);
        var proposedLength = length;
        var changed = true;
        switch (mismatch)
        {
            case "status": status = "available"; break;
            case "proposal-id": proposalId = "sha256:" + new string('f', 64); break;
            case "path": path = "DungeonEscape.Core/Other.cs"; break;
            case "original-hash": originalHash = new string('f', 64); break;
            case "proposed-hash": proposedHash = new string('e', 64); break;
            case "original-length": originalLength = -1; break;
            case "proposed-length": proposedLength++; break;
            case "unchanged": changed = false; break;
            case "preview": preview = ""; break;
            case "preview-too-large": preview = new string('x', 128 * 1024 + 1); break;
            case "warnings-null": warnings = null; break;
            case "warning-invalid": warnings = new[] { "bad\nwarning" }; break;
            case "truncated-without-warning": truncated = true; break;
        }
        return JsonSerializer.Serialize(new
        {
            status, proposalId, path, originalSha256 = originalHash, proposedSha256 = proposedHash,
            originalLength, proposedLength, contentChanged = changed, preview,
            previewTruncated = truncated, warnings
        });
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeClient(string status, string preparation) : IReadOnlyToolClient
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>([
                new("get_git_diff", JsonSerializer.SerializeToElement(new { }), true, false, false,
                    Idempotent: true),
                new("prepare_write", JsonSerializer.SerializeToElement(new { }), true, false, false,
                    Idempotent: true),
                new("apply_write", JsonSerializer.SerializeToElement(new { }), false, true, false,
                    Idempotent: false)
            ]);
        public Task<ToolCallResult> CallAsync(string name, JsonElement arguments,
            CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult<ToolCallResult>(name switch
            {
                "get_git_diff" => new(true, status),
                "prepare_write" => new(true, preparation),
                _ => new(false, "", "unexpected")
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingIssuer : IWriteApprovalIssuer
    {
        public int Calls { get; private set; }
        public SignedWriteApproval Issue(WriteApprovalScope scope)
        {
            Calls++;
            throw new Xunit.Sdk.XunitException("Signing must not occur.");
        }
    }

    private sealed class CountingHuman : IHumanApprovalProvider
    {
        public int Calls { get; private set; }
        public Task<ApprovalDecision> DecideAsync(HumanApprovalRequest request, CancellationToken token)
        {
            Calls++;
            throw new Xunit.Sdk.XunitException("Human approval must not be requested.");
        }
    }

    private sealed class NoTools : IAgentToolGateway
    {
        public IReadOnlyList<string> AllowedTools => [];
        public IReadOnlyList<string> AllowedToolsFor(AgentRole role) => [];
        public Task<IReadOnlyList<McpToolDefinition>> ListAllowedToolsAsync(AgentRole role,
            CancellationToken token) => Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
        public Task<ToolCallResult> CallAsync(AgentRole role, string name, JsonElement arguments,
            CancellationToken token) => Task.FromResult(new ToolCallResult(false, ""));
    }
}
