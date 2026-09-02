using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers <c>Build</c> as a first-class NodeType — the coordination nodes of the build
/// protocol (<c>Doc/Architecture/BuildCoordination</c>). The ROOT lives at
/// <see cref="RootPath"/>; each CHUNK at <c>Admin/Build/{chunkName}</c>. Both carry a
/// <see cref="BuildState"/> payload.
///
/// <para><b>Who becomes the build master.</b> Nobody is elected. Candidates register a
/// <see cref="BuildClaimRequest"/> under their own holder id in
/// <see cref="BuildState.RequestedClaims"/> (per-candidate keys — RFC 7396 merge-safe), and the
/// node's OWN hub arbitrates: <see cref="InstallClaimArbiter"/> grants the earliest pending
/// request and steals a claim whose holder has gone. Correctness comes from node state, never
/// from an in-memory gate.</para>
///
/// <para><b>🚨 The grant is taken on a durable LOCK, not on the hub's mirror (#1424).</b> A hub's
/// serialised <c>Update</c> lambda makes the decision exclusive within ONE Orleans cluster, and that
/// is all it can ever make it: a second cluster over the same database activates its OWN hub for
/// <see cref="RootPath"/>, runs its OWN arbiter against its OWN mirror, and grants its own candidate.
/// Both writes are minted at the same next version, the store's monotonic condition applies at equal
/// versions, and neither writer is told it lost — so on 2026-08-13 the ephemeral bake Job and the
/// rolling serving pod each claimed <c>Admin/Build</c> and each ran the full 268-type bake. Worse, the
/// grant is observed off the RAM mirror, which commits ~200 ms BEFORE the persistence sampler reaches
/// storage, so no amount of adjudication after the fact can stop the second builder starting.
/// <see cref="ArbitrateDurably"/> therefore inverts the order: read the claim LOCK
/// (<see cref="ClaimPath"/> — the ONE witness both clusters can see, since the Orleans membership
/// table cannot be, being per-cluster by construction, and no change feed crosses processes), decide
/// against it, and publish the grant on the Build node only after an atomic
/// <see cref="IStorageAdapter.WriteIfVersion"/> has said this cluster won.</para>
///
/// <para><b>Takeover is decided by cluster MEMBERSHIP, not by a clock</b> (#1355). A staleness
/// budget is a stand-in for "is the holder still alive?", and where a cluster exists it already
/// answers that authoritatively — it runs probes, indirect probes and a membership table for
/// exactly this question. See <see cref="Arbitrate"/> for the three-way rule; the clock survives
/// only as the fallback for hosts with no cluster.</para>
/// </summary>
public static class BuildNodeType
{
    /// <summary>The node-type identifier string for Build nodes.</summary>
    public const string NodeType = "Build";

    /// <summary>The build root — the node whose <see cref="BuildState.Ready"/> map is the GO signal.</summary>
    public const string RootPath = "Admin/Build";

    /// <summary>Last segment of a claim LOCK node — see <see cref="ClaimPath"/>.</summary>
    internal const string ClaimSegment = "_Claim";

    /// <summary>
    /// The claim LOCK for a Build node: a tiny satellite carrying ONLY the current holder, written
    /// exclusively through <see cref="IStorageAdapter.WriteIfVersion"/> and
    /// <see cref="IStorageAdapter.DeleteIfExists"/>.
    ///
    /// <para>🚨 <b>Why the claim cannot live on the Build node's own row (#1424).</b> Every cluster
    /// keeps its own mirror of <c>Admin/Build</c>, and that mirror is flushed to storage as a WHOLE
    /// NODE by the ordinary persistence sampler — a plain, unconditional write from a hub that may
    /// never have held the claim. `MeshNode.Version` is a per-node counter, not a cross-cluster
    /// logical clock, so both mirrors climb it independently and the later flush simply wins the
    /// whole row. A losing cluster therefore overwrites the winner's <c>ClaimedBy</c> with its own
    /// <c>null</c>, the arbiter sees a free build on its next pass, and the exclusivity a
    /// compare-and-set had just established is undone by a write that knows nothing about it. This
    /// was observed directly: making the GRANT exclusive was not enough while the granted field
    /// still lived on a row two mirrors flush wholesale.</para>
    ///
    /// <para>The lock has no hub, no mirror and no sampler — nothing writes it except the two atomic
    /// storage primitives — so "who holds the build" has exactly one writer path and one witness.
    /// <see cref="BuildState.ClaimedBy"/> on the Build node remains the OBSERVABLE projection the
    /// GUI and <c>ObserveBuildClaim</c> read; a clobber there costs a stale view, never a second
    /// builder.</para>
    /// </summary>
    /// <param name="buildPath">The Build node (root or chunk) whose claim is wanted.</param>
    /// <returns>The lock node's path.</returns>
    public static string ClaimPath(string buildPath) => $"{buildPath}/{ClaimSegment}";

    /// <summary>Whether <paramref name="path"/> IS a claim lock rather than a Build node.</summary>
    internal static bool IsClaimPath(string? path) =>
        path is not null && path.EndsWith('/' + ClaimSegment, StringComparison.Ordinal);

    /// <summary>
    /// How long a claim survives without a heartbeat before the arbiter hands it to the next
    /// candidate — the FALLBACK rule, used only where cluster membership has no opinion about the
    /// holder (<c>ClusterMemberState.Unknown</c>: no cluster at all, or an identity it cannot
    /// resolve). Generously larger than <see cref="HeartbeatInterval"/>: without a membership
    /// verdict a missed beat under load must never produce two concurrent builders — the exact
    /// storm the claim exists to prevent. Where membership CAN answer, this bound is not consulted
    /// in either direction: a gone holder is taken over at once and a live one is never taken.
    /// </summary>
    public static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often a claim holder refreshes <see cref="BuildState.HeartbeatAt"/> while building.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a registration must have been visible before the arbiter may grant (#1424). One
    /// arbiter runs per Orleans cluster that touches the node, over its own mirror of the same
    /// durable row — the window is the time a concurrent registration from ANOTHER cluster needs
    /// to propagate (a Postgres NOTIFY plus a mirror refresh, measured sub-second; 5 s covers a
    /// slow feed) so that every arbiter elects from the same candidate set and computes the same
    /// winner. Costs 5 s once per build against a bake measured in minutes.
    /// </summary>
    public static readonly TimeSpan GrantSettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers the Build node type on the mesh builder by adding its MeshNode definition.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddBuildType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the Build node type: content payload, no UI create
    /// (only the protocol writes these nodes), and the claim arbiter installed on every
    /// Build node's own hub.
    /// </summary>
    /// <returns>The Build MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Build",
        NodeType = MeshNode.NodeTypePath,   // this MeshNode IS a NodeType definition
        Icon = "/static/NodeTypeIcons/task-list.svg",
        ExcludeFromContext = new HashSet<string> { "create" }, // no UI create — only the build protocol writes these
        Content = new NodeTypeDefinition
        {
            Description = "Build coordination node. The root carries the chunk plan and the per-fingerprint GO signal; each chunk carries its queries, its claim and the release paths it wrote.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<BuildState>())
            .AddDefaultLayoutAreas()
            // 🚨 The OBSERVABLE overload, deliberately (#1868). InstallClaimArbiter resolves the
            // hub's own node stream (workspace.GetMeshNodeStream() → SynchronizationStream..ctor →
            // GetHostedHub(sync/{clientId}, HostedHubCreation.Always)), so on the SYNCHRONOUS
            // overload it constructed a hub — and a second container — from inside
            // MessageHubConfiguration.Build, before StartMessageProcessing and before the
            // workspace's own data sources had been started on the init turn. The observable
            // overload runs on the InitializeHubRequest turn, after Build returns and still before
            // the Initialize gate opens, so the arbiter's triggers are wired against a workspace
            // that is actually running.
            .WithInitialization(StartClaimArbiter)
    };

    /// <summary>
    /// Installs the claim arbiter on the hub's INIT TURN and couples it to the hub's lifetime.
    /// A static method group, so <c>WithInitialization</c>'s delegate-identity idempotency still
    /// collapses repeat registrations from composed configurators.
    /// </summary>
    /// <param name="hub">The Build node's own hub.</param>
    /// <returns>An observable that completes once the arbiter is installed.</returns>
    private static IObservable<Unit> StartClaimArbiter(IMessageHub hub) =>
        // Defer so the work happens at SUBSCRIBE time (the init turn), not when the observable is
        // constructed — HandleInitialize builds the whole Concat chain up front.
        Observable.Defer(() =>
        {
            hub.RegisterForDisposal(InstallClaimArbiter(hub));
            return Observable.Return(Unit.Default);
        });

    /// <summary>
    /// The claim arbiter — runs on each Build node's OWN hub.
    ///
    /// <para>Two triggers, one decision procedure: every own-stream emission (a candidate
    /// registered, a holder released) and a slow periodic tick (a dead holder emits nothing — the
    /// stale steal can only come from a timer). Both funnel into <see cref="ArbitrateDurably"/>,
    /// which re-reads the durable row and does nothing when there is nothing to do, so redundant
    /// triggers write nothing.</para>
    /// </summary>
    /// <param name="hub">The Build node's own hub.</param>
    /// <returns>The subscription to dispose with the hub.</returns>
    internal static IDisposable InstallClaimArbiter(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<MeshNode>>();
        var workspace = hub.GetWorkspace();

        // A claim LOCK is not a build. It shares the Build node type so it needs no registration of
        // its own, but it must never arbitrate — it has no candidates and no claim of its own, and
        // an arbiter there would go looking for `…/_Claim/_Claim`.
        if (IsClaimPath(hub.Address.Path))
            return System.Reactive.Disposables.Disposable.Empty;

        // Absent on a monolith, a test, a dev box and the Orleans CLIENT host — all of which is
        // "no cluster", which Arbitrate reads as Unknown and answers with the heartbeat clock.
        var membership = hub.ServiceProvider.GetService<IClusterMembership>();

        // The cross-cluster witness. Absent only on a host with no persistence at all, which is
        // single-process by construction — there the mirror IS the whole world (see ArbitrateDurably).
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();

        void TryArbitrate() =>
            ArbitrateDurably(hub, storage, membership, logger)
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(
                        ex, "Build claim arbitration failed on {Address}", hub.Address));

        var onEmission = ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => workspace.GetMeshNodeStream()
                .Select(node => node?.ContentAs<BuildState>(hub.JsonSerializerOptions))
                .Where(state => state?.RequestedClaims is { Count: > 0 })
                .Select(state => string.Join(
                    "|",
                    state!.RequestedClaims!.Keys.OrderBy(k => k, StringComparer.Ordinal)
                        .Append(state.ClaimedBy ?? string.Empty)))
                .DistinctUntilChanged()
                // A registration inside the settle window arbitrates to "not yet" (#1424), and no
                // further emission is coming if nobody else registers — so every trigger also
                // schedules ONE re-check just past the window. Redundant re-checks are free: an
                // already-granted (or still-too-young) node comes back unchanged and writes
                // nothing. SelectMany (not Switch): a second candidate's trigger must not cancel
                // the first's pending re-check.
                .SelectMany(key => Observable
                    .Timer(GrantSettleWindow + TimeSpan.FromSeconds(1))
                    .Select(_ => key)
                    .StartWith(key)),
            _ => TryArbitrate(),
            logger,
            "build claim arbiter");

        // 🚨 The LOCK changing is its own trigger, and a required one now that the decision is taken
        // there. The mirror emission above fires the instant a holder RELEASES its claim, but the
        // release clears the node's projection first and drops the lock a moment later — so a pass
        // triggered by the mirror reads a lock that still names the outgoing holder, correctly
        // declines to grant, and then has nothing left to wake it until the slow tick. The store
        // publishes Changes from inside its own write/delete, so this edge fires exactly when the
        // lock actually moved: level-triggered on a real state change, never a poll or a retry. It
        // cannot loop — a pass with nothing to grant writes nothing, so it publishes nothing.
        var claimPath = ClaimPath(hub.Address.Path);
        var onDurableChange = storage is null
            ? System.Reactive.Disposables.Disposable.Empty
            : storage.Changes
                .Where(change =>
                    string.Equals(change.Path, claimPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(change.Path, hub.Address.Path, StringComparison.OrdinalIgnoreCase))
                .Subscribe(
                    _ => TryArbitrate(),
                    ex => logger?.LogWarning(
                        ex, "Build claim arbiter lost the durable change feed on {Address}", hub.Address));

        // A holder that died emits nothing — its departure can only be observed by a timer, whether
        // the evidence is a membership verdict or an aged heartbeat. The tick is cheap by
        // construction: Arbitrate returns the node unchanged unless a pending claim exists AND the
        // holder no longer holds it, so a quiet node writes nothing.
        var staleTick = Observable.Interval(HeartbeatInterval)
            .Subscribe(_ => TryArbitrate());

        return new System.Reactive.Disposables.CompositeDisposable(
            onEmission, onDurableChange, staleTick);
    }

    /// <summary>
    /// One arbitration pass, taken on the DURABLE row so it is exclusive across Orleans clusters
    /// and not merely within one (#1424). Three steps, and the ORDER is the fix:
    ///
    /// <list type="number">
    /// <item>Read the claim LOCK (<see cref="ClaimPath"/>). It is the only witness a second cluster
    /// also sees — Orleans membership is per-cluster by construction, and the PG <c>LISTEN/NOTIFY</c>
    /// feed (live in the partitioned wiring since #1816) delivers only to a session that was
    /// already listening when the row committed, never as a replay — so a mirror can be
    /// arbitrarily far behind another cluster's grant and will never be told.</item>
    /// <item>Decide with <see cref="Arbitrate"/> over the LOCK's holder state and THIS cluster's
    /// pending registrations. Registrations come from the mirror because a candidate that registered
    /// milliseconds ago has not reached storage yet — and a cluster can only ever grant one of its
    /// own candidates anyway. Holder state comes only from the lock: that is the contested field,
    /// and the one place it cannot be overwritten by a whole-node flush.</item>
    /// <item>Commit with <see cref="IStorageAdapter.WriteIfVersion"/> against the version step 1
    /// read, and publish the grant on the Build node only if the store says we won. That publication
    /// is what <c>ObserveBuildClaim</c> emits on, and it commits ~200 ms before the persistence
    /// sampler reaches storage — so the durable decision has to come FIRST or the loser has already
    /// started baking by the time it could be told.</item>
    /// </list>
    ///
    /// <para><b>Losing writes nothing and retries nothing.</b> A refused compare-and-set means
    /// another cluster's grant is already durable; this cluster simply does not grant, its candidate
    /// runs out its <c>GrantWindow</c> and follows the GO — the path the protocol already has for
    /// "the claim is held elsewhere". No timer, no election, no backoff loop: the arbiter is
    /// level-triggered and the next legitimate trigger re-decides against fresh durable state.</para>
    ///
    /// <para>🚨 <b>Fail OPEN, deliberately.</b> When no adapter owns the path (<c>null</c> from the
    /// try-then-claim chain) or there is no persistence at all, the grant is taken on the mirror
    /// exactly as before. Being wrong in this direction costs ONE duplicated bake — bounded and
    /// non-corrupting, because every downstream write is content-addressed (the assembly store is
    /// keyed by content, release versions are minted from content hashes, the GO is keyed by
    /// fingerprint). Being wrong in the other direction — refusing to grant because exclusivity
    /// could not be proven — would leave the fingerprint with no GO at all, which holds every
    /// silo's readiness probe down and stalls the rollout. That asymmetry is the same one the bake
    /// gate applies (fail closed on a measured regression, fail open when nothing is measuring) and
    /// the opposite of <c>AssemblyCacheRetention</c>, where the wrong answer deletes bytes a running
    /// pod needs.</para>
    /// </summary>
    /// <param name="hub">The Build node's own hub.</param>
    /// <param name="storage">The durable store, or <c>null</c> on a host without persistence.</param>
    /// <param name="membership">Cluster membership, or <c>null</c> where this host is in no cluster.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Cold observable completing when the pass is done.</returns>
    internal static IObservable<Unit> ArbitrateDurably(
        IMessageHub hub,
        IStorageAdapter? storage,
        IClusterMembership? membership,
        ILogger? logger)
    {
        var workspace = hub.GetWorkspace();
        var options = hub.JsonSerializerOptions;

        if (storage is null)
            return GrantOnMirror(workspace, options, membership, DateTime.UtcNow);

        var claimPath = ClaimPath(hub.Address.Path);
        return workspace.GetMeshNodeStream()
            .Where(n => n is not null)
            .Take(1)
            .SelectMany(mirror =>
            {
                // Candidates come from THIS hub's mirror: a registration written milliseconds ago
                // has not reached storage yet, and a cluster can only ever grant one of its own
                // candidates anyway. Checked BEFORE the lock is read, so the periodic tick on a
                // quiet build node costs nothing at all — no storage round-trip, no write.
                var pending = mirror.ContentAs<BuildState>(options)?.RequestedClaims;
                if (pending is not { Count: > 0 })
                    return Observable.Return(Unit.Default);

                return storage.Read(claimPath, options)
                    .Take(1)
                    .SelectMany(held => CommitGrant(
                        workspace, storage, options, membership, logger,
                        pending, claimPath, held));
            });
    }

    /// <summary>
    /// The pre-#1424 behaviour, kept for hosts with no durable store — a single process, where the
    /// hub's own serialised <c>Update</c> IS the whole arbitration, and for the fail-open path.
    /// </summary>
    private static IObservable<Unit> GrantOnMirror(
        IWorkspace workspace, System.Text.Json.JsonSerializerOptions options,
        IClusterMembership? membership, DateTime now)
        => workspace.GetMeshNodeStream()
            .Update(node => Arbitrate(node, options, now, membership))
            .Select(_ => Unit.Default);

    private static IObservable<Unit> CommitGrant(
        IWorkspace workspace,
        IStorageAdapter storage,
        System.Text.Json.JsonSerializerOptions options,
        IClusterMembership? membership,
        ILogger? logger,
        ImmutableDictionary<string, BuildClaimRequest> pending,
        string claimPath,
        MeshNode? held)
    {
        // Holder state comes from the LOCK — never from the mirror and never from the Build node's
        // own durable row (see ClaimPath for why that row cannot hold it).
        var decisionInput = (held ?? NewClaimNode(claimPath)) with
        {
            Content = (held?.ContentAs<BuildState>(options) ?? new BuildState()) with
            {
                RequestedClaims = pending,
            }
        };

        var decided = Arbitrate(decisionInput, options, DateTime.UtcNow, membership);
        if (ReferenceEquals(decided, decisionInput))
            return Observable.Return(Unit.Default);   // nothing to grant — the quiet path, no write

        var granted = decided.ContentAs<BuildState>(options)!;
        var expectedVersion = held?.Version ?? 0;
        var toWrite = decided with
        {
            // The lock carries the CLAIM and nothing else; registrations stay on the Build node,
            // which is where candidates write them.
            Content = granted with { RequestedClaims = null },
            Version = MeshNode.NextVersion(expectedVersion),
            LastModified = DateTimeOffset.UtcNow,
        };

        return storage.WriteIfVersion(toWrite, expectedVersion, options)
            .Take(1)
            .SelectMany(applied =>
            {
                if (applied is false)
                {
                    // Another cluster's arbiter got there first. Say so — a silently-not-granted
                    // claim is indistinguishable from an arbiter that never ran.
                    logger?.LogInformation(
                        "Build claim {ClaimPath}: NOT granting {Holder} — the lock moved past "
                        + "Version={Expected} while this pass ran, which means another cluster's "
                        + "arbiter granted first. This candidate follows the GO instead of building.",
                        claimPath, granted.ClaimedBy, expectedVersion);
                    return Observable.Return(Unit.Default);
                }

                if (applied is null)
                    logger?.LogDebug(
                        "Build claim {ClaimPath}: no storage provider owns this path, so the grant to "
                        + "{Holder} is taken on the mirror alone (single-store host).",
                        claimPath, granted.ClaimedBy);

                // Won the lock (or fail-open). Publish the grant on the Build node so
                // ObserveBuildClaim emits — the field set only, never a wholesale replace: a
                // registration written milliseconds ago may not have reached storage, and replacing
                // the mirror would drop it and strand its candidate.
                //
                // 🚨 …and the publish may REFUSE (#1193). `pending` was read off this mirror two
                // storage round-trips ago, and a candidate is free to stand down inside that
                // window. ApplyGrant is the serialised point that can still see it did, so the
                // refusal is authoritative — and it is the arbiter's job, not the candidate's, to
                // put the lock back where it found it.
                return workspace.GetMeshNodeStream()
                    .Update(current => ApplyGrant(current, granted, options))
                    .Take(1)
                    .SelectMany(published => HandBackAStoodDownGrant(
                        storage, options, logger, granted, claimPath, published));
            });
    }

    /// <summary>
    /// Undoes a grant the mirror REFUSED to publish, by releasing the lock this pass just took
    /// (#1193).
    ///
    /// <para><b>Why a grant can need undoing.</b> The candidate set is read off the mirror at the
    /// START of an arbitration pass and the grant is committed two storage round-trips later. A
    /// follower that reaches its answer some other way stands down inside that window
    /// (<c>WithdrawBuildClaim</c>), and BOTH of its halves are conditional on state this pass is
    /// about to change: it removes its registration, then finds no lock of its own to release. The
    /// pass then commits a grant to a process that has already finished with the build and is no
    /// longer listening.</para>
    ///
    /// <para>Where the debris lands depends on how the two writers interleave, and neither
    /// half converges on its own:</para>
    /// <list type="bullet">
    /// <item>the mirror's claim PROJECTION keeps naming the withdrawn candidate at
    /// <see cref="BuildStatus.Planning"/> — measured, and on a host whose arbiter decides on the
    /// mirror (<c>GrantOnMirror</c>: no durable store, or the fail-open path) that projection IS
    /// the claim;</item>
    /// <item>or the claim LOCK does — the one witness every cluster's arbiter decides on — when the
    /// stand-down read it a moment before this pass wrote it.</item>
    /// </list>
    ///
    /// <para>Either way <see cref="HolderStillHoldsIt"/> then defends it BY DESIGN, because the
    /// process really is alive (#1355: a stopped heartbeat on a running process means busy, not
    /// dead). With cluster membership the claim is never taken over at all; without it every later
    /// candidate waits out <see cref="ClaimStaleAfter"/> and is handed the same dead claim again.
    /// No builder, no bake, no ready pod — the readiness stall #1440 fixed from the candidate's
    /// side, arriving instead through the arbiter's own commit.</para>
    ///
    /// <para>Level-triggered on real state, with no timing anywhere: the mirror either names our
    /// winner as holder (published — nothing to do) or it does not (refused), and the lock is
    /// dropped only while it still names that winner, so a pass that lost a later race removes
    /// nothing. The delete publishes on the store's change feed, which is the arbiter's own
    /// trigger, so a queued candidate is elected immediately rather than at the slow tick.</para>
    /// </summary>
    internal static IObservable<Unit> HandBackAStoodDownGrant(
        IStorageAdapter storage,
        System.Text.Json.JsonSerializerOptions options,
        ILogger? logger,
        BuildState granted,
        string claimPath,
        MeshNode? published)
    {
        if (published?.ContentAs<BuildState>(options)?.ClaimedBy == granted.ClaimedBy)
            return Observable.Return(Unit.Default);

        logger?.LogInformation(
            "Build claim {ClaimPath}: {Holder} won the lock but had already STOOD DOWN before the "
            + "grant could be published, so the build would have stayed locked to a live process "
            + "that is no longer listening. Releasing the lock — the next candidate elects freely.",
            claimPath, granted.ClaimedBy);

        return storage.Read(claimPath, options)
            .Take(1)
            .SelectMany(held =>
                held is not null
                && held.ContentAs<BuildState>(options)?.ClaimedBy == granted.ClaimedBy
                    ? storage.DeleteIfExists(claimPath).Take(1).Select(_ => Unit.Default)
                    : Observable.Return(Unit.Default));
    }

    /// <summary>
    /// A fresh, unheld claim lock — the node the compare-and-set INSERTS when nobody holds the
    /// build (<c>expectedVersion 0</c>).
    /// </summary>
    private static MeshNode NewClaimNode(string claimPath)
    {
        var split = claimPath.LastIndexOf('/');
        return new MeshNode(claimPath[(split + 1)..], claimPath[..split])
        {
            NodeType = NodeType,
            Name = "Build claim",
            Content = new BuildState(),
        };
    }

    /// <summary>
    /// Applies a grant this cluster WON durably onto its own mirror — the claim field set only, so
    /// nothing the mirror holds and storage has not seen yet is lost.
    /// </summary>
    internal static MeshNode ApplyGrant(
        MeshNode node, BuildState granted, System.Text.Json.JsonSerializerOptions options)
    {
        if (node is null) return node!;
        var state = node.ContentAs<BuildState>(options) ?? new BuildState();
        if (state.ClaimedBy == granted.ClaimedBy && state.Status == granted.Status)
            return node;   // already reflects this grant — a redundant pass writes nothing

        // 🚨 THE WINNER MUST STILL BE A CANDIDATE AT PUBLISH TIME (#1193). The election ran on a
        // candidate set read before two storage round-trips, and a follower that reached its answer
        // some other way stands down inside that window (WithdrawBuildClaim). This lambda runs on
        // the node's own serialised write path, so it is the last — and only — point that can still
        // see that it did. Publishing anyway leaves the projection naming a live process that will
        // never act on the build and never release it, and the takeover rule then defends that
        // holder BY DESIGN; see HandBackAStoodDownGrant for the whole failure and its measurement.
        //
        // The guard is "neither holder nor candidate", and the first clause is load-bearing: a
        // holder MID-BAKE has no registration left either — its own grant consumed it — so a bare
        // "is it still queued?" test would read a running bake as a candidate that stood down.
        if (state.ClaimedBy != granted.ClaimedBy
            && granted.ClaimedBy is { } winner
            && state.RequestedClaims?.ContainsKey(winner) != true)
            return node;

        return node with
        {
            Content = state with
            {
                ClaimedBy = granted.ClaimedBy,
                ClaimedByIdentity = granted.ClaimedByIdentity,
                ClaimedAt = granted.ClaimedAt,
                HeartbeatAt = granted.HeartbeatAt,
                FrameworkVersion = granted.FrameworkVersion,
                Status = granted.Status,
                Error = null,
                RequestedClaims = granted.ClaimedBy is { } holder
                    ? state.RequestedClaims?.Remove(holder)
                    : state.RequestedClaims,
            }
        };
    }

    /// <summary>
    /// The single decision procedure, pure over its inputs. Grants the earliest pending claim when
    /// the node is unclaimed or the
    /// current holder is gone; otherwise returns the node unchanged. Pure over its inputs — the
    /// instant and the membership verdict are both parameters — so every takeover rule is testable
    /// without a cluster and without waiting on wall-clock.
    /// </summary>
    /// <param name="node">The Build node as read inside the update lambda.</param>
    /// <param name="options">Serializer options for content recovery.</param>
    /// <param name="now">The decision instant.</param>
    /// <param name="membership">
    /// Cluster membership, or <c>null</c> where this host is in no cluster (monolith, test, dev,
    /// Orleans client) — which is the same thing as a verdict of
    /// <see cref="ClusterMemberState.Unknown"/> and leaves the heartbeat clock in charge.
    /// </param>
    /// <returns>The node with a granted claim, or the node unchanged.</returns>
    public static MeshNode Arbitrate(
        MeshNode node, System.Text.Json.JsonSerializerOptions options, DateTime now,
        IClusterMembership? membership = null)
    {
        if (node is null) return node!;
        var state = node.ContentAs<BuildState>(options);
        if (state?.RequestedClaims is not { Count: > 0 } pending)
            return node;

        if (HolderStillHoldsIt(state, now, membership))
            return node;

        // 🚨 #1424 — CONVERGE BEFORE DECIDING, on the ROOT. There is one arbiter per Orleans
        // cluster that touches this node (a bake Job's cluster and the serving cluster each
        // activate their own hub over the same durable row), so the grant must be a DETERMINISTIC
        // ELECTION over data both sides have seen — never "first arbiter to look wins", which is
        // how the pilot got two winners and two full bakes. Holding the grant until the oldest
        // registration has had a propagation window lets a concurrent registration from the other
        // cluster land; the ordering below then computes the SAME winner on every arbiter, making
        // concurrent grant writes idempotent. Under partition this degrades to the pilot's
        // behaviour (a redundant bake into content-addressed stores), never a wedge and never
        // corruption.
        //
        // FRESH ROOT ELECTIONS ONLY. The race this window closes is the simultaneous-boot one —
        // several processes registering on an UNCLAIMED root within milliseconds of a rollout. A
        // TAKEOVER is a different shape: the successor queue accumulated while the holder was
        // alive and is long converged, and #1355's immediate-takeover property must not wait on a
        // window. Chunks are excluded too: the root election decides THE builder, chunk claims
        // arrive pre-arbitrated by it, and 37 chunks × 5 s of settle would put minutes of pure
        // waiting into a bake measured at ~1m42s. When parallel builders make chunk contention
        // real, chunk elections get the same treatment.
        if (state.ClaimedBy is null
            && string.Equals(node.Path, RootPath, StringComparison.OrdinalIgnoreCase)
            && now - pending.Min(kv => kv.Value.RequestedAt) < GrantSettleWindow)
            return node;

        var granted = pending
            .OrderByDescending(kv => kv.Value.Priority)
            .ThenBy(kv => kv.Value.RequestedAt)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .First();

        return node with
        {
            Content = state with
            {
                ClaimedBy = granted.Key,
                ClaimedByIdentity = granted.Value.ClusterIdentity,
                ClaimedAt = now,
                HeartbeatAt = now,
                FrameworkVersion = granted.Value.FrameworkVersion,
                Status = BuildStatus.Planning,
                Error = null,
                RequestedClaims = state.RequestedClaims!.Remove(granted.Key),
            }
        };
    }

    /// <summary>
    /// Whether the current claim must be left alone — the takeover rule, and the whole of #1355.
    ///
    /// <para>A staleness budget only ever approximated "is the holder still alive?". It conflates a
    /// dead process with a busy, starved or descheduled one, so it is wrong in both directions: it
    /// makes the fleet wait out ten minutes for a pod that is already gone, and it licenses evicting
    /// a pod that is merely slow. Where a cluster exists it knows the answer outright, so we ask it:</para>
    ///
    /// <list type="table">
    /// <item><term>Gone</term><description>take over IMMEDIATELY — there is no budget to wait out
    /// once the cluster has positively recorded the holder as departed.</description></item>
    /// <item><term>Alive</term><description>NEVER take over, however old the heartbeat looks. A
    /// heartbeat that stopped while the process runs means starved or busy, and two builders is the
    /// storm the claim exists to prevent.</description></item>
    /// <item><term>Unknown</term><description>fall back to <see cref="ClaimStaleAfter"/>. This is
    /// the only path that still consults the clock, and it covers hosts with no cluster (monolith,
    /// test, dev), a claim written before identities were stamped, and an identity membership
    /// cannot resolve.</description></item>
    /// </list>
    ///
    /// <para>🚨 Absence from the membership snapshot is Unknown, never Gone — see
    /// <c>OrleansClusterMembership</c>. Reading "not in my snapshot" as death on a freshly-started
    /// silo whose snapshot is not hydrated yet would evict a perfectly live builder, which is the
    /// one failure this whole change is meant to make impossible.</para>
    /// </summary>
    private static bool HolderStillHoldsIt(
        BuildState state, DateTime now, IClusterMembership? membership)
    {
        if (state.ClaimedBy is null) return false;
        if (state.Status is not (BuildStatus.Planning or BuildStatus.Building)) return false;

        var verdict = state.ClaimedByIdentity is { Length: > 0 } identity && membership is not null
            ? membership.StateOf(identity)
            : ClusterMemberState.Unknown;

        if (verdict is ClusterMemberState.Gone) return false;
        if (verdict is ClusterMemberState.Alive) return true;

        // Unknown: the clock, exactly as before this change. A claim carrying no instant at all
        // has nothing to age, so it is not defensible — the next candidate takes it.
        return (state.HeartbeatAt ?? state.ClaimedAt) is { } beat
            && now - beat <= ClaimStaleAfter;
    }
}
