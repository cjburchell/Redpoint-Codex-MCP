namespace Redpoint.AgentOrchestrator;

public sealed class OrchestrationWorkflow(string workflowId, IAgentExecutor executor,
    IAgentToolGateway tools, IWorkflowTraceSink trace,
    IApprovedWriteExecutor? approvedWrites = null, IHumanApprovalProvider? humanApproval = null)
{
    public const int MaximumEngineerAttempts = 2;
    public const int MaximumReviewerAttempts = 2;

    public async Task<WorkflowResult> RunAsync(WorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        await Emit("WorkflowStarted", "Read-only deterministic orchestration started.");
        var stage = WorkflowStage.Created;
        EngineerToReviewer? proposal = null;
        ReviewerToManager? review = null;
        IReadOnlyList<string> requiredChanges = [];

        for (var attempt = 1; attempt <= MaximumEngineerAttempts; attempt++)
        {
            await TransitionAsync(stage, WorkflowStage.Engineer, attempt == 1
                ? "Manager dispatched initial analysis."
                : "Manager dispatched the single allowed revision.");
            stage = WorkflowStage.Engineer;
            if (attempt > 1)
            {
                await Emit("RetryConsumed", "Engineer retry budget consumed.", AgentRole.Manager, attempt);
            }

            var managerToEngineer = new ManagerToEngineer(workflowId, attempt, request.Task,
                request.AcceptanceCriteria, request.Constraints, request.SuppliedContext,
                tools.AllowedTools, requiredChanges);
            var engineerRun = Guid.NewGuid().ToString("N");
            await Emit("AgentDispatched", "Unity Engineer received a typed assignment.",
                AgentRole.UnityEngineer, attempt, engineerRun);
            AgentExecutionResult<EngineerToReviewer> engineerResult;
            try
            {
                engineerResult = await executor.ExecuteEngineerAsync(managerToEngineer,
                    new(workflowId, attempt, tools, trace), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Emit("AgentCompleted", "Unity Engineer execution failed.",
                    AgentRole.UnityEngineer, attempt, engineerRun);
                return await Fail("Unity Engineer execution failed: " + exception.GetType().Name,
                    attempt, attempt - 1, proposal, review);
            }
            await Emit("AgentCompleted", engineerResult.Error ?? "Unity Engineer completed.",
                engineerResult.Role, attempt, engineerRun);

            if (engineerResult.Role != AgentRole.UnityEngineer)
                return await Fail("Engineer executor attempted to act as or self-approve through another role.",
                    attempt, attempt - 1, proposal, review);
            if (engineerResult.Error is not null)
                return await Fail("Unity Engineer execution failed: " + engineerResult.Error,
                    attempt, attempt - 1, proposal, review);
            proposal = engineerResult.Output;
            if (!Valid(proposal, workflowId, attempt))
                return await Fail("Unity Engineer returned malformed output.", attempt, attempt - 1,
                    proposal, review);
            var validProposal = proposal!;

            await TransitionAsync(stage, WorkflowStage.Reviewer,
                "Engineer result sent for independent review.");
            stage = WorkflowStage.Reviewer;
            var assignment = new ReviewAssignment(workflowId, attempt, request.Task,
                request.AcceptanceCriteria, request.Constraints, validProposal, tools.AllowedTools);
            var reviewerRun = Guid.NewGuid().ToString("N");
            await Emit("AgentDispatched", "Reviewer received requirements and the typed Engineer result.",
                AgentRole.Reviewer, attempt, reviewerRun);
            AgentExecutionResult<ReviewerToManager> reviewerResult;
            try
            {
                reviewerResult = await executor.ExecuteReviewerAsync(assignment,
                    new(workflowId, attempt, tools, trace), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Emit("AgentCompleted", "Reviewer execution failed.",
                    AgentRole.Reviewer, attempt, reviewerRun);
                return await Fail("Reviewer execution failed: " + exception.GetType().Name,
                    attempt, attempt, proposal, review);
            }
            await Emit("AgentCompleted", reviewerResult.Error ?? "Reviewer completed.",
                reviewerResult.Role, attempt, reviewerRun);

            if (reviewerResult.Role != AgentRole.Reviewer)
                return await Fail("Reviewer executor returned an unauthorized role.", attempt, attempt,
                    proposal, review);
            if (reviewerResult.Error is not null)
                return await Fail("Reviewer execution failed: " + reviewerResult.Error,
                    attempt, attempt, proposal, review);
            review = reviewerResult.Output;
            if (!Valid(review, workflowId, attempt))
                return await Fail("Reviewer returned malformed output.", attempt, attempt, proposal, review);
            var validReview = review!;

            switch (validReview.Verdict)
            {
                case ReviewVerdict.Approve:
                    return await RunQa(request, attempt, validProposal, validReview, stage, cancellationToken);
                case ReviewVerdict.Inconclusive:
                    await TransitionAsync(stage, WorkflowStage.Completed,
                        "Review was inconclusive; returning to human.");
                    return await Complete(WorkflowStatus.Incomplete, attempt, attempt, validProposal, validReview,
                        "Review was inconclusive; human evaluation is required.");
                case ReviewVerdict.RequestChanges when attempt < MaximumEngineerAttempts:
                    requiredChanges = validReview.RequiredChanges;
                    await Emit("ChangeRequested", "Reviewer requested one bounded Engineer revision.",
                        AgentRole.Reviewer, attempt);
                    break;
                case ReviewVerdict.RequestChanges:
                    await Emit("ChangeRequested", "Reviewer requested changes after the retry limit.",
                        AgentRole.Reviewer, attempt);
                    await Emit("HumanDecisionRequired", "Second REQUEST_CHANGES cannot trigger another retry.",
                        AgentRole.Manager, attempt);
                    await TransitionAsync(stage, WorkflowStage.Completed,
                        "Retry limit reached; human decision required.");
                    return await Complete(WorkflowStatus.NeedsHumanDecision, attempt, attempt, validProposal, validReview,
                        "The bounded revision was not approved; no further automatic retry is allowed.");
            }
        }

        return await Fail("Workflow exhausted its bounded attempts.", MaximumEngineerAttempts,
            MaximumReviewerAttempts, proposal, review);
    }

    public async Task<WorkflowResult> RunApprovedWriteAsync(WorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        await Emit("WorkflowStarted", "Human-approved write orchestration started.");
        if (approvedWrites is null || humanApproval is null)
            return await Fail("Approved-write dependencies are not configured.", 0, 0, null, null);
        try { await approvedWrites.EnsureTargetCleanAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Emit("WriteRejected", $"phase=startup;reason={SafeReason(exception)}",
                AgentRole.Manager);
            return await Fail("Approved-write mode requires a clean target working tree.",
                0, 0, null, null);
        }
        var stage = WorkflowStage.Created;
        EngineerToReviewer? proposal = null;
        ReviewerToManager? review = null;
        IReadOnlyList<string> requiredChanges = [];

        for (var attempt = 1; attempt <= MaximumEngineerAttempts; attempt++)
        {
            await TransitionAsync(stage, WorkflowStage.Engineer, attempt == 1
                ? "Manager dispatched an exact one-file change assignment."
                : "Manager dispatched the single allowed proposal revision.");
            stage = WorkflowStage.Engineer;
            if (attempt > 1) await Emit("RetryConsumed", "Engineer retry budget consumed.",
                AgentRole.Manager, attempt);
            var engineerAssignment = new ManagerToEngineer(workflowId, attempt, request.Task,
                request.AcceptanceCriteria, request.Constraints, request.SuppliedContext,
                tools.AllowedTools, requiredChanges, true);
            var engineerRun = Guid.NewGuid().ToString("N");
            await Emit("AgentDispatched", "Unity Engineer received a typed write proposal assignment.",
                AgentRole.UnityEngineer, attempt, engineerRun);
            AgentExecutionResult<EngineerToReviewer> engineerResult;
            try
            {
                engineerResult = await executor.ExecuteEngineerAsync(engineerAssignment,
                    new(workflowId, attempt, tools, trace), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await Fail("Unity Engineer execution failed: " + exception.GetType().Name,
                    attempt, attempt - 1, proposal, review);
            }
            await Emit("AgentCompleted", engineerResult.Error ?? "Unity Engineer completed.",
                engineerResult.Role, attempt, engineerRun);
            proposal = engineerResult.Output;
            if (engineerResult.Role != AgentRole.UnityEngineer || engineerResult.Error is not null
                || !Valid(proposal, workflowId, attempt))
                return await Fail("Unity Engineer returned an invalid write proposal.", attempt,
                    attempt - 1, proposal, review);
            try { ChangeProposalValidator.Validate(proposal!.Change); }
            catch (ArgumentException)
            {
                return await Fail("Unity Engineer change proposal was malformed or oversized.",
                    attempt, attempt - 1, proposal, review);
            }
            var validProposal = proposal!;
            await Emit("ChangeProposed", $"path={validProposal.Change!.Path};attempt={attempt}",
                AgentRole.UnityEngineer, attempt);

            await TransitionAsync(stage, WorkflowStage.Reviewer,
                "Exact Engineer proposal sent for independent review.");
            stage = WorkflowStage.Reviewer;
            var reviewerAssignment = new ReviewAssignment(workflowId, attempt, request.Task,
                request.AcceptanceCriteria, request.Constraints, validProposal, tools.AllowedTools);
            var reviewerRun = Guid.NewGuid().ToString("N");
            await Emit("AgentDispatched", "Reviewer received the exact proposed content.",
                AgentRole.Reviewer, attempt, reviewerRun);
            AgentExecutionResult<ReviewerToManager> reviewerResult;
            try
            {
                reviewerResult = await executor.ExecuteReviewerAsync(reviewerAssignment,
                    new(workflowId, attempt, tools, trace), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await Fail("Reviewer execution failed: " + exception.GetType().Name,
                    attempt, attempt, proposal, review);
            }
            await Emit("AgentCompleted", reviewerResult.Error ?? "Reviewer completed.",
                reviewerResult.Role, attempt, reviewerRun);
            review = reviewerResult.Output;
            if (reviewerResult.Role != AgentRole.Reviewer || reviewerResult.Error is not null
                || !Valid(review, workflowId, attempt))
                return await Fail("Reviewer returned invalid output.", attempt, attempt, proposal, review);
            var validReview = review!;
            if (validReview.Verdict == ReviewVerdict.RequestChanges && attempt < MaximumEngineerAttempts)
            {
                requiredChanges = validReview.RequiredChanges;
                await Emit("ChangeRequested", "Reviewer requested the one allowed proposal revision.",
                    AgentRole.Reviewer, attempt);
                continue;
            }
            if (validReview.Verdict == ReviewVerdict.RequestChanges)
            {
                await Emit("HumanDecisionRequired", "Proposal retry limit was reached.",
                    AgentRole.Manager, attempt);
                await TransitionAsync(stage, WorkflowStage.Completed, "Retry limit reached.");
                return await Complete(WorkflowStatus.NeedsHumanDecision, attempt, attempt,
                    validProposal, validReview, "The revised proposal was not approved.");
            }
            if (validReview.Verdict == ReviewVerdict.Inconclusive)
            {
                await Emit("HumanDecisionRequired", "Reviewer was inconclusive.", AgentRole.Manager, attempt);
                await TransitionAsync(stage, WorkflowStage.Completed, "Reviewer was inconclusive.");
                return await Complete(WorkflowStatus.NeedsHumanDecision, attempt, attempt,
                    validProposal, validReview, "Reviewer evaluation requires human handling.");
            }

            await Emit("ReviewerApprovedProposal", $"path={validProposal.Change!.Path}",
                AgentRole.Reviewer, attempt);
            await TransitionAsync(stage, WorkflowStage.PreparingChange,
                "Reviewer approval permits trusted preparation only.");
            stage = WorkflowStage.PreparingChange;
            PreparedWrite prepared;
            try { prepared = await approvedWrites.PrepareAsync(validProposal.Change!, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Emit("WriteRejected", $"phase=prepare;reason={SafeReason(exception)}",
                    AgentRole.Manager, attempt);
                return await Fail("Trusted change preparation failed.", attempt, attempt,
                    validProposal, validReview);
            }
            await Emit("WritePrepared", ScopeDetail(prepared), AgentRole.Manager, attempt);
            if (prepared.PreviewTruncated)
            {
                await Emit("WriteRejected", "phase=approval;reason=preview-truncated",
                    AgentRole.Manager, attempt);
                return await Fail("Prepared preview was truncated; unseen content cannot be approved.",
                    attempt, attempt, validProposal, validReview, preparedChange: prepared);
            }
            await TransitionAsync(stage, WorkflowStage.AwaitingHumanApproval,
                "Exact prepared change awaits explicit human approval.");
            stage = WorkflowStage.AwaitingHumanApproval;
            await Emit("HumanApprovalRequested", ScopeDetail(prepared), AgentRole.Manager, attempt);
            ApprovalDecision decision;
            try
            {
                decision = await humanApproval.DecideAsync(
                    new(approvedWrites.TargetRepository, prepared, validReview.Verdict), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Emit("HumanApprovalRejected", "outcome=Cancelled", AgentRole.Manager, attempt);
                throw;
            }
            var expectedScope = new Redpoint.WriteApproval.WriteApprovalScope(prepared.ProposalId,
                prepared.Path, prepared.OriginalSha256, prepared.ProposedSha256, prepared.ProposedLength);
            if (decision.ProposalId != prepared.ProposalId || decision.Scope != expectedScope)
                return await Fail("Human approval decision did not match the prepared proposal.",
                    attempt, attempt, validProposal, validReview, preparedChange: prepared,
                    humanDecision: decision);
            if (decision.Outcome != ApprovalOutcome.Approved)
            {
                await Emit("HumanApprovalRejected", $"outcome={decision.Outcome}",
                    AgentRole.Manager, attempt);
                await TransitionAsync(stage, WorkflowStage.Completed, "Human did not approve the write.");
                return await Complete(WorkflowStatus.Incomplete, attempt, attempt, validProposal,
                    validReview, $"Human {decision.Outcome}; no write occurred.",
                    preparedChange: prepared, humanDecision: decision);
            }
            await Emit("HumanApprovalGranted",
                $"proposalId={prepared.ProposalId};actor={decision.Actor ?? "unavailable"}",
                AgentRole.Manager, attempt);
            await TransitionAsync(stage, WorkflowStage.ApplyingChange,
                "Trusted issuer may authorize the exact prepared scope.");
            stage = WorkflowStage.ApplyingChange;
            await Emit("WriteApplyStarted", ScopeDetail(prepared), AgentRole.Manager, attempt);
            AppliedWrite applied;
            try { applied = await approvedWrites.ApplyAsync(prepared, decision, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await Emit("WriteRejected", $"phase=apply;reason={SafeReason(exception)}",
                    AgentRole.Manager, attempt);
                return await Fail("Approved write execution failed; inspect the target before retrying.",
                    attempt, attempt, validProposal, validReview, preparedChange: prepared,
                    humanDecision: decision);
            }
            await Emit("WriteApplied",
                $"proposalId={applied.ProposalId};path={applied.Path};resultingSha256={applied.ResultingSha256};approvalId={applied.ApprovalId}",
                AgentRole.Manager, attempt);
            return await RunQa(request, attempt, validProposal, validReview, stage,
                cancellationToken, prepared, applied, decision);
        }
        return await Fail("Workflow exhausted its bounded attempts.", MaximumEngineerAttempts,
            MaximumReviewerAttempts, proposal, review);
    }

    private async Task<WorkflowResult> RunQa(WorkflowRequest request, int attempt,
        EngineerToReviewer proposal, ReviewerToManager review, WorkflowStage stage,
        CancellationToken cancellationToken, PreparedWrite? prepared = null,
        AppliedWrite? applied = null, ApprovalDecision? humanDecision = null)
    {
        await TransitionAsync(stage, WorkflowStage.QA, "Reviewer approved; independent QA validation dispatched.");
        var assignment = new QaAssignment(workflowId, attempt, request.Task, request.AcceptanceCriteria,
            request.Constraints, proposal, review, tools.AllowedToolsFor(AgentRole.QA), prepared, applied);
        var runId = Guid.NewGuid().ToString("N");
        await Emit("AgentDispatched", "QA received a typed validation assignment.",
            AgentRole.QA, attempt, runId);
        await Emit("QADispatched", applied is null
                ? "QA received the analysis-only baseline assignment."
                : "QA received authoritative applied-change metadata.",
            AgentRole.QA, attempt, runId);
        AgentExecutionResult<QaToManager> qaResult;
        try
        {
            qaResult = await executor.ExecuteQaAsync(assignment,
                new(workflowId, attempt, tools, trace), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Emit("AgentCompleted", "QA execution failed.", AgentRole.QA, attempt, runId);
            return await Fail("QA execution failed: " + exception.GetType().Name,
                attempt, attempt, proposal, review, null, 1);
        }
        await Emit("AgentCompleted", qaResult.Error ?? "QA completed.", qaResult.Role, attempt, runId);
        if (qaResult.Role != AgentRole.QA)
            return await Fail("QA executor returned an unauthorized role.", attempt, attempt,
                proposal, review, qaResult.Output, 1);
        if (qaResult.Error is not null)
            return await Fail("QA execution failed: " + qaResult.Error, attempt, attempt,
                proposal, review, qaResult.Output, 1);
        var qa = qaResult.Output;
        if (!Valid(qa, workflowId, attempt))
            return await Fail("QA returned malformed output.", attempt, attempt, proposal, review, qa, 1);
        var validQa = qa!;
        await Emit("QACompleted", validQa.Outcome.ToString(), AgentRole.QA, attempt);
        await Emit("QaOutcome", validQa.Outcome.ToString(), AgentRole.QA, attempt);
        switch (validQa.Outcome)
        {
            case QaOutcome.Pass:
                await TransitionAsync(WorkflowStage.QA, WorkflowStage.Completed,
                    "QA passed available baseline validation.");
                return await Complete(WorkflowStatus.Completed, attempt, attempt, proposal, review,
                    applied is null ? "Reviewer approved and QA passed available baseline validation."
                        : "Human-approved write was applied and QA passed post-write validation.",
                    1, validQa, prepared, applied, humanDecision);
            case QaOutcome.Fail:
            case QaOutcome.Inconclusive:
                await Emit("HumanDecisionRequired",
                    $"QA returned {validQa.Outcome}; no automatic repair loop is allowed.",
                    AgentRole.Manager, attempt);
                await TransitionAsync(WorkflowStage.QA, WorkflowStage.Completed,
                    "QA requires human evaluation.");
                return await Complete(WorkflowStatus.NeedsHumanDecision, attempt, attempt, proposal, review,
                    $"QA returned {validQa.Outcome}; human evaluation is required.", 1, validQa,
                    prepared, applied, humanDecision);
            default:
                return await Fail("QA returned an unsupported outcome.", attempt, attempt,
                    proposal, review, validQa, 1);
        }
    }

    private async ValueTask TransitionAsync(WorkflowStage current, WorkflowStage next, string reason)
    {
        var allowed = (current, next) switch
        {
            (WorkflowStage.Created, WorkflowStage.Engineer) => true,
            (WorkflowStage.Engineer, WorkflowStage.Reviewer) => true,
            (WorkflowStage.Reviewer, WorkflowStage.Engineer) => true,
            (WorkflowStage.Reviewer, WorkflowStage.Completed) => true,
            (WorkflowStage.Reviewer, WorkflowStage.QA) => true,
            (WorkflowStage.Reviewer, WorkflowStage.PreparingChange) => true,
            (WorkflowStage.PreparingChange, WorkflowStage.AwaitingHumanApproval) => true,
            (WorkflowStage.AwaitingHumanApproval, WorkflowStage.ApplyingChange) => true,
            (WorkflowStage.AwaitingHumanApproval, WorkflowStage.Completed) => true,
            (WorkflowStage.ApplyingChange, WorkflowStage.QA) => true,
            (WorkflowStage.QA, WorkflowStage.Completed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Invalid workflow transition: {current} -> {next}.");
        await trace.EmitAsync(WorkflowTraceEvent.Create(workflowId, "Transition",
            $"{current} -> {next}: {reason}"));
    }

    private async Task<WorkflowResult> Complete(WorkflowStatus status, int engineers, int reviewers,
        EngineerToReviewer proposal, ReviewerToManager review, string summary, int qaAttempts = 0,
        QaToManager? qa = null, PreparedWrite? preparedChange = null,
        AppliedWrite? appliedChange = null, ApprovalDecision? humanDecision = null)
    {
        await Emit("WorkflowCompleted", $"{status}: {summary}");
        return new(workflowId, status, engineers, reviewers, qaAttempts, proposal, review, qa, summary,
            preparedChange, appliedChange, humanDecision);
    }

    private async Task<WorkflowResult> Fail(string summary, int engineers, int reviewers,
        EngineerToReviewer? proposal, ReviewerToManager? review, QaToManager? qa = null,
        int qaAttempts = 0, PreparedWrite? preparedChange = null,
        AppliedWrite? appliedChange = null, ApprovalDecision? humanDecision = null)
    {
        await Emit("WorkflowCompleted", $"Failed: {summary}");
        return new(workflowId, WorkflowStatus.Failed, engineers, reviewers, qaAttempts,
            proposal, review, qa, summary, preparedChange, appliedChange, humanDecision);
    }

    private ValueTask Emit(string type, string detail, AgentRole? role = null, int? attempt = null,
        string? runId = null) => trace.EmitAsync(WorkflowTraceEvent.Create(workflowId, type, detail,
            role, attempt, runId));

    private static bool Valid(EngineerToReviewer? value, string id, int attempt) =>
        value is not null && value.WorkflowId == id && value.Attempt == attempt
        && !string.IsNullOrWhiteSpace(value.Interpretation)
        && !string.IsNullOrWhiteSpace(value.ProposedSolution);

    private static bool Valid(ReviewerToManager? value, string id, int attempt) =>
        value is not null && value.WorkflowId == id && value.Attempt == attempt
        && (value.Verdict != ReviewVerdict.RequestChanges || value.RequiredChanges.Count > 0);

    private static bool Valid(QaToManager? value, string id, int attempt) =>
        value is not null && value.WorkflowId == id && value.Attempt == attempt
        && QaResultValidator.IsValid(value);

    private static string ScopeDetail(PreparedWrite value) =>
        $"proposalId={value.ProposalId};path={value.Path};originalSha256={value.OriginalSha256};proposedSha256={value.ProposedSha256}";

    private static string SafeReason(Exception exception) => exception is TrustedWriteException trusted
        ? trusted.Code : exception.GetType().Name;
}
