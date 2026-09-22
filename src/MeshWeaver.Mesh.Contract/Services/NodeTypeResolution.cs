using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>THE one rule for "does this NodeType resolve?"</b> — a NodeType DECLARATION at the type's
/// PATH, found either as a static node (<see cref="StaticNodeProviderExtensions.FindStaticNode"/>)
/// or in persistence. Not a <c>TypeRegistry</c> fact, not a compiled assembly: see
/// <c>Doc/Architecture/ImportWriteOrdering</c> for why those two
/// are deliberately not conflated.
///
/// <para>🚨 <b>"A node is there" is a DIFFERENT question, and answering this one with that one is
/// Systemorph/MeshWeaver#5008/#2231.</b> They diverge the moment two things want one name — a
/// Store plugin's root at the bare path <c>Feedback</c> against its declaration at
/// <c>Feedback/Feedback</c> — and the divergence was invisible because only ACTIVATION applied the
/// content test. <see cref="INodeTypeDeclarationProbe"/> is now the one test both sides apply; see
/// <see cref="Resolve"/> for the ordering and why it may only ever say "definitely not".</para>
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
    /// <see cref="IStorageAdapter"/>. With no storage adapter registered the answer is
    /// <c>false</c> — identical to the create path, which refuses in that case rather than
    /// guessing.
    /// </summary>
    public static IObservable<bool> Resolves(IMessageHub hub, string? nodeType) =>
        Resolve(hub, nodeType).Select(v => v.Resolves);

    /// <summary>
    /// <see cref="Resolves"/> with the REASON attached, so a boundary that refuses can say what is
    /// in the way instead of the generic "not registered".
    ///
    /// <para>🚨 <b>Occupancy is not registration.</b> This used to answer from
    /// <see cref="IStorageAdapter.Exists"/> alone, and that is the whole of
    /// Systemorph/MeshWeaver#5008/#2231: a Store plugin's root sits at the bare path
    /// <c>Feedback</c> while its declaration is <c>Feedback/Feedback</c>, so a write naming the
    /// bare path was ACCEPTED here on the plugin root and then REFUSED by activation, which has
    /// applied the content test since #2245. The two boundaries now ask
    /// <see cref="INodeTypeDeclarationProbe"/>, so they cannot drift.</para>
    ///
    /// <para>🚨 <b>The damage is narrower than this class's #2993 sentence, and saying it the
    /// #2993 way overstates it.</b> A type resolving to NOTHING leaves a node with no per-node hub
    /// at all. A type resolving to an OCCUPANT does not: the ROW still reads — measured, the
    /// stranded production instance returns its full content through an ordinary read — while the
    /// HUB binds the occupant's configuration, or the error overlay activation applies once it
    /// detects the collision. So the node is not lost; its page serves the diagnostic instead of
    /// the type's views and its typed requests are NACKed. What the two cases share is the part
    /// that matters here: the write boundary accepted a NodeType the activation boundary will
    /// not.</para>
    ///
    /// <para><b>Read, then Exists — in that order, and the fallback is not redundant.</b> One read
    /// answers both questions on the common path. A <c>null</c> read is NOT taken as absence,
    /// because <see cref="IStorageAdapter.Exists"/> is the predicate this boundary has always
    /// used and the two can disagree (a provider that answers existence without handing over the
    /// row); falling back preserves the previous ACCEPT decision exactly, so the only behaviour
    /// this change adds is a refusal the probe positively convicts.</para>
    ///
    /// <para>A faulted probe propagates rather than being swallowed: every caller already
    /// distinguishes "not registered" from "could not tell" (<see cref="ProbeFailedMessage"/>),
    /// and turning a non-verdict into a verdict is the one thing that would send someone off to
    /// create a type that already exists.</para>
    /// </summary>
    /// <param name="hub">The hub whose static providers, storage adapter and declaration probe
    /// answer the question.</param>
    /// <param name="nodeType">The NodeType path being judged.</param>
    /// <returns>The single verdict.</returns>
    public static IObservable<NodeTypeVerdict> Resolve(IMessageHub hub, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
            return Observable.Return(NodeTypeVerdict.Registered);
        var probe = hub.ServiceProvider.GetService<INodeTypeDeclarationProbe>();
        if (hub.ServiceProvider.FindStaticNode(nodeType) is { } staticNode)
            return Observable.Return(Judge(staticNode, probe));
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return(NodeTypeVerdict.Absent);
        return persistence.Read(nodeType, hub.JsonSerializerOptions)
            .Take(1)
            .SelectMany(node => node is not null
                ? Observable.Return(Judge(node, probe))
                : persistence.Exists(nodeType).Take(1).Select(exists =>
                    exists ? NodeTypeVerdict.Registered : NodeTypeVerdict.Absent));
    }

    /// <summary>
    /// The one-sided application of the probe: a conviction refuses and carries the occupant;
    /// everything else — including no probe registered — resolves.
    /// </summary>
    private static NodeTypeVerdict Judge(MeshNode candidate, INodeTypeDeclarationProbe? probe)
        => probe?.DescribeNonDeclaration(candidate) is { } occupant
            ? new NodeTypeVerdict(false, occupant)
            : NodeTypeVerdict.Registered;

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

    /// <summary>The catalog key whose English is <see cref="RejectionMessage"/>.</summary>
    public const string RejectionMessageKey = "activity.nodeType.notRegistered";

    /// <summary>
    /// The refusal for the case a bare "not registered" describes badly: SOMETHING is at the
    /// type's path, it is just not a declaration. Saying "not registered" there sends the reader
    /// off to create a node that is already sitting in the way, which is how
    /// Systemorph/MeshWeaver#2231 spent a month reading as a lookup-ordering puzzle.
    ///
    /// <para>The wording is deliberately the same shape the ACTIVATION boundary already prints
    /// (<c>NodeTypeEnrichmentHelpers</c>'s collision overlay), because they are one fact reported
    /// at two moments and a reader who has met one has learned the other.</para>
    /// </summary>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType path that is occupied.</param>
    /// <param name="occupant">What is in the way, as
    /// <see cref="INodeTypeDeclarationProbe.DescribeNonDeclaration"/> describes it.</param>
    /// <returns>The refusal.</returns>
    public static string OccupiedMessage(string path, string nodeType, string occupant) =>
        $"NodeType '{nodeType}' is not registered: {occupant} — refusing to set it on '{path}'. "
        + "A node exists at that path, but it is not a NodeType declaration, so the instance would "
        + "bind that node's hub configuration instead of a type's: its page serves an error "
        + "overlay rather than the type's views, and typed requests are refused. Name the "
        + "declaration's real path, or move whatever occupies this one out of the way. Creating a "
        + "node here will NOT help — the path is already taken.";

    /// <summary>The catalog key whose English is <see cref="OccupiedMessage"/>.</summary>
    public const string OccupiedMessageKey = "activity.nodeType.pathOccupied";

    /// <summary>
    /// <see cref="OccupiedMessage"/> paired with <see cref="OccupiedMessageKey"/>. The occupant
    /// description travels as an ARGUMENT: the sentence around it is ours and translates, the
    /// node path and NodeType inside it are data and do not.
    /// </summary>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType path that is occupied.</param>
    /// <param name="occupant">What is in the way.</param>
    /// <returns>The localizable refusal.</returns>
    public static LocalizableText Occupied(string path, string nodeType, string occupant) =>
        LocalizableText.Keyed(OccupiedMessage(path, nodeType, occupant), OccupiedMessageKey,
            ("path", path), ("nodeType", nodeType), ("occupant", occupant));

    /// <summary>
    /// <see cref="RejectionMessage"/> or <see cref="OccupiedMessage"/>, chosen by whether the
    /// verdict named an occupant — so no call site has to remember there are two.
    /// </summary>
    /// <param name="verdict">The verdict from <see cref="Resolve"/>.</param>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType that does not resolve.</param>
    /// <returns>The refusal text.</returns>
    public static string RefusalFor(NodeTypeVerdict verdict, string path, string nodeType) =>
        verdict.Occupant is { } occupant
            ? OccupiedMessage(path, nodeType, occupant)
            : RejectionMessage(path, nodeType);

    /// <summary>
    /// <see cref="Rejection"/> or <see cref="Occupied"/>, chosen the same way as
    /// <see cref="RefusalFor"/>.
    /// </summary>
    /// <param name="verdict">The verdict from <see cref="Resolve"/>.</param>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType that does not resolve.</param>
    /// <returns>The localizable refusal.</returns>
    public static LocalizableText LocalizedRefusalFor(
        NodeTypeVerdict verdict, string path, string nodeType) =>
        verdict.Occupant is { } occupant
            ? Occupied(path, nodeType, occupant)
            : Rejection(path, nodeType);

    /// <summary>
    /// <see cref="RejectionMessage"/> paired with <see cref="RejectionMessageKey"/>, so a write
    /// boundary that logs it into an activity transcript gets the viewer's language instead of the
    /// author's. The English is byte-identical to <see cref="RejectionMessage"/> — it IS that call —
    /// so the wire <c>Error</c> and the transcript fallback cannot drift.
    /// </summary>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType that does not resolve.</param>
    /// <returns>The localizable refusal.</returns>
    public static LocalizableText Rejection(string path, string nodeType) =>
        LocalizableText.Keyed(RejectionMessage(path, nodeType), RejectionMessageKey,
            ("path", path), ("nodeType", nodeType));

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

    /// <summary>The catalog key whose English is <see cref="ProbeFailedMessage"/>.</summary>
    public const string ProbeFailedMessageKey = "activity.nodeType.probeFailed";

    /// <summary>
    /// <see cref="ProbeFailedMessage"/> paired with <see cref="ProbeFailedMessageKey"/>. The
    /// upstream <c>error.Message</c> travels as an ARGUMENT rather than being folded into the key:
    /// the sentence around it is ours and translates, the fault text is the storage adapter's and
    /// does not.
    /// </summary>
    /// <param name="path">The node being written.</param>
    /// <param name="nodeType">The NodeType whose existence could not be established.</param>
    /// <param name="error">The fault the probe raised.</param>
    /// <returns>The localizable refusal.</returns>
    public static LocalizableText ProbeFailed(string path, string nodeType, Exception error) =>
        LocalizableText.Keyed(ProbeFailedMessage(path, nodeType, error), ProbeFailedMessageKey,
            ("path", path), ("nodeType", nodeType), ("error", error.Message));
}
