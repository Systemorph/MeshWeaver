using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Social;

/// <summary>
/// Abstraction over a social platform's "publish / read-back / fetch-history" surface.
/// One implementation per platform (LinkedIn, X, Instagram). Concrete implementations
/// are registered via <c>TryAddEnumerable&lt;IPlatformPublisher&gt;</c> and resolved
/// by the schedulers via <see cref="Platform"/> string match.
/// </summary>
public interface IPlatformPublisher
{
    /// <summary>
    /// Platform identifier matching the <c>Platform</c> field on a post
    /// (e.g. "LinkedIn", "Twitter", "Instagram").
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Posts <paramref name="request"/> to the platform and returns the platform's
    /// identifiers for the created item. The caller persists <see cref="PublishResult.Urn"/>
    /// back onto the mesh node for later stat lookups.
    /// </summary>
    Task<PublishResult> PublishAsync(PlatformPublishRequest request, CancellationToken ct);

    /// <summary>
    /// Fetches engagement stats for a previously-published post identified by platform URN.
    /// Called periodically by the stats refresher.
    /// </summary>
    Task<PostStats> GetStatsAsync(string urn, PlatformCredential credential, CancellationToken ct);

    /// <summary>
    /// Streams past posts authored by the profile identified by <paramref name="credential"/>,
    /// newest first. Used by the history-ingest job to backfill Post nodes for content
    /// that was published before the mesh started tracking it. Implementations should
    /// respect <paramref name="sinceInclusive"/> (skip items older than this) and
    /// <paramref name="maxItems"/> (stop after N items) so a long history can be
    /// paged without exhausting quotas.
    /// </summary>
    IAsyncEnumerable<PastPost> ListPastPostsAsync(
        PlatformCredential credential,
        System.DateTimeOffset? sinceInclusive,
        int maxItems,
        CancellationToken ct);

    /// <summary>
    /// Streams comments on a single previously-published post. Used by the engagement
    /// pull endpoint to materialize per-commenter satellites under the post node.
    /// Implementations that don't support per-comment author lookup should yield
    /// nothing (not throw) so the caller can simply move on.
    /// </summary>
    IAsyncEnumerable<EngagementComment> ListCommentsAsync(
        string urn,
        PlatformCredential credential,
        int maxItems,
        CancellationToken ct);

    /// <summary>
    /// Streams likes/reactions on a single previously-published post. Same contract
    /// as <see cref="ListCommentsAsync"/>: yield-nothing when the platform doesn't
    /// expose per-actor identity, instead of throwing.
    /// </summary>
    IAsyncEnumerable<EngagementLike> ListLikesAsync(
        string urn,
        PlatformCredential credential,
        int maxItems,
        CancellationToken ct);
}

/// <summary>
/// Inputs for a single publish call. The post node's media (if any) is already
/// resolved to an absolute URL by the scheduler so the publisher doesn't need to
/// know about content collections.
/// </summary>
public sealed record PlatformPublishRequest(
    string PostPath,
    string AuthorHandle,
    string Text,
    System.Collections.Generic.IReadOnlyList<string> MediaUrls,
    PlatformCredential Credential);

/// <summary>
/// Outcome of a successful publish call. Failures throw or surface via
/// <see cref="Error"/> with <see cref="Urn"/> null — the scheduler retries with
/// exponential backoff and marks the post rejected after N attempts.
/// </summary>
public sealed record PublishResult(
    string? Urn,
    string? PostUrl,
    System.DateTimeOffset PublishedAt,
    string? Error = null);

/// <summary>
/// Engagement stats at a point in time. Platforms that don't expose a particular
/// metric return 0 for that field rather than throwing.
/// </summary>
public sealed record PostStats(
    int Impressions,
    int Likes,
    int Comments,
    int Shares,
    System.DateTimeOffset RetrievedAt);

/// <summary>
/// A historic post as returned by <see cref="IPlatformPublisher.ListPastPostsAsync"/>.
/// Maps 1:1 onto a future mesh <c>SocialMediaPost</c> node when the history job ingests it.
/// </summary>
public sealed record PastPost(
    string Urn,
    string? PostUrl,
    string Text,
    System.Collections.Generic.IReadOnlyList<string> MediaUrls,
    System.DateTimeOffset PublishedAt,
    PostStats? Stats);

/// <summary>
/// A single comment on a post. <see cref="ActorName"/> and <see cref="ActorProfileUrl"/>
/// are best-effort: many platforms only return the actor URN for non-connections.
/// </summary>
public sealed record EngagementComment(
    string Urn,
    string ActorUrn,
    string? ActorName,
    string? ActorProfileUrl,
    string Text,
    System.DateTimeOffset CreatedAt);

/// <summary>
/// A single reaction/like on a post. <see cref="ReactionType"/> defaults to "LIKE"
/// for platforms without typed reactions.
/// </summary>
public sealed record EngagementLike(
    string Urn,
    string ActorUrn,
    string? ActorName,
    string? ActorProfileUrl,
    System.DateTimeOffset CreatedAt,
    string? ReactionType);
