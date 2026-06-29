using MeshWeaver.Mesh;

namespace MeshWeaver.Social;

/// <summary>
/// Glue between <see cref="MeshWeaver.Mesh.Approval"/> nodes and the publishing pipeline.
/// Implementations inspect a just-approved <c>Approval</c> node, resolve the target
/// post (via <c>PrimaryNodePath</c>), look up the post's platform + credential, and
/// either publish immediately or enqueue for the scheduler.
///
/// <para>
/// 100% reactive — every method returns <see cref="IObservable{T}"/>. Compose with
/// <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. NEVER bridge to <c>Task</c>
/// (that's a 100% deadlock surface; see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>).
/// </para>
/// </summary>
public interface IApprovalPublishBridge
{
    /// <summary>
    /// Resolves the publishable snapshot for an approved <paramref name="approval"/>, or
    /// emits <c>null</c> if the target node isn't a publishable post (different node type,
    /// already published, missing credentials, etc.).
    /// </summary>
    IObservable<PublishableSnapshot?> Resolve(Approval approval);

    /// <summary>
    /// Persists a successful publish result back onto the target post node (sets
    /// <c>PlatformUrn</c>, <c>PlatformUrl</c>, <c>PublishedAt</c>).
    /// </summary>
    IObservable<System.Reactive.Unit> ApplyPublish(string postPath, PublishResult result);

    /// <summary>
    /// Patches engagement stats onto a post node after a stats refresh.
    /// </summary>
    IObservable<System.Reactive.Unit> ApplyStats(string postPath, PostStats stats);
}

/// <summary>
/// Everything the scheduler needs to call <see cref="IPlatformPublisher.PublishAsync"/>
/// without understanding any specific post model. The bridge materializes this from
/// the mesh once an approval flips green.
/// </summary>
public sealed record PublishableSnapshot(
    string PostPath,
    string Platform,
    string AuthorHandle,
    string Text,
    System.Collections.Generic.IReadOnlyList<string> MediaUrls,
    PlatformCredential Credential,
    System.DateTimeOffset ScheduledAt);
