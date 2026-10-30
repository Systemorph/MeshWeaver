using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// A rule judged at the OWNER's COMMIT point: against the entity exactly as it is about to be
/// committed (after the merge) and the entity it replaces, inside the same turn that applies it.
///
/// <para><b>Why the commit point and nowhere earlier.</b> A cross-hub write reaches an owner as a
/// merge patch or as a data change, and between its delivery and its commit the owner can merge
/// other queued writes. A delivery-time check sees a different predecessor than the merge does.
/// It also has to re-implement the owner's merge (string splices, stale-base three-way merges)
/// to know what the write produces. Judged here, the rule sees the real result against the real
/// predecessor, for every route that commits into the owner: a merge patch (every
/// <c>GetMeshNodeStream(path).Update</c>, <c>IMeshService.UpdateNode</c> and the upsert's update
/// branch), a <c>DataChangeRequest</c> (a synced write-back, an entity-reference patch, or a
/// node carried to a hub that is not its own), and an OWN write — <c>GetMeshNodeStream().Update</c>
/// issued on the owning hub itself (a rollback, an activity script). Every route asks the guards
/// through <see cref="EntityCommitGuards.FirstRefusal"/>.</para>
///
/// <para><b>Contract.</b> Synchronous and pure: it runs on the commit turn, so it must not read,
/// wait or resolve anything. Return null to let the entity commit, or the refusal (already in the
/// writer's language) to commit nothing for it. Lives in <c>MeshWeaver.Data.Contract</c> for the
/// reason <see cref="IActiveDeletionScopes"/> does: the committing layer (<c>MeshWeaver.Data</c>)
/// and the rules' owners both reference it.</para>
/// </summary>
public interface IEntityCommitGuard
{
    /// <summary>
    /// The refusal for committing <paramref name="after"/> in place of <paramref name="before"/>, or
    /// null when it may commit.
    /// </summary>
    /// <param name="collection">The collection being committed to (e.g. <c>MeshNode</c>).</param>
    /// <param name="before">The entity the owner holds now, or null when it holds none under this key.</param>
    /// <param name="after">The entity as it is about to commit.</param>
    /// <param name="ownerPath">The committing hub's own path.</param>
    /// <param name="writer">The writer's identity, which also carries the refusal's locale.</param>
    /// <param name="options">The owner's serializer options.</param>
    /// <returns>The refusal, or null.</returns>
    string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options);
}
