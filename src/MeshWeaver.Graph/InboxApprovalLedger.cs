using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The rows of the inbox's approvals ledger — the approvals the viewer REQUESTED that still wait,
/// and the ones recently DECIDED. The record is core's <see cref="Approval"/>; the row keeps the
/// approval's own title and time (a stored instant, shown in the viewer's zone by
/// <see cref="InboxRows.Of(MeshNode, InboxSection, string?, string?, string?, int, string?, System.Globalization.CultureInfo?, System.Text.Json.JsonSerializerOptions?)"/>)
/// and says WHO the other party is and the outcome in the From cell.
/// </summary>
internal static class InboxApprovalLedger
{
    /// <summary>The sentences a ledger row says, resolved once on the render turn; <c>{0}</c> is the person.</summary>
    internal sealed record Texts(string RequestedFrom, string ApprovedBy, string RejectedBy)
    {
        /// <summary>The texts.</summary>
        internal static Texts Of(string requestedFrom, string approvedBy, string rejectedBy)
            => new(requestedFrom, approvedBy, rejectedBy);
    }

    /// <summary>
    /// <paramref name="row"/> with the From cell saying what the approval is about for the viewer:
    /// "Requested from X" while pending, "Approved by X" / "Rejected by X" once decided. Any other
    /// row, and a node whose content is not an approval, is returned as it is.
    /// </summary>
    /// <param name="row">The row <see cref="InboxRows"/> built.</param>
    /// <param name="node">The node it was built from.</param>
    /// <param name="kind">The ledger group of the leg that brought it, or null.</param>
    /// <param name="texts">The sentences.</param>
    /// <param name="options">The hub's serializer options.</param>
    internal static InboxRow Decorate(InboxRow row, MeshNode node, ApprovalLedgerKind? kind, Texts texts, System.Text.Json.JsonSerializerOptions options)
    {
        if (kind is null || node.ContentAs<Approval>(options) is not { } approval)
            return row;
        return row with
        {
            From = kind == ApprovalLedgerKind.Requested
                ? Say(texts.RequestedFrom, approval.Approver)
                : Say(approval.Status == ApprovalStatus.Rejected ? texts.RejectedBy : texts.ApprovedBy, approval.Approver),
        };
    }

    private static string Say(string text, string person) => text.Replace("{0}", person, StringComparison.Ordinal);
}
