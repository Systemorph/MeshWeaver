using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The mesh's <see cref="IHubScopeDisposalSequencer"/>: a hosted hub's lifetime scope closes NOW
/// while the mesh is live (a recycle frees its scope promptly — an Autofac parent tracks every child
/// scope until it is closed), and AFTER the teardown drains while the mesh is tearing down.
///
/// <para>🚨 The deferral target is <see cref="MeshTeardownSignal.Completed"/>, NOT the
/// <see cref="AsyncDisposeQueue"/>: the queue's consumer runs in the background the moment an item
/// is posted, so an enqueued close would still land BEFORE <see cref="IoPoolRegistry.DrainAll()"/>
/// has joined the pooled leaves that resolve from that scope. The terminal signal fires once every
/// drain phase is accounted for (<c>MeshTeardownExtensions.DrainAsync</c> phase 4, and the same
/// point in <c>MonolithMeshTestBase.DisposeAsync</c>) — which is precisely "queues and pools last,
/// scopes after them". It is the phase rule <c>MeshDataSource.UnloadNodeAssemblyContexts</c> already
/// follows for the collectible ALCs, applied one layer up to the scopes those assemblies' instances
/// live in.</para>
///
/// <para>Whole-mesh teardown is recognised by the ROOT mesh hub's <see cref="IMessageHub.IsDisposing"/>
/// — resolved at call time from the root provider, never in the constructor, because a hub can be
/// hosted (and therefore registered here) while the mesh hub singleton is still being constructed.
/// A subtree recycle (a package root going down with its children) leaves the root live, so those
/// scopes close inline exactly as before: nothing on a live mesh is ever deferred to a signal that
/// only a teardown fires.</para>
///
/// <para>Limit, stated: a teardown that disposes the mesh hub without running the drain
/// orchestration (a run-boundary tool that block-joins <c>DisposalCompleted</c> and exits) never
/// fires the signal, so its child scopes stay open until the process ends — the same lifetime they
/// had before scopes were closed at all, and one the root container's own disposal still reclaims
/// because Autofac disposes the child scopes it tracks.</para>
/// </summary>
public sealed class TeardownOrderedScopeDisposal(
    IServiceProvider rootProvider,
    ILogger<TeardownOrderedScopeDisposal>? logger = null) : IHubScopeDisposalSequencer
{
    /// <inheritdoc />
    public void CloseWhenDrained(Address hub, Action closeScope)
    {
        IMessageHub? mesh;
        MeshTeardownSignal? signal;
        try
        {
            mesh = rootProvider.GetService<IMessageHub>();
            signal = rootProvider.GetService<MeshTeardownSignal>();
        }
        catch (ObjectDisposedException)
        {
            // The root itself is gone — the drains ran (or nothing will run them). Close now.
            closeScope();
            return;
        }

        if (mesh is null || signal is null || !mesh.IsDisposing)
        {
            closeScope();
            return;
        }

        // ReplaySubject(1)-backed and completing: the subscription releases itself on the report,
        // and a hub that terminates AFTER the report (a late construction the join disposed) still
        // gets it immediately — never a silently-skipped close. Both arms close: a faulted signal
        // is the case where holding the scope open would leak the most.
        signal.Completed.Subscribe(
            _ => closeScope(),
            ex =>
            {
                logger?.LogWarning(ex,
                    "[DISPOSE-CONTAINER] {Address}: the teardown signal faulted before the lifetime "
                    + "scope was closed — closing it now", hub);
                closeScope();
            });
    }
}
