using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// IObservable wrapper that detects fire-and-forget callsites at runtime. If an
/// instance is garbage-collected without ever having <c>Subscribe</c> called on
/// it, a warning is logged via <see cref="ILoggerFactory"/> resolved from the
/// supplied <see cref="IServiceProvider"/>. This catches the cold-observable bug
/// where a caller invokes a side-effect-on-subscribe API (e.g.
/// <c>workspace.GetMeshNodeStream().Update(...)</c>) without subscribing — the
/// side effect silently never runs and the caller has no compile-time signal.
/// </summary>
internal sealed class RequireSubscribeObservable<T> : IObservable<T>
{
    private readonly IObservable<T> _inner;
    private readonly string _what;
    private readonly IServiceProvider _services;
    private int _subscribed;

    public RequireSubscribeObservable(IObservable<T> inner, string what, IServiceProvider services)
    {
        _inner = inner;
        _what = what;
        _services = services;
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        Interlocked.Exchange(ref _subscribed, 1);
        return _inner.Subscribe(observer);
    }

    ~RequireSubscribeObservable()
    {
        if (_subscribed != 0) return;
        try
        {
            var logger = _services.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.RequireSubscribe");
            logger?.LogWarning(
                "Fire-and-forget callsite detected: '{What}' returned a cold IObservable that was never subscribed — the side effect did NOT run. Add .Subscribe(_ => {{ }}, ex => logger.LogWarning(ex, ...)) at the callsite. See Doc/Architecture/AsynchronousCalls.md → 'Subscribe is mandatory'.",
                _what);
        }
        catch
        {
            // Finalizer must never throw — service provider may already be disposed.
        }
    }
}

/// <summary>
/// Reactive handle to a <see cref="MeshNode"/> for both reads and writes. The handle
/// is path-aware: with no path it targets the workspace's own hub MeshNode; with a
/// path matching the workspace's hub address it also targets own; otherwise it
/// targets the remote per-node hub via <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>.
/// Implements <see cref="IObservable{MeshNode}"/> so existing <c>.Where</c>/<c>.Select</c>
/// read consumers keep working unchanged. Writers call <see cref="Update"/> — which
/// returns an <see cref="IObservable{MeshNode}"/> that the caller MUST Subscribe to.
/// The Update side effect runs on Subscribe; errors flow to <c>OnError</c>. No
/// fire-and-forget at any callsite.
/// </summary>
public sealed class MeshNodeStreamHandle : IObservable<MeshNode>
{
    private readonly IWorkspace _workspace;
    private readonly string? _path;
    private readonly IMeshNodeStreamCache? _cache;
    private readonly bool _bypassCache;
    private readonly JsonSerializerOptions _jsonOptions;

    // Mesh-wide $type→CLR-Type resolver (see IMeshContentTypeRegistry). When Content arrives as a
    // bare JsonElement whose $type this workspace's options can't resolve (a dynamically-compiled
    // NodeType after a re-import), EnsureTypedContent re-types it from here instead of degrading.
    private readonly MeshWeaver.Mesh.Services.IMeshContentTypeRegistry? _contentTypeRegistry;

    // 🚨 How long the caller's write holds a PENDING hub.Observe callback for the OWNER's
    // PatchDataResponse before handing the wait over to LatePatchResponseRegistry. This is a
    // QUIESCING-BUDGET bound, not a verdict deadline: a responseSubjects entry held past a
    // couple of seconds is what the hub's Quiescing leak detector counts as a leaked callback
    // (QuiescingTimedOut — a hard test-teardown failure), which is the whole reason the late
    // watch is a registry plus a hub handler rather than a long detached Observe. See
    // LatePatchResponseRegistry.
    //
    // 🚨 IT IS NOT A SUCCESS BOUND — issue #2661. Until then, expiry EMITTED THE OPTIMISTIC
    // SNAPSHOT AND COMPLETED THE CALLER AS A SUCCESS, so a write the owner went on to refuse was
    // reported as saved. That is the fail-open: "saved" means the owner COMMITTED (it does not
    // mean the DB flushed, and it certainly does not mean two seconds elapsed with no bad news).
    // add and delete have always waited for exactly that verdict — DataChangeStatus.Committed,
    // or a real failure (UpdateDataPath / DeleteDataPath in MeshWeaver.Data) — and update was the
    // odd one out. It no longer is: expiry of this bound emits NOTHING and completes NOTHING; it
    // only moves the wait onto the registry, and the caller's terminal is the owner's verdict
    // wherever it arrives.
    //
    // 🚨 The cost, stated plainly because it is real: for a BUSY owner the caller now waits for
    // the owner's action-block queue to drain to this patch instead of being told "saved" at ~2s.
    // That is the maintainer's explicit call ("for update, it has to wait until queue drains").
    // The owner's own terminal is contractually bounded — the identity-gated ack watcher's 20s
    // Timeout plus IPostCommitFlush's 10s, plus RegisterOwnerDisposingNack for teardown
    // (MeshWeaver.Data/DataExtensions.cs) — which is precisely what LateResponseWatchBound (30s)
    // is documented to dominate, so that window is the caller's outer bound too.
    private static readonly TimeSpan UpdateResponseWaitBound = TimeSpan.FromSeconds(2);

    // 🚨 Slack added to LateResponseWatchBound before the caller's write is failed for SILENCE.
    // Now sourced from LatePatchResponseRegistry rather than restated here: this grace is what puts
    // the caller's outer write bound at 31s, and a test that waits on a write must sit strictly
    // ABOVE that to let the framework's OwnerUnreachable win the race and name the cause. A private
    // copy was invisible to the test tree, which independently authored the SAME 30s as
    // LateResponseWatchBound and so could never observe the verdict (#2819).
    private static readonly TimeSpan VerdictBoundGrace = LatePatchResponseRegistry.VerdictBoundGrace;

    // 🚨 Retry budget for the provably-safe NACK cases — the ones where the owner STATED the
    // patch never applied: OwnerDisposing, OwnerNotReady, and Conflict. Each re-enqueue re-runs
    // the ORIGINAL update lambda against the freshest state and re-diffs, so a superseding write
    // makes it a no-op;
    // two re-enqueues cover a re-enqueue that itself lands on a disposing fresh activation
    // (recycle churn). NEVER retried: silence (a busy owner still applies the original
    // patch) and every other NACK code (validation/RLS/NotFound are terminal verdicts).
    private const int MaxOwnerDisposingReenqueues = 2;

    // 🚨 How long a CONFLICT re-attempt waits for this hub's mirror to carry state the owner has
    // not already refused. Not a retry interval and not a backoff — it is the bound on ONE wait
    // for a fact that is already on its way: the owner committed the winning write BEFORE it
    // NACK'd us, so the newer state is in flight to the mirror we are about to read. The wait is
    // for the arrival, not for a repeat. Generous against a sub-second propagation, and short
    // enough to sit well inside the caller's own budget.
    private static readonly TimeSpan ConflictRebaseBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 🚨 The emission a (re)attempt rebuilds its patch from — and the whole of the #1910 fix.
    ///
    /// <para><b>The defect.</b> A cross-hub <c>stream.Update</c> reads this hub's MIRROR, runs the
    /// lambda against it, and ships a merge patch carrying the base values it diffed against. The
    /// owner three-way-merges: any key whose live value has moved since that base is REFUSED, and
    /// when nothing lands it answers <c>Conflict</c> — "re-read and re-apply". <c>UpdateRemote</c>
    /// does re-enqueue on that verdict, and it does re-run the lambda rather than re-post the
    /// patch. But it re-ran the lambda against <c>mirror.Take(1)</c>, i.e. against whatever the
    /// mirror holds AT THAT INSTANT — which, immediately after a NACK, is very often still the
    /// version the owner just refused. The lambda then recomputes the same values from the same
    /// base, <c>ExtractBaseValues</c> produces the same base, and the owner refuses identically.
    /// Three attempts, three identical patches, then a terminal <c>MeshNodeStreamException</c> and
    /// the caller's write is gone. A retry whose input cannot change is not a retry.</para>
    ///
    /// <para><b>The rule.</b> A Conflict means the owner is provably AHEAD of
    /// <paramref name="refusedBaseVersion"/> — it minted a version for the write that beat us
    /// before it answered us — so the state that makes the re-attempt converge already exists and
    /// is on its way here. Wait for it, then rebase on it.</para>
    ///
    /// <para><b>Conflict only.</b> <c>OwnerDisposing</c> / <c>OwnerNotReady</c> re-enqueues pass
    /// <c>0</c> and are unaffected: those say the patch never reached a merge at all, so no newer
    /// version exists and waiting for one would burn the bound for nothing.</para>
    ///
    /// <para><b>Never parks.</b> If the mirror does not advance within
    /// <see cref="ConflictRebaseBound"/> the re-attempt proceeds against what it has — exactly the
    /// previous behaviour — and <paramref name="onStaleMirror"/> says so, so "the write was
    /// refused AND the mirror never caught up" is a reported fact rather than a silent
    /// re-refusal. This is why the change can only improve on the old behaviour: it converges
    /// where the old shape could not, and degrades to the old shape where it cannot.</para>
    ///
    /// <para>Static, with the mirror and the scheduler as seams, so the re-read rule is asserted
    /// deterministically — no hub, no cluster, no wall clock.</para>
    /// </summary>
    /// <param name="mirror">This hub's view of the node — replays its current state to a new
    /// subscriber and emits again as the owner's commits arrive.</param>
    /// <param name="refusedBaseVersion">The version the owner refused, or <c>0</c> for a first
    /// attempt (and for the non-staleness re-enqueue codes), which reads the mirror as before.</param>
    /// <param name="onStaleMirror">Called with <paramref name="refusedBaseVersion"/> when the
    /// bound elapsed and the re-attempt fell back to un-advanced state.</param>
    /// <param name="scheduler">Timer seam for tests.</param>
    internal static IObservable<MeshNode> RebaseSource(
        IObservable<MeshNode> mirror,
        long refusedBaseVersion,
        Action<long> onStaleMirror,
        IScheduler? scheduler = null)
        => refusedBaseVersion <= 0
            // A first attempt reads the mirror exactly as it always did — the ordinary write path
            // gains no filter, no timer and no second subscription.
            ? mirror.Take(1)
            : mirror
                .Where(node => node.Version > refusedBaseVersion)
                .Take(1)
                .Timeout(
                    ConflictRebaseBound,
                    Observable.Defer(() =>
                    {
                        onStaleMirror(refusedBaseVersion);
                        return mirror.Take(1);
                    }),
                    scheduler ?? Scheduler.Default)
                // 🚨 An EMPTY completion must not reach the caller. Filtering introduces a
                // completion the un-filtered shape could not produce: a mirror that ends (its hub
                // torn down) while holding only the refused version completes this source with no
                // value, the write's observer is never called, and the caller waits on a pipeline
                // that has already finished. A source that cannot answer must SAY so — the same
                // rule the compile pipeline's DefaultIfEmpty totality guard applies.
                .Select(node => (MeshNode?)node)
                .DefaultIfEmpty(null)
                .SelectMany(node => node is null
                    ? Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Update aborted: the owner refused this write as stale at version "
                        + $"{refusedBaseVersion} and the mirror ended before carrying anything "
                        + "newer, so there is no state to re-apply against. The write did NOT "
                        + "land; re-issue it."))
                    : Observable.Return(node));

    /// <summary>
    /// 🚨 The base a QUEUED write diffs against — issues #2305 / #2291.
    ///
    /// <para><b>The defect.</b> <c>MeshNodeStreamCache</c> funnels every write to a path through one
    /// per-path serial queue, and its own contract said "the next queued Update sees post-patch state
    /// via the local Handle". It did not. The queue advanced on the write's LOCAL emit — never on the
    /// owner's ECHO — while the next write read <c>mirror.Take(1)</c>, i.e. the mirror as it stands.
    /// Under load the echo has not arrived, so write N+1 ships a base that PREDATES write N. The owner
    /// then three-way-merges an already-applied value against a base it has moved past, sees a string
    /// changed on both sides with overlapping edits, and REFUSES the leaf — keeping the value write N
    /// wrote.</para>
    ///
    /// <para><b>The residual, and why the symptom came back (#2346).</b> This hand-off closes the gap
    /// only when there IS a pending state to hand forward, and the first fix published one solely on
    /// an ack that arrived inside <c>UpdateResponseWaitBound</c>. On a busy owner the ack does not,
    /// the caller takes its optimistic emit — and THAT released the queue slot. So on exactly the
    /// runs where the owner is slow, the successor got no base at all and the defect above reappeared
    /// verbatim. The slot is now released by the hand-off itself (see <c>onLocalState</c>), and a LATE
    /// ack publishes just like an early one.</para>
    ///
    /// <para><b>Why that is not a concurrent-writer conflict.</b> The two writes are the SAME mirror's,
    /// strictly ordered by the queue. The staleness is self-inflicted: the writer superseded its own
    /// base and then told the owner otherwise. The owner is right to refuse a stale base — the writer is
    /// wrong to ship one.</para>
    ///
    /// <para><b>The symptom it produced.</b> An agent round's response cell. Push 1 writes
    /// <c>Text = "Generating response..."</c>; the terminal push writes <c>Text</c> = the answer,
    /// <c>Status = Completed</c> and <c>Summary</c>. With a stale base only <c>Text</c> conflicts, so
    /// <c>Status</c> and <c>Summary</c> land and <c>Text</c> is refused — a COMPLETED cell still reading
    /// "Generating response..." while the answer sits in <c>Summary</c>. Partial per-field resolution is
    /// intentional (<c>PatchDataRequestTest</c>, <c>CrossHubPatchAtomicityTest</c>) and is NOT what is
    /// wrong here; the stale base is. More generally this froze a streaming cell's text at the first
    /// chunk that landed, for as long as the echo lagged the write rate.</para>
    ///
    /// <para><b>The rule.</b> While the mirror has not advanced past the state the previous queued write
    /// produced, that state IS this mirror's freshest knowledge of the node — diff against it. The
    /// instant the mirror carries anything newer (the echo, or ANOTHER writer's commit — the owner mints
    /// <c>Version + 1</c> on every applied change) the mirror wins again and a genuine cross-mirror
    /// conflict is detected exactly as before. So this can only remove FALSE conflicts: it never hides a
    /// real one, and it never suppresses a base.</para>
    ///
    /// <para>Static, with the mirror and the pending state as seams, so the rule is asserted
    /// deterministically — no hub, no cluster, no wall clock.</para>
    /// </summary>
    /// <param name="source">The mirror emission a write would otherwise diff against.</param>
    /// <param name="pendingSelfWrite">The node the PREVIOUS write on this path's serial queue computed
    /// locally, or <c>null</c> when there is none (a first write, a write after an error, or a retry —
    /// a CONFLICT re-attempt must re-read the owner's state, never the value it just superseded).</param>
    internal static IObservable<MeshNode> PatchBaseSource(
        IObservable<MeshNode> source,
        MeshNode? pendingSelfWrite)
        => pendingSelfWrite is null
            ? source
            : source.Select(node =>
                // Same node, and the mirror has not yet carried anything past what we wrote ⇒ our own
                // pending state is the freshest base this mirror has. Path equality is a guard against
                // a mis-keyed hand-off, not an expected case — the queue is per path.
                string.Equals(node.Path, pendingSelfWrite.Path, StringComparison.OrdinalIgnoreCase)
                && pendingSelfWrite.Version >= node.Version
                    ? pendingSelfWrite
                    : node);

    internal MeshNodeStreamHandle(IWorkspace workspace, string? path = null,
        IMeshNodeStreamCache? cache = null, bool bypassCache = false)
    {
        _workspace = workspace;
        _path = path;
        _cache = cache;
        // 🚨 The ONLY handle allowed to open the raw cross-hub MeshNode sync
        // stream (GetRemoteStream) is the cache's own upstream handle. Every
        // other non-own handle MUST route through the cache so reads and writes
        // share the single live mirror. A non-own handle that is neither cached
        // nor the bypass handle is a misconfiguration — fail loud, never open a
        // second divergent stream.
        _bypassCache = bypassCache;
        _jsonOptions = workspace.Hub.JsonSerializerOptions;
        _contentTypeRegistry = workspace.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IMeshContentTypeRegistry>();
    }

    private bool IsOwn => _path is null
        || string.Equals(_path, _workspace.Hub.Address.Path, StringComparison.Ordinal)
        || string.Equals(_path, _workspace.Hub.Address.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Resolves this handle's stream AND — for a REMOTE path — declares this caller as a holder
    /// of it (see <c>Workspace.AcquireRemoteStreamUnchecked</c>). Dispose the returned lease
    /// together with whatever subscription/operation used the stream: that declaration is what
    /// lets a change-feed-evicted stream be reclaimed the moment its last holder leaves, instead
    /// of being parked for the process lifetime (#1324 — every cross-hub write to a subscribed
    /// path otherwise left a client `sync/` hub and its owner-side twin behind). Own-hub streams
    /// are local reductions owned by the data source; they take no lease.
    /// </summary>
    private (ISynchronizationStream<MeshNode> Stream, IDisposable Lease) AcquireStream()
    {
        if (IsOwn)
            return (_workspace.GetStream(new MeshNodeReference())
                    ?? throw new InvalidOperationException(
                        "MeshNode stream is not available — the workspace has no MeshNodeReference reducer."),
                Disposable.Empty);
        // 🚨 Open the remote MeshNode subscription under the system identity.
        // Reading MeshNode content is infrastructure (routing, path resolution,
        // permission probing, NodeType activation, satellite enumeration). The
        // user-rights gate lives at the APPLICATION layer where the value is
        // consumed (handlers, layout areas) — not at the sync-stream seam.
        // Without this, the SubscribeRequest is stamped with whatever ambient
        // identity happens to be on the thread (often `sync/<streamId>` for
        // workspace emission threads, or null), and the owner's
        // AccessControlPipeline denies because no AccessAssignment exists for
        // sync hub addresses — symptom: "user 'sync/…' lacks Read permission".
        // Matches MeshNodeStreamCache and PathResolutionService, both of which
        // also open MeshNode reads under ImpersonateAsSystem.
        var accessService = _workspace.Hub.ServiceProvider.GetService<AccessService>();
        using (accessService?.ImpersonateAsSystem())
        {
            // 🚨 Sanctioned escape hatch: the public GetRemoteStream<MeshNode> logs a
            // discouraged-usage warning. This cache/bypass handle is the sanctioned hot
            // path, so it opens the raw single-node remote reduce via the internal
            // unchecked overload — no warning noise.
            return ((Workspace)_workspace).AcquireRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
                new Address(_path!), new MeshNodeReference());
        }
    }

    /// <summary>
    /// Idle-release seam for the shared mesh-node cache (MeshNodeStreamCache): atomically
    /// detaches — WITHOUT disposing — every workspace-cached remote sync stream this handle
    /// reads/writes through (the live cache entry plus any change-feed-evicted predecessors
    /// still parked on the workspace). After the detach, a concurrent read/write for the same
    /// path builds a FRESH upstream, so the caller can safely dispose the returned streams
    /// once it has re-verified (under its own entry gate) that no subscriber remains; on a
    /// lost race it MUST hand them back via <see cref="ReparkUpstreams"/>. Disposing a
    /// detached stream posts <c>UnsubscribeRequest</c> to the owner and tears down the
    /// per-stream <c>sync/</c> hub — the client-side heartbeat dies with it. Empty for
    /// own-hub handles (nothing remote to release).
    /// </summary>
    internal IReadOnlyList<ISynchronizationStream> DetachUpstreams()
    {
        if (IsOwn || _workspace is not Workspace workspace)
            return [];
        return workspace.DetachRemoteStreams(new Address(_path!), new MeshNodeReference());
    }

    /// <summary>
    /// Returns streams obtained from <see cref="DetachUpstreams"/> to the workspace's
    /// parked-stream set when the caller's release race was lost (a consumer re-attached
    /// between detach and the final zero-subscriber check). The workspace re-owns their
    /// lifetime — they stay live for attached subscribers and are disposed by a later
    /// successful release or at workspace disposal.
    /// </summary>
    internal void ReparkUpstreams(IReadOnlyList<ISynchronizationStream> streams)
    {
        if (streams.Count == 0 || _workspace is not Workspace workspace)
            return;
        workspace.ParkRemoteStreams(streams);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 🚨 Every emission passes through <see cref="EnsureTypedContent"/> so the
    /// subscriber always sees a typed <see cref="MeshNode.Content"/> — never a
    /// raw <see cref="JsonElement"/>. Different data sources store Content in
    /// different shapes (InMemory keeps typed instances, file-system / Postgres
    /// round-trip through JSON serialization and land as JsonElement). Without
    /// the boundary conversion, every callsite that pattern-matches
    /// <c>node.Content is MyType t</c> would have to remember to re-deserialise,
    /// and the silent <c>?? new MyType()</c> fallback (writing a default-valued
    /// content over a real one) is the bug class behind the CheckInbox /
    /// AppendUserInput silent-Status-reset failure mode. Round-trip is no-op
    /// when Content is already typed.
    /// </remarks>
    public IDisposable Subscribe(IObserver<MeshNode> observer)
    {
        try
        {
            // ♻️ A TRANSIENT NODE PROBE HAS NO MESH NODE — so its OWN-node stream is EMPTY, and
            // that is the truthful answer rather than a failure. This is the THIRD own-node read
            // seam and the last one that was not saying it: `GetMeshNodeOutcome` answers a probe's
            // own address `Absent` (#2468) and `MeshNodeStreamCache.GetStreamRaw` answers an empty
            // stream (#2894); this one — the reduce-backed own-node stream every own-node WATCHER
            // subscribes to — threw instead.
            //
            // What it threw was `InvalidOperationException("Failed to create stream")`, from
            // `Workspace.ReduceLocalStream`: the probe is built `startDataSources: false`, so the
            // own-MeshNode reduce has no started data source to reduce from and `ReduceStream`
            // returns null. Watchers installed by a NodeType's own HubConfiguration —
            // `WatchControlPlane` on every Activity-shaped type, `BuildNodeType`'s claim arbiter —
            // then reported that as a TRANSIENT fault (a bare InvalidOperationException matches
            // none of `SubscribeWithReEstablish`'s three terminal classifiers) and armed a 1 s
            // re-establish against a hub that was already disposing: an ERROR-level line per swept
            // NodeType on every mesh start, about a non-event. Where the reduce DID succeed the
            // cost was the other face — `SynchronizationStream`'s constructor opening a `sync/`
            // sub-hub into the probe's own disposal, the `ProbeHubCostTest` warning
            // `startDataSources: false` exists to remove. Systemorph/MeshWeaver#2990.
            //
            // Completing empty is not a swallow: there is no node at a probe's synthetic address
            // and there never will be (see TransientProbeAddresses), so a reader learns exactly
            // what is true, immediately, instead of a diagnosis that names no reference and no
            // owner. Scoped to the probe's OWN address — a probe reading any REAL path is
            // untouched, and so is every write (Update never routes through AcquireStream).
            if (IsOwn && _workspace.Hub.Configuration.Get<TransientNodeProbe>() is not null)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var typedObserver = new TypedContentObserver(observer, _jsonOptions, _contentTypeRegistry);
            // 🚨 Cross-hub reads route through IMeshNodeStreamCache (when one is
            // registered): one shared process-wide upstream subscription per
            // path. The cache holds the upstream alive; ad-hoc GetRemoteStream
            // here would open a separate handle, multiplying subscriptions and
            // making writes invisible to readers of the cached stream. See
            // Doc/GUI/ItemTemplateMeshNodeStreamBinding.
            if (_cache is not null && !IsOwn && _path is not null)
                return new CompositeDisposable(
                    _cache.GetStream(_path, _jsonOptions)
                        .Where(n => n is not null)
                        .Subscribe(typedObserver),
                    // The observer holds the late-retype wait (see TypedContentObserver); it must
                    // die with the subscription, not with the last emission.
                    typedObserver);

            // 🚨 The lease lives exactly as long as this subscription. The shared mesh-node
            // cache's hydration comes through here, so its entry IS the declared holder of the
            // path's upstream for the entry's whole life — which is what makes every OTHER
            // (write-scoped) stream for the same path reclaimable on eviction.
            var (stream, lease) = AcquireStream();
            try
            {
                var subscription = stream
                    .Where(change => change.Value != null)
                    .Select(change => change.Value!)
                    .Subscribe(typedObserver);
                return new CompositeDisposable(subscription, lease, typedObserver);
            }
            catch
            {
                // A lease the caller never receives can never be released, and an
                // unreleased lease pins the mirror against reclamation forever.
                lease.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            observer.OnError(ex);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Observer that round-trips <see cref="MeshNode.Content"/> through the
    /// workspace's <see cref="JsonSerializerOptions"/> when it arrives as a
    /// raw <see cref="JsonElement"/>. No-op when Content is already typed.
    /// Applied at the <see cref="MeshNodeStreamHandle"/> boundary so every
    /// subscriber sees the same typed shape regardless of how the underlying
    /// data source stores the value.
    /// <para>
    /// When <see cref="EnsureTypedContent"/> throws (deserialization /
    /// missing TypeRegistry entry), the exception is routed to
    /// <see cref="IObserver{T}.OnError"/> so subscribers see the typed
    /// <see cref="MeshNodeStreamException"/> in their error handler — never
    /// up the producer stack where it would tear down unrelated streams.
    /// </para>
    /// </summary>
    /// <remarks>
    /// 🚨 <b>A degrade here is PROVISIONAL, never terminal (Systemorph/MeshWeaver#2952).</b>
    /// <see cref="EnsureTypedContent"/> can only answer with what is registered AT THAT INSTANT,
    /// and for an in-mesh NodeType — <c>Source/*.cs</c> compiled by Roslyn at RUNTIME — "not
    /// registered" is a state that ENDS a few hundred milliseconds later, when the compile calls
    /// <c>MeshDataSource.WithContentType</c>. Nothing observed that, so a subscription that opened
    /// on the losing side of the race held an untyped <see cref="JsonElement"/> for the life of the
    /// hub: the node itself never changes, so no further emission ever arrives to re-convert, the
    /// view renders empty and a reactive wait for the typed shape times out. So when the
    /// conversion degrades, this observer WAITS on
    /// <see cref="MeshWeaver.Mesh.Services.IMeshContentTypeRegistry.Registrations"/> and re-emits
    /// the same node typed the moment a registration makes it resolvable.
    ///
    /// <para>It is a subscription to the actual EVENT, not a poll: no timer, no interval, no
    /// re-subscribe. It arms only on the already-degraded path (so the typed hot path pays
    /// nothing), at most one wait is armed at a time, and it disarms on the next emission, on a
    /// terminal, and on dispose.</para>
    /// </remarks>
    private sealed class TypedContentObserver(
        IObserver<MeshNode> inner, JsonSerializerOptions jsonOptions,
        MeshWeaver.Mesh.Services.IMeshContentTypeRegistry? contentTypeRegistry = null)
        : IObserver<MeshNode>, IDisposable
    {
        // 🚨 A late re-type is delivered off the registering thread (the registry's documented
        // contract), so it can race a stream emission. Rx's own gate serialises the two — the
        // IObserver grammar is not optional, and hand-rolling the lock here would be one more
        // bespoke primitive to get wrong.
        private readonly IObserver<MeshNode> _out = System.Reactive.Observer.Synchronize(inner);

        // The ONE armed wait. Assigning a new value disposes the previous, so a fresh emission
        // supersedes the node the old wait was holding.
        private readonly SerialDisposable _lateRetype = new();

        public void OnNext(MeshNode value)
        {
            MeshNode typed;
            try
            {
                typed = EnsureTypedContent(value, jsonOptions, contentTypeRegistry);
            }
            catch (System.Exception ex)
            {
                _lateRetype.Disposable = Disposable.Empty;
                _out.OnError(ex);
                return;
            }
            // Emit FIRST, then arm: the wait re-checks immediately on subscribe (see below), and
            // emitting first is what guarantees the subscriber can never see the typed value
            // before the untyped one it supersedes.
            _out.OnNext(typed);
            ArmLateRetype(value, typed);
        }

        /// <summary>
        /// Arms (or clears) the wait for a content type that is not registered YET. Clearing on a
        /// successful conversion matters as much as arming: a node that types fine must not leave
        /// a subscription behind on every emission.
        /// </summary>
        private void ArmLateRetype(MeshNode raw, MeshNode typed)
        {
            if (contentTypeRegistry is null || typed.Content is not JsonElement degraded)
            {
                _lateRetype.Disposable = Disposable.Empty;
                return;
            }

            // Read the stored discriminator ONCE, here, so the per-registration filter below is a
            // string compare and never touches the document again.
            var discriminator = degraded.ValueKind == JsonValueKind.Object
                && degraded.TryGetProperty("$type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()
                    : null;

            // 🚨 Neither route has an INPUT: no stored discriminator and no NodeType means
            // TryRecoverForNodeType has nothing to key on, now or ever. Arming here would leave a
            // wait per degraded subscriber that no registration can ever complete — dead weight for
            // the life of the subscription. Content this shape is free-form JSON by design (see
            // ContentDiscriminatorValidator: "content WITHOUT a $type stays legal"), so this is a
            // real state, not a corner case.
            if (discriminator is null && string.IsNullOrEmpty(raw.NodeType))
            {
                _lateRetype.Disposable = Disposable.Empty;
                return;
            }

            _lateRetype.Disposable = contentTypeRegistry.Registrations
                // 🚨 String compares only, BEFORE any deserialization. A boot registers every
                // content type in the mesh, and without this each one would re-deserialize this
                // node's whole JsonElement in every degraded subscription — N×M for nothing. The
                // predicate mirrors exactly the two routes TryRecoverForNodeType can take, so a
                // registration it drops is one that provably could not have resolved this node.
                .Where(r => CouldResolve(r, raw, discriminator))
                .Select(_ => System.Reactive.Unit.Default)
                // 🚨 StartWith closes the gap between the conversion above and this Subscribe: a
                // registration landing in that window would otherwise be missed, and the wait
                // would hold out for a LATER one that may never come. It runs on the immediate
                // scheduler, so the re-check is synchronous and — on the ordinary path, where
                // nothing registered in the window — costs one failed lookup. It is placed AFTER
                // the filter deliberately: the filter answers "could THIS registration matter",
                // and the gap re-check has no registration to judge.
                .StartWith(System.Reactive.Unit.Default)
                .Select(_ => Retype(raw))
                .Where(n => n is not null)
                .Take(1)
                .Subscribe(
                    n => _out.OnNext(n!),
                    // NOT swallowed. Two things can fault here and both must be visible: the
                    // notification channel (this node will now never be re-typed) and the
                    // conversion itself (the same MeshNodeStreamException the primary path raises,
                    // reaching the subscriber the same way). A silent never-recovers is precisely
                    // the defect this wait exists to end, so it must not be reintroduced here.
                    ex => _out.OnError(ex));
        }

        /// <summary>
        /// Could <paramref name="registration"/> possibly make this node resolvable? It mirrors the
        /// TWO routes <c>TryRecoverForNodeType</c> takes, and nothing else:
        /// <list type="number">
        /// <item>the EXACT route — the registration was keyed on this node's own
        /// <see cref="MeshNode.NodeType"/> (matched case-insensitively, as the registry's NodeType
        /// map is keyed);</item>
        /// <item>the NAME route — the registered type's short or full name IS the stored
        /// <c>$type</c> (matched ordinally, as <c>ClaimDiscriminator</c> keys it).</item>
        /// </list>
        ///
        /// <para>🚨 It must stay a superset of what could resolve, never a guess at what will: a
        /// registration wrongly dropped here re-creates the exact defect this wait exists to end.
        /// A node with neither a NodeType nor a <c>$type</c> matches nothing — correctly, because
        /// neither route could ever have answered for it. The conversion still runs afterwards and
        /// the answer is still kept only when it is genuinely typed; this only decides whether it
        /// is worth ASKING.</para>
        /// </summary>
        private static bool CouldResolve(
            MeshWeaver.Mesh.Services.MeshContentTypeRegistration registration,
            MeshNode raw,
            string? discriminator)
            => (registration.NodeTypePath is { Length: > 0 } path
                    && raw.NodeType is { Length: > 0 } nodeType
                    && string.Equals(path, nodeType, StringComparison.OrdinalIgnoreCase))
               || (discriminator is not null
                    && (string.Equals(registration.ContentType.Name, discriminator, StringComparison.Ordinal)
                        || string.Equals(registration.ContentType.FullName, discriminator, StringComparison.Ordinal)));

        /// <summary>Re-runs the conversion; null when it still degrades. Throws exactly what the
        /// primary path throws — the Subscribe above routes it to the subscriber.</summary>
        private MeshNode? Retype(MeshNode raw)
        {
            var retyped = EnsureTypedContent(raw, jsonOptions, contentTypeRegistry);
            return retyped.Content is JsonElement ? null : retyped;
        }

        public void OnError(Exception error)
        {
            _lateRetype.Disposable = Disposable.Empty;
            _out.OnError(error);
        }

        public void OnCompleted()
        {
            _lateRetype.Disposable = Disposable.Empty;
            _out.OnCompleted();
        }

        public void Dispose() => _lateRetype.Dispose();
    }

    /// <summary>
    /// Deserialises <paramref name="node"/>'s Content if it arrived as a
    /// raw <see cref="JsonElement"/>. Pass-through when Content is null or
    /// already typed. Uses <see cref="JsonSerializerOptions"/>'s polymorphic
    /// <c>$type</c> discriminator to land on the concrete domain type
    /// (e.g. <c>MeshThread</c>, <c>NodeTypeDefinition</c>).
    /// <para>
    /// 🚨 Throws <see cref="MeshNodeStreamException"/> with
    /// <see cref="MeshNodeErrorCode.Deserialization"/> when deserialization
    /// fails — the diagnostic carries the (truncated) raw JSON and the
    /// discriminator value so callers can pinpoint the missing TypeRegistry
    /// entry. The previous swallow-and-return-untyped behaviour silently
    /// fed JsonElement back to subscribers, which then fell back to
    /// <c>node.Content as MyType ?? new MyType()</c> and overwrote every
    /// other field on the next stream.Update — the silent-corruption bug
    /// class behind CheckInbox / AppendUserInput / ThreadStreamingIdentity
    /// flakes. Loud failure here is the contract: subscribers get OnError
    /// with the typed exception; the GUI layout-area boundary renders a
    /// typed error card; tests can assert on <c>Error.Code</c>.
    /// </para>
    /// </summary>
    internal static MeshNode EnsureTypedContent(
        MeshNode node, JsonSerializerOptions jsonOptions,
        MeshWeaver.Mesh.Services.IMeshContentTypeRegistry? contentTypeRegistry = null)
    {
        if (node.Content is JsonElement je)
        {
            try
            {
                var deserialized = je.Deserialize<object>(jsonOptions);
                // Deserialize<object> degrades BACK to a JsonElement when these options' (frozen)
                // TypeRegistry can't resolve the $type — a dynamically-compiled NodeType whose
                // registration lives only in another hub's options. Re-type it from the mesh-wide
                // content-type registry (the reimport-renders-empty cure) before handing subscribers
                // an untyped value they would silently `as MyType ?? new MyType()`.
                if (deserialized is JsonElement degraded && contentTypeRegistry is not null)
                {
                    var recovered = contentTypeRegistry.TryRecoverForNodeType(node.NodeType, degraded, jsonOptions);
                    if (recovered is not null)
                        return node with { Content = recovered };
                }
                return node with { Content = deserialized };
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new MeshNodeStreamException(BuildDeserializationError(node, je, ex), ex);
            }
            catch (System.NotSupportedException ex)
            {
                // JsonSerializer raises NotSupportedException when the
                // polymorphic discriminator names a type that isn't
                // registered in the consumer hub's options — the recurring
                // "type 'X' is not registered in this hub's TypeRegistry"
                // footgun. Try the mesh-wide registry first (it may own the
                // dynamically-compiled type this hub's options lack); only if
                // it can't resolve do we surface loudly with the raw JSON
                // snippet so the caller sees which discriminator is missing.
                var recovered = contentTypeRegistry?.TryRecoverForNodeType(node.NodeType, je, jsonOptions);
                if (recovered is not null)
                    return node with { Content = recovered };
                throw new MeshNodeStreamException(BuildDeserializationError(node, je, ex), ex);
            }
        }
        return node;
    }

    private static MeshNodeError BuildDeserializationError(
        MeshNode node, JsonElement je, System.Exception ex)
    {
        // Truncate the raw JSON snippet to keep diagnostics readable while
        // still preserving the discriminator + first few fields — usually
        // enough to identify which TypeRegistry entry is missing.
        const int maxJsonChars = 400;
        var raw = je.GetRawText();
        var snippet = raw.Length <= maxJsonChars ? raw : raw[..maxJsonChars] + "…";
        var discriminator = je.ValueKind == JsonValueKind.Object && je.TryGetProperty("$type", out var t)
            ? t.GetString() ?? "<null>"
            : "<no $type>";
        return new MeshNodeError(
            MeshNodeErrorCode.Deserialization,
            node.Path ?? "<unknown>",
            $"Failed to deserialize MeshNode.Content (discriminator $type='{discriminator}'): {ex.Message}",
            snippet);
    }

    /// <summary>
    /// Serialises <paramref name="node"/>'s Content to a <see cref="JsonElement"/>
    /// using the workspace's <see cref="JsonSerializerOptions"/>. Pass-through
    /// when Content is null or already a JsonElement. Used on the outbound
    /// path of <see cref="Update"/> so the patch the framework computes on the
    /// wire is self-describing (the <c>$type</c> discriminator is written by
    /// the caller's TypeRegistry-aware options, which the cache hub may not
    /// have).
    /// </summary>
    internal static MeshNode EnsureSerialisedContent(MeshNode node, JsonSerializerOptions jsonOptions)
    {
        if (node.Content is null or JsonElement)
            return node;
        return node with
        {
            Content = JsonSerializer.SerializeToElement(node.Content, jsonOptions)
        };
    }

    /// <summary>
    /// 🚨 <b>THE typed write.</b> Applies <paramref name="update"/> to the node's content read as
    /// <typeparamref name="TContent"/> — <b>the caller names the type</b>, so nothing has to guess
    /// which CLR type a <c>$type</c> discriminator means.
    ///
    /// <para><b>Why this exists.</b> The untyped <see cref="Update(Func{MeshNode, MeshNode})"/> hands
    /// the lambda a <see cref="MeshNode"/> and every writer then re-derives the content type itself,
    /// overwhelmingly as <c>node.Content as TContent ?? new TContent()</c>. That idiom is a silent
    /// data-loss bug: whenever the content arrives as JSON the cast yields <c>null</c>, the
    /// <c>?? new()</c> materialises a DEFAULT record, and the write persists those defaults over
    /// every field the caller never meant to touch (the CheckInbox / AppendUserInput
    /// Status-reset class). Making the type a TYPE ARGUMENT removes the guess at the root:
    /// <see cref="MeshNodeContentExtensions.ContentAs{T}"/> converts a typed instance, a degraded
    /// <see cref="JsonElement"/>, an as-written <c>JsonNode</c>, or a same-named record from another
    /// build — all into the <typeparamref name="TContent"/> the caller asked for, with no
    /// name→Type lookup anywhere.</para>
    ///
    /// <para>🚨 <b>Unconvertible content FAILS THE WRITE — it never writes a default.</b>
    /// <see cref="MeshNodeContentExtensions.ContentAs{T}"/> returns <c>null</c> for unconvertible
    /// content because a READ must stay bad-data tolerant; a WRITE must not, or the caller's own
    /// content is what gets destroyed. So a node whose Content is present but cannot be read as
    /// <typeparamref name="TContent"/> faults the returned observable with a
    /// <see cref="MeshNodeStreamException"/> (<see cref="MeshNodeErrorCode.Deserialization"/>)
    /// carrying the node path, the actual runtime type and a JSON excerpt — the write does not
    /// happen.</para>
    ///
    /// <para>Content that is simply ABSENT (<c>Content is null</c>) is not an error and not
    /// representable here — use the
    /// <see cref="Update{TContent}(Func{MeshNode, TContent, MeshNode})"/> overload, whose
    /// <c>null</c> means exactly and only "no content yet".</para>
    /// </summary>
    /// <typeparam name="TContent">The content type the caller knows this node carries.</typeparam>
    /// <param name="update">Receives the current content, returns the replacement.</param>
    public IObservable<MeshNode> Update<TContent>(Func<TContent, TContent> update)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(update);
        return Update(node => node with { Content = update(RequireContent<TContent>(node)) });
    }

    /// <summary>
    /// The typed write for a lambda that also needs the <see cref="MeshNode"/> itself (to set
    /// Name/NodeType/… alongside Content, or to create the content when there is none yet).
    /// Same contract as <see cref="Update{TContent}(Func{TContent, TContent})"/> with one
    /// difference, and it is the important one:
    ///
    /// <para>🚨 <b><c>null</c> means ABSENT, never "could not be read".</b> A node with no content
    /// yields <c>null</c> so the caller can initialise it; content that is PRESENT but not
    /// convertible to <typeparamref name="TContent"/> faults the observable with
    /// <see cref="MeshNodeStreamException"/> instead of quietly arriving as <c>null</c>. Conflating
    /// the two is what turns "the content did not deserialise" into "the content was empty, write
    /// a fresh one" — the write that destroys the real record.</para>
    /// </summary>
    /// <typeparam name="TContent">The content type the caller knows this node carries.</typeparam>
    /// <param name="update">Receives the node and its content (<c>null</c> only when absent).</param>
    public IObservable<MeshNode> Update<TContent>(Func<MeshNode, TContent?, MeshNode> update)
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(update);
        return Update(node => update(node, ResolveContent<TContent>(node)));
    }

    /// <summary>
    /// Content as <typeparamref name="TContent"/> for a WRITE: <c>null</c> only when Content is
    /// absent; unconvertible content throws (see the overload docs).
    /// </summary>
    private TContent? ResolveContent<TContent>(MeshNode node) where TContent : class
    {
        if (node.Content is null)
            return null;
        // No logger: an unconvertible value here is about to become an exception carrying the same
        // diagnosis, so ContentAs's LogError would only duplicate it at a level the caller cannot
        // suppress. The failing read IS the thrown error.
        var content = node.ContentAs<TContent>(_jsonOptions);
        return content ?? throw new MeshNodeStreamException(
            BuildTypedWriteError<TContent>(node,
                $"content is {Describe(node.Content)} and could not be read as "
                + $"'{typeof(TContent).Name}'"));
    }

    /// <summary>Content as <typeparamref name="TContent"/>, required to be present.</summary>
    private TContent RequireContent<TContent>(MeshNode node) where TContent : class
        => ResolveContent<TContent>(node)
           ?? throw new MeshNodeStreamException(
               BuildTypedWriteError<TContent>(node,
                   $"the node has no content to update as '{typeof(TContent).Name}'"));

    private MeshNodeError BuildTypedWriteError<TContent>(MeshNode node, string what) =>
        new(MeshNodeErrorCode.Deserialization,
            node.Path ?? _path ?? "<own>",
            $"Update<{typeof(TContent).Name}> refused to write: {what}. The write was NOT applied — "
            + "writing a default-valued record here would persist those defaults over every field "
            + "the caller never touched.",
            ContentExcerpt(node.Content));

    // Assembly-qualified on purpose: a dynamic node assembly compiles without a namespace, so the
    // bare type name is identical on both sides of a cross-assembly mismatch and only the assembly
    // identity makes it diagnosable.
    private static string Describe(object? content) =>
        content is null
            ? "absent"
            : content is JsonElement or System.Text.Json.Nodes.JsonNode
                ? $"untyped JSON ({content.GetType().Name})"
                : $"a {content.GetType().FullName} ({content.GetType().Assembly.GetName().Name})";

    private const int TypedWriteExcerptLimit = 400;

    private static string? ContentExcerpt(object? content)
    {
        if (content is null)
            return null;
        var raw = content switch
        {
            JsonElement je => je.GetRawText(),
            System.Text.Json.Nodes.JsonNode jn => jn.ToJsonString(),
            _ => null,
        };
        return raw is null
            ? null
            : raw.Length <= TypedWriteExcerptLimit
                ? raw
                : raw[..TypedWriteExcerptLimit] + "…";
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the targeted MeshNode and returns an
    /// <see cref="IObservable{MeshNode}"/> that emits the post-update node on the first
    /// emission past the pre-update snapshot. <b>Caller MUST Subscribe</b> — the cold
    /// observable's side effect runs on Subscribe, errors flow to <c>OnError</c>.
    /// <list type="bullet">
    ///   <item><description><b>Own</b> (no path or path == hub address): writes through
    ///     the data source's primary EntityStore stream so all local subscribers see
    ///     the new value and the type source's persister picks it up for save.</description></item>
    ///   <item><description><b>Remote</b> (path != hub address): calls
    ///     <c>ISynchronizationStream&lt;MeshNode&gt;.Update(...)</c> on the workspace's
    ///     cached remote stream so the patch routes to the owning per-node hub via
    ///     the data sync protocol.</description></item>
    /// </list>
    /// <para>🚨 Prefer the typed <see cref="Update{TContent}(Func{TContent, TContent})"/> whenever
    /// the lambda reads Content: it names the type instead of re-deriving it, and it fails loudly
    /// rather than letting a <c>Content as T ?? new T()</c> write defaults over real fields.</para>
    /// </summary>
    public IObservable<MeshNode> Update(Func<MeshNode, MeshNode> update)
        => UpdateQueued(update, pendingSelfWrite: null);

    /// <summary>
    /// <c>Update</c> with the state the PREVIOUS write on this path's serial queue computed locally.
    /// Only <see cref="IMeshNodeStreamCache"/>'s per-path queue may pass one — it is the single caller
    /// that can prove the two writes are ordered and come from this same mirror. See
    /// <see cref="PatchBaseSource"/> for why the mirror alone is not a sound base (#2305 / #2291).
    /// <para>A distinct NAME rather than an overload: <c>Update</c> is referenced from a dozen
    /// <c>&lt;see cref&gt;</c>s across this file and its callers, and an overload makes every one of
    /// them ambiguous (CS0419).</para>
    /// </summary>
    /// <param name="update">The caller's lambda, as for <c>Update</c>.</param>
    /// <param name="pendingSelfWrite">What the PREVIOUS queued write on this path computed, or null.</param>
    /// <param name="onLocalState">The queue's HAND-OFF. Invoked with the node the OWNER has
    /// acknowledged taking — to be handed to the next queued write as its base — or with
    /// <c>null</c> to say "the verdict is in, there is nothing to hand forward". Either way it is
    /// what RELEASES the per-path queue slot, so the successor starts on a known outcome instead of
    /// on this write's optimistic emit (#2346).
    /// <para>🚨 A NODE is published ONLY on the owner's success ack — early or LATE — and on a
    /// no-op; never on the optimistic emit, never on a rejection, never from inside a retry. A write
    /// that did not land mints no version, so a base published from an unlanded write is never
    /// corrected by anything: the next write diffs its own unlanded value, produces an EMPTY patch,
    /// and is skipped as a no-op — silently, and for every retry after it. That regression is real
    /// and was caught by <c>TwoSiloRecycleConvergenceTest</c>.</para>
    /// <para>🚨 <c>null</c> is the other half, and it is not optional: a re-enqueued attempt or a
    /// terminal late NACK will never produce a base, so without it the successor would sit out the
    /// whole <c>QueueAdvanceBound</c> waiting for a signal that cannot come.</para>
    /// <para>🚨 Invoked with the node in the shape the write path itself diffs — BEFORE the
    /// typed-Content projection on the returned observable, and AFTER the audit stamp. Taking it off
    /// the observable instead would hand the successor a re-typed node whose re-serialisation need not
    /// match (a recovered <c>$type</c>, a suppressed default), and the successor's diff would then
    /// carry phantom keys.</para>
    /// <para>A CONFLICT/OwnerDisposing re-enqueue carries a SETTLE-ONLY wrapper: it may release the
    /// slot when its own verdict lands (the re-attempt is still this write, so the successor must not
    /// start alongside it) but it may never publish a base — a re-attempt runs asynchronously and
    /// could land after a LATER write already published, replacing a newer base with an older one.</para>
    /// </param>
    internal IObservable<MeshNode> UpdateQueued(
        Func<MeshNode, MeshNode> update,
        MeshNode? pendingSelfWrite,
        Action<MeshNode?>? onLocalState = null)
    {
        // 🚨 AccessContext capture for the LAMBDA invocation. The user's
        // `update` lambda runs on whatever thread the underlying writer fires
        // it on — for UpdateRemote that's the remote stream's emission thread
        // (workspace emission scheduler, AsyncLocal NOT flowed); for UpdateOwn
        // that's the data source's action block (a dedicated worker thread,
        // also no AsyncLocal flow). Without re-stamping, the lambda sees a
        // null AccessContext.Context and any downstream framework call that
        // reads `Context ?? CircuitContext` to attribute writes (e.g. inner
        // satellite-node Updates inside the lambda, IDataChangeNotifier
        // emissions, audit logs) sees null → owner-side RLS denies → silent
        // failure (chat hangs, delegations never stamp, inboxes stay empty).
        // The diagnostic for this exact shape is
        // TypedErrorPropagationTest.AccessContext_PreservedAcrossSubscribeAndUpdateHops.
        //
        // Capture order: prefer the per-delivery Context (set by the message
        // hub before invoking handlers), fall back to CircuitContext (set by
        // long-lived test fixtures / Blazor circuits). Matches what
        // UpdateRemote already captures eagerly for the outbound
        // PatchDataRequest's WithAccessContext.
        var accessService = _workspace.Hub.ServiceProvider
            .GetService<MeshWeaver.Messaging.AccessService>();
        var capturedForLambda = accessService?.Context ?? accessService?.CircuitContext;

        // 🚨 Typed-Content wrap (read direction): the lambda sees Content
        // already deserialised to its registered domain type (e.g. MeshThread,
        // NodeTypeDefinition). Without this, lambdas that pattern-match
        // `node.Content as MyType` silently fall back to the
        // `?? new MyType()` default whenever the underlying data source
        // happens to store Content as a raw JsonElement (file-system /
        // Postgres / Cosmos all round-trip through JSON serialisation;
        // InMemory keeps typed). The default-valued fallback then overwrites
        // every other field on the next stream.Update — see
        // ThreadInput.AppendUserInput + the CheckInbox flake (test sets
        // Status=Executing, AppendUserInput's `node.Content as MeshThread ??
        // new MeshThread()` quietly resets it to Idle when Content arrives
        // as JsonElement, the SubmissionWatcher then sees Idle+pending and
        // dispatches a round the test was trying to prevent).
        //
        // No outbound serialisation: the cold pipeline downstream (UpdateOwn
        // writes typed into the data source's collection; UpdateRemote /
        // cache.Update run JsonSerializer.SerializeToNode on the typed
        // updated node before computing the patch) handles either typed or
        // JsonElement equivalently. Forcing JsonElement on the output broke
        // OWN-path equality checks (data source dedup compares by
        // reference / structural equality; serialise-deserialise breaks
        // reference and can perturb structural).
        Func<MeshNode, MeshNode> wrappedUpdate = node =>
        {
            // Re-stamp AsyncLocal so the lambda body sees the caller's
            // identity, no matter what thread invoked it. No-op when no
            // identity was set (background flows that genuinely have no
            // user — these should ImpersonateAsSystem explicitly).
            using (capturedForLambda is null || accessService is null
                ? null
                : accessService.SwitchAccessContext(capturedForLambda))
            {
                return update(EnsureTypedContent(node, _jsonOptions, _contentTypeRegistry));
            }
        };

        return new RequireSubscribeObservable<MeshNode>(
            // 🚨 CarryAccessContext is the cross-cutting "AccessContext survives
            // Subscribe()" wrap. Capture happens here — synchronously — on the
            // caller's thread where MessageHub has already set AsyncLocal from
            // delivery.AccessContext. The captured value rides as a closure on
            // the returned cold pipeline and re-stamps AsyncLocal on every
            // emission, so a Subscribe callback that lands on a different thread
            // still observes the caller's user. See
            // AccessContextCaptureExtensions / AccessContextPropagation.md.
            //
            // Cross-hub writes route through IMeshNodeStreamCache (when one is
            // registered): the cache's shared handle is what every reader is
            // subscribed to, so the patch is observed in order. Own writes and
            // cache-less writes fall back to the direct paths.
            (IsOwn
                ? UpdateOwn(wrappedUpdate)
                : _cache is not null && _path is not null
                    ? _cache.Update(_path, wrappedUpdate, _jsonOptions)
                    : _bypassCache
                        // 🚨 Field-merge writes go through the CORRELATED PatchDataRequest
                        // path, NOT the sync-stream write. UpdateViaSyncStream emits an
                        // OPTIMISTIC local snapshot and never waits for the owner — so an
                        // owner-side RLS denial / validation rejection / invalid-path write
                        // is silently swallowed (the caller already "succeeded"), and the
                        // unvalidated SetCurrentRequest lands the change anyway. UpdateRemote
                        // posts the JSON-merge patch, AWAITS the owner's PatchDataResponse /
                        // DeliveryFailure, and surfaces a denial as UnauthorizedAccessException
                        // / rejection as MeshNodeStreamException on the caller's OnError —
                        // delivered to ONLY the submitting caller (request/response is
                        // inherently correlated; it never calls reduced.OnError, so the
                        // SHARED read stream every other subscriber/mirror reads is never
                        // faulted). It also confirms read-after-write (waits for the commit),
                        // and since #2661 that is unconditional: the caller's terminal IS
                        // the owner's commit verdict, never a response bound expiring.
                        // Overwrite (full-replace, static-repo import) stays on the
                        // sync-stream path — see Overwrite below.
                        ? UpdateRemote(wrappedUpdate,
                            pendingSelfWrite: pendingSelfWrite, onLocalState: onLocalState)
                        : throw new InvalidOperationException(
                            $"Cross-hub MeshNode write to '{_path}' requires an IMeshNodeStreamCache "
                            + $"on hub '{_workspace.Hub.Address}', but none is registered. All non-own "
                            + "MeshNode access must route through the cache (the single shared mirror) — "
                            + "the raw GetRemoteStream fallback is gone."))
                // restoreNullCapture: a write-result emission must NEVER inherit the
                // plumbing's ambient identity (system-security from the cache's read
                // path) into the caller's callback — a nested write there would post
                // AS SYSTEM. Null capture → callback runs with Context=null and
                // PostPipeline's `Context ?? CircuitContext` resolves the real user.
                .CarryAccessContext(_workspace.Hub.ServiceProvider, restoreNullCapture: true)
                // The post-update emission also goes through the typed
                // converter — callers chaining `.Select(node => node.Content as MyType)`
                // off the Update's returned observable get the same typed
                // shape as Subscribe.
                .Select(node => EnsureTypedContent(node, _jsonOptions, _contentTypeRegistry)),
            $"MeshNodeStreamHandle.Update(path='{_path ?? "<own>"}')",
            _workspace.Hub.ServiceProvider);
    }

    /// <summary>
    /// 🚨 OVERWRITE — assert the full authoritative state of this node, DECOUPLED from the
    /// merge-sync protocol. Unlike <see cref="Update"/> (which ships a recursive JSON-merge-patch
    /// that the owner merges field-by-field against its current state), Overwrite lands the
    /// COMPLETE <paramref name="node"/> as a <see cref="ChangeType.Full"/> — so it replaces the
    /// owner's state wholesale and re-asserts on every mirror unconditionally (the sync stream's
    /// monotonicity guard lets Fulls through regardless of version). This is the write the
    /// static-repo import uses to materialize source nodes; it is NOT for ordinary field edits.
    /// <para>Returns an <see cref="IObservable{MeshNode}"/> the caller MUST Subscribe to (the
    /// write runs on Subscribe). The node must already exist / its owning hub must be live — an
    /// overwrite needs a live owner to land on; create absent nodes first.</para>
    /// </summary>
    public IObservable<MeshNode> Overwrite(MeshNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new RequireSubscribeObservable<MeshNode>(
            (IsOwn
                // Own write already replaces the whole instance in the local collection — a
                // constant transform IS a full overwrite. No sync-merge involved on the own path.
                ? UpdateOwn(_ => node)
                : _cache is not null && _path is not null
                    ? _cache.Overwrite(_path, node, _jsonOptions)
                    : _bypassCache
                        ? OverwriteViaSyncStream(node)
                        : throw new InvalidOperationException(
                            $"Cross-hub MeshNode overwrite to '{_path}' requires an IMeshNodeStreamCache "
                            + $"on hub '{_workspace.Hub.Address}', but none is registered."))
                // Same clamp as Update — see the comment there.
                .CarryAccessContext(_workspace.Hub.ServiceProvider, restoreNullCapture: true)
                .Select(n => EnsureTypedContent(n, _jsonOptions, _contentTypeRegistry)),
            $"MeshNodeStreamHandle.Overwrite(path='{_path ?? "<own>"}')",
            _workspace.Hub.ServiceProvider);
    }

    /// <summary>
    /// 🚨 ADOPT a state that is ALREADY DURABLE — an OBSERVATION, never a write.
    ///
    /// <para>Use this — and ONLY this — when <paramref name="persisted"/> is the node exactly as
    /// STORAGE holds it, at exactly the <see cref="MeshNode.Version"/> storage holds it under: the
    /// entity carried by a storage change notification for a write somebody else made (another
    /// writer, a second silo, a migration, GitSync). It is the *provenance* of the node — off the
    /// durable change feed — that makes this the right call, not anything about its content; a
    /// genuine change never arrives that way, so there is no content heuristic to get wrong.</para>
    ///
    /// <para><b>Two things it does that <see cref="Update"/> must not.</b>
    /// (1) It does NOT mint. <c>UpdateOwn</c>'s unconditional
    /// <c>NextVersion(Math.Max(current.Version, updated.Version))</c> would leave the hub holding
    /// <c>durable + 1</c> — a revision that exists nowhere, with content identical to the durable
    /// row (#1432). (2) It records the durable version in <see cref="PostCommitFlushRegistry"/>,
    /// so the per-node persistence sampler and the dispose-time flush both skip writing the
    /// adopted state back — the SAME "one change, one durable write" gate #1249 built, and
    /// sound for the same reason: two DISTINCT own-node states can never share a version.</para>
    ///
    /// <para><b>Forward-only</b>, so it stays the lagged-echo defence it replaces: a persisted
    /// snapshot may replace in-RAM state only when it is STRICTLY NEWER. The durable write and
    /// its change notification are off-turn, so under a write burst the notification LAGS the
    /// in-RAM commit; re-applying that stale snapshot silently dropped every field added since
    /// it was persisted (<c>CrossHubPatchAtomicityTest</c>). At or below the live version this
    /// completes with the unchanged node and touches nothing.</para>
    ///
    /// <para>🚨 NOT <c>AdoptDurableTruth</c> (<c>MeshDataSource</c>), which is the opposite case:
    /// there a write was REFUSED by the monotonic guard, so the owner must climb strictly ABOVE
    /// the durable row for its next save to land — that one mints deliberately, through
    /// <see cref="Update"/>.</para>
    /// </summary>
    /// <param name="persisted">The node exactly as persisted, carrying its durable version.</param>
    /// <returns>A cold observable — the adoption runs on Subscribe — emitting the state the
    /// node holds afterwards (the adopted node, or the unchanged live node when it is already
    /// at or past the durable version).</returns>
    public IObservable<MeshNode> AdoptPersisted(MeshNode persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (!IsOwn)
            throw new InvalidOperationException(
                $"AdoptPersisted is an OWN-node operation, but this handle targets '{_path}' from "
                + $"hub '{_workspace.Hub.Address}'. Only the owning hub observes its own node's "
                + "storage change feed; a cross-hub caller has no durable-version authority.");
        return new RequireSubscribeObservable<MeshNode>(
            UpdateOwn(_ => persisted, adoptPersisted: true)
                // Same clamp as Update — see the comment there.
                .CarryAccessContext(_workspace.Hub.ServiceProvider, restoreNullCapture: true)
                .Select(n => EnsureTypedContent(n, _jsonOptions, _contentTypeRegistry)),
            $"MeshNodeStreamHandle.AdoptPersisted(path='{_path ?? "<own>"}')",
            _workspace.Hub.ServiceProvider);
    }

    private IObservable<MeshNode> UpdateOwn(Func<MeshNode, MeshNode> update, bool adoptPersisted = false)
        => Observable.Create<MeshNode>(observer =>
        {
            var refStream = _workspace.GetStream(new MeshNodeReference())
                ?? throw new InvalidOperationException(
                    "MeshNode stream is not available — the workspace has no MeshNodeReference reducer.");

            var dataSource = _workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
            if (dataSource == null)
                throw new InvalidOperationException("No data source registered for MeshNode");
            var dsStream = dataSource.GetStreamForPartition(null)
                ?? throw new InvalidOperationException("No stream for MeshNode partition");

            // 🚨 Single-emit guard shared by the post-write stream echo AND the no-op
            // short-circuit below. Without it a no-op write would either hang (echo never
            // arrives) or, defensively, a late echo could OnNext after OnCompleted.
            var emitted = 0;
            void EmitOnce(MeshNode node)
            {
                if (System.Threading.Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnNext(node);
                observer.OnCompleted();
            }

            // Resolve the target Path: an explicit _path wins, otherwise default to the
            // workspace's own hub path. The InstanceCollection holds the OWN MeshNode
            // alongside any satellite nodes the data source has loaded (e.g. NodeType
            // hubs accumulate Release/* satellites after each compile). Looking up by
            // terminal-segment Id alone is non-deterministic when multiple instances
            // share the same Id; match on the full Path so the OWN node is always
            // resolved correctly. When neither path is available, fall back to
            // FirstOrDefault — only legacy single-instance shapes hit this branch.
            var targetPath = _path ?? _workspace.Hub.Address.Path;

            // 🚨 Echo detection is WRITE-IDENTITY-based, never emission-count-based.
            // The update lambda stamps the strictly-increasing Version it writes
            // (MeshNode.NextVersion — always > current.Version) into stampedVersion
            // BEFORE the commit; the subscription emits only a state of the TARGET
            // node at-or-past that stamp — a state that provably CONTAINS this
            // caller's write.
            //
            // The previous shape ("baseline = first observed stream version; emit on
            // any emission with a higher version") accepted FOREIGN emissions as the
            // echo. Two real failure modes (the FrameworkStaleInstanceRenderTest CI
            // flake, 2026-07-20 run 29749071939):
            //   1. A concurrent write to ANOTHER instance in the same collection (a
            //      Source/Release/_Activity satellite — which ReduceToMeshNode's
            //      patch path even surfaced as the emission's Value) or a concurrent
            //      writer on the SAME node bumped the stream version in the window
            //      between Subscribe and this caller's update lambda running — the
            //      observable completed with a PRE-WRITE (or sibling) node while the
            //      lambda hadn't run. HandleDispatchCompile then read
            //      weTransitioned == false, skipped the compile dispatch, and the
            //      Pending→Compiling flip landed with NO compile driver → NodeType
            //      wedged at Compiling forever (the 60s GetCompilationPathRequest
            //      timeout in the CI trace was downstream of that wedge).
            //   2. Under load, the subscription's initial replay could be delivered
            //      AFTER the write applied — the post-write state became the
            //      baseline and the true echo never came → the observable hung.
            // Version-gating on the caller's own stamp eliminates both: pre-stamp
            // emissions are ignored, and the post-commit echo (guaranteed — the
            // subscription attaches before the write, and a replay delivered after
            // the commit already carries Version >= stamp) always satisfies the
            // gate. The happens-before chain (stamp write → commit under the
            // stream's synchronization → emission/replay reads the committed state)
            // makes the stamp visible on the emission thread whenever a post-write
            // state is; Volatile is belt-and-suspenders.
            long stampedVersion = -1;
            var sub = refStream.Subscribe(change =>
            {
                var stamped = System.Threading.Volatile.Read(ref stampedVersion);
                if (stamped < 0) return; // our write hasn't been applied yet
                if (change.Value is not { } node) return;
                // Only the referenced node can satisfy the echo — a sibling emission
                // (same collection, different Path) must never complete this write.
                if (!string.IsNullOrEmpty(targetPath)
                    && !string.Equals(node.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                    return;
                if (node.Version < stamped) return; // pre-write state
                EmitOnce(node);
            }, observer.OnError);

            try
            {
                dsStream.Update(state =>
                {
                    var store = state ?? new EntityStore();
                    var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
                    if (collection is null)
                        throw new InvalidOperationException(
                            $"MeshNode collection not found. Available: [{string.Join(", ", store.Collections.Keys)}]");

                    var current = string.IsNullOrEmpty(targetPath)
                        ? collection.Instances.Values.OfType<MeshNode>().FirstOrDefault()
                        : collection.Instances.Values.OfType<MeshNode>()
                            .FirstOrDefault(n => string.Equals(n.Path, targetPath, StringComparison.OrdinalIgnoreCase));
                    if (current == null)
                        throw new InvalidOperationException(
                            $"MeshNode '{targetPath ?? "<own>"}' not found. Available: [{string.Join(", ", collection.Instances.Keys.Select(k => k.ToString()))}]");

                    var updated = update(current);
                    // 🚨 No-op completion. When the update makes no net change to the node
                    // (a guard `return node`, an empty drain, or a lambda that rebuilds
                    // IDENTICAL content), there is nothing to version and nothing to write:
                    // the reduced stream emits nothing (DistinctUntilChanged drops an
                    // identical value), the refStream subscription above never sees a second
                    // emission and this observable would HANG forever — the host-hang behind
                    // check_inbox's no-timeout TCS (InboxToolIntegrationTest
                    // .CheckInbox_TwoCallsBackToBack exceeded the 90s blame-hang deadline →
                    // test-host crash → shard abort). Complete the observable directly with
                    // the unchanged node and skip the meaningless write.
                    // 🚨 SerializedEquals is the load-bearing third check: record Equals is
                    // reference-based for Content / collection fields, so a lambda that
                    // rebuilds identical content slips past it — and every such "write"
                    // minted a Version and persisted a history row for an edit that never
                    // happened (the v1170-without-edits report).
                    if (ReferenceEquals(updated, current) || Equals(updated, current)
                        || MeshNode.SerializedEquals(current, updated, _jsonOptions))
                    {
                        EmitOnce(current);
                        return null; // nothing to apply — true no-op
                    }
                    // 🚨 Version is the ONE reliable ordering field and it is stamped
                    // by the OWNING hub here (this is an own write on the owner).
                    // DateTime/LastModified is NOT a cross-machine clock, so it must
                    // never drive reconciliation; the owner's monotonic Version does.
                    // 🚨 The counter is the NODE's own — `current.Version + 1`, never the hub
                    // clock (which counts unrelated messages and resets to 0 on reactivation,
                    // stamping a LOWER version after a recycle → rollback + split-brain, #325).
                    // See MeshNode.NextVersion / Doc/Architecture/MeshNodeVersioning.md.
                    // Take the max with the version the update lambda RETURNED: a durable-truth
                    // rebase (AdoptDurableTruth — the owner re-adopting a stored row that is
                    // AHEAD of its in-memory state after a MonotonicWriteGuard refusal) hands
                    // back a node carrying the stored Version; re-stamping it off the stale
                    // in-memory `current` alone would discard that signal and keep minting
                    // below the durable row forever. Ordinary lambdas (`current with {…}`)
                    // carry current.Version, so the extra term is a no-op for them; version
                    // restore writes Version = 0 and is likewise unaffected.
                    if (adoptPersisted)
                    {
                        // 🚨 ADOPTION — an OBSERVATION of state that is ALREADY durable, so it
                        // neither mints nor writes. See AdoptPersisted for the full contract.
                        //
                        // Forward-only, checked HERE against the authoritative in-turn `current`
                        // (a caller-side check reads a snapshot that the action block may have
                        // already moved past): a persisted snapshot may replace in-RAM state only
                        // when it is STRICTLY NEWER. A lagged echo of our own write completes with
                        // the unchanged node, so the in-RAM stream only ever moves forward.
                        if (updated.Version <= current.Version)
                        {
                            EmitOnce(current);
                            return null;
                        }
                        // The adopted version IS durable — record it on the same per-path
                        // high-water HandleSaveMeshNode / FlushPendingOwnSave consult, so neither
                        // the 200 ms persistence sampler nor the dispose-time flush writes this
                        // observation back to storage (#1432; mechanism and soundness: #1249 /
                        // PostCommitFlushRegistry). Recorded inside the commit turn, so an
                        // adoption that did NOT apply never raises the mark.
                        _workspace.Hub.ServiceProvider.GetService<PostCommitFlushRegistry>()
                            ?.Record(updated.Path, updated.Version);
                    }
                    else
                    {
                        updated = updated with
                        {
                            Version = MeshNode.NextVersion(Math.Max(current.Version, updated.Version))
                        };
                    }
                    // Stamp BEFORE the commit — the echo subscription above only emits
                    // once it can see this write's Version on the target node.
                    System.Threading.Volatile.Write(ref stampedVersion, updated.Version);
                    var newStore = store.Update(nameof(MeshNode), c => c.Update(updated.Id, updated));
                    return dsStream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                        [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }],
                        dsStream.StreamId));
                }, observer.OnError);
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }

            return sub;
        });

    /// <summary>
    /// Cross-hub write through the SYNC STREAM — no application-level
    /// <c>PatchDataRequest</c> hub message. Opens the owner's mirror via
    /// <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>, waits for the
    /// authoritative initial snapshot, then calls
    /// <see cref="ISynchronizationStream{TStream}.Update(System.Func{TStream, ChangeItem{TStream}}, System.Action{System.Exception})"/>.
    /// The lambda runs against the stream's <c>Current</c> (the OWNER's
    /// authoritative state, reconciled through the sync feed) — NOT a stale
    /// client snapshot — and the resulting <see cref="ChangeItem{TStream}"/>
    /// propagates to the owner over the sync-stream change feed
    /// (<c>SetCurrentRequest</c>), the SAME channel owner→subscriber echoes ride.
    /// <para>This collapses the former dual write path (PatchDataRequest +
    /// the sync stream's own bidirectional propagation) into ONE — the conflict
    /// between them is what live-locked the grain between the client's stale
    /// mirror state and the server commit (Resubmit_AfterExecution_DoesNotDeadlock).
    /// Write-permission is enforced on the owner side where the inbound change is
    /// applied (see the MeshNode sync-write gate).</para>
    /// </summary>
    private IObservable<MeshNode> UpdateViaSyncStream(Func<MeshNode, MeshNode> update)
        => WriteViaSyncStream(update, full: false);

    /// <summary>
    /// Overwrite via the sync stream — same scaffold as <see cref="UpdateViaSyncStream"/> but the
    /// change is shipped as a <see cref="ChangeType.Full"/> (see <see cref="Overwrite"/>). Waits
    /// for the owner's initial snapshot (so the owner hub is live) then re-asserts the full node.
    /// </summary>
    private IObservable<MeshNode> OverwriteViaSyncStream(MeshNode node)
        => WriteViaSyncStream(_ => node, full: true);

    private IObservable<MeshNode> WriteViaSyncStream(Func<MeshNode, MeshNode> update, bool full)
        => Observable.Create<MeshNode>(observer =>
        {
            var diagLogger = _workspace.Hub.ServiceProvider
                .GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.MeshNodeStreamHandle");

            var accessService = _workspace.Hub.ServiceProvider
                .GetService<MeshWeaver.Messaging.AccessService>();
            var capturedContext = accessService?.Context ?? accessService?.CircuitContext;

            // 🚨 Sanctioned escape hatch (cache/bypass write path) — route through the
            // internal unchecked overload; the public GetRemoteStream<MeshNode> warns.
            // The LEASE (disposed with this subscription) is what tells the workspace this
            // write still needs the stream, so an eviction landing mid-write parks it rather
            // than reclaiming it — and reclaims it the instant the write is done (#1324).
            //
            // 🚨 Unlike UpdateRemote — which only READS the initial snapshot off the stream and
            // then writes with hub.Post — this path writes THROUGH the stream, and EmitOnce
            // completes the caller inside the stream's own UpdateStreamRequest turn (just before
            // SetCurrent). So the lease ends a hair before the turn does. Leasing is still
            // strictly the safer choice: leaving this path undeclared would let an eviction
            // reclaim the very mirror the overwrite is writing through. Closing the last
            // microseconds needs an `applied` hook on the value-based SetFull/Update overloads
            // (SynchronizationStream already runs one post-apply, same turn, for the
            // ChangeItem-based overload) — worth doing when that API is next touched.
            var (remoteStream, streamLease) = ((Workspace)_workspace)
                .AcquireRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
                    new Address(_path!), new MeshNodeReference());

            var composite = new CompositeDisposable(streamLease);
            var emitted = 0;
            void EmitOnce(MeshNode value)
            {
                if (System.Threading.Interlocked.Exchange(ref emitted, 1) != 0) return;
                if (accessService is not null && capturedContext is not null)
                    using (accessService.SwitchAccessContext(capturedContext))
                    { observer.OnNext(value); observer.OnCompleted(); }
                else { observer.OnNext(value); observer.OnCompleted(); }
            }

            // Wait for the authoritative initial snapshot so the lambda diffs
            // against a real (owner-reconciled) value, then write through the
            // sync stream. remoteStream.Update reads Current on the stream's own
            // serialized action block, applies the lambda, and ships the result
            // to the owner via the change feed — no PatchDataRequest.
            var initialSub = remoteStream
                .Timeout(TimeSpan.FromSeconds(30))
                .Where(change => change.Value is not null)
                .Take(1)
                .Subscribe(
                    _ =>
                    {
                        // 🚨 VALUE-based update — the sync stream builds the
                        // ChangeItem (per-entity Updates + ChangeType + owner-only
                        // Version) consistently. We supply only the value transform;
                        // we never hand-roll a ChangeItem (a hand-rolled EntityUpdate
                        // silently failed the owner's write-back). Audit identity
                        // (LastModifiedBy) is content, so we still stamp it here.
                        // Explicit Func type disambiguates from the ChangeItem overload.
                        Func<MeshNode?, MeshNode?> valueUpdate = current =>
                        {
                            if (current is null) return null;
                            var updated = update(current);
                            if (updated.LastModifiedBy == current.LastModifiedBy
                                && !string.IsNullOrEmpty(capturedContext?.ObjectId))
                                updated = updated with { LastModifiedBy = capturedContext.ObjectId };
                            EmitOnce(updated);
                            return updated;
                        };
                        Action<Exception> onWriteError = ex =>
                        {
                            diagLogger?.LogWarning(ex,
                                "[WriteViaSyncStream] write lambda errored hub={Hub} target={Path} full={Full}",
                                _workspace.Hub.Address, _path, full);
                            observer.OnError(ex);
                        };
                        // full ⇒ ChangeType.Full overwrite (re-assert wholesale); else field-merge.
                        // 🚨 Carry the caller's AccessContext onto the sync-stream post. This callback
                        // runs on the remote-stream initial-snapshot thread, where the AsyncLocal
                        // AccessContext is gone — so without re-asserting the captured context the
                        // UpdateStreamRequest is posted with no AccessContext and the owner's
                        // PostPipeline fails closed (the write is silently dropped). UpdateRemote
                        // already stamps WithAccessContext on its PatchDataRequest; this is the missing
                        // symmetric carry for the Full/Overwrite path. See AccessContextPropagation.md.
                        using (accessService is not null && capturedContext is not null
                                   ? accessService.SwitchAccessContext(capturedContext)
                                   : System.Reactive.Disposables.Disposable.Empty)
                        {
                            if (full)
                                remoteStream.SetFull(valueUpdate, onWriteError);
                            else
                                remoteStream.Update(valueUpdate, onWriteError);
                        }
                    },
                    ex =>
                    {
                        if (ex is TimeoutException)
                            // 🚨 Carry the original as INNER. The wait is bounded at 30s, but the
                            // terminal that ends it may be something else entirely — an owner that
                            // never answered the SubscribeRequest inside the request budget reads
                            // as a TimeoutException too, and flattening it to this sentence is
                            // what made the boot-install failures unreadable (#2387).
                            observer.OnError(new TimeoutException(
                                $"Update aborted: no initial state arrived for '{_path}' within 30s.", ex));
                        else observer.OnError(ex);
                    });
            composite.Add(initialSub);
            return composite;
        });

    /// <summary>
    /// Remote write — eventual-consistency path. Snapshots the local mirror's
    /// view, applies the user lambda, computes a recursive JSON-merge-patch
    /// (RFC 7396) DIFF between the snapshot and the result, then posts that
    /// diff via <see cref="PatchDataRequest"/> to the owning per-node hub.
    /// The owner merges the diff against its CURRENT authoritative state,
    /// preserving fields touched by concurrent writers from other mirrors —
    /// no <c>ChangeType.Full</c> overwrite, no "stale-mirror clobber".
    /// <para>The returned observable emits the post-merge MeshNode once the
    /// owner's response arrives, then completes.</para>
    /// </summary>
    /// <param name="update">The update lambda (already typed-content/context-wrapped).</param>
    /// <param name="attempt">Re-enqueue depth: 0 for a caller write, incremented by the
    /// late OwnerDisposing-NACK re-enqueue, capped at <see cref="MaxOwnerDisposingReenqueues"/>.
    /// Carried as a parameter — never static state.</param>
    /// <param name="refusedBaseVersion">On a CONFLICT re-enqueue, the node version the owner
    /// refused this write against, so the re-attempt rebases on something newer
    /// (<see cref="RebaseSource"/>). 0 for a caller write and for the re-enqueue codes that never
    /// reached a merge.</param>
    private IObservable<MeshNode> UpdateRemote(
        Func<MeshNode, MeshNode> update, int attempt = 0, long refusedBaseVersion = 0,
        MeshNode? pendingSelfWrite = null, Action<MeshNode?>? onLocalState = null)
        => Observable.Create<MeshNode>(observer =>
        {
            var diagLogger = _workspace.Hub.ServiceProvider
                .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.MeshNodeStreamHandle");
            diagLogger?.LogDebug(
                "[UpdateRemote] BEGIN hub={Hub} target={Path} attempt={Attempt}",
                _workspace.Hub.Address, _path, attempt);

            // 🚨 Capture AccessContext SYNCHRONOUSLY here, NOT inside the
            // deferred initialSub.Subscribe callback below. The outer
            // CarryAccessContext wrap (in Update) restores AsyncLocal on
            // every emission of the OUTER observable, but it doesn't reach
            // the inner Subscribe callback below — that callback fires when
            // the remote stream's initial state arrives, often on a different
            // thread (workspace emission scheduler) where AsyncLocal is null.
            // Without this eager capture, the inner read at PatchDataRequest
            // post time sees null Context and the patch goes out unattributed
            // → "Access denied: user 'sync/...' lacks Update permission" with
            // the hub's own address as the failing principal. Capture once
            // here (Subscribe time = caller's thread, AsyncLocal valid because
            // the outer CarryAccessContext just restored it) and close over
            // it for the deferred callback.
            var accessServiceAtEntry = _workspace.Hub.ServiceProvider
                .GetService<MeshWeaver.Messaging.AccessService>();
            var capturedContextAtEntry = accessServiceAtEntry?.Context
                ?? accessServiceAtEntry?.CircuitContext;

            // 🚨 Sanctioned escape hatch (cache/bypass write path) — route through the
            // internal unchecked overload; the public GetRemoteStream<MeshNode> warns.
            // Leased for the life of this subscription — see WriteViaSyncStream's note and
            // Workspace._remoteStreamLeases (#1324): without the declaration the workspace
            // cannot tell a write-scoped mirror from one a live reader still needs.
            var (remoteStream, streamLease) = ((Workspace)_workspace)
                .AcquireRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
                    new Address(_path!), new MeshNodeReference());

            var composite = new CompositeDisposable(streamLease);

            // Wait for the per-node hub's initial SubscribeResponse before
            // running the user lambda — the lambda needs a non-null current
            // to diff against. A 30 s outer timeout bounds the wait so a
            // missing per-node hub surfaces with a precise TimeoutException.
            //
            // 🚨 …and on a CONFLICT re-attempt, wait for state the owner has not already
            // refused. See RebaseSource: re-running the lambda against the SAME mirror emission
            // recomputes the same values from the same base and is refused identically, so the
            // re-enqueue cannot converge (#1910).
            //
            // 🚨 …and when the PREVIOUS write on this path's serial queue has not been echoed back
            // yet, the mirror is behind THIS mirror's own last write — diffing against it ships a
            // base the writer itself superseded, which the owner refuses as a conflict that never
            // happened (#2305 / #2291). See PatchBaseSource.
            var initialSub = PatchBaseSource(
                    RebaseSource(
                        remoteStream
                            .Timeout(TimeSpan.FromSeconds(30))
                            .Where(change => change.Value is not null)
                            .Select(change => change.Value!),
                        refusedBaseVersion,
                        staleVersion => diagLogger?.LogWarning(
                            "[UpdateRemote] STALE_MIRROR hub={Hub} target={Path} attempt={Attempt} — the "
                            + "owner refused this write as stale at version {Version}, but this hub's "
                            + "mirror did not advance past it within {Bound}; rebuilding the patch "
                            + "against the state it has. The re-attempt may be refused again",
                            _workspace.Hub.Address, _path, attempt, staleVersion, ConflictRebaseBound)),
                    pendingSelfWrite)
                // Says which base this write actually built on — the one question worth asking when a
                // cross-hub write's field comes back refused. SELF_REBASE means the mirror had not
                // caught up and we diffed against our own predecessor's result instead.
                .Do(node =>
                {
                    if (ReferenceEquals(node, pendingSelfWrite))
                        diagLogger?.LogDebug(
                            "[UpdateRemote] SELF_REBASE hub={Hub} target={Path} version={Version} — the "
                            + "mirror has not carried the previous queued write yet; diffing against it",
                            _workspace.Hub.Address, _path, node.Version);
                })
                .Subscribe(
                    current =>
                    {
                        try
                        {
                            var updated = update(current);
                            if (ReferenceEquals(updated, current) || Equals(updated, current))
                            {
                                // A lambda that returns the node unchanged is a legitimate
                                // no-op (an identical upsert, a guard `return node`, a
                                // re-import of unchanged content) — it completes without a
                                // write and without a version bump.
                                // 🚨 …UNLESS Content is still a raw JsonElement: then the
                                // no-op is almost certainly a typed pattern match
                                // (`curr.Content is MeshThread t`) that failed because the
                                // framework did not deserialize to the registered type, and
                                // the caller's stream.Update() "succeeds" with no observable
                                // effect (the CancelStream failure mode where
                                // RequestedCancellationAt stayed null). Warn for THAT shape
                                // only, so a real defect stays loud while intentional no-ops
                                // stay quiet.
                                if (current.Content is System.Text.Json.JsonElement)
                                    diagLogger?.LogWarning(
                                        "[UpdateRemote] NO-OP hub={Hub} target={Path} contentType=JsonElement — lambda returned unchanged; check the lambda's content-type pattern match",
                                        _workspace.Hub.Address, _path);
                                else
                                    diagLogger?.LogDebug(
                                        "[UpdateRemote] NO-OP hub={Hub} target={Path} contentType={ContentType} — lambda returned the node unchanged; nothing to write",
                                        _workspace.Hub.Address, _path,
                                        current.Content?.GetType().Name ?? "<null>");
                                // Nothing written, so nothing new to claim — but `current` is a state
                                // the owner is believed to hold (the mirror, or a predecessor's ACKED
                                // result). Carrying it keeps the chain intact across a no-op instead
                                // of dropping the successor back onto a mirror that may be behind.
                                onLocalState?.Invoke(current);
                                observer.OnNext(current);
                                observer.OnCompleted();
                                return;
                            }

                            var jsonOpts = _workspace.Hub.JsonSerializerOptions;
                            var currentNode = System.Text.Json.JsonSerializer
                                .SerializeToNode(current, jsonOpts) as System.Text.Json.Nodes.JsonObject
                                ?? new System.Text.Json.Nodes.JsonObject();
                            var updatedNode = System.Text.Json.JsonSerializer
                                .SerializeToNode(updated, jsonOpts) as System.Text.Json.Nodes.JsonObject
                                ?? new System.Text.Json.Nodes.JsonObject();
                            // 🚨 Diff the lambda's ACTUAL output FIRST — before the audit stamp
                            // below. The stamp used to run first, so a lambda that changed
                            // NOTHING (a rebuilt-but-identical content slips past the
                            // record-Equals check above) still produced a lastModified-only
                            // patch, and the owner minted a Version + persisted a history row
                            // for it on every save (the v1170-without-edits report). Only a REAL
                            // change earns the audit stamp and the send.
                            var patch = ComputeMergePatchDiff(currentNode, updatedNode);

                            if (patch.Count == 0)
                            {
                                diagLogger?.LogDebug(
                                    "[UpdateRemote] NO-OP hub={Hub} target={Path} — diff empty after serialisation",
                                    _workspace.Hub.Address, _path);
                                onLocalState?.Invoke(current);
                                observer.OnNext(current);
                                observer.OnCompleted();
                                return;
                            }

                            // Auto-stamp LastModified + LastModifiedBy when the lambda left
                            // them untouched. The OWN-stream's DataChangedEvent fan-out
                            // includes them in the diff, so consumers that want a
                            // content-change tick read them directly off their MeshNode
                            // emission. LastModifiedBy = the caller's AUTHENTICATED identity
                            // (capturedContextAtEntry) — the same AccessContext stamped on
                            // the outgoing patch, so a client can't forge a different author.
                            // This preserves the audit trail UpdateNodeRequest used to stamp
                            // from UpdatedBy now that writes go through stream.Update.
                            var stamped = false;
                            if (updated.LastModified == current.LastModified)
                            {
                                updated = updated with { LastModified = DateTimeOffset.UtcNow };
                                stamped = true;
                            }
                            if (updated.LastModifiedBy == current.LastModifiedBy
                                && !string.IsNullOrEmpty(capturedContextAtEntry?.ObjectId))
                            {
                                updated = updated with { LastModifiedBy = capturedContextAtEntry.ObjectId };
                                stamped = true;
                            }
                            if (stamped)
                                // 🚨 O(1) in the node — issue #1284. The stamp touches exactly two
                                // TOP-LEVEL SCALARS, and the answer to "what did that change in the
                                // patch?" is known before asking. Re-serialising the whole node and
                                // re-walking the whole merge diff to rediscover it was a second
                                // full pass over the document on EVERY cross-hub write — for a
                                // streaming answer cell, 40 kB walked twice per 100 ms tick for the
                                // length of the answer. The two splice PRs are post-passes over the
                                // patch this code builds, so neither of them touched it.
                                //
                                // 🚨 Worth having, but NOT the 44% #1172's profile indicted:
                                // WriteConstructionAllocationTest measures this whole prologue at
                                // ~7-9 bytes per document character against ~85 for the write end
                                // to end, so under a tenth of the document-proportional cost lives
                                // here. The rest is downstream — the owner's merge, persistence and
                                // its version row, and one fan-out per subscriber.
                                //
                                // The outcome is IDENTICAL to the re-diff, not merely close: the
                                // per-key decision is the same DeepEquals against the same
                                // currentNode that ComputeMergePatchDiff would make, and the stamp
                                // cannot affect any other key. (`updatedNode` is deliberately left
                                // pre-stamp — nothing reads it past this point.)
                                StampAuditFields(patch, currentNode, updated, jsonOpts);

                            var patchJson = patch.ToJsonString(jsonOpts);
                            diagLogger?.LogDebug(
                                "[UpdateRemote] POST-PATCH hub={Hub} target={Path} keys={Keys}",
                                _workspace.Hub.Address, _path, patch.Count);

                            // Post PatchDataRequest to the OWNER. The owner reads its
                            // OWN current state, recursively merges the diff (RFC 7396),
                            // and commits — leaving any fields not in the diff intact.
                            //
                            // CRITICAL: stamp the caller's AccessContext on the
                            // outgoing delivery. Without this, Orleans routing
                            // delivers the request with accessContext=null, the
                            // owner's RLS denies it, and the patch silently drops
                            // → mirror never sees an echo → caller hangs on the
                            // 10s post-update timeout. The PostPipeline warning
                            // "<msg> posted with no AccessContext" surfaces this.
                            //
                            // Use the eagerly-captured context from the Observable.Create
                            // entry above — the AsyncLocal at THIS callback's
                            // thread is unreliable (the initialSub callback can land
                            // on the workspace emission scheduler with no context
                            // flow). The captured value reflects the caller's
                            // identity at the moment Update was invoked, which is
                            // what we want stamped on the outbound patch.
                            // Carry the writer's BASE value at exactly the changed leaves so the OWNER can
                            // THREE-WAY merge a reordered/stale patch (string edits splice-merge, conflicting
                            // scalars are refused) instead of blindly flapping a field — see MeshNodePatchMerge.
                            var baseValues = MeshNodePatchMerge.ExtractBaseValues(currentNode, patch);

                            var capturedContext = capturedContextAtEntry;
                            // 🚨 NOT posted here any more (#2882). The old shape was
                            // Post(...) → arm the late watch → Observe(delivery), which registers
                            // the response subject only AFTER the delivery is on its way. The hub
                            // DROPS a response whose id has no registered subject yet ("No subject
                            // found for response … treating as processed", HandleCallbacks), and a
                            // WARM owning per-node hub answers in sub-millisecond time — so a
                            // thread-pool preemption between Post and Observe lost the verdict and
                            // the caller waited out the full 31 s WriteVerdictBound with a trail
                            // that could only say REGISTERED_AFTER_POST. The read path had the
                            // identical defect and the identical fix (GetMeshNode's Issue(), pinned
                            // by GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost).
                            // The request and options are built here; the post happens inside
                            // Observe(request, options, requestId) below, AFTER the late watch is
                            // armed and the subject is registered.
                            var patchRequest = new PatchDataRequest(new MeshNodeReference(), new RawJson(patchJson))
                            {
                                BaseValues = baseValues is null
                                    ? null
                                    : new RawJson(baseValues.ToJsonString(jsonOpts))
                            };
                            Func<PostOptions, PostOptions> patchPostOptions = o =>
                            {
                                o = o.WithTarget(new Address(_path!));
                                return capturedContext is null
                                    ? o
                                    : o.WithAccessContext(capturedContext);
                            };

                            // 🚨 EXACTLY-ONCE caller terminal (#2661). The verdict can now reach
                            // the caller on FOUR seams — the bounded response wait, the late
                            // PatchDataResponse watch, the late DeliveryFailure watch, and the
                            // outer verdict bound — and the last three fire from the cache hub's
                            // action block / a timer, concurrently with each other. Rx's own
                            // single-terminal contract is a rule the SOURCE must keep, not one it
                            // enforces, so the claim is explicit.
                            var callerSettled = 0;
                            bool ClaimTerminal() =>
                                System.Threading.Interlocked.Exchange(ref callerSettled, 1) == 0;

                            // 🚨 Restore the caller's identity around OnNext: we're
                            // on the remote-stream emission thread (opened under
                            // ImpersonateAsSystem), so SwitchAccessContext back to
                            // capturedContextAtEntry (Context ?? CircuitContext) so
                            // the caller's Subscribe sees their identity, not
                            // system-security.
                            //
                            // 🚨 This is the SUCCESS terminal and it is now reached from exactly
                            // one place: the owner's ACK, early or late. It is no longer reachable
                            // from a timeout — see UpdateResponseWaitBound.
                            void EmitTerminal()
                            {
                                if (!ClaimTerminal()) return;
                                if (accessServiceAtEntry is not null && capturedContextAtEntry is not null)
                                {
                                    using (accessServiceAtEntry.SwitchAccessContext(capturedContextAtEntry))
                                    {
                                        observer.OnNext(updated);
                                        observer.OnCompleted();
                                    }
                                }
                                else
                                {
                                    observer.OnNext(updated);
                                    observer.OnCompleted();
                                }
                            }

                            // The FAILURE terminal, same identity restoration as EmitTerminal so a
                            // caller's OnError callback runs as the caller and not as
                            // system-security. Every rejection path funnels through here.
                            void RaiseError(Exception error)
                            {
                                if (accessServiceAtEntry is not null && capturedContextAtEntry is not null)
                                {
                                    using (accessServiceAtEntry.SwitchAccessContext(capturedContextAtEntry))
                                        observer.OnError(error);
                                }
                                else
                                {
                                    observer.OnError(error);
                                }
                            }

                            void FailTerminal(Exception error)
                            {
                                if (ClaimTerminal()) RaiseError(error);
                            }

                            // A re-enqueued attempt IS this write, still in flight, so its outcome
                            // is the caller's outcome. OnNext rides through unclaimed (the claim is
                            // the TERMINAL, and a value precedes it); the terminal claims.
                            //
                            // 🚨 `attachToCaller` is not a detail. The EARLY
                            // re-enqueue runs while the caller is demonstrably still subscribed, so
                            // it joins the composite and dies with the caller — cancelling a write
                            // nobody is waiting for is right. The LATE one must NOT: it fires from
                            // the cache hub's action block, possibly after a fire-and-forget caller
                            // has walked away, and the whole point of the late-NACK re-enqueue is
                            // that a provably-unapplied write still LANDS (LateNackReenqueueTest).
                            // Tying it to the composite would cancel exactly the recovery it exists
                            // to perform. A terminal delivered to a detached observer is a safe
                            // no-op, so chaining costs nothing there.
                            void ChainTerminal(IObservable<MeshNode> reattempt, bool attachToCaller)
                            {
                                var sub = reattempt.Subscribe(
                                    n =>
                                    {
                                        if (System.Threading.Volatile.Read(ref callerSettled) == 0)
                                            observer.OnNext(n);
                                    },
                                    FailTerminal,
                                    () =>
                                    {
                                        if (ClaimTerminal())
                                            observer.OnCompleted();
                                    });
                                if (attachToCaller)
                                    composite.Add(sub);
                            }

                            // 🚨 Arm the LATE-response watch BEFORE opening the bounded wait —
                            // exactly-once hand-off: a response racing the 2s timeout is either
                            // consumed by the still-pending Observe callback (which Completes the
                            // watch synchronously, below) or falls through to the cache hub's
                            // PatchDataResponse handler, which finds the watch still armed. A
                            // LATE NACK whose code is OwnerDisposing (the owner's explicit "the
                            // patch NEVER applied") re-enqueues the ORIGINAL update lambda —
                            // recursion through UpdateRemote re-reads the freshest state and
                            // re-diffs, so it is idempotent and ordering-safe (a superseding
                            // write makes the re-diff a no-op). Silence and every other late
                            // code are never retried. See LatePatchResponseRegistry.
                            var lateRegistry = _workspace.Hub.ServiceProvider
                                .GetService<LatePatchResponseRegistry>();
                            // 🚨 Minted BEFORE the post — the whole point of the #2882 seam. The
                            // late watch below and the response subject are both keyed by this id,
                            // and both must exist before anything can answer.
                            var requestId = Guid.NewGuid().AsString();
                            lateRegistry?.Register(requestId, _path!, resp =>
                            {
                                // 🚨 EVERY branch below now settles the CALLER too (#2661). Before,
                                // the caller had already been completed as a success at
                                // UpdateResponseWaitBound, so a late verdict could only be logged;
                                // now the late verdict IS the caller's terminal.

                                if (resp.Success && resp.NodeError is null)
                                {
                                    // 🚨 A LATE ack is STILL the owner's ack — the one signal that
                                    // may become the next queued write's base (#2346). It arrives
                                    // here rather than above only because the owner was busy past
                                    // UpdateResponseWaitBound, which is precisely the load under
                                    // which the successor most needs a sound base: without this the
                                    // hand-off was skipped on every slow owner and the successor
                                    // diffed against a mirror that predated this very write, which
                                    // the owner then refused as a conflict that never happened.
                                    // Publishing here also RELEASES the queue slot, so the successor
                                    // starts on the ack instead of sitting out QueueAdvanceBound.
                                    onLocalState?.Invoke(updated);
                                    diagLogger?.LogDebug(
                                        "[UpdateRemote] LATE_ACK hub={Hub} target={Path} — the owner committed; completing the caller on its verdict",
                                        _workspace.Hub.Address, _path);
                                    // The owner COMMITTED. That — not the elapsed bound — is what
                                    // "saved" means, so this is the caller's success terminal.
                                    EmitTerminal();
                                    return;
                                }
                                var lateErr = resp.NodeError ?? new MeshNodeError(
                                    MeshNodeErrorCode.Unknown, _path!,
                                    resp.Error ?? "Update rejected by owner");
                                // OwnerNotReady carries the same provably-never-applied contract
                                // as OwnerDisposing (activation had not loaded its state — #667),
                                // so the same idempotent re-enqueue applies — and so does Conflict,
                                // which the owner emits only when nothing landed (see the primary
                                // rejection site for the concurrent-writer data loss it caused).
                                if (lateErr.Code is MeshNodeErrorCode.OwnerDisposing
                                        or MeshNodeErrorCode.OwnerNotReady
                                        or MeshNodeErrorCode.Conflict
                                    && attempt < MaxOwnerDisposingReenqueues)
                                {
                                    // 🚨 Conflict alone carries a stale BASE — see RebaseSource.
                                    // OwnerDisposing / OwnerNotReady mean the patch never reached
                                    // a merge, so there is no newer version to wait for and
                                    // waiting would only burn the bound.
                                    var lateRebaseFrom = lateErr.Code == MeshNodeErrorCode.Conflict
                                        ? current.Version
                                        : 0;
                                    diagLogger?.LogWarning(
                                        "[UpdateRemote] LATE_NACK_REENQUEUE hub={Hub} target={Path} attempt={Attempt} code={Code} — the patch was never applied; re-enqueueing the original update, rebased on state newer than {RebaseFrom}",
                                        _workspace.Hub.Address, _path, attempt + 1, lateErr.Code, lateRebaseFrom);
                                    // Restore the writer's identity: this callback runs on the
                                    // cache hub's action block where the AsyncLocal context is
                                    // the hub's own, and the re-posted patch must carry the
                                    // ORIGINAL caller's AccessContext (UpdateRemote's eager
                                    // capture runs synchronously inside Subscribe).
                                    // 🚨 Settle-only hand-off, same rule as the early re-enqueue:
                                    // the slot (if this NACK beat QueueAdvanceBound) is released by
                                    // the re-attempt's verdict, and a re-attempt's result is never
                                    // published as a successor's base.
                                    using (accessServiceAtEntry is not null && capturedContextAtEntry is not null
                                        ? accessServiceAtEntry.SwitchAccessContext(capturedContextAtEntry)
                                        : null)
                                    {
                                        // 🚨 CHAINED to the caller, exactly like the early
                                        // OWNER_NACK_REENQUEUE arm (#2661). The re-attempt is this
                                        // write still in flight, so its verdict is the caller's;
                                        // swallowing it into a log line was only defensible while
                                        // the caller had already been told "saved".
                                        ChainTerminal(
                                            UpdateRemote(update, attempt + 1, lateRebaseFrom,
                                                    onLocalState: onLocalState is null
                                                        ? null
                                                        : _ => onLocalState(null))
                                                .Do(_ => { }, ex2 => diagLogger?.LogWarning(ex2,
                                                    "[UpdateRemote] LATE_NACK_REENQUEUE failed hub={Hub} target={Path} attempt={Attempt}",
                                                    _workspace.Hub.Address, _path, attempt + 1)),
                                            attachToCaller: false);
                                    }
                                }
                                else
                                {
                                    // Terminal: the write provably did not apply and will not be
                                    // retried, so there is nothing to hand forward and nothing left
                                    // to wait for — release the slot rather than stall it.
                                    onLocalState?.Invoke(null);
                                    diagLogger?.LogWarning(
                                        "[UpdateRemote] LATE_NACK_TERMINAL hub={Hub} target={Path} code={Code} attempt={Attempt} msg={Msg} — the write did NOT apply and is not auto-retryable; faulting the caller",
                                        _workspace.Hub.Address, _path, lateErr.Code, attempt, lateErr.Message);
                                    FailTerminal(new MeshNodeStreamException(lateErr));
                                }
                            },
                            failure =>
                            {
                                // 🚨🚨 THE #2661 DEFECT, closed. An RLS refusal is NOT a
                                // PatchDataResponse — AccessControlPipeline posts a
                                // DeliveryFailure{ErrorType.Unauthorized} ahead of the owner's
                                // action block. When it beat UpdateResponseWaitBound it already
                                // faulted the caller (the OWNER_DENIED arm below); when it LOST
                                // that race it reached nothing at all, and the caller kept a
                                // success for a write the owner had refused. It now lands here and
                                // faults the caller with the SAME exception the early arm raises,
                                // because a denial is a denial whenever it arrives.
                                //
                                // Nothing landed, so there is no base to hand forward: release the
                                // queue slot rather than let the successor sit out QueueAdvanceBound.
                                onLocalState?.Invoke(null);
                                if (failure.ErrorType == ErrorType.Unauthorized)
                                {
                                    diagLogger?.LogWarning(
                                        "[UpdateRemote] LATE_OWNER_DENIED hub={Hub} target={Path} msg={Msg} — the denial arrived after the response bound; faulting the caller",
                                        _workspace.Hub.Address, _path, failure.Message);
                                    FailTerminal(new UnauthorizedAccessException(
                                        failure.Message ?? $"Access denied updating '{_path}'"));
                                    return;
                                }
                                diagLogger?.LogWarning(
                                    "[UpdateRemote] LATE_DELIVERY_FAILURE hub={Hub} target={Path} errorType={ErrorType} msg={Msg}",
                                    _workspace.Hub.Address, _path, failure.ErrorType, failure.Message);
                                FailTerminal(new MeshNodeStreamException(new MeshNodeError(
                                    MeshNodeErrorCode.Unknown, _path!,
                                    failure.Message ?? $"Update of '{_path}' failed: {failure.ErrorType}")));
                            });

                            // 🚨 The caller's terminal is the OWNER'S COMMIT VERDICT — never a
                            // bound expiring (#2661). add and delete have always worked this way
                            // (RequestChange → DataChangeStatus.Committed, else a real failure);
                            // update shipped a PatchDataRequest instead — because a cross-hub
                            // writer holds only a MIRROR of the node, so its own workspace's
                            // RequestChange would commit into the mirror and the owner would never
                            // see the write — and then guessed the verdict when the bound expired.
                            // The verdict machinery was never the problem: the owner's ack is
                            // strictly STRONGER than add/delete's Committed (identity-gated
                            // post-commit emission → IPostCommitFlush durable flush → ack). Only
                            // the caller's terminal was decoupled from it.
                            //
                            // So this subscription is the FAST seam, not the whole wait. It carries
                            // the verdict when it arrives promptly — a denial (RLS →
                            // UnauthorizedAccessException), a rejection (→ MeshNodeStreamException),
                            // a re-enqueueable NACK, or the ack. When it does NOT, expiry hands the
                            // wait to LatePatchResponseRegistry (armed above, and now watching
                            // DeliveryFailure as well as PatchDataResponse) and emits NOTHING.
                            // The value a success finally carries is still the locally-computed
                            // snapshot — the RFC 7396 patch is deterministic, so `updated` matches
                            // the owner's reconciled state.
                            //
                            // Every terminal below disarms the late watch (Complete); ONLY the
                            // TimeoutException branch leaves it armed — that is the one case where
                            // the owner's verdict is still outstanding.
                            // Registers the subject under requestId, THEN posts (#2882). Null =
                            // the owner address could not be resolved — nothing was posted and
                            // nothing will answer, so disarm the watch armed above and fail the
                            // caller exactly as the old Post-returned-null branch did.
                            var responseObservable = _workspace.Hub.Observe(
                                patchRequest, patchPostOptions, requestId);
                            if (responseObservable is null)
                            {
                                lateRegistry?.TryComplete(requestId);
                                FailTerminal(new MeshNodeStreamException(new MeshNodeError(
                                    MeshNodeErrorCode.OwnerUnreachable,
                                    _path!,
                                    "Post of PatchDataRequest returned null — owner address could not be resolved")));
                                return;
                            }
                            var responseSub = responseObservable
                                .Timeout(UpdateResponseWaitBound)
                                .Take(1)
                                .Subscribe(
                                    d =>
                                    {
                                        lateRegistry?.Complete(requestId);
                                        // Owner posted a structured rejection (deserialization /
                                        // validation gate) as PatchDataResponse.NodeError, or a
                                        // non-success ack → surface as a typed exception (fail-fast).
                                        if (d.Message is PatchDataResponse resp
                                            && (resp.NodeError is not null || !resp.Success))
                                        {
                                            var err = resp.NodeError ?? new MeshNodeError(
                                                MeshNodeErrorCode.Unknown, _path!,
                                                resp.Error ?? "Update rejected by owner");
                                            // 🚨 Refuse-and-REDIRECT (#648): OwnerDisposing /
                                            // OwnerNotReady are the owner's explicit statement
                                            // that the patch was provably NEVER applied — a
                                            // superseded activation quiescing, or an activation
                                            // whose durable seed had not loaded. Re-enqueue the
                                            // SAME update against the fresh/loaded activation
                                            // (the re-diff against the freshest state makes it
                                            // idempotent), chaining its outcome to THIS caller.
                                            // Every other code stays a fail-fast terminal.
                                            // 🚨 Conflict belongs to this set, and dropping it
                                            // was silent data loss. The owner emits Conflict ONLY
                                            // when the merge refused keys AND the node is
                                            // byte-identical to its pre-merge state — i.e. nothing
                                            // landed — and its own message says "re-read and
                                            // re-apply". Nobody did: the write was surfaced as a
                                            // terminal error and the caller's change vanished.
                                            // That is exactly what stream.Update promises NOT to
                                            // do; concurrent writers are supposed to coalesce.
                                            // Re-enqueueing re-runs the update lambda against the
                                            // owner's current state, which IS the re-read-and-
                                            // re-apply the owner asked for.
                                            //
                                            // Measured: 5 concurrent TrackActivity calls, 4
                                            // increments (UserActivityTrackingTests
                                            // .TrackActivity_ConcurrentSamePath_DoesNotRaceAlreadyExists,
                                            // CI shard 1) — one writer composed against a base the
                                            // other four had already moved, was refused, and its
                                            // increment was thrown away.
                                            if (err.Code is MeshNodeErrorCode.OwnerDisposing
                                                    or MeshNodeErrorCode.OwnerNotReady
                                                    or MeshNodeErrorCode.Conflict
                                                && attempt < MaxOwnerDisposingReenqueues)
                                            {
                                                // 🚨 A Conflict says the owner is provably PAST
                                                // the base we diffed against — it committed the
                                                // winning write before answering us. Re-running
                                                // the lambda against the mirror as it stands
                                                // recomputes the same patch from the same base
                                                // and is refused identically, which is why the
                                                // re-enqueue could not converge (#1910). Carry
                                                // the refused version so the re-attempt rebases
                                                // on state newer than it. OwnerDisposing /
                                                // OwnerNotReady never reached a merge, so there
                                                // is nothing newer to wait for — they pass 0.
                                                var rebaseFrom = err.Code == MeshNodeErrorCode.Conflict
                                                    ? current.Version
                                                    : 0;
                                                diagLogger?.LogWarning(
                                                    "[UpdateRemote] OWNER_NACK_REENQUEUE hub={Hub} target={Path} code={Code} attempt={Attempt} — the patch was never applied; re-enqueueing rebased on state newer than {RebaseFrom}",
                                                    _workspace.Hub.Address, _path, err.Code, attempt + 1, rebaseFrom);
                                                // 🚨 The queue slot stays HELD across the re-attempt
                                                // and is released by ITS verdict — the re-attempt is
                                                // this write, still in flight, and letting the
                                                // successor start alongside it would put two writes
                                                // on the same path at once, which is the exact thing
                                                // the queue exists to prevent. The settle-only
                                                // callback passed down releases the slot without
                                                // publishing a base: a re-attempt's result must never
                                                // become a successor's base, because it runs
                                                // asynchronously and could land after a LATER write
                                                // already published, replacing a newer base with an
                                                // older one.
                                                using (accessServiceAtEntry is not null && capturedContextAtEntry is not null
                                                    ? accessServiceAtEntry.SwitchAccessContext(capturedContextAtEntry)
                                                    : null)
                                                {
                                                    ChainTerminal(UpdateRemote(update, attempt + 1, rebaseFrom,
                                                            onLocalState: onLocalState is null
                                                                ? null
                                                                : _ => onLocalState(null)),
                                                        attachToCaller: true);
                                                }
                                                return;
                                            }
                                            diagLogger?.LogWarning(
                                                "[UpdateRemote] OWNER_REJECTED hub={Hub} target={Path} code={Code} msg={Msg}",
                                                _workspace.Hub.Address, _path, err.Code, err.Message);
                                            FailTerminal(new MeshNodeStreamException(err));
                                        }
                                        else
                                        {
                                            // Success ack — patch accepted; the activity is started.
                                            //
                                            // 🚨 ONLY HERE may this write's result become the next
                                            // queued write's base (#2305 / #2291). The ack is the
                                            // owner stating it took the patch, and nothing weaker
                                            // will do: publishing the OPTIMISTIC snapshot instead
                                            // hands the successor a value the owner may never have
                                            // taken, and — because a write that did not land mints
                                            // no version — nothing ever clears it. A caller that
                                            // retries the same write then diffs against its own
                                            // unlanded value, computes an EMPTY patch, and skips
                                            // the write silently, forever
                                            // (TwoSiloRecycleConvergenceTest: the post-recycle
                                            // write retried past a disposing owner and the store
                                            // never advanced).
                                            //
                                            // 🚨 THIS call is what releases the queue slot — the
                                            // hand-off, not EmitTerminal. #2346 moved the release
                                            // off the caller's terminal for exactly the reason this
                                            // whole comment gives, and MeshNodeStreamCache says so
                                            // at the advance site ("Advance the per-path queue on
                                            // the HAND-OFF … NOT on req.Result"). The stale claim
                                            // that EmitTerminal releases the slot is what made
                                            // #2661's own analysis fear that holding the caller's
                                            // terminal would stall the successor. It does not:
                                            // the successor waits on the hand-off, which is issued
                                            // right here, on the ack.
                                            onLocalState?.Invoke(updated);
                                            EmitTerminal();
                                        }
                                    },
                                    ex =>
                                    {
                                        // RLS denial: the AccessControlPipeline's
                                        // [RequiresPermission(Update)] gate posts
                                        // DeliveryFailure(Unauthorized) → surfaced here as
                                        // DeliveryFailureException. Map to UnauthorizedAccessException
                                        // (fail-fast); its Message already reads
                                        // "Access denied: user '…' lacks Update …".
                                        if (ex is DeliveryFailureException dfx
                                            && dfx.Failure?.ErrorType == ErrorType.Unauthorized)
                                        {
                                            lateRegistry?.Complete(requestId);
                                            diagLogger?.LogWarning(
                                                "[UpdateRemote] OWNER_DENIED hub={Hub} target={Path} msg={Msg}",
                                                _workspace.Hub.Address, _path, dfx.Failure.Message);
                                            FailTerminal(new UnauthorizedAccessException(
                                                dfx.Failure.Message ?? $"Access denied updating '{_path}'"));
                                        }
                                        else if (ex is TimeoutException)
                                        {
                                            // 🚨🚨 THE #2661 FIX. This branch used to call
                                            // EmitTerminal() — the optimistic snapshot, emitted AND
                                            // COMPLETED as a success — which is the write path
                                            // failing OPEN: a bound expiring is not a commit, and a
                                            // denial or rejection that lost the race against it
                                            // reached nobody. "Saved" now means the owner committed,
                                            // exactly as it does for add and delete, so this branch
                                            // emits NOTHING and completes NOTHING. It only HANDS THE
                                            // WAIT OVER: the late watch is already armed and, since
                                            // #2661, watches DeliveryFailure too.
                                            //
                                            // The queue slot is untouched here, deliberately — it is
                                            // released by the HAND-OFF (onLocalState), never by the
                                            // caller's terminal (#2346), so holding the caller open
                                            // does NOT stall the successor.
                                            if (lateRegistry is null)
                                            {
                                                // No registry on this hub (the bypass-cache escape
                                                // hatch) ⇒ there is no seam a late verdict could
                                                // arrive on, so waiting for one could only hang.
                                                // Keep the old optimistic terminal, and say so —
                                                // this is the one place the fail-open survives, and
                                                // it survives because nothing better is reachable.
                                                diagLogger?.LogWarning(
                                                    "[UpdateRemote] RESPONSE_TIMEOUT hub={Hub} target={Path} — no LatePatchResponseRegistry on this hub, so the owner's verdict can never be observed; completing OPTIMISTICALLY (unconfirmed)",
                                                    _workspace.Hub.Address, _path);
                                                EmitTerminal();
                                                return;
                                            }
                                            diagLogger?.LogDebug(
                                                "[UpdateRemote] RESPONSE_TIMEOUT hub={Hub} target={Path} — owner busy; the caller now waits on the late watch for the commit verdict",
                                                _workspace.Hub.Address, _path);

                                            // 🚨 The OUTER verdict bound. Past
                                            // LateResponseWatchBound the registry itself stops
                                            // honouring a response, so without this the caller
                                            // would wait forever on a verdict nothing can deliver.
                                            //
                                            // It FAULTS rather than completing optimistically, and
                                            // that is a deliberate judgement, not an oversight:
                                            // silence here is not "the owner is busy" — a busy
                                            // owner acks late and the LATE_ACK arm above completes
                                            // the caller as a success. Silence here means the owner
                                            // produced NO terminal at all inside a window built to
                                            // dominate every owner-side terminal path (the 20s
                                            // identity-gated ack watcher, the 10s post-commit
                                            // flush, and RegisterOwnerDisposingNack for teardown).
                                            // At that point we do not know whether the write landed,
                                            // and reporting "saved" for a write we cannot confirm is
                                            // the very defect this change closes — one bound later.
                                            // The write stays posted and the update lambda re-diffs
                                            // idempotently, so a caller that retries loses nothing.
                                            // The bound the caller actually experiences, measured from the
                                            // post — reported verbatim in the log and the error so a
                                            // diagnostic never names a deadline different from the one that
                                            // fired.
                                            var verdictBound = LatePatchResponseRegistry.LateResponseWatchBound
                                                               + VerdictBoundGrace;
                                            var verdictBoundSeconds = verdictBound.TotalSeconds;
                                            composite.Add(Observable
                                                .Timer(verdictBound - UpdateResponseWaitBound)
                                                .Subscribe(_ =>
                                                {
                                                    // 🚨 ARBITRATE FIRST, act second — and arbitrate on the
                                                    // REGISTRY ENTRY, not on a settled-flag. Dispatch removes the
                                                    // entry and THEN runs the late callback, so between those two
                                                    // steps a verdict is provably in flight while nothing has been
                                                    // claimed yet; a deadline that read the flag could fire in that
                                                    // window and fault a write the owner had actually answered.
                                                    // Taking the entry is the one act that proves no verdict has
                                                    // claimed this patch. Losing means the verdict wins — correct.
                                                    if (!lateRegistry.TryComplete(requestId)) return;
                                                    if (!ClaimTerminal()) return;
                                                    // Only now may this touch shared state: the hand-off is
                                                    // released with nothing to publish, because nothing landed.
                                                    onLocalState?.Invoke(null);
                                                    // 🚨 The TRAIL is the diagnostic. "No terminal" names a
                                                    // symptom; the request-fate ledger names the EDGE — never
                                                    // received by the owner, DEFERRED behind an init gate that
                                                    // never opened, HANDLER_EXIT with no reply, or a reply
                                                    // RESPONSE_POSTED to a hub nobody awaited on. Seven distinct
                                                    // tests failed with this one sentence on 2026-08-30
                                                    // (MeshWeaver.Plugins#941) and not one failure block could
                                                    // say which; the trail survives the requester's own 2 s
                                                    // timeout precisely so it can be read here.
                                                    var trail = _workspace.Hub.DescribeRequestFate(requestId);
                                                    diagLogger?.LogWarning(
                                                        "[UpdateRemote] VERDICT_TIMEOUT hub={Hub} target={Path} bound={BoundSeconds}s — the owner produced no terminal for this patch inside the late-response window, which dominates every owner-side terminal path; the write is NOT confirmed. Request trail: {Trail}",
                                                        _workspace.Hub.Address, _path, verdictBoundSeconds, trail);
                                                    RaiseError(new MeshNodeStreamException(new MeshNodeError(
                                                        MeshNodeErrorCode.OwnerUnreachable, _path!,
                                                        $"The owner of '{_path}' returned no verdict for this update within "
                                                        + $"{verdictBoundSeconds:0}s. "
                                                        + "The patch was posted and may still apply, but it is NOT confirmed — "
                                                        + "re-read the node and re-apply if the change is required. "
                                                        + $"Request trail: {trail}")));
                                                }));
                                        }
                                        else
                                        {
                                            lateRegistry?.Complete(requestId);
                                            // Other owner-side / delivery error → surface it (fail-fast).
                                            diagLogger?.LogWarning(ex,
                                                "[UpdateRemote] response wait errored hub={Hub} target={Path}",
                                                _workspace.Hub.Address, _path);
                                            var msg = ex is DeliveryFailureException d2
                                                ? (d2.Failure?.Message ?? ex.Message)
                                                : ex.Message;
                                            FailTerminal(new MeshNodeStreamException(
                                                new MeshNodeError(MeshNodeErrorCode.Unknown, _path!, msg)));
                                        }
                                    });
                            composite.Add(responseSub);
                        }
                        catch (Exception ex)
                        {
                            observer.OnError(ex);
                        }
                    },
                    ex =>
                    {
                        diagLogger?.LogWarning(ex,
                            "[UpdateRemote] ERROR hub={Hub} target={Path} type={ExType}",
                            _workspace.Hub.Address, _path, ex.GetType().Name);
                        if (ex is TimeoutException)
                        {
                            // 🚨 The original rides as INNER — this sentence names the bound, not
                            // the terminal that ended the wait, and the two are routinely different
                            // (an owner that never answered the SubscribeRequest inside the request
                            // budget errors with a TimeoutException of its own). Flattening it left
                            // the boot-install failures claiming a 30s wait that never happened
                            // (#2387).
                            observer.OnError(new TimeoutException(
                                $"Update aborted: no initial state arrived for '{_path}' within 30s. " +
                                "Likely causes — (1) RLS silently rejected the prior CreateNode, " +
                                "(2) the path is misspelled / points at a namespace no NodeType claims, " +
                                "(3) the node was deleted between create and update, or (4) the per-node " +
                                "hub activated but its MeshDataSource didn't load the node from persistence.",
                                ex));
                        }
                        else
                        {
                            observer.OnError(ex);
                        }
                    });
            composite.Add(initialSub);
            return composite;
        });

    /// <summary>
    /// Writes the audit stamp (<see cref="MeshNode.LastModified"/> /
    /// <see cref="MeshNode.LastModifiedBy"/>) straight into an already-computed merge patch, in
    /// O(1) — the cheap half of issue #1284's input side.
    ///
    /// <para>Each key is emitted under exactly the condition
    /// <see cref="MeshNodeStreamHandle.ComputeMergePatchDiff"/> would emit it under: its serialised
    /// value is not <c>DeepEquals</c> to the same key in <paramref name="currentNode"/>. So this is
    /// the re-diff's answer for these two keys, arrived at without walking the document — and it
    /// cannot differ, because a top-level scalar assignment reaches no other key.</para>
    ///
    /// <para>Key names are derived the way <c>System.Text.Json</c> derives them (the same
    /// <c>PropertyNamingPolicy.ConvertName</c> convention as <c>DataExtensions</c>'s content/trigger
    /// keys), so a naming-policy change carries automatically rather than silently writing a key
    /// nobody reads. A wrong key is the one failure mode with no runtime signal — the write would
    /// succeed carrying a property nothing deserialises — so it is pinned end-to-end by
    /// <c>WriteConstructionAllocationTest.AWriteStillStampsTheAuditFieldsOnTheOwner</c>, which reads
    /// the TYPED audit fields back off the owner after a cross-hub write.</para>
    /// </summary>
    private static void StampAuditFields(
        System.Text.Json.Nodes.JsonObject patch,
        System.Text.Json.Nodes.JsonObject currentNode,
        MeshNode updated,
        System.Text.Json.JsonSerializerOptions jsonOpts)
    {
        Stamp(AuditJsonKey(nameof(MeshNode.LastModified), jsonOpts), updated.LastModified);
        Stamp(AuditJsonKey(nameof(MeshNode.LastModifiedBy), jsonOpts), updated.LastModifiedBy);

        void Stamp(string key, object? value)
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(value, jsonOpts);
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(currentNode[key], node))
                patch[key] = node;
        }
    }

    /// <summary>The JSON property name <paramref name="clrName"/> serialises to under
    /// <paramref name="jsonOpts"/>.</summary>
    private static string AuditJsonKey(string clrName, System.Text.Json.JsonSerializerOptions jsonOpts)
        => jsonOpts.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;

    /// <summary>
    /// Recursive JSON-merge-patch (RFC 7396) diff between two equally-shaped
    /// JsonObjects. The result, when applied to <paramref name="current"/>
    /// (e.g. via <c>HandlePatchDataRequest</c>'s recursive merge), reproduces
    /// <paramref name="updated"/>. Keys in current and missing in updated
    /// emit <c>null</c> (RFC 7396 remove). Equal values emit nothing.
    /// <para>🚨 A changed <b>large string</b> leaf is emitted as a SPLICE
    /// (<see cref="PatchStringSplice"/>) rather than the whole new value, so a field that
    /// grows one chunk at a time — an agent response cell streaming tokens — costs
    /// <c>O(chunk)</c> per write instead of <c>O(length)</c>. Below
    /// <see cref="PatchStringSplice.MinSpliceLength"/>, or when the splice would not
    /// actually be smaller, the full value is emitted exactly as before.</para>
    /// </summary>
    // internal (not private) so a test can drive the REAL diff a cross-hub write ships, rather than a
    // hand-rolled stand-in that would drift from it — see ResponseTextSurvivesUnechoedWriteTest.
    internal static System.Text.Json.Nodes.JsonObject ComputeMergePatchDiff(
        System.Text.Json.Nodes.JsonObject current,
        System.Text.Json.Nodes.JsonObject updated)
    {
        var patch = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, updatedValue) in updated)
        {
            var currentValue = current[key];
            if (currentValue is System.Text.Json.Nodes.JsonObject co
                && updatedValue is System.Text.Json.Nodes.JsonObject uo)
            {
                var sub = ComputeMergePatchDiff(co, uo);
                if (sub.Count > 0)
                    patch[key] = sub;
                continue;
            }
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(currentValue, updatedValue))
                continue;
            // Big string that changed on one side → ship only the splice. The matching base
            // fingerprint is written by MeshNodePatchMerge.ExtractBaseValues, which recognises
            // this shape; the owner applies the splice only once that fingerprint proves its
            // live text is the text this diff was computed against.
            if (currentValue is System.Text.Json.Nodes.JsonValue cv
                && cv.TryGetValue<string>(out var oldStr)
                && updatedValue is System.Text.Json.Nodes.JsonValue uv
                && uv.TryGetValue<string>(out var newStr)
                && PatchStringSplice.TryEncode(oldStr, newStr, out var spliced))
            {
                patch[key] = spliced;
                continue;
            }
            patch[key] = updatedValue?.DeepClone();
        }
        // Keys removed in updated → emit null per RFC 7396.
        foreach (var (key, _) in current)
        {
            if (!updated.ContainsKey(key))
                patch[key] = null;
        }
        return patch;
    }
}

/// <summary>
/// Reactive helpers for reading <see cref="MeshNode"/> content from workspaces.
/// Canonical replacement for the lagged
/// <c>QueryAsync&lt;MeshNode&gt;($"path:{path}").FirstOrDefaultAsync()</c> pattern.
/// </summary>
public static class MeshNodeStreamExtensions
{
    /// <summary>
    /// Reactive handle to the current hub's own MeshNode. No query index, no await,
    /// no staleness, live updates on content changes. Compose with <c>.Take(1)</c>
    /// for one-shot reads or keep subscribed for live views.
    /// <para>
    /// The returned <see cref="MeshNodeStreamHandle"/> implements
    /// <see cref="IObservable{MeshNode}"/> so all existing read consumers (Where/Select
    /// chains) keep working. Writers call <c>.Update(update)</c> on the same handle —
    /// returns <c>IObservable&lt;MeshNode&gt;</c> that callers MUST Subscribe to. No
    /// fire-and-forget; subscribe with <c>(_ =&gt; …, ex =&gt; logger.LogWarning(ex, …))</c>.
    /// </para>
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IWorkspace workspace)
        => new(workspace);

    /// <summary>
    /// Reactive handle to a MeshNode at <paramref name="path"/>. Path-aware:
    /// <list type="number">
    ///   <item><description><b>Own hub</b> — when <paramref name="path"/> matches the
    ///     workspace's hub address: handle reads/writes via the local
    ///     <see cref="MeshNodeReference"/> reducer + data source primary stream.</description></item>
    ///   <item><description><b>Cross-hub via <see cref="IMeshNodeStreamCache"/></b> — when
    ///     a cache is registered on the workspace's hub: routes reads through
    ///     <c>cache.GetStream(path)</c> and writes through <c>cache.Update(path, fn)</c>.
    ///     One shared upstream subscription process-wide; writes are observed
    ///     by every reader on the same path.</description></item>
    ///   <item><description><b>Remote (fallback)</b> — when no cache is registered:
    ///     subscribes to and writes through the owning per-node hub via
    ///     <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>.</description></item>
    /// </list>
    /// Callers Subscribe (read) or call <c>.Update(update).Subscribe(...)</c> (write).
    /// If the node does not exist at <paramref name="path"/>, the per-node hub never
    /// activates and the remote subscription does not emit — bound reads with
    /// <c>.Take(1).Timeout(...)</c> and treat absence as "not found".
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IWorkspace workspace, string path)
    {
        // Own-hub path: no cache redirect (same data source; cache wouldn't help).
        // Cross-hub: prefer the cache when one is registered so we share the
        // process-wide upstream + write-coherence with every other reader/writer
        // on the same path. The cache itself MUST NOT call this — it would
        // recurse forever. The cache uses GetMeshNodeStreamBypassCache.
        var ownPath = workspace.Hub.Address.Path;
        if (string.Equals(path, ownPath, StringComparison.Ordinal)
            || string.Equals(path, workspace.Hub.Address.ToString(), StringComparison.Ordinal))
            return new MeshNodeStreamHandle(workspace);

        // NOTE: a hosted-hub fast-path was tried here (route hosted addresses via GetRemoteStream over a
        // MeshNodeReference — bypassCache — instead of the cache). GetMeshNodeStream_FromHostedHub_…
        // disproved it: the cache path round-trips read + write coherence for a hosted per-node hub
        // cleanly, while the raw bypassCache stream leaks the write's DataChangeRequest reply (the cache
        // owns that mirror lifecycle, the bypass does not). The cache IS the correct transport for a
        // cross-hub node — hosted or not — so the decision stays cache-only. (The nested-area CLICK bug is
        // a separate layout-area concern: the ClickedEvent's Area is built root-relative on the client but
        // stored context-relative on the nested area's host, so GetControl misses — not a stream-routing fix.)
        var cache = workspace.Hub.ServiceProvider.GetService(typeof(IMeshNodeStreamCache))
            as IMeshNodeStreamCache;
        return new MeshNodeStreamHandle(workspace, path, cache);
    }

    /// <summary>
    /// Hub-scoped convenience: the canonical way to read/write a single MeshNode
    /// by path from anywhere holding an <see cref="IMessageHub"/>. Abstracts away
    /// the <c>GetWorkspace()</c> hop — callers write <c>hub.GetMeshNodeStream(path)</c>,
    /// never <c>hub.GetWorkspace().GetMeshNodeStream(path)</c>.
    /// <para>The returned handle ALWAYS types <see cref="MeshNode.Content"/> (no
    /// callsite touches <c>JsonSerializerOptions</c>) and routes cross-hub
    /// reads/writes through the shared <see cref="IMeshNodeStreamCache"/>. This is
    /// the replacement for direct <c>cache.GetStream(path)</c> /
    /// <c>cache.Update(path, fn)</c> — those untyped overloads are gone.</para>
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IMessageHub hub, string path)
        => hub.GetWorkspace().GetMeshNodeStream(path);

    /// <summary>
    /// Like <see cref="GetMeshNodeStream(IWorkspace, string)"/> but bypasses the
    /// <see cref="IMeshNodeStreamCache"/>. Used by the cache itself to open its
    /// upstream subscription without recursing back into the cache.
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStreamBypassCache(this IWorkspace workspace, string path)
        => new(workspace, path, bypassCache: true);

    /// <summary>
    /// Forwarder that delegates to <see cref="MeshNodeStreamHandle.Update"/>. Returns
    /// <see cref="IObservable{MeshNode}"/>; CALLERS MUST SUBSCRIBE — the cold observable's
    /// side effect runs on Subscribe, errors flow to <c>OnError</c>.
    /// <para>
    /// Prefer <c>workspace.GetMeshNodeStream().Update(update)</c> at new callsites — uniform
    /// read/write API on a single handle. This forwarder is kept so the existing 30+
    /// callsites can migrate incrementally.
    /// </para>
    /// </summary>
    [Obsolete("Use workspace.GetMeshNodeStream(path?).Update(update).Subscribe(...) — uniform read/write API; callers must subscribe so writes can't be silently dropped.")]
    public static IObservable<MeshNode> UpdateMeshNode(this IWorkspace workspace,
        Func<MeshNode, MeshNode> update,
        string? nodePath = null)
        => (nodePath is null
            ? workspace.GetMeshNodeStream()
            : workspace.GetMeshNodeStream(nodePath)).Update(update);

    /// <summary>
    /// One-shot read of the <see cref="MeshNode"/> at <paramref name="path"/> via
    /// the owning per-node hub's <see cref="MeshNodeReference"/> reducer. Posts a
    /// <see cref="GetDataRequest"/> + registers a callback — true request/response,
    /// no <c>SubscribeRequest</c>, no lingering subscription. Use this instead of
    /// <c>workspace.GetMeshNodeStream(path).Take(1)</c> for handlers / helpers /
    /// click actions that just need the current value once.
    ///
    /// <para>
    /// Emits <c>null</c> when the node is genuinely absent: routing failure (routing
    /// returns DeliveryFailure with NotFound; routing NEVER falls back to an ancestor, so a
    /// returned non-null node is always the requested path), a read-validator verdict that
    /// hides the node, or a response carrying no data. Failures during deserialisation also
    /// fall through as <c>null</c>; turn on debug-level logging on this type to see the
    /// underlying exception.
    /// </para>
    ///
    /// <para>
    /// 🚨 A <b>timeout is NOT</b> one of those: by default it surfaces as a
    /// <see cref="TimeoutException"/> naming the path, the elapsed time and the hub's
    /// in-flight snapshot (see <paramref name="onTimeout"/>). "The read gave up" and
    /// "the node does not exist" are different facts and callers get to tell them apart.
    /// </para>
    ///
    /// <para>
    /// 🚨 The remaining collapse is deliberate and named: "genuinely absent", "being deleted"
    /// and "could not be read" all arrive here as <c>null</c>, because that IS what most callers
    /// want and changing this signature would sweep 100+ of them for no gain. A caller for which
    /// those differ — anything that CREATES, re-applies or persists on absence — must read
    /// <see cref="GetMeshNodeOutcome"/> instead, which is this same read with the distinction
    /// kept. <c>InstanceSyncWorker.PullOne</c> is what happens otherwise: it re-created a node
    /// whose delete was in flight (Systemorph/MeshWeaver#1471).
    /// </para>
    ///
    /// <para>
    /// For a <b>live</b> single-node subscription that re-emits on every change,
    /// use <see cref="GetMeshNodeStream(IWorkspace, string)"/> instead — and stay
    /// subscribed (no <c>.Take(1)</c>). See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    /// <param name="hub">The hub the caller holds. When it is the ROOT MESH HUB (the router), the
    /// read is issued on <see cref="MeshExtensions.NodeOperationIssuingHub"/> instead — the router
    /// must be neither end of a delivery (ROUTER_TRAFFIC).</param>
    /// <param name="path">The mesh path to read.</param>
    /// <param name="timeout">Wall-clock budget for the read; defaults to 10 seconds.</param>
    /// <param name="onTimeout">
    /// What happens when the budget elapses. Defaults to
    /// <see cref="ReadTimeoutBehavior.Throw"/>. Pass <see cref="ReadTimeoutBehavior.EmitNull"/>
    /// ONLY where "indeterminate ⇒ treat as absent" is the documented, deliberate contract of
    /// the caller (a cosmetic fallback, an idempotent-upsert existence probe) — never to
    /// silence a stall you have not reasoned about.
    /// </param>
    public static IObservable<MeshNode?> GetMeshNode(this IMessageHub hub, string path,
        TimeSpan? timeout = null,
        ReadTimeoutBehavior onTimeout = ReadTimeoutBehavior.Throw)
        // ONE implementation, so the interrogable read and the convenience read can never drift:
        // this is GetMeshNodeOutcome with the distinction discarded. Node is non-null exactly for
        // Present, so every non-Present status maps to the null this method has always emitted.
        => hub.GetMeshNodeOutcome(path, timeout, onTimeout).Select(outcome => outcome.Node);

    /// <summary>
    /// The same one-shot read as <see cref="GetMeshNode"/>, but reporting <b>why</b> — see
    /// <see cref="NodeReadStatus"/>. Use this wherever "not there" would lead to a WRITE:
    /// a create, an upsert, a replication apply, a re-persist. Those are precisely the callers
    /// for which "absent", "being deleted" and "I could not read it" must not be the same input.
    ///
    /// <para>The mapping, in full:
    /// <list type="bullet">
    ///   <item><see cref="NodeReadStatus.Present"/> — a node came back.</item>
    ///   <item><see cref="NodeReadStatus.Absent"/> — routing said
    ///     <see cref="ErrorType.NotFound"/>, the owner answered with no data, a read validator
    ///     hid it (a hidden node is invisible by contract), or the reader is a TRANSIENT NODE
    ///     PROBE being asked for its OWN address, which by contract is never a mesh node — that
    ///     one is answered without a round-trip at all (see the guard in the body).</item>
    ///   <item><see cref="NodeReadStatus.DeleteInProgress"/> — the owner answered with its delete
    ///     tombstone (<c>GetDataResponse.Absence</c>).</item>
    ///   <item><see cref="NodeReadStatus.Unavailable"/> — the delivery failed for any other
    ///     reason, the payload would not materialise, or the budget elapsed under
    ///     <see cref="ReadTimeoutBehavior.EmitNull"/>.</item>
    /// </list>
    /// Errors that <see cref="GetMeshNode"/> already surfaces still surface here as
    /// <c>OnError</c>: <see cref="ErrorType.Unauthorized"/>, an
    /// <see cref="ErrorType.ShuttingDown"/> that persists past the whole budget (the paced
    /// re-probe loop exhausted — <see cref="AddressRecyclingException"/>), and a timeout under
    /// <see cref="ReadTimeoutBehavior.Throw"/>.</para>
    /// </summary>
    /// <param name="hub">The hub the caller holds (see <see cref="GetMeshNode"/>).</param>
    /// <param name="path">The mesh path to read.</param>
    /// <param name="timeout">Wall-clock budget for the read; defaults to 10 seconds.</param>
    /// <param name="onTimeout">What happens when the budget elapses (see <see cref="GetMeshNode"/>).
    /// <see cref="ReadTimeoutBehavior.EmitNull"/> yields <see cref="NodeReadStatus.Unavailable"/>
    /// carrying the <see cref="TimeoutException"/> — indeterminate, never "absent".</param>
    public static IObservable<NodeReadOutcome> GetMeshNodeOutcome(this IMessageHub hub, string path,
        TimeSpan? timeout = null,
        ReadTimeoutBehavior onTimeout = ReadTimeoutBehavior.Throw)
        => Observable.Create<NodeReadOutcome>(observer =>
        {
            // 🚨 Never issue the read on the ROOT MESH HUB — the router. Mesh-singleton services
            // (plugin-catalog boot, log-incident ingest, credential resolvers) hold the DI-injected
            // root hub, and a GetDataRequest posted there makes the router an END of the delivery in
            // both directions: the request reaches the per-node hub stamped Sender = mesh/{id}, and
            // the GetDataResponse (or, for a missing node, the DeliveryFailure) is addressed straight
            // back at mesh/{id} — both exactly what the ROUTER_TRAFFIC detector reports (production
            // 2026-08: "GetDataResponse has the mesh hub as target (sender: Hosting/_Access/…)" /
            // "DeliveryFailure … (sender: Plugins/_DefaultInstallLedger)"). Same seam MeshService
            // uses for CRUD; a non-router caller gets itself back, unchanged.
            var issuingHub = hub.NodeOperationIssuingHub();

            // ♻️ A TRANSIENT NODE PROBE HAS NO MESH NODE — so reading its OWN address is a CYCLE,
            // and the only way it ever ended was by spending the entire budget.
            //
            // A probe hub (`$model-probe/{guid}`, `$schema-probe/{guid}`) exists to have a
            // NodeType's INSTANCE configuration applied to it so its type registry / schema can be
            // snapshotted, and is disposed in the same breath. AsTransientNodeProbe states the
            // contract outright: "with no own-node subscription and no persistence sampler it has
            // no node identity". But that instance configuration is content written for a REAL
            // per-node hub, where the hub's address IS its mesh path — so deriving a path from
            // `Hub.Address` and reading it is an ordinary, correct thing for a loader to do. On the
            // probe that same derivation collapses onto the probe's own synthetic address.
            //
            // The read is then posted to the probe itself, where it parks behind the
            // DataContextInit / MeshNodeInit gates — and it is the probe's OWN data-context
            // initialization that issued it, so the gates cannot open until the read completes and
            // the read cannot complete until the gates open. Systemorph/MeshWeaver#2468: every such
            // read burned its full 10 s and then errored from the CTS timer thread.
            //
            // Absent is the TRUTHFUL answer, not a shortcut: there is no node at this address and
            // there never will be. Answering immediately is the same reasoning as "a gate that can
            // never open must answer immediately rather than sit on a timeout" — and it is scoped
            // to the probe's own address, so a probe reading any REAL path is untouched.
            if (issuingHub.Configuration.Get<TransientNodeProbe>() is not null
                && string.Equals(path, issuingHub.Address.ToString(), StringComparison.Ordinal))
            {
                try
                {
                    issuingHub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode")
                        ?.LogDebug(
                            "GetMeshNode('{Path}') on a transient node probe reads the probe's OWN "
                            + "address — a probe has no mesh node, so this is answered Absent "
                            + "immediately rather than parked behind the probe's own init gates.",
                            path);
                }
                catch
                {
                    // Logging must never mask the verdict it is reporting.
                }
                observer.OnNext(NodeReadOutcome.Absent);
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var budget = timeout ?? TimeSpan.FromSeconds(10);
            var started = Stopwatch.StartNew();
            var cts = new CancellationTokenSource(budget);
            var emitted = 0;
            // Inner hub.Observe subscription tracker. Captured so the returned
            // disposable can tear it down — without this, the outer CTS-timeout
            // path emits null and the outer observer disposes, but the inner
            // Subscribe keeps the hub-level callback registered, surfacing as
            // a "pending callback at dispose" Quiescing-watchdog failure.
            // ♻️ Recycling (ShuttingDown) is re-probed WITHIN the caller's budget, not a fixed
            // number of times. ErrorType.ShuttingDown's own contract (Events.cs) is "retry-worthy,
            // never terminal … the sender must read this as 'ask again', not 'gone'" — and the
            // previous one-immediate-re-probe cap violated it under any recycle longer than a few
            // milliseconds: a package-root hub whose dispose wedges for the 8s force-teardown
            // watchdog window (MeshWeaver#1701) NACKed BOTH probes within ~1s, and a 15s-budget
            // compile-path read settled terminally failed with 14s of its budget unused — every
            // NodeType compile reading the root in that window went CompilationStatus=Error and
            // the satellite gate reported phantom, module-varying "compile failures". The first
            // NACK still earns an IMMEDIATE re-probe (a normal recycle completes in well under a
            // second, so the healthy path stays zero-latency); every further NACK re-probes on the
            // pacing timer until the budget CTS fires. Termination is unchanged — the budget is
            // the caller's own, never raised here — and a recycler that outlasts it surfaces the
            // typed AddressRecyclingException (never null for Throw callers; Unavailable — the
            // timeout-shaped indeterminate — for EmitNull callers, whose documented contract is
            // "indeterminate ⇒ treat as absent").
            var shuttingDownNacks = 0;
            Exception? lastRecyclingNack = null;
            // 🚨 The DISTINCT owner activations seen across the re-probe loop — the datum that
            // separates the two failures this loop can end in, which have opposite fixes:
            // ONE hub wedged in teardown (every probe hits the same corpse; the address never
            // reactivates) versus a RECYCLE STORM (each probe hits a fresh activation that dies
            // before it can answer). "Still recycling after 110 probes" says nothing about which
            // (#2025) and cost a full CI cycle to not answer. The NACK carries an activation id
            // (MessageService), so counting distinct NACK texts counts activations.
            var owners = new HashSet<string>(StringComparer.Ordinal);
            var reProbePacing = new SerialDisposable();
            // SerialDisposable, not a bare IDisposable: the ShuttingDown re-probe below REPLACES this
            // subscription, and assigning into a SerialDisposable that has already been disposed
            // disposes the newcomer immediately — so a teardown racing the re-probe cannot leak the
            // second hub-level callback (which is the very "pending callback at dispose" failure the
            // note above exists to prevent).
            var innerSubscription = new SerialDisposable();

            void EmitOnce(NodeReadOutcome outcome)
            {
                if (Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnNext(outcome);
                observer.OnCompleted();
            }

            // Surface a denial/validation error instead of collapsing it to a null the
            // caller can't tell apart from "node not found". Mutually exclusive with
            // EmitOnce via the same `emitted` latch.
            void EmitError(Exception error)
            {
                if (Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnError(error);
            }

            // ♻️ The recycler outlasted the caller's whole budget. Truthful and typed — the
            // caller learns the address was RECYCLING (never "not found", never a bare stall)
            // and can classify it as an availability fact (ApplyCompileFailure stamps
            // CompilationStatus.Unavailable on it, exactly like SourceDiscoveryUnavailable).
            // Routed by onTimeout the same way the plain timeout is: EmitNull callers opted
            // into "indeterminate ⇒ absent" and get the Unavailable outcome (null via
            // GetMeshNode); Throw callers get the error surfaced.
            void EmitRecyclingExhausted()
            {
                int distinctOwners;
                lock (owners) distinctOwners = owners.Count;
                var error = new AddressRecyclingException(
                    $"GetMeshNode('{path}'): the owning hub was still recycling (ShuttingDown) after "
                    + $"{Volatile.Read(ref shuttingDownNacks)} probe(s) over {started.Elapsed.TotalSeconds:F1}s "
                    + $"(budget {budget.TotalSeconds:F0}s) — the address is recycling, NOT absent. "
                    + RecyclingShape(distinctOwners)
                    + " Surfacing rather than returning null, which the caller cannot tell apart from "
                    + "'node not found'. Retry the read once the address has reactivated.",
                    lastRecyclingNack);
                try
                {
                    hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode")
                        ?.LogWarning("{Message}", error.Message);
                }
                catch
                {
                    // Logging must never mask the verdict it is reporting.
                }
                if (onTimeout == ReadTimeoutBehavior.EmitNull)
                    EmitOnce(NodeReadOutcome.Unavailable(error));
                else
                    EmitError(error);
            }

            // ⏱️ TIMEOUT IS NOT "NOT FOUND". A read that gave up knows nothing about the node;
            // collapsing that into the same `null` the not-found path emits made every caller
            // silently substitute "missing" for "the mesh stalled" — and made the stall itself
            // invisible (a Debug log nobody reads). Surface it, loudly, with the hub's own
            // in-flight snapshot so the next occurrence says WHY: our GetDataRequest still
            // outstanding = the reply never came (dead per-node hub / dropped response);
            // the hub Executing(...) something else for seconds = action-block congestion or
            // ThreadPool starvation. Callers that genuinely want "indeterminate ⇒ absent"
            // opt in explicitly via ReadTimeoutBehavior.EmitNull — and even they get the
            // warning below, so no stall is ever fully silent.
            cts.Token.Register(() =>
            {
                if (Volatile.Read(ref emitted) != 0) return;
                // ♻️ The budget elapsed while the address was recycling: the paced re-probe loop
                // above never got a real answer. Say THAT — "the owning per-node hub never
                // answered" is the wrong diagnosis when it answered ShuttingDown on every probe.
                if (Volatile.Read(ref shuttingDownNacks) > 0)
                {
                    EmitRecyclingExhausted();
                    return;
                }
                var elapsed = started.Elapsed;
                string diagnostics;
                // The pending-request snapshot must come from the ISSUING hub — that is where our
                // GetDataRequest's callback is registered and where a lost reply shows as pending.
                try { diagnostics = issuingHub.GetPendingRequestDiagnostics(); }
                catch (Exception diagEx) { diagnostics = $"<diagnostics unavailable: {diagEx.GetType().Name}>"; }
                // …and the OWNER's state, which is what actually decides the verdict. The reader's
                // snapshot alone proves only that the reader is innocent (idle queues + our request
                // still pending = "the reply never came"), leaving "owner never activated" and
                // "owner answered, reply lost" indistinguishable. HostedHubCreation.Never is a pure
                // probe — a dictionary lookup that must NEVER activate the hub as a side effect of
                // diagnosing it.
                string targetState;
                try
                {
                    targetState = string.Equals(hub.Address.ToString(), path, StringComparison.Ordinal)
                        ? "Target: this hub itself."
                        : hub.GetHostedHub(new Address(path), HostedHubCreation.Never) is { } owner
                            ? $"Target: {owner.GetPendingRequestDiagnostics()}"
                            : $"Target: NO LOCAL HUB at '{path}' — it never activated here (or is owned "
                              + "by another silo), so no reply was ever going to be produced.";
                }
                catch (Exception targetEx)
                {
                    targetState = $"<target diagnostics unavailable: {targetEx.GetType().Name}>";
                }
                var message =
                    $"GetMeshNode('{path}') timed out after {elapsed.TotalSeconds:F1}s "
                    + $"(budget {budget.TotalSeconds:F0}s) — the owning per-node hub never answered the "
                    + $"GetDataRequest. This is NOT 'node not found'. Reader: {diagnostics} {targetState}";
                // Best-effort log. This runs on the CTS timer thread and the hub (with its
                // ServiceProvider) may already be torn down — an exception escaping here would
                // be an unobserved fault on a pool thread, i.e. exactly the class of failure
                // this change exists to remove. The emission below must happen regardless.
                try
                {
                    hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode")
                        ?.LogWarning("{Message}", message);
                }
                catch
                {
                    // Logging must never mask the timeout it is reporting.
                }
                // EmitNull's contract is "indeterminate ⇒ the caller treats it as absent" — but the
                // OUTCOME says what actually happened, so a caller that asked for the distinction
                // still learns the read never established anything. GetMeshNode maps it to the
                // null EmitNull has always emitted.
                if (onTimeout == ReadTimeoutBehavior.EmitNull)
                    EmitOnce(NodeReadOutcome.Unavailable(new TimeoutException(message)));
                else
                    EmitError(new TimeoutException(message));
            });

            // The read is issued through a local function so the ShuttingDown arm below can
            // re-issue it ONCE against a fresh activation. Declared here rather than inlined so
            // there is exactly one copy of the register-before-post ordering.
            void Issue()
            {
                try
                {
                    // 🚨 Register the response subject BEFORE posting. Observe<TResponse> pre-registers the
                    // callback (WithMessageId) and only then posts — see MessageHub.Observe(object, options):
                    // "registering the subject BEFORE posting avoids the race where a synchronously-handled
                    // response arrives before the subscription is in place."
                    //
                    // The previous Post(request) + Observe(delivery) ordering registered the subject AFTER the
                    // post. The hub DROPS any response whose requestId has no registered subject yet ("No
                    // subject found for response ... treating as processed", HandleCallbacks). A WARM owning
                    // per-node hub answers in sub-millisecond time, so under thread-pool contention a
                    // preemption between Post and Observe let the reply land before the subject existed -> the
                    // reply was dropped and this read hung to its timeout. That was the intermittent bulk flake
                    // in WorkspaceCacheEvictionTest (ReadNode -> GetMeshNode), proven deterministically by
                    // GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost.
                    innerSubscription.Disposable = issuingHub
                        .Observe<GetDataResponse>(
                            new GetDataRequest(new MeshNodeReference()),
                            o => o.WithTarget(new Address(path)))
                        .Subscribe(
                            d =>
                            {
                                try
                                {
                                    if (d.Message is GetDataResponse resp)
                                    {
                                        // 🚨 The owner said the node's DELETE is in flight — null BY DESIGN,
                                        // not "there is nothing here" (MeshDataSource.AddReadValidatorPipeline's
                                        // tombstone). Checked before the Error/absence branches because it is
                                        // the one absence a caller must never answer with a create (#1471).
                                        if (resp.Absence == DataAbsenceReason.DeleteInProgress)
                                        {
                                            EmitOnce(NodeReadOutcome.DeleteInProgress);
                                            return;
                                        }
                                        // A null-Data response carrying an Error is an application-level
                                        // read-validator verdict (INodeValidator → GetDataResponse{Error},
                                        // e.g. NodeHidden / a policy filter — see
                                        // MeshDataSource.AddReadValidatorPipeline). The documented contract is
                                        // that such a filtered node is INVISIBLE to the reader → resolve to
                                        // absent (indistinguishable from "not found", which is the point of
                                        // hiding). A *genuine* access denial is enforced at the delivery layer
                                        // (AccessControlPipeline → DeliveryFailure{ErrorType.Unauthorized}) and
                                        // surfaces via the OnError branch below — it never arrives as a
                                        // GetDataResponse{Error}. Log the verdict so it isn't entirely silent.
                                        if (resp.Data is null && !string.IsNullOrEmpty(resp.Error))
                                        {
                                            hub.ServiceProvider.GetService<ILoggerFactory>()
                                                ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode")
                                                ?.LogDebug("GetMeshNode read-validator filtered {Path}: {Error}", path, resp.Error);
                                            EmitOnce(NodeReadOutcome.Absent);
                                            return;
                                        }
                                        if (resp.Data is null)
                                        {
                                            EmitOnce(NodeReadOutcome.Absent);
                                            return;
                                        }
                                        MeshNode? node = resp.Data as MeshNode;
                                        if (node == null && resp.Data is JsonElement je)
                                            node = je.Deserialize<MeshNode>(hub.JsonSerializerOptions);
                                        // Data came back but would not materialise into a MeshNode. That is a
                                        // failed read, NOT evidence the node is missing — the two used to be
                                        // the same null.
                                        EmitOnce(node is null
                                            ? NodeReadOutcome.Unavailable(new InvalidOperationException(
                                                $"GetMeshNode('{path}'): the owner returned data of type "
                                                + $"'{resp.Data.GetType().Name}' which could not be read as a MeshNode."))
                                            : NodeReadOutcome.Present(node));
                                    }
                                    else
                                    {
                                        // Not a GetDataResponse at all — the read established nothing.
                                        EmitOnce(NodeReadOutcome.Unavailable(new InvalidOperationException(
                                            $"GetMeshNode('{path}'): the answer was not a GetDataResponse.")));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                                        ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");
                                    logger?.LogDebug(ex, "GetMeshNode callback failed for {Path}", path);
                                    EmitOnce(NodeReadOutcome.Unavailable(ex));
                                }
                            },
                            ex =>
                            {
                                // Access denial (ErrorType.Unauthorized) is a real error the
                                // caller — and ultimately the user — must see: surface it.
                                // Genuine not-found (routing NotFound) stays a null emission,
                                // the documented contract. Without this, a denied read was
                                // indistinguishable from a missing node and silently fell back.
                                if (ex is DeliveryFailureException dfe
                                    && dfe.Failure?.ErrorType == ErrorType.Unauthorized)
                                {
                                    EmitError(ex);
                                    return;
                                }
                                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");

                                // 🚨 SHUTTING DOWN IS NOT ABSENCE. ErrorType.ShuttingDown's own contract
                                // (Events.cs) is "retry-worthy, never terminal … the sender must read this as
                                // 'ask again', not 'gone'", and routing mints it deliberately INSTEAD of
                                // NotFound for a live-but-recycling address (MonolithRoutingService:
                                // `isShuttingDown ? ErrorType.ShuttingDown : ErrorType.NotFound`). Collapsing
                                // it into the same null a genuine not-found emits threw that distinction away
                                // and told the caller the node does not exist while it was merely recycling.
                                //
                                // Field evidence: ThreadAgentIntegrationTest on CI — the ACME/ProductLaunch
                                // instance hub self-recycled via the overlay self-heal watcher, its parked
                                // GetDataRequest was NACKed ShuttingDown (9d8880c68), and the test failed with
                                // "ACME/ProductLaunch node should exist" 6.4s into a 60s budget. Before that
                                // NACK existed the same race HUNG for 60s; the symptom migrated from a stall
                                // to a confident wrong answer.
                                //
                                // Re-issue inside the caller's remaining budget. This is not a
                                // retry-to-paper-over: each re-probe lands on a FRESH activation attempt (a
                                // SubscribeRequest/GetDataRequest creates one on arrival) and terminates
                                // authoritatively — if the node is genuinely gone, routing answers NotFound and
                                // we emit null; if it recycles for the ENTIRE budget, we surface the typed
                                // recycling verdict rather than lie. Exactly the reasoning
                                // MeshNodeStreamCache.IsTransientOwnerFailure already applies on the
                                // live-stream path, where "is shutting down" is classified transient for this
                                // same reason. The first NACK re-probes immediately (zero latency for the
                                // sub-second recycle); later NACKs ride the pacing timer so a wedged dispose
                                // (the 8s force-teardown window, MeshWeaver#1701) is polled, not hammered.
                                if (ex is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown })
                                {
                                    lastRecyclingNack = ex;
                                    // Extract just the activation TOKEN, never the whole message (#2376
                                    // Copilot review): a NACK's text can carry per-DELIVERY noise (a
                                    // request id, a type name) that differs on every retry even against
                                    // the SAME activation, and comparing whole strings would then count
                                    // one wedged owner as a false storm. A NACK with no token at all
                                    // (a site that has not been taught to embed one yet) contributes
                                    // nothing to the set rather than a guess — RecyclingShape(0) says
                                    // nothing, which is honest; miscounting would not be.
                                    var ownerTag = ExtractActivationTag(ex.Message);
                                    if (ownerTag is not null)
                                        lock (owners) owners.Add(ownerTag);
                                    var probes = Interlocked.Increment(ref shuttingDownNacks);
                                    if (!cts.IsCancellationRequested && Volatile.Read(ref emitted) == 0)
                                    {
                                        if (probes == 1)
                                        {
                                            logger?.LogDebug(
                                                "GetMeshNode: {Path} NACKed ShuttingDown — the address is recycling, "
                                                + "not absent. Re-probing immediately within the remaining budget.",
                                                path);
                                            Issue();
                                        }
                                        else
                                        {
                                            logger?.LogDebug(
                                                "GetMeshNode: {Path} is still recycling after {Probes} probe(s) — "
                                                + "re-probing on the pacing timer within the remaining budget.",
                                                path, probes);
                                            // Observable.Timer runs on the DefaultScheduler — OFF any hub action
                                            // block. SerialDisposable: teardown (or a superseding NACK) disposes
                                            // the pending tick, so no timer outlives the read.
                                            reProbePacing.Disposable = Observable
                                                .Timer(RecyclingReProbePace)
                                                .Subscribe(_ =>
                                                {
                                                    if (!cts.IsCancellationRequested
                                                        && Volatile.Read(ref emitted) == 0)
                                                        Issue();
                                                });
                                        }
                                        return;
                                    }
                                    EmitRecyclingExhausted();
                                    return;
                                }

                                logger?.LogDebug(ex, "GetMeshNode delivery failed for {Path}", path);
                                // 🚨 Only NotFound is evidence of absence — routing mints it for a path
                                // that is not there, and NEVER falls back to an ancestor. Every other
                                // delivery failure (Exception, Rejected, Failed, RoutingLoop …) means the
                                // read did not happen, which says nothing about whether the node exists.
                                // Both collapsed into the same null before, so a broken read was
                                // indistinguishable from a missing node (#1471).
                                EmitOnce(ex is DeliveryFailureException { Failure.ErrorType: ErrorType.NotFound }
                                    ? NodeReadOutcome.Absent
                                    : NodeReadOutcome.Unavailable(ex));
                            });
                }
                catch (Exception ex)
                {
                    var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");
                    logger?.LogDebug(ex, "GetMeshNode post failed for {Path}", path);
                    EmitOnce(NodeReadOutcome.Unavailable(ex));
                }
            }

            Issue();

            return Disposable.Create(() =>
            {
                reProbePacing.Dispose();
                innerSubscription.Dispose();
                cts.Dispose();
            });
        });

    /// <summary>
    /// How long a recycling (ShuttingDown-NACKing) address rests between re-probes after the
    /// first immediate one. Short enough that a read resumes well within a normal read budget
    /// once the address reactivates; long enough that a wedged dispose (the 8s force-teardown
    /// watchdog window) is polled a handful of times, not hammered. The caller's own budget —
    /// never this constant — bounds the loop.
    /// </summary>
    private static readonly TimeSpan RecyclingReProbePace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Names WHICH recycling failure this was, from the number of distinct owner activations that
    /// NACKed — the one thing a probe count cannot tell you, and the thing that decides where to
    /// look next (#2025).
    ///
    /// <para>One activation across every probe means the address never came back: a hub wedged in
    /// teardown, so look at its disposal. Many means the address DID come back, repeatedly, and
    /// each successor died before it could answer: a recycle storm, so look at whatever is asking
    /// for the recycles — a NodeType republishing, an overlay self-heal watcher firing per
    /// publication. Those have opposite fixes, and the previous message ("still recycling after
    /// 110 probes") was consistent with both.</para>
    /// </summary>
    /// <param name="distinctOwners">Distinct owner activations observed across the re-probe loop.</param>
    /// <returns>One sentence naming the shape, or an empty string when nothing was observed.</returns>
    internal static string RecyclingShape(int distinctOwners) => distinctOwners switch
    {
        <= 0 => string.Empty,
        1 => " Every probe was answered by the SAME owner activation, so the address never "
             + "reactivated — look at that hub's DISPOSAL (a teardown that never completes), not "
             + "at whoever asked for the recycle.",
        _ => $" The probes were answered by {distinctOwners} DISTINCT owner activations, so the "
             + "address did reactivate and each successor died before it could answer — a recycle "
             + "STORM. Look at whatever is requesting the recycles (a NodeType republishing, a "
             + "self-heal watcher firing per publication), not at any single hub's disposal.",
    };

    /// <summary>
    /// Extracts the <c>activation #XXXXXXXX</c> token a ShuttingDown NACK embeds (MessageService's
    /// <c>ActivationTag()</c>), or <c>null</c> when the NACK carries none.
    ///
    /// <para>🚨 Deliberately NOT "compare the whole message" (#2376 Copilot review, #2025). A
    /// NACK's text can carry per-DELIVERY noise — a request id, a type name — that differs on
    /// every retry even against the SAME activation, so treating the whole string as the owner key
    /// over-counts a single wedged owner into a false STORM verdict. Extracting just the stable
    /// token, and returning <c>null</c> (excluded from the owner count, never guessed) when a NACK
    /// site has not been taught to embed one, keeps the count accurate in both directions: never
    /// inflated by per-delivery text, never fabricated from a NACK that carries no identity at
    /// all.</para>
    /// </summary>
    /// <param name="message">A <see cref="DeliveryFailureException"/> message from a ShuttingDown NACK.</param>
    /// <returns>The hex activation id, or <c>null</c> when the message carries none.</returns>
    internal static string? ExtractActivationTag(string message)
    {
        const string marker = "activation #";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = start;
        while (end < message.Length && Uri.IsHexDigit(message[end]))
            end++;
        return end > start ? message[start..end] : null;
    }
}

/// <summary>
/// The owning hub of a read address answered <see cref="ErrorType.ShuttingDown"/> on every probe
/// for the reader's ENTIRE budget — the address is RECYCLING, not absent (routing mints
/// ShuttingDown deliberately instead of NotFound for a live-but-recycling address). An
/// availability fact, never a verdict about the node or about code that reads it: compile
/// pipelines classify it like <c>SourceDiscoveryUnavailableException</c>
/// (<c>CompilationStatus.Unavailable</c>), and callers retry once the address has reactivated.
/// Minted only by <see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/> after its
/// budget-bounded, paced re-probe loop (MeshWeaver#1701) is exhausted.
/// </summary>
public sealed class AddressRecyclingException(string message, Exception? inner)
    : InvalidOperationException(message, inner);
