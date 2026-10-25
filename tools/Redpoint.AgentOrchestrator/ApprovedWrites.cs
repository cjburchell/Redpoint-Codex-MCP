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
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumPreviewCharacters = 128 * 1024;

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
        var proposedBytes = Encoding.UTF8.GetBytes(proposal.ProposedContent);
        var proposedSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(proposedBytes)).ToLowerInvariant();
        var expectedProposalId = WriteProposalIdentity.Create(proposal.Path,
            proposal.ExpectedOriginalSha256, proposedSha256, proposedBytes.Length);
        if (prepared.Status != "prepared"
            || prepared.ProposalId != expectedProposalId
            || prepared.Path != proposal.Path
            || prepared.OriginalSha256 != proposal.ExpectedOriginalSha256
            || prepared.ProposedSha256 != proposedSha256
            || prepared.OriginalLength is < 0 or > MaximumFileBytes
            || prepared.ProposedLength != proposedBytes.Length
            || prepared.ProposedLength > MaximumFileBytes
            || !prepared.ContentChanged
            || prepared.OriginalSha256 == prepared.ProposedSha256
            || string.IsNullOrEmpty(prepared.Preview)
            || prepared.Preview.Length > MaximumPreviewCharacters
            || prepared.Warnings is null || prepared.Warnings.Count > 10
            || prepared.Warnings.Any(warning => string.IsNullOrWhiteSpace(warning)
                || warning.Length > 1024 || warning.Any(char.IsControl))
            || prepared.PreviewTruncated && prepared.Warnings.Count == 0)
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
        GitStatusEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<GitStatusEvidence>(status.Content, JsonOptions); }
        catch (JsonException) { throw new TrustedWriteException("working-tree-check-malformed"); }
        if (evidence is null || evidence.Status != "available" || !evidence.Clean
            || evidence.Truncated || !evidence.StagedIncluded || !evidence.UnstagedIncluded
            || !evidence.UntrackedIncluded || evidence.Warnings is null || evidence.Warnings.Count != 0)
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

    private sealed record GitStatusEvidence(string Status, bool Clean, bool Truncated,
        bool StagedIncluded, bool UnstagedIncluded, bool UntrackedIncluded,
        IReadOnlyList<string> Warnings);
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
            || proposal.Path.Contains('\\')
            || Path.IsPathRooted(proposal.Path)
            || proposal.Path.Split('/').Any(segment => segment is "" or "." or "..")
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
