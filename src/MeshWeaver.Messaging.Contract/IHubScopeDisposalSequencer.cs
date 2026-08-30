namespace MeshWeaver.Messaging;

/// <summary>
/// Sequences the closing of a hosted hub's OWN lifetime scope against the mesh's teardown drains.
///
/// <para>A hosted hub owns the Autofac scope it was built in, and <c>HostedHubsCollection</c> closes
/// that scope once the hub's <c>DisposalCompleted</c> fires. That signal covers only the hub's action
/// block and message round-trips — NOT the offloaded I/O the hub issued on the mesh's
/// <c>IIoPool</c>s, nor the async cleanup it enqueued, nor the synced-query pipelines still
/// resolving services from that scope on a scheduler thread. During a WHOLE-MESH teardown those are
/// joined by the mesh's drain phases (<c>IoPoolRegistry.DrainAll()</c> → <c>AsyncDisposeQueue</c>)
/// strictly AFTER every hub has signalled — so a scope closed on the hub's signal alone is closed
/// UNDER live work: every straggler capture in CI names this shape
/// (<c>PermissionEvaluator.GetEffectivePermissions</c>, <c>MeshNodeStreamCache.GetQueryRaw</c>,
/// <c>SynchronizationStream.CaptureCallerAccessContext</c>, <c>IoPool.InvokeCore</c> …) resolving
/// from a <c>LifetimeScope</c> that "has already been disposed", and the one that escapes onto a
/// bare thread is the anonymous "Catastrophic failure: ObjectDisposedException" that reds a green
/// shard (MeshWeaver.Plugins#870).</para>
///
/// <para>The mesh layer knows the drain order; this assembly does not. So the collection hands the
/// close to this sequencer, resolved from the owning hub's provider, and the mesh registers the
/// implementation that closes NOW while the mesh is live (a recycle must free its scope promptly —
/// an Autofac parent tracks every child scope until it is closed) and defers to the terminal
/// teardown signal while the mesh is tearing down — the same phase rule
/// <c>MeshDataSource.UnloadNodeAssemblyContexts</c> follows for the collectible ALCs. A mesh with
/// no implementation registered (bare messaging-only hubs) keeps the historical inline close.</para>
/// </summary>
public interface IHubScopeDisposalSequencer
{
    /// <summary>
    /// Closes the scope <paramref name="hub"/> owned by invoking <paramref name="closeScope"/> —
    /// immediately when the mesh is live, or after the mesh's teardown drains have joined every
    /// pooled leaf and async cleanup when the mesh is tearing down. Never throws: a sequencer that
    /// cannot decide must still close.
    /// </summary>
    /// <param name="hub">The address of the hub whose disposal has terminated.</param>
    /// <param name="closeScope">Closes the hub's lifetime scope; idempotent and non-throwing.</param>
    void CloseWhenDrained(Address hub, Action closeScope);
}
