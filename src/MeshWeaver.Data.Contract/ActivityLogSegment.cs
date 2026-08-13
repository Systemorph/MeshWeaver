using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

/// <summary>
/// A sealed slice of an activity's transcript, stored as a satellite MeshNode at
/// <c>{activityPath}/_Log/{index:D6}</c> once the messages have scrolled out of
/// <see cref="ActivityLog.Messages"/> (the head's bounded window).
///
/// <para><b>Why the transcript moves off the head.</b> Every <c>stream.Update</c> re-serialises the
/// WHOLE <c>MeshNode.Content</c> to compute its patch, so appending N lines to one growing list costs
/// O(N²) — on memex-cloud a single import activity spent ~719 MB of serialisation across 5,239 writes
/// to a 141 kB node. No delta field fixes that: the cross-hub path ships an RFC 7396 merge patch, which
/// clones a changed array whole, and the three-way merge's base extraction clones the previous array
/// too. Bounding the head is what changes the asymptotics — with the window fixed, each write is O(1)
/// and the whole activity is O(N).</para>
///
/// <para><b>Segments are append-once and immutable.</b> A segment is written exactly once, by the
/// append that sealed it, and never updated afterwards — so nothing ever reads-then-writes a segment
/// path, and no reader may point-read one that might be absent (that opens the shared stream cache's
/// storm breaker on a path a concurrent write is about to use). Enumerate them with a children query
/// on <c>{activityPath}/_Log</c>; the ordering key is <see cref="FirstOrdinal"/>.</para>
/// </summary>
public record ActivityLogSegment
{
    /// <summary>Unique identifier of this segment — mirrors the node id.</summary>
    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();

    /// <summary>
    /// The activity-wide ordinal of this segment's FIRST message: how many messages the activity had
    /// already recorded before this slice. Segments are ordered by it, and it is what lets a reader
    /// stitch segments and the head window back into the original sequence.
    /// </summary>
    public int FirstOrdinal { get; init; }

    /// <summary>The messages in this slice, in order.</summary>
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;

    /// <summary>The path of the activity this slice belongs to.</summary>
    public string? ActivityPath { get; init; }
}
