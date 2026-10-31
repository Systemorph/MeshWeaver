using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The ONE place the <see cref="IEntityCommitGuard"/>s are asked. Every commit turn that writes an
/// entity into an owner's store calls it, whatever route produced the write: the in-turn merge patch,
/// the data-change apply, and the owner's OWN write (<c>GetMeshNodeStream().Update</c> on the owning
/// hub). A route that commits without asking it bypasses every rule judged at the commit point.
/// </summary>
public static class EntityCommitGuards
{
    /// <summary>
    /// The first refusal for committing <paramref name="after"/> in place of <paramref name="before"/>,
    /// or null when every guard lets it commit. Synchronous: it runs on the commit turn.
    /// </summary>
    /// <param name="guards">The guards registered for the committing hub.</param>
    /// <param name="collection">The collection being committed to (e.g. <c>MeshNode</c>).</param>
    /// <param name="before">The entity the owner holds now, or null when it holds none under this key.</param>
    /// <param name="after">The entity as it is about to commit.</param>
    /// <param name="ownerPath">The committing hub's own path.</param>
    /// <param name="writer">The writer's identity, which also carries the refusal's locale.</param>
    /// <param name="options">The owner's serializer options.</param>
    /// <returns>The first refusal, or null.</returns>
    public static string? FirstRefusal(IReadOnlyList<IEntityCommitGuard> guards, string collection,
        object? before, object after, string ownerPath, AccessContext? writer, JsonSerializerOptions options)
    {
        foreach (var guard in guards)
            if (guard.Refusal(collection, before, after, ownerPath, writer, options) is { } refusal)
                return refusal;
        return null;
    }
}
