using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// The <c>IMeshService.UpdateNode</c> pipeline: existence check + client-side
/// <see cref="INodeValidator"/> (Update) run + the canonical <c>stream.Update</c>.
/// Reconstructs the pre-checks the deleted <c>UpdateNodeRequest</c> handler performed so the
/// <c>IMeshService</c> surface keeps mapping rejections to the documented exception types:
/// a missing node → <see cref="InvalidOperationException"/> ("Node not found"); a validator
/// rejection → <see cref="UnauthorizedAccessException"/>.
/// <para>RLS / structural validators (<see cref="IOwnerEnforcedNodeValidator"/>) are
/// SKIPPED here — RLS on Update is enforced authoritatively by the owning per-node hub's
/// <c>[RequiresPermission(Update)]</c> pipeline and surfaced by <c>UpdateRemote</c>. Only
/// app-integrity validators (version, name, …) run client-side.</para>
/// </summary>
internal static class NodeUpdatePipeline
{
    public static IObservable<MeshNode> UpdateWithValidation(IMessageHub hub, MeshNode node)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;

        // 1. Read the existing node (authoritative, via the owning hub) — needed both for
        //    the not-found check and as ExistingNode for Update validators. A 10s ceiling
        //    bounds the read; for a path no node owns it never emits non-null → not found.
        return hub.GetMeshNodeStream(node.Path)
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode, Exception>(ex =>
                // A non-existent / unreachable node surfaces either as a fast
                // DeliveryFailureException ("No node found at …") from the owner/routing,
                // or — if the per-node hub never completes its handshake — as the 10s
                // Timeout. Both map to the documented "Node not found"
                // InvalidOperationException; any other read error propagates unchanged.
                Observable.Throw<MeshNode>(
                    ex is DeliveryFailureException or TimeoutException
                        ? new InvalidOperationException($"Node not found: {node.Path}")
                        : ex))
            .SelectMany(existing => RunUpdateValidators(hub, node, existing, ctx)
                .SelectMany(error => error is not null
                    ? Observable.Throw<MeshNode>(error)
                    // 3. All validators passed → the canonical write, under the caller's
                    //    identity (see DoUpdate). The version bump happens INSIDE the write
                    //    lambda, off the live node it receives — not off this early `existing`
                    //    read, which is already stale by the time the cross-hub write lands.
                    : DoUpdate(hub, accessService, ctx, node)));
    }

    // The canonical write. 🚨 Re-establish the caller's identity at SUBSCRIBE: the
    // existence-read continuation above runs on a pool thread that lost the AsyncLocal
    // AccessContext, and UpdateRemote captures accessService.Context at subscribe time —
    // so without this a viewer's cross-hub update would go out under the wrong (e.g. hub/
    // system) identity and the owner's [RequiresPermission(Update)] check would NOT deny
    // it (the McpUpdate_User1CannotUpdate regression). Observable.Using sets the context
    // before UpdateRemote's capture and restores it when the write observable terminates.
    private static IObservable<MeshNode> DoUpdate(
        IMessageHub hub, AccessService? accessService, AccessContext? ctx, MeshNode node)
        => Observable.Using(
            () => accessService is not null && ctx is not null
                ? accessService.SwitchAccessContext(ctx)
                : Disposable.Empty,
            // 🚨 Use the lambda parameter (the LIVE owner-reconciled node) as the write
            // base — never discard it (`_ => node`). A client/subscriber NEVER mints a
            // version: it carries the BASE version it just observed (the live node's),
            // and the OWNER assigns the fresh monotonic version on apply. Bumping the
            // version client-side (the old `Math.Max(existing,…) + 1`) ships a frame
            // whose base is out of date by the time it lands, so the owner's
            // version-guarded merge mishandles it — the read-your-writes-after-update bug.
            _ => hub.GetMeshNodeStream(node.Path)
                .Update(live => node with { Version = live.Version }));

    // 2. Run client-side Update validators sequentially (Concat preserves short-circuit:
    //    the chain stops at the first failure). Returns the mapped exception or null.
    private static IObservable<Exception?> RunUpdateValidators(
        IMessageHub hub, MeshNode node, MeshNode existing, AccessContext? ctx)
    {
        var validators = hub.ServiceProvider.GetServices<INodeValidator>()
            .Where(v => v is not IOwnerEnforcedNodeValidator
                        && (v.SupportedOperations.Count == 0
                            || v.SupportedOperations.Contains(NodeOperation.Update)))
            .ToList();
        if (validators.Count == 0)
            return Observable.Return<Exception?>(null);

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = node,
            ExistingNode = existing,
            AccessContext = ctx,
        };

        return validators
            .Select(v => v.Validate(context))
            .Concat()
            .Where(r => !r.IsValid)
            .Take(1)
            .Select(r => (Exception?)(r.Reason is NodeRejectionReason.NodeNotFound
                    or NodeRejectionReason.InvalidNodeType
                ? new InvalidOperationException(r.ErrorMessage ?? $"Update rejected for: {node.Path}")
                : new UnauthorizedAccessException(r.ErrorMessage ?? "Update rejected by validator")))
            .DefaultIfEmpty(null);
    }
}
