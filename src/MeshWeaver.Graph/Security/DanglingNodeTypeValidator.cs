using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Refuses an <see cref="NodeOperation.Update"/> that would give a node a
/// <see cref="MeshNode.NodeType"/> which resolves to nothing — the update-path half of issue #2993.
///
/// <para><b>The hole this closes.</b> The CREATE path has always rejected an unregistered NodeType
/// (<c>NodeCreationRejectionReason.InvalidNodeType</c>). The UPDATE path checked nothing, and no
/// registered validator produced <c>InvalidNodeType</c> for <see cref="NodeOperation.Update"/> —
/// every producer was on the create path. So <c>update</c> was a supported route to CREATE the
/// orphan condition: an instance whose type resolves to nothing has no per-node hub, which means
/// every read of it times out as <c>Unavailable</c> rather than failing, the view renders empty,
/// and nothing anywhere names the cause. A live example on production was
/// <c>rbuergi/_Draft/PartnerRe_EslProposalQA</c> carrying <c>nodeType: EmailDraft</c>.</para>
///
/// <para><b>It judges a CHANGE, never a state</b> (<see cref="NodeTypeResolution.ChangesNodeType"/>).
/// An update that keeps the node's current NodeType passes even when that type is already dangling
/// — and that is deliberate, not an oversight: <c>patch</c> refuses <c>nodeType</c> outright
/// (<c>MeshOperations.PatchableFields</c>), so a full-node <c>update</c> is the ONLY route by which
/// an already-mistyped node can be repaired. A guard that refused every update to such a node would
/// close the one repair path there is. Retyping it to something that DOES resolve is likewise
/// allowed — that IS the repair.</para>
///
/// <para><b>Where it runs.</b> <c>NodeUpdatePipeline</c> (<c>IMeshService.UpdateNode</c>, which is
/// what the MCP <c>update</c> tool calls). It is deliberately NOT an
/// <see cref="IOwnerEnforcedNodeValidator"/>: this is app integrity, not permission, and it must
/// surface to the caller BEFORE the write is issued. The other update verb —
/// <c>CreateOrUpdateNodeRequest</c> — runs no validators at all, so it carries the same rule inline
/// in <c>MeshExtensions.ApplyUpdateViaStream</c>, off this same shared predicate. Full reasoning:
/// <c>Doc/Architecture/DanglingNodeTypes</c>.</para>
/// </summary>
public sealed class DanglingNodeTypeValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<DanglingNodeTypeValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the dangling-NodeType update guard.
    /// </summary>
    /// <param name="hub">The hub whose static-node providers and storage adapter answer the
    /// existence question.</param>
    /// <param name="logger">The logger used to record refused updates.</param>
    public DanglingNodeTypeValidator(IMessageHub hub, ILogger<DanglingNodeTypeValidator> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Update only — the create path applies the same rule in its own pipeline.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];

    /// <summary>
    /// Validates an update, refusing it when it would set a <see cref="MeshNode.NodeType"/> that
    /// resolves to neither a static node nor a persisted node.
    /// </summary>
    /// <param name="context">The validation context describing the node and the existing state.</param>
    /// <returns>An observable emitting the single verdict.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!NodeTypeResolution.ChangesNodeType(node.NodeType, context.ExistingNode?.NodeType))
            return Observable.Return(NodeValidationResult.Valid());

        return NodeTypeResolution.Resolves(_hub, node.NodeType)
            .Select(resolves =>
            {
                if (resolves)
                    return NodeValidationResult.Valid();
                _logger.LogWarning(
                    "DanglingNodeTypeGuard: blocked update of '{Path}' — NodeType '{NodeType}' "
                    + "(was '{ExistingNodeType}') resolves to no node, so the instance would have "
                    + "no per-node hub and would read as Unavailable forever.",
                    node.Path, node.NodeType, context.ExistingNode?.NodeType);
                return NodeValidationResult.Invalid(
                    NodeTypeResolution.RejectionMessage(node.Path, node.NodeType!),
                    NodeRejectionReason.InvalidNodeType);
            })
            // 🚨 A faulted probe is NOT a verdict, and it must not read as one. Refusing is right —
            // a write here could strand the node permanently — but the message says which of the
            // two it is, so nobody goes off creating a type that may already exist.
            .Catch((Exception ex) =>
            {
                _logger.LogWarning(ex,
                    "DanglingNodeTypeGuard: the NodeType existence probe for '{NodeType}' faulted "
                    + "while validating '{Path}'; refusing rather than risking a dangling type.",
                    node.NodeType, node.Path);
                return Observable.Return(NodeValidationResult.Unavailable(
                    NodeTypeResolution.ProbeFailedMessage(node.Path, node.NodeType!, ex)));
            });
    }
}
