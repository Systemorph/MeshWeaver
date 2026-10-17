using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Refuses a Create or Update whose <see cref="MeshNode.Icon"/> is not an acceptable icon —
/// <see cref="IconSafety"/> carries the rule and its reasoning.
///
/// <para><b>Defence in depth.</b> Renderers draw an inline-SVG icon as an inert image, which is the
/// protection against an icon that carries active content. This validator keeps such a value out of
/// the store in the first place, naming the element, attribute, scheme or character it refused.</para>
///
/// <para><b>It judges a CHANGE, never a state.</b> An Update that leaves the icon exactly as stored
/// passes, whatever the stored value is: a node whose icon predates the rule must stay editable, and
/// it still loads and renders (inert) — nothing here runs on a read.</para>
///
/// <para><b>Where it runs.</b> The creation chain (every create, including a move's copy leg and an
/// import's create branch) and <c>NodeUpdatePipeline</c> (<c>IMeshService.UpdateNode</c>, behind the
/// MCP <c>update</c> and <c>patch</c> tools). <c>CreateOrUpdateNodeRequest</c>'s update branch runs no
/// validators, so it applies the same <see cref="IconSafety.Refusal"/> inline. Not an
/// <see cref="IOwnerEnforcedNodeValidator"/>: this is content integrity, and it must surface to the
/// caller before the write is issued. A raw cross-hub <c>PatchDataRequest</c> runs none of the
/// app-integrity validators (Doc/Architecture/ParticipantIngress → "Not closed here"), so this is a
/// second line, never the only one.</para>
/// </summary>
public sealed class IconMarkupValidator : INodeValidator
{
    /// <summary>Create + Update — the two surfaces that place an icon on a node.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    /// <summary>
    /// Validates the node's icon.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the one validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(Judge(context));

    /// <summary>
    /// The verdict, synchronously — the same rule <see cref="Validate"/> emits.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>The validation result.</returns>
    public static NodeValidationResult Judge(NodeValidationContext context)
    {
        var icon = context.Node.Icon;
        if (context.Operation == NodeOperation.Update
            && string.Equals(icon, context.ExistingNode?.Icon, StringComparison.Ordinal))
            return NodeValidationResult.Valid();

        return IconSafety.Refusal(icon) is { } refusal
            ? NodeValidationResult.Invalid(IconSafety.RefusalMessage(context.Node.Path, refusal))
            : NodeValidationResult.Valid();
    }
}
