using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Resolves the OWNER <see cref="AccessContext"/> of a synchronization stream's content from the
/// stream's CURRENT value — the MeshNode-aware bridge that lets
/// <see cref="SynchronizationStream{TStream}.Update(System.Func{TStream,MeshWeaver.Data.ChangeItem{TStream}},System.Action{System.Exception})"/> inject the node OWNER as a last-resort identity
/// when neither a live AsyncLocal context nor a captured creation context survives.
///
/// <para>This closes the cold-start FIRST-write race: a freshly-activated owner hub establishes its
/// standing identity ASYNCHRONOUSLY (the per-node owner identity / <c>SetThreadHubIdentity</c> resolve
/// <c>CreatedBy</c> via a round-trip), so the very first cross-hub write can reach the data-source sync
/// stream before that lands — with no live context and a null creation context. The node is ALREADY in
/// the stream's <c>Current</c> at write time, so its <c>CreatedBy</c> is available WITHOUT any async
/// round-trip and WITHOUT a race.</para>
///
/// <para>Implemented in the graph/hosting layer (which knows <c>MeshNode</c>); <see cref="MeshWeaver.Data"/>
/// sits below Mesh.Contract in the project graph and cannot read <c>MeshNode</c> itself. The result is
/// still filtered through the real-user invariant by the caller, so a hub/system principal can never
/// leak into <c>CreatedBy</c>.</para>
/// </summary>
public interface IStreamOwnerResolver
{
    /// <summary>
    /// Returns the owner identity carried by <paramref name="currentValue"/> for the node at
    /// <paramref name="nodePath"/>, or <c>null</c> when none can be determined (not a node stream, the
    /// node isn't present, or it has no attributed owner).
    /// </summary>
    AccessContext? ResolveOwner(object? currentValue, Address nodePath);
}
