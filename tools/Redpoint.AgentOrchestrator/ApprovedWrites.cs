using System.Text;
using System.Text.Json;
using Redpoint.WriteApproval;

namespace Redpoint.AgentOrchestrator;

public interface IHumanApprovalProvider
{
    Task<ApprovalDecision> DecideAsync(HumanApprovalRequest request,
        CancellationToken cancellationToken);
}

public interface IApprovedWriteExecutor
{
    string TargetRepository { get; }
    Task EnsureTargetCleanAsync(CancellationToken cancellationToken);
    Task<PreparedWrite> PrepareAsync(ChangeProposal proposal, CancellationToken cancellationToken);
    Task<AppliedWrite> ApplyAsync(PreparedWrite prepared, ApprovalDecision decision,
        CancellationToken cancellationToken);
}

public sealed class TrustedApprovedWriteExecutor(IReadOnlyToolClient client,
    IWriteApprovalIssuer approvalIssuer, string targetRepository) : IApprovedWriteExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNameCaseInsensitive = true };

    public string TargetRepository { get; } = Path.GetFullPath(targetRepository);

    public async Task<PreparedWrite> PrepareAsync(ChangeProposal proposal,
        CancellationToken cancellationToken)
    {
        ChangeProposalValidator.Validate(proposal);
        await EnsureTargetCleanAsync(cancellationToken);
        await RequireDescriptor("prepare_write", true, false, false, true, cancellationToken);
        var result = await client.CallAsync("prepare_write", JsonSerializer.SerializeToElement(new
        {
            path = proposal.Path,
            proposedText = proposal.ProposedContent,
            expectedCurrentSha256 = proposal.ExpectedOriginalSha256
        }), cancellationToken);
        if (!result.Succeeded) throw new TrustedWriteException("prepare-failed");
        var prepared = JsonSerializer.Deserialize<PreparedWrite>(result.Content, JsonOptions)
            ?? throw new TrustedWriteException("malformed-prepare-result");
        if (string.IsNullOrWhiteSpace(prepared.ProposalId)
            || string.IsNullOrWhiteSpace(prepared.Path)
            || prepared.OriginalSha256 != proposal.ExpectedOriginalSha256
            || prepared.ProposedLength != Encoding.UTF8.GetByteCount(proposal.ProposedContent))
            throw new TrustedWriteException("prepare-result-mismatch");
        return prepared;
    }

    public async Task EnsureTargetCleanAsync(CancellationToken cancellationToken)
    {
        await RequireDescriptor("get_git_diff", true, false, false, true, cancellationToken);
        var status = await client.CallAsync("get_git_diff", JsonSerializer.SerializeToElement(new
        {
            includeStaged = true, includeUnstaged = true, includeUntrackedMetadata = true,
            maxFiles = 1, maxPatchCharacters = 1
        }), cancellationToken);
        if (!status.Succeeded) throw new TrustedWriteException("working-tree-check-failed");
        using (var document = JsonDocument.Parse(status.Content))
            if (!document.RootElement.TryGetPropertyIgnoreCase("clean", out var clean)
                || clean.ValueKind != JsonValueKind.True)
                throw new TrustedWriteException("working-tree-not-clean");
    }

    public async Task<AppliedWrite> ApplyAsync(PreparedWrite prepared, ApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        var expectedScope = new WriteApprovalScope(prepared.ProposalId, prepared.Path,
            prepared.OriginalSha256, prepared.ProposedSha256, prepared.ProposedLength);
        var now = DateTimeOffset.UtcNow;
        if (decision.Outcome != ApprovalOutcome.Approved || decision.ProposalId != prepared.ProposalId
            || decision.Scope != expectedScope || decision.DecidedAtUtc < now.AddMinutes(-10)
            || decision.DecidedAtUtc > now.AddMinutes(1))
            throw new TrustedWriteException("approval-scope-mismatch");
        await RequireDescriptor("apply_write", false, true, false, false, cancellationToken);
        var signed = approvalIssuer.Issue(expectedScope);
        var result = await client.CallAsync("apply_write", JsonSerializer.SerializeToElement(new
        {
            proposalId = prepared.ProposalId,
            path = prepared.Path,
            originalSha256 = prepared.OriginalSha256,
            proposedSha256 = prepared.ProposedSha256,
            approvalToken = signed.Token
        }), cancellationToken);
        if (!result.Succeeded) throw new TrustedWriteException("apply-failed");
        var applied = JsonSerializer.Deserialize<AppliedWrite>(result.Content, JsonOptions)
            ?? throw new TrustedWriteException("malformed-apply-result");
        if (applied.Status is not ("applied" or "unchanged")
            || applied.ProposalId != prepared.ProposalId || applied.Path != prepared.Path
            || applied.ResultingSha256 != prepared.ProposedSha256
            || applied.ApprovalId != signed.ApprovalId)
            throw new TrustedWriteException("apply-result-mismatch");
        return applied;
    }

    private async Task RequireDescriptor(string name, bool readOnly, bool destructive,
        bool openWorld, bool idempotent, CancellationToken cancellationToken)
    {
        var matches = (await client.ListToolsAsync(cancellationToken)).Where(tool => tool.Name == name).ToArray();
        if (matches.Length != 1 || matches[0].ReadOnly != readOnly
            || matches[0].Destructive != destructive || matches[0].OpenWorld != openWorld
            || matches[0].Idempotent != idempotent)
            throw new TrustedWriteException("unsafe-tool-metadata");
    }
}

public sealed class TrustedWriteException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public static class ChangeProposalValidator
{
    public const int MaximumProposedBytes = 1024 * 1024;

    public static void Validate(ChangeProposal? proposal)
    {
        if (proposal is null || string.IsNullOrWhiteSpace(proposal.Path)
            || proposal.Path.Length > 1024 || proposal.Path.Any(char.IsControl)
            || proposal.Path.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(proposal.Path)
            || proposal.ExpectedOriginalSha256 is not { Length: 64 }
            || !proposal.ExpectedOriginalSha256.All(char.IsAsciiHexDigit)
            || string.IsNullOrEmpty(proposal.ProposedContent)
            || Encoding.UTF8.GetByteCount(proposal.ProposedContent) > MaximumProposedBytes
            || string.IsNullOrWhiteSpace(proposal.Rationale))
            throw new ArgumentException("The Engineer change proposal is invalid or exceeds its bound.");
    }
}

public sealed class ConsoleHumanApprovalProvider(TextReader input, TextWriter output,
    bool trustedInteractiveInput, string? actor = null) : IHumanApprovalProvider
{
    public static ConsoleHumanApprovalProvider Create() => new(Console.In, Console.Out,
        !Console.IsInputRedirected, Environment.UserName);

    public async Task<ApprovalDecision> DecideAsync(HumanApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var scope = new WriteApprovalScope(request.Prepared.ProposalId, request.Prepared.Path,
            request.Prepared.OriginalSha256, request.Prepared.ProposedSha256,
            request.Prepared.ProposedLength);
        if (!trustedInteractiveInput || request.Prepared.PreviewTruncated)
            return Decision(ApprovalOutcome.Cancelled, scope);
        await output.WriteLineAsync("=======================================");
        await output.WriteLineAsync(" REDPOINT — CHANGE APPROVAL");
        await output.WriteLineAsync("=======================================");
        await output.WriteLineAsync($"Target: {request.TargetRepository}");
        await output.WriteLineAsync($"File: {request.Prepared.Path}");
        await output.WriteLineAsync($"Proposal: {request.Prepared.ProposalId}");
        await output.WriteLineAsync($"Original SHA-256: {request.Prepared.OriginalSha256}");
        await output.WriteLineAsync($"Proposed SHA-256: {request.Prepared.ProposedSha256}");
        await output.WriteLineAsync($"Reviewer: {request.ReviewerVerdict}");
        await output.WriteLineAsync("Preview:");
        await output.WriteLineAsync(request.Prepared.Preview);
        foreach (var warning in request.Prepared.Warnings) await output.WriteLineAsync("Warning: " + warning);
        await output.WriteLineAsync("[A] Approve  [R] Reject  [C] Cancel");
        cancellationToken.ThrowIfCancellationRequested();
        var response = await input.ReadLineAsync(cancellationToken);
        return Decision(response?.Trim().ToUpperInvariant() switch
        {
            "A" or "APPROVE" => ApprovalOutcome.Approved,
            "R" or "REJECT" => ApprovalOutcome.Rejected,
            _ => ApprovalOutcome.Cancelled
        }, scope);
    }

    private ApprovalDecision Decision(ApprovalOutcome outcome, WriteApprovalScope scope) =>
        new(scope.ProposalId, scope, outcome, DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(actor) ? null : actor);
}

internal static class JsonElementExtensions
{
    internal static bool TryGetPropertyIgnoreCase(this JsonElement element, string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        value = default;
        return false;
    }
}
