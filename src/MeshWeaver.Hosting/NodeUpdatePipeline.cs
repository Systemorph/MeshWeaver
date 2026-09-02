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
                // 🚨 A VERDICT AND A NON-VERDICT ARE NOT THE SAME ANSWER, and this seam used to
                // report them as one. A DeliveryFailureException from the owner/routing IS a
                // verdict — the mesh looked and there is no node there. The 10s Timeout is NOT:
                // it means the per-node hub never completed its handshake, which is exactly what
                // a node whose NodeType no longer resolves does. Collapsing both into
                // "Node not found" told a caller that an EXISTING node was absent, and the only
                // repair that reads as sensible from there is delete-and-recreate — on live
                // content. This is the same lie the READ path was fixed for (#974), which is why
                // MeshOperations.UnavailableMessage spells out "this is NOT 'not found'".
                Observable.Throw<MeshNode>(ex switch
                {
                    DeliveryFailureException => new InvalidOperationException(
                        $"Node not found: {node.Path}"),
                    TimeoutException => new InvalidOperationException(
                        $"Unavailable: {node.Path} — this update could not read the node's current "
                        + "state, so it is UNKNOWN whether the node exists. This is NOT 'not "
                        + "found': do not delete or recreate anything on the strength of it. A "
                        + "node whose NodeType no longer resolves fails exactly this way. "
                        + "Retry shortly.", ex),
                    _ => ex
                }))
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
                .Update(live =>
                {
                    // 🚨 No-op gate BEFORE the LastModified re-stamp: an UpdateNode carrying a
                    // node identical to the live one (an importer re-asserting state, an MCP
                    // update that changed nothing, a plugin re-install of unchanged content)
                    // must NOT bump the version or persist a history row. Normalise the two
                    // framework-owned fields to the live values so only REAL field differences
                    // count, then return the LIVE instance — the stream's reference-equality
                    // no-op gate completes the write without touching anything.
                    var candidate = node with
                    {
                        Version = live.Version,
                        LastModified = live.LastModified,
                    };
                    if (MeshNode.SerializedEquals(live, candidate, hub.JsonSerializerOptions))
                        return live;
                    // 🚨 Re-stamp LastModified at APPLY time (real change only).
                    // NodeFactory.UpdateNode carries the caller's node verbatim, whose
                    // LastModified is the value the caller READ (e.g. `current with { … }`),
                    // NOT the edit time — so without this the node persists with a stale
                    // LastModified. NodeType IsDirty / CurrentSourceVersions key on
                    // LastModified.UtcTicks, so a stale stamp leaves an edited source
                    // looking clean and the V2 recompile never fires
                    // (SyncedQueryFreshnessContractTest pins exactly this). Prod's
                    // CachingStorageAdapter stamps it too; the in-memory adapter does
                    // not, so the owner-apply lambda here is the one place every
                    // storage config shares.
                    return candidate with { LastModified = DateTimeOffset.UtcNow };
                }));

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
            // 🚨 Unavailable belongs on the LEFT of this fork, with NotFound and InvalidNodeType.
            // It is a validator saying "I reached NO decision because a read I depend on did not
            // answer" — mapping it to UnauthorizedAccessException would tell the caller they lack a
            // permission they may well hold, which is the same verdict/non-verdict conflation the
            // Catch above exists to prevent.
            .Select(r => (Exception?)(r.Reason is NodeRejectionReason.NodeNotFound
                    or NodeRejectionReason.InvalidNodeType
                    or NodeRejectionReason.Unavailable
                ? new InvalidOperationException(r.ErrorMessage ?? $"Update rejected for: {node.Path}")
                : new UnauthorizedAccessException(r.ErrorMessage ?? "Update rejected by validator")))
            .DefaultIfEmpty(null);
    }
}
