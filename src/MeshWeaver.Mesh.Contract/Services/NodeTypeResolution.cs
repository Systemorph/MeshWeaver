using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>THE one rule for "does this NodeType resolve?"</b> — a node at the type's PATH, found
/// either as a static node (<see cref="StaticNodeProviderExtensions.FindStaticNode"/>) or in
/// persistence (<see cref="IStorageAdapter.Exists"/>). Not a <c>TypeRegistry</c> fact, not a
/// compiled assembly: see
/// <c>Doc/Architecture/ImportWriteOrdering</c> for why those two
/// are deliberately not conflated.
///
/// <para><b>Why it is a shared helper rather than three copies.</b> The create path has applied
/// this rule since forever; the UPDATE paths did not apply it at all, which made <c>update</c> a
/// supported route to give a node a <c>NodeType</c> that resolves to nothing (issue #2993). Closing
/// that with a second, independently-written copy of the predicate is how the two drift: a create
/// that refuses and an update that accepts are indistinguishable from a create that accepts, and
/// the difference only shows up as an instance nobody can read. One implementation, every
/// boundary.</para>
///
/// <para>The full decision — what each write verb does, why the importer has a named bypass, and
/// what happens when a type is pruned out from under its instances — is
/// <c>Doc/Architecture/DanglingNodeTypes</c>.</para>
/// </summary>
public static class NodeTypeResolution
{
    /// <summary>
    /// True when <paramref name="incomingNodeType"/> would give the node a DIFFERENT
    /// <see cref="MeshNode.NodeType"/> than it currently has — the only case a write boundary has
    /// to judge.
    ///
    /// <para>🚨 <b>The "no change" test is NULL, not null-or-empty</b>, because that is what the
    /// merge does: <c>UpdateAccordingToSourceNode</c> applies <c>sourceNode.NodeType ?? state.NodeType</c>,
    /// so <c>null</c> means "keep what state has" and an EMPTY STRING is a real value the merge
    /// WILL write. A predicate that folded empty into "no change" would name one rule while the
    /// merge applied another, and a later caller reading the name would be wrong. Clearing a type
    /// is still allowed — an untyped node is legal, activates on the mesh default chain, and is
    /// therefore never the dangling condition — but it is allowed because
    /// <see cref="Resolves"/> says an empty type resolves, NOT because this pretended it was a
    /// no-op.</para>
    ///
    /// <para>An incoming type EQUAL to the existing one is a round-trip of what is already stored,
    /// not a new write of a dangling type. Refusing it would make an already-stranded node
    /// un-editable — and <c>patch</c> refuses <c>nodeType</c> outright, so a full-node update is
    /// the ONLY repair route there is. This is the same carve-out, for the same reason, that
    /// <c>ContentDiscriminatorValidator</c> applies to a round-tripped <c>$type</c>.</para>
    /// </summary>
    public static bool ChangesNodeType(string? incomingNodeType, string? existingNodeType) =>
        incomingNodeType is not null
        && !string.Equals(incomingNodeType, existingNodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Existence of the node at the type's path, by exactly the rule
    /// <c>MeshExtensions</c>' create path applies: an empty type resolves (untyped nodes are
    /// legal), then <see cref="StaticNodeProviderExtensions.FindStaticNode"/>, then
    /// <see cref="IStorageAdapter.Exists"/>. With no storage adapter registered the answer is
    /// <c>false</c> — identical to the create path, which refuses in that case rather than
    /// guessing.
    /// </summary>
    public static IObservable<bool> Resolves(IMessageHub hub, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return Observable.Return(true);
        if (hub.ServiceProvider.FindStaticNode(nodeType) is not null)
            return Observable.Return(true);
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        return persistence is null
            ? Observable.Return(false)
            : persistence.Exists(nodeType).Take(1);
    }

    /// <summary>
    /// The refusal every write boundary posts, so a caller reading it once has learned the rule
    /// wherever it hit them. Names the type, says what the write would have produced, and names
    /// the two ways forward — because the message IS the repair instruction.
    /// </summary>
    public static string RejectionMessage(string path, string nodeType) =>
        $"NodeType '{nodeType}' is not registered — refusing to set it on '{path}'. A node whose "
        + "NodeType resolves to nothing has no per-node hub: it reads as Unavailable, renders "
        + "empty, and never reaches a verdict. Import or create the NodeType first, or name a "
        + "type that exists. (Updating this node WITHOUT changing its NodeType is always allowed, "
        + "so an already-mistyped node can still be repaired by naming a type that resolves.)";

    /// <summary>
    /// The refusal for the case where the existence probe itself FAULTED. 🚨 A verdict and a
    /// non-verdict are not the same answer — the same distinction <c>NodeUpdatePipeline</c> draws
    /// between a routing NotFound and a read timeout. Saying "not registered" here would send a
    /// caller off to create a type that may well already exist.
    /// </summary>
    public static string ProbeFailedMessage(string path, string nodeType, Exception error) =>
        $"Could not verify that NodeType '{nodeType}' exists, so the update of '{path}' was "
        + $"refused rather than risk writing a NodeType that resolves to nothing: {error.Message}. "
        + "This is NOT 'the type does not exist' — do not create anything on the strength of it. "
        + "Retry shortly.";
}
