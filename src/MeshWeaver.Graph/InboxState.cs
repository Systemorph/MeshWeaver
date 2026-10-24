using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The lifecycle state an inbox row shows beside its title — read from the underlying node (its
/// content's <c>status</c>, or what the leg that found it implies), never copied anywhere. A check
/// mark is reserved for DONE states (<see cref="Approved"/>, <see cref="Done"/>): a request still
/// waiting for a decision must never look finished.
/// </summary>
public static class InboxState
{
    /// <summary>An approval waiting for the viewer's decision.</summary>
    public const string Awaiting = "awaiting";
    /// <summary>An approval that was approved.</summary>
    public const string Approved = "approved";
    /// <summary>An approval that was rejected.</summary>
    public const string Rejected = "rejected";
    /// <summary>An approval that lapsed.</summary>
    public const string Expired = "expired";
    /// <summary>An activity that was requested and has not started.</summary>
    public const string Requested = "requested";
    /// <summary>An activity that is running.</summary>
    public const string Running = "running";
    /// <summary>An activity that finished.</summary>
    public const string Done = "done";
    /// <summary>An activity that failed.</summary>
    public const string Failed = "failed";
    /// <summary>An activity that was cancelled.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>A mail, message or notification not yet read.</summary>
    public const string Unread = "unread";
    /// <summary>An approval the system refused to carry out (a target that is <c>Refused</c>).</summary>
    public const string Refused = "refused";
    /// <summary>An approval that was retired because what it was planned against moved on, or that a newer request replaced.</summary>
    public const string Stale = "stale";

    /// <summary>Every state, in the order a filter would list them.</summary>
    public static ImmutableArray<string> All { get; } =
        [Awaiting, Approved, Rejected, Expired, Requested, Running, Done, Failed, Cancelled, Unread, Refused, Stale];

    /// <summary>The catalog key of a state's label.</summary>
    /// <param name="state">A state, or empty.</param>
    public static string LabelKey(string state) => "inbox.state." + state;

    /// <summary>
    /// The state of a row. A stored <paramref name="contentStatus"/> the vocabulary knows wins; otherwise the
    /// leg decides: an approval in the obligation band or the "asked by me" ledger group is awaiting a decision,
    /// an activity in the running band runs and one in the obligation band failed, and an unread item is unread.
    /// Anything else has no state (empty). Pure.
    /// </summary>
    /// <param name="sourceId">The row's source id.</param>
    /// <param name="band">The band id of its leg.</param>
    /// <param name="requestedLedger">Whether the leg is the approvals ledger's "asked by the viewer, still pending" group.</param>
    /// <param name="unread">Whether the item is unread.</param>
    /// <param name="contentStatus">The node content's <c>status</c>, or null when the projection dropped it.</param>
    public static string Of(string sourceId, string band, bool requestedLedger, bool unread, string? contentStatus)
    {
        if (FromStatus(contentStatus) is { Length: > 0 } stored)
            return stored;
        if (string.Equals(sourceId, InboxQueries.ApprovalsSource, StringComparison.Ordinal))
            return requestedLedger || string.Equals(band, InboxBand.NeedsYou, StringComparison.Ordinal) ? Awaiting : string.Empty;
        if (string.Equals(sourceId, "Activities", StringComparison.Ordinal))
            return string.Equals(band, InboxBand.Running, StringComparison.Ordinal) ? Running
                : string.Equals(band, InboxBand.NeedsYou, StringComparison.Ordinal) ? Failed
                : string.Empty;
        return unread ? Unread : string.Empty;
    }

    /// <summary>The state a stored status word names, or empty when the word is not part of the vocabulary.</summary>
    /// <param name="status">A stored status.</param>
    public static string FromStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "pending" or "awaiting" or "awaitingdecision" => Awaiting,
            "approved" => Approved,
            "rejected" or "declined" => Rejected,
            "refused" => Refused,
            "stale" or "superseded" => Stale,
            "expired" => Expired,
            "requested" or "queued" => Requested,
            "running" or "inprogress" => Running,
            "succeeded" or "warning" or "done" or "completed" => Done,
            "failed" or "error" => Failed,
            "cancelled" or "canceled" => Cancelled,
            _ => string.Empty,
        };

    /// <summary>The badge colour of a state: amber waiting, blue under way, green done, red refused or failed, grey lapsed.</summary>
    /// <param name="state">A state, or empty.</param>
    public static string Color(string state) => state switch
    {
        Awaiting => "#9D5D00",
        Approved or Done => "#107C10",
        Rejected or Failed or Refused => "#C42B1C",
        Expired or Cancelled or Stale => "#6E6E6E",
        Requested or Running or Unread => "#0F6CBD",
        _ => string.Empty,
    };

    /// <summary>The mark before a state's label: a check only for what is done, a cross for what was refused or failed.</summary>
    /// <param name="state">A state, or empty.</param>
    public static string Mark(string state) => state switch
    {
        Approved or Done => "✓ ",
        Rejected or Failed or Refused => "✕ ",
        _ => string.Empty,
    };
}
