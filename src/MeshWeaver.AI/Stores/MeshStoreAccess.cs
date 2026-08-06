using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Stores;

/// <summary>
/// The mesh access an agent store needs — the ONE place where the Microsoft Agent Framework's
/// storage abstractions meet our reactive mesh.
///
/// <para><b>Why this is composed, not inherited.</b> Each MAF store type
/// (<c>AgentFileStore</c>, <c>AgentSkillsSource</c>) is an abstract CLASS, so it already occupies
/// the base slot. Every one of them nonetheless needs the same three things done the same way:
/// (1) run mesh work on a bounded I/O pool instead of the calling thread, (2) restore the caller's
/// <see cref="AccessContext"/> before the mesh call is built, and (3) reach the mesh through the
/// canonical reactive surfaces. Getting any of those wrong fails silently — a denied write, a
/// wedged hub — so they live here once, and each store holds one of these.</para>
///
/// <para><b>No <c>async</c>, anywhere.</b> Everything here returns <see cref="IObservable{T}"/>.
/// The only <c>await</c> in the whole path is the one sealed inside <see cref="IIoPool"/>. Nothing
/// here captures a hub scheduler, so nothing here can park a hub action block or a grain turn.</para>
/// </summary>
/// <remarks>
/// See <c>Doc/Architecture/ControlledIoPooling.md</c>, <c>Doc/Architecture/AsynchronousCalls.md</c>,
/// <c>Doc/Architecture/AccessContextPropagation.md</c> and
/// <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
/// </remarks>
public sealed class MeshStoreAccess
{
    /// <summary>How long a bounded single-node probe waits before treating the node as absent.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly IIoPool pool;
    private readonly AccessService? accessService;
    private readonly AccessContext? principal;
    private readonly string owner;

    /// <summary>The hub whose workspace the store reads and writes through.</summary>
    public IMessageHub Hub { get; }

    /// <summary>
    /// Creates the access helper, resolving the dedicated <see cref="IoPoolNames.AgentStore"/> pool
    /// and capturing the CONSTRUCTING caller's identity.
    ///
    /// <para>🚨 The capture must happen here, not per call. MAF invokes a store from wherever its own
    /// loop happens to be running — a continuation thread with no <c>AsyncLocal</c> flow — so by the
    /// time the operation runs there is no ambient identity left to read. A store is constructed per
    /// agent round, on the hub, while the round's identity IS current; that is the only moment the
    /// real principal is observable. Every operation then re-stamps it around the mesh call. Without
    /// this, every store write would run with a null context and be denied by owner-side RLS — the
    /// silent-failure shape AccessContextPropagation.md documents.</para>
    /// </summary>
    /// <param name="hub">Hub supplying the workspace and the I/O pool registry.</param>
    /// <param name="owner">Owning store's type name; used to key its shared synced queries.</param>
    public MeshStoreAccess(IMessageHub hub, string owner)
    {
        Hub = hub;
        this.owner = owner;
        pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.AgentStore)
               ?? IoPool.Unbounded;
        accessService = hub.ServiceProvider.GetService<AccessService>();
        principal = accessService?.Context ?? accessService?.CircuitContext;
    }

    /// <summary>
    /// Runs a ONE-SHOT mesh operation — a single-node read, or a write — on the pool under the
    /// captured principal.
    ///
    /// <para><paramref name="work"/> is invoked ON the pool thread, INSIDE the identity scope, not at
    /// call time. That ordering is load-bearing: <c>MeshNodeStreamHandle.Update</c> and the mesh
    /// write primitives capture <c>AccessService.Context</c> when the observable is BUILT, so the
    /// switch has to be in scope while <paramref name="work"/> composes the chain.</para>
    ///
    /// <para>The observable must emit at least once and complete —
    /// <see cref="IIoPool.InvokeObservable{T}"/> holds a slot for that window. For a query, which
    /// never completes, use <see cref="Stream{T}"/>.</para>
    /// </summary>
    /// <typeparam name="T">Result element type.</typeparam>
    /// <param name="work">Factory composing the operation. Runs on the pool, under the principal.</param>
    public IObservable<T> Once<T>(Func<IObservable<T>> work) =>
        pool.InvokeObservable(_ =>
        {
            using var identity = accessService?.SwitchAccessContext(principal);
            return work();
        });

    /// <summary>
    /// Runs a LIVE mesh operation — the read side of CQRS: a query or a node stream, which re-emits
    /// and never completes.
    ///
    /// <para>🚨 It must NOT go through <see cref="Once{T}"/>.
    /// <see cref="IIoPool.InvokeObservable{T}"/> is one-shot — it holds a slot until the source
    /// COMPLETES and then emits the last value. Pointing it at a never-completing feed leaks a pool
    /// slot per call and collapses a live query into a single snapshot.
    /// <see cref="IIoPool.SubscribeThroughPool{T}"/> is the primitive for this shape: the SUBSCRIBE
    /// is pooled and drainable, the resulting subscription lives on.</para>
    ///
    /// <para>Callers that genuinely need one value (an SDK boundary that can only carry one) add
    /// their own <c>.Take(1)</c> — that decision belongs at the boundary, not here, so the live shape
    /// stays available to everyone else.</para>
    /// </summary>
    /// <typeparam name="T">Emission type.</typeparam>
    /// <param name="work">Factory composing the feed. Runs on the pool at subscribe, under the principal.</param>
    public IObservable<T> Stream<T>(Func<IObservable<T>> work) =>
        pool.SubscribeThroughPool(Observable.Defer(() =>
        {
            using var identity = accessService?.SwitchAccessContext(principal);
            return work();
        }));

    /// <summary>
    /// The LIVE authoritative read of one node — <c>GetMeshNodeStream(path)</c>, the shared per-path
    /// handle. Re-emits on every change; never the eventually-consistent query index.
    /// </summary>
    /// <param name="path">Full mesh path.</param>
    public IObservable<MeshNode> ReadNode(string path) => Hub.GetMeshNodeStream(path);

    /// <summary>
    /// A BOUNDED single-node read: the node, or <c>null</c> when it genuinely does not exist.
    ///
    /// <para>This is <c>hub.GetMeshNode</c>, the framework's one-shot read — deliberately NOT
    /// <c>GetMeshNodeStream(path).Take(1)</c> with a timeout. A missing node's per-node hub never
    /// activates, and the resulting subscription does not go quiet, it FAULTS with a NotFound
    /// <c>DeliveryFailure</c>; a timeout would never fire and the caller would see an exception
    /// where it expects "absent". <c>GetMeshNode</c> already encodes the distinction that matters:
    /// genuine absence is a <c>null</c> emission, an access denial stays an error, and a read that
    /// gave up stays a <see cref="TimeoutException"/> — "not found", "not allowed" and "too slow"
    /// are three different facts and callers get to tell them apart. Re-deriving that on top of the
    /// live handle would mean classifying delivery failures by hand.</para>
    ///
    /// <para>Use it only where a single value is genuinely required (an SDK boundary, an existence
    /// check); everything else stays on the live <see cref="ReadNode"/>.</para>
    /// </summary>
    /// <param name="path">Full mesh path.</param>
    public IObservable<MeshNode?> ProbeNode(string path) => Hub.GetMeshNode(path, ReadTimeout);

    /// <summary>
    /// The LIVE synced query — <c>hub.GetQuery(id, queries)</c>. Shares one upstream subscription per
    /// (id, user), applies per-user RLS at the source, replays the current snapshot on subscribe, and
    /// re-emits as the result set evolves. Emits whole <see cref="MeshNode"/>s (content included), so
    /// consumers never need a second read per row.
    /// </summary>
    /// <param name="queries">One or more mesh queries; the collection is their union.</param>
    public IObservable<IEnumerable<MeshNode>> Query(params string[] queries) =>
        // The id keys the shared cache; deriving it from the queries themselves keeps distinct query
        // sets in distinct caches without inventing a naming scheme to keep in sync.
        Hub.GetQuery($"{owner}:{string.Join("|", queries)}", queries);
}
