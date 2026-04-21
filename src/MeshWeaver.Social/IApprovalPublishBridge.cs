using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;

namespace MeshWeaver.Social;

/// <summary>
/// Glue between <see cref="MeshWeaver.Mesh.Approval"/> nodes and the publishing pipeline.
/// Implementations inspect a just-approved <c>Approval</c> node, resolve the target
/// post (via <c>PrimaryNodePath</c>), look up the post's platform + credential, and
/// either publish immediately or enqueue for the scheduler.
///
/// The Social project deliberately does NOT hardcode the post nodeType or content shape;
/// the hosting app supplies a <see cref="IApprovalPublishBridge"/> that maps an
/// approval's <c>PrimaryNodePath</c> → a <see cref="PublishableSnapshot"/> describing
/// what to publish. This keeps Social reusable across apps with different post models.
/// </summary>
public interface IApprovalPublishBridge
{
    /// <summary>
    /// Resolves the publishable snapshot for an approved <paramref name="approval"/>, or
    /// <c>null</c> if the target node isn't a publishable post (different node type,
    /// already published, missing credentials, etc.). Called both from the approval
    /// event handler and the scheduler.
    /// </summary>
    Task<PublishableSnapshot?> ResolveAsync(Approval approval, CancellationToken ct);

    /// <summary>
    /// Persists a successful publish result back onto the target post node (sets
    /// <c>PlatformUrn</c>, <c>PlatformUrl</c>, <c>PublishedAt</c>). Called by the
    /// scheduler after <see cref="IPlatformPublisher.PublishAsync"/> returns.
    /// </summary>
    Task ApplyPublishAsync(string postPath, PublishResult result, CancellationToken ct);

    /// <summary>
    /// Patches engagement stats onto a post node after a stats refresh.
    /// </summary>
    Task ApplyStatsAsync(string postPath, PostStats stats, CancellationToken ct);
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
