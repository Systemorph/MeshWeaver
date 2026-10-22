using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Runs the <see cref="INodeTypeTransitionValidator"/>s on a write that RETYPES a node, for the write
/// routes that run no validator chain of their own. Today that is the upsert's update branch
/// (<c>CreateOrUpdateNodeRequest</c>).
///
/// <para><b>The existing node is the STORED one.</b> When a caller already holds it, it passes it in.
/// When it does not, the gate reads it from storage. That covers the upsert that lost its create race:
/// it never read the node that won. Judging the incoming node as its own predecessor would make a
/// retype compare against itself and pass having checked nothing.</para>
///
/// <para><b>Exactly one verdict per validator</b>, the same contract the creation runner holds every
/// validator to. The first emission is the verdict (<c>Take(1)</c>). An EMPTY completion is a
/// refusal, because "no verdict" is not consent. A validator that throws while building its
/// observable is caught by <c>Defer</c> and surfaces as the chain's error.</para>
/// </summary>
public static class NodeTypeTransitionGate
{
    /// <summary>Catalog key: a transition validator completed without a verdict.</summary>
    public const string GaveNoVerdictKey = "activity.node.update.transitionValidatorGaveNoVerdict";

    /// <summary>Catalog key: a transition validator refused without a message.</summary>
    public const string RefusedWithoutMessageKey = "activity.node.update.transitionValidatorRefused";

    /// <summary>
    /// The first refusal of a retype of <paramref name="node"/>, or null when every transition
    /// validator approved it (or the write does not change the NodeType).
    /// </summary>
    /// <param name="hub">The hub whose services hold the validators and the storage adapter.</param>
    /// <param name="node">The node as the write would leave it.</param>
    /// <param name="stored">The stored node when the caller holds it; null to read it from storage.</param>
    /// <param name="accessContext">The writer's identity, which also carries the refusal's locale.</param>
    /// <returns>One emission: the refusal and its reason, or null.</returns>
    public static IObservable<(LocalizableText Refusal, NodeRejectionReason Reason)?> FirstRefusal(
        IMessageHub hub, MeshNode node, MeshNode? stored, AccessContext? accessContext)
    {
        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v is INodeTypeTransitionValidator
                        && (v.SupportedOperations.Count == 0
                            || v.SupportedOperations.Contains(NodeOperation.Update)))
            .ToList();
        if (validators.Count == 0)
            return Observable.Return<(LocalizableText, NodeRejectionReason)?>(null);

        var existing = stored is not null
            ? Observable.Return<MeshNode?>(stored)
            : hub.ServiceProvider.GetService<IStorageAdapter>() is { } storage
                ? Observable.Defer(() => storage.Read(node.Path, hub.JsonSerializerOptions)).Take(1).DefaultIfEmpty(null)
                : Observable.Return<MeshNode?>(null);

        return existing.SelectMany(current =>
        {
            var context = new NodeValidationContext
            {
                Operation = NodeOperation.Update,
                Node = node,
                ExistingNode = current,
                AccessContext = accessContext,
            };
            return validators
                .Select(v => Observable.Defer(() => v.Validate(context))
                    .Take(1)
                    .Select(result => (Result: (NodeValidationResult?)result, Validator: v.GetType().Name))
                    .DefaultIfEmpty((Result: null, Validator: v.GetType().Name)))
                .Concat()
                .Where(verdict => verdict.Result is not { IsValid: true })
                .Take(1)
                .Select(verdict => Refusal(node, verdict.Result, verdict.Validator))
                .DefaultIfEmpty(null);
        });
    }

    private static (LocalizableText Refusal, NodeRejectionReason Reason)? Refusal(
        MeshNode node, NodeValidationResult? result, string validator)
    {
        if (result is null)
            return (LocalizableText.Keyed(
                    $"Updating '{node.Path}' was refused: the NodeType transition validator "
                    + $"{validator} completed without a verdict. No verdict is not consent, "
                    + "so nothing was written; a validator must emit exactly one result.",
                    GaveNoVerdictKey, ("path", node.Path), ("validator", validator)),
                NodeRejectionReason.Unavailable);
        var text = MeshExtensions.ValidatorRefusalText(result.ErrorMessage)
                   ?? LocalizableText.Keyed(
                       $"Updating '{node.Path}' was refused by the NodeType transition validator {validator}.",
                       RefusedWithoutMessageKey, ("path", node.Path), ("validator", validator));
        return (text, result.Reason);
    }
}
