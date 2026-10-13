using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The decisions an approver can give. An open vocabulary of string constants: a request names one,
/// and an approval type that does not know a value refuses it.
/// </summary>
public static class ApprovalDecisions
{
    /// <summary>Approve.</summary>
    public const string Approve = "Approve";

    /// <summary>Reject.</summary>
    public const string Reject = "Reject";
}

/// <summary>
/// "Decide this approval" — sent to the hub of the node an approval request points at (an approval,
/// an instance action, …), as the APPROVER. The node's own type decides what deciding means and
/// authorizes it on the request's author, exactly as its own Approve button does; the personal
/// inbox's Approve all / Reject all send one per ticked approval request and know nothing about the
/// kinds behind them.
///
/// <para>A type that does not handle it simply does not answer, and the sender reports the item as
/// not decided. A refusal is an answer (<see cref="DecideApprovalResponse.Done"/> false with a
/// reason), never a fault.</para>
/// </summary>
/// <param name="Decision">An <see cref="ApprovalDecisions"/> constant.</param>
public sealed record DecideApprovalRequest(string Decision) : IRequest<DecideApprovalResponse>
{
    /// <summary>
    /// The step-up receipt that confirms this decision, when the instance requires one per decision
    /// (core <c>Doc/Architecture/ApprovalStepUp</c>); null when none is given. A type that needs one
    /// and gets none answers <see cref="DecideApprovalResponse.StepUpRequired"/>.
    /// </summary>
    public string? StepUpReceipt { get; init; }
}

/// <summary>What became of one <see cref="DecideApprovalRequest"/>.</summary>
/// <param name="Done">True when the decision was recorded.</param>
/// <param name="Reason">
/// Why not, when <paramref name="Done"/> is false — shown to the approver verbatim, so a refusal the
/// type chose to state (already decided, not yours to decide), never an exception's message.
/// </param>
public sealed record DecideApprovalResponse(bool Done, string? Reason = null)
{
    /// <summary>
    /// True when the decision is recorded but held until a step-up confirmation arrives — it is not
    /// in effect yet. <see cref="StepUpBinding"/> names what the confirmation must cover.
    /// </summary>
    public bool StepUpRequired { get; init; }

    /// <summary>The value a step-up confirmation for this decision must be bound to, when one is required.</summary>
    public string? StepUpBinding { get; init; }
}
