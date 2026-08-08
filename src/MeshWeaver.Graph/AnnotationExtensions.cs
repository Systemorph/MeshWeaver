namespace MeshWeaver.Graph;

/// <summary>
/// The retired tracked-change satellite sub-partition.
/// <para>
/// 🚨 <b>Nothing writes <c>_Tracking</c> satellites any more.</b> Tracked changes are a VIEW MODEL
/// computed from the version history (<see cref="ChangeProjection"/>): the history already records
/// author, timestamp and full before/after content for every change, so persisting a second copy
/// only added failure modes (anchors going stale as the document moves, orphaned satellite state, a
/// reconcile surface, and two sources of truth for "what changed"). A suggested edit is now applied
/// as a normal versioned write, and "reject" is a revert — which itself lands in the history.
/// </para>
/// <para>
/// The constant survives for the deprecation window: rows written by older builds stay READABLE
/// (the <c>_Tracking → annotations</c> table mapping, the <c>TrackedChange</c> node type and its
/// satellite access rule are all still registered), and the central Collaboration plugin keeps a
/// legacy reader that accepts / rejects them. Once no deployment carries such rows, this constant
/// and those registrations go together.
/// </para>
/// Comments are NOT affected — they are genuinely additional data and remain satellites in
/// <c>_Comment</c> (see <see cref="CommentsExtensions"/>).
/// </summary>
public static class AnnotationExtensions
{
    /// <summary>
    /// The sub-partition legacy tracked-change satellites were stored in. Read-only — see the class
    /// remarks.
    /// </summary>
    public const string TrackingPartition = "_Tracking";
}
