using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Drives the pre-warm bake THROUGH the build protocol (<c>Doc/Architecture/BuildCoordination</c>)
/// instead of the file lease: the claim on <c>Admin/Build</c> decides who bakes, chunk nodes under
/// it record what each part of the build produced, and the per-fingerprint GO on the root is what
/// every non-building silo waits for — a subscription, not a poll of the assembly share.
///
/// <para><b>Execution is unchanged.</b> The winner runs the same sequential, dependency-ordered
/// sweep (<c>WarmPending</c>) it always ran — one global order, so cross-chunk source dependencies
/// stay structurally correct. What changed is coordination: the claim replaced the file lease that
/// used to live beside the assembly cache (its one-builder and steal-on-stale properties live on
/// in the claim arbiter), chunk nodes make the build observable (queries in, release paths out,
/// one <c>_Activity</c> each), and non-builders complete on the GO emission instead of re-probing
/// the share every 60 s. Chunk-scoped execution (per-chunk sweeps on a disposable
/// separate-ServiceId bake silo) plugs into the same nodes next.</para>
/// </summary>
public static class BuildProtocolDriver
{
    /// <summary>
    /// Config key: route the bake through the build protocol. Default: ON — this is the only
    /// bake coordination there is; <c>false</c> is the escape hatch that bakes solo, uncoordinated.
    /// </summary>
    public const string EnabledConfigKey = "PreWarm:BuildProtocol";

    /// <summary>
    /// How long a candidate waits for the arbiter's grant before concluding another process is
    /// building and switching to the GO subscription. The arbiter sits on the build node's own
    /// hub, so an uncontended grant arrives in milliseconds — this bound only matters when the
    /// claim is genuinely held elsewhere.
    /// </summary>
    public static readonly TimeSpan GrantWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many times the claim HANDSHAKE is attempted when the coordination node cannot be
    /// reached at all (#1635).
    ///
    /// <para>The handshake is the first thing the sweep does, and it is a <c>SubscribeRequest</c>
    /// to <c>Admin/Build</c>. At pod startup that grain may not have activated yet, so the request
    /// dies on the hub's own 60 s request budget with a <see cref="TimeoutException"/> — which
    /// propagated out of <see cref="Run"/>, faulted the whole warm-up stream and made the pod
    /// REFUSE READINESS, stalling the rollout on a race that clears itself in seconds. Observed on
    /// <c>memex-portal-deployment-f96d46ddf-mrgqf</c>, 2026-08-15 07:44:25Z.</para>
    ///
    /// <para>🚨 <b>The retry does NOT make it permissive.</b> When every attempt fails the
    /// handshake still throws — the sweep still faults, readiness is still refused, the rollout
    /// still stalls. What changes is that a transient race no longer counts as unreachable, and
    /// that the terminal failure is a <see cref="BuildCoordinationUnreachableException"/> which
    /// SAYS the coordination node was unreachable instead of leaving an operator to infer it from
    /// a bare timeout. A check that cannot reach its evidence must fail loudly, never
    /// permissively.</para>
    /// </summary>
    public const int CoordinationAttempts = 3;

    /// <summary>
    /// Backoff before re-attempting the claim handshake. Small and bounded on purpose: each failed
    /// attempt already costs the hub's own request budget (60 s), so the wait between them only has
    /// to outlast a grain activation, not a deployment.
    /// </summary>
    /// <param name="attemptsSoFar">How many attempts have already failed (1-based).</param>
    public static TimeSpan CoordinationBackoff(int attemptsSoFar) =>
        TimeSpan.FromSeconds(attemptsSoFar <= 1 ? 5 : 15);

    /// <summary>
    /// Claim → bake → GO; or, when the claim is held elsewhere, subscribe to the GO and report the
    /// share's state when it arrives.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="report">The store probe report (carries the framework fingerprint + pending set).</param>
    /// <param name="definitions">Discovered NodeType definitions by path.</param>
    /// <param name="store">The shared assembly store, for the follower's post-GO probe.</param>
    /// <param name="bake">The actual sweep to run when this process wins the claim.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The sweep's outcomes (winner), or the post-GO probe's outcomes (follower).</returns>
    public static IObservable<PreWarmOutcome> Run(
        IMessageHub mesh,
        NodeTypeBakeReport report,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        Func<IObservable<PreWarmOutcome>> bake,
        ILogger? logger)
    {
        // Unique per process RUN: two boots of the same pod must not look like one claimant, or
        // the second boot would inherit (and heartbeat) a claim whose bake died with the first.
        var holder = $"{Environment.MachineName}/{Guid.NewGuid():N}";
        var fingerprint = report.FrameworkVersion;

        // A DEDICATED bake process outranks every serving pod in the claim election (#1424): when
        // a bake Job is running, the pods lose deterministically, follow its GO, and never pay the
        // bake's cost — and when none is, priority 0 candidates elect among themselves and the
        // pods remain their own fallback. Same mode key the host's bake entrypoint switches on.
        var priority = string.Equals(
            mesh.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()
                ?["Deployment:Mode"],
            "Bake", StringComparison.OrdinalIgnoreCase)
            ? BuildClaimRequest.BakePriority
            : 0;

        // 🚨 The handshake is RETRIED, the bake is not. Everything downstream of the registration
        // is the sweep itself, whose failures are verdicts about this image and must reach the
        // gate untouched. Only reaching the coordination node at all is retried — see
        // CoordinationAttempts for why, and for why exhausting them is still a refusal.
        //
        // The whole subscription-borne composition is ONE DOOR. When it cannot be opened at all,
        // the durable witness is asked — see WhenTheSubscriptionDoorIsShut (#3404).
        return WhenTheSubscriptionDoorIsShut(
            RetryUnreachableCoordination(
                    () => mesh.RequestBuildClaim(holder, fingerprint, priority: priority).Take(1),
                    CoordinationAttempts,
                    CoordinationBackoff,
                    Scheduler.Default,
                    logger)
                .SelectMany(_ => mesh.ObserveBuildClaim(holder)
                    .Take(1)
                    .Timeout(GrantWindow)
                    .Select(__ => true)
                    // The Catch bounds the GRANT WAIT and nothing else. It used to wrap the bake as
                    // well, so a TimeoutException raised anywhere inside the sweep demoted the winner
                    // to a follower — still holding its claim, now waiting for a GO only it could ever
                    // publish.
                    .Catch((TimeoutException _) => Observable.Return(false))
                    .SelectMany(granted => granted
                        ? BakeAsMaster(mesh, holder, fingerprint, definitions, bake, logger)
                        : FollowGo(mesh, holder, fingerprint, definitions, store, bake, logger))),
            mesh, fingerprint, definitions, store, logger);
    }

    // ── the pre-warmer's second door ────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-warmer's SECOND DOOR: when the coordination node cannot be REACHED at all, ask the
    /// DURABLE witness whether this image's GO is already recorded, and let readiness follow that
    /// answer (#3404).
    ///
    /// <para><b>The defect this closes.</b> Everything the pre-warmer knew about the build arrived
    /// through ONE door — a <c>SubscribeRequest</c> to <c>Admin/Build</c>. When that request went
    /// unanswered the driver exhausted <see cref="CoordinationAttempts"/> and refused readiness, so
    /// the rollout held the previous image. Measured on <c>memex-cloud</c> 2026-09-06: two pods
    /// refused at 11:44:32Z and 11:49:37Z for a fingerprint whose GO had been written on an already
    /// <c>Ready</c> build root at <b>11:28:56Z</b> — sixteen minutes earlier. They refused a build
    /// that had already been approved, because they could not open a subscription to read a verdict
    /// that was already durable.</para>
    ///
    /// <para><b>The shape is the follower's, not a new one.</b> <see cref="FollowGo"/> has had two
    /// doors since #1440: <c>ReadBuildGo</c> — the durable row, the same witness the claim arbiter
    /// decides on — merged with <c>ObserveBuildGo</c>, this cluster's mirror. Only the pre-warmer's
    /// ENTRY was single-doored. This is the same durable read, at the one point that never had it.
    /// </para>
    ///
    /// <para>🚨 <b>The GO must be THIS process's, and nothing else counts.</b> A GO is
    /// per-fingerprint: the root's <c>Ready</c> map is keyed by framework version precisely so an
    /// old-image pod stays ready through a rollout while the new image is still unproven.
    /// <c>ReadBuildGo</c> looks the fingerprint up by exact key, so a GO written for ANY other
    /// framework version reads as no GO here and this door stays shut. Accepting a foreign GO would
    /// grant readiness for a build this process is not running — which is the one wrong answer this
    /// door could give, and strictly worse than the refusal it replaces.</para>
    ///
    /// <para>🚨 <b>Fail-closed survives untouched where there is a real negative.</b> A witness that
    /// ANSWERS "no GO for this fingerprint" re-throws the original
    /// <see cref="BuildCoordinationUnreachableException"/>, so the sweep still faults, readiness is
    /// still refused, and the health payload still classifies it through
    /// <see cref="DescribesUnreachableCoordination"/>. Nothing here widens a timeout, retries,
    /// polls, or swallows: the transport fault is still logged by
    /// <see cref="RetryUnreachableCoordination"/> at its own severity with its own diagnostic
    /// detail, because it remains the only signal that the pod↔<c>Admin/Build</c>-hub path is
    /// broken. This changes the readiness VERDICT, never the visibility of the fault.</para>
    ///
    /// <para>🚨 <b>THE THIRD STATE, and why this door needed a third one (#3404).</b> The witness
    /// read has three outcomes, not two — <see cref="BuildGoWitness"/> — and the first cut of this
    /// door had only two branches, so <c>Undetermined</c> was ACTED ON and REPORTED as a definitive
    /// negative ("the durable witness carries no GO"). That is the same defect the door was built to
    /// close, one level down: a read that never completed rendered as an answer.</para>
    ///
    /// <para>🚨 <b>And the second door is NOT in a different failure domain from the first.</b> In
    /// the fleet's portal wiring <c>AddPartitionStorageHubs</c> replaces
    /// <see cref="IStorageAdapter"/> with <c>RoutingProxyAdapter</c>, which serves
    /// <c>ReadBuildGo</c> as <c>hub.Observe&lt;ReadNodeResponse&gt;(…)</c> over the SAME hub
    /// transport a <c>SubscribeRequest</c> travels on. The candidates for #3404's silence are a
    /// routing loss, a wedged per-node hub, a lost reply, the deferred-queue ordering defect
    /// (#3408) and a root that stops emitting — and the first, fourth and fifth take both doors
    /// down together. So on the very fault this door exists for, the expected reading is
    /// <c>Undetermined</c>, not <c>NoGo</c>.</para>
    ///
    /// <para><b>What the process does with <c>Undetermined</c>: it MEASURES rather than guesses.</b>
    /// Fail-open ("grant, lazy compile covers correctness") and fail-closed ("refuse, finding
    /// nothing is not passing") are both guesses about a build nobody asked. This pod holds a third
    /// witness that the broken transport cannot touch: its own <see cref="IAssemblyStore"/> — a blob
    /// container or a mounted volume, never a hub message — probed against the LIVE framework
    /// identity. See <see cref="WhenTheWitnessCannotBeRead"/> for the verdict rule and why granting
    /// on it is strictly STRICTER than granting on a GO.</para>
    ///
    /// <para><b>No stand-down, deliberately.</b> The follower's post-GO path withdraws its claim
    /// first, because it really did register one. This path did not: the registration is written by
    /// <c>RequestBuildClaim</c> through <c>GetMeshNodeStream(path).Update(current =&gt; …)</c>, and
    /// an <c>Update</c> whose stream never delivered current state never computed a patch, so no
    /// candidate entry can exist to hand back. Calling <c>WithdrawBuildClaim</c> here would post a
    /// second write into the same unreachable hub and re-fault the stream on the way out.</para>
    /// </summary>
    /// <param name="subscriptionDoor">The subscription-borne composition — claim, grant, bake or follow.</param>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="fingerprint">This process's framework fingerprint.</param>
    /// <param name="definitions">Discovered NodeType definitions by path.</param>
    /// <param name="store">The shared assembly store, for the post-GO probe.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The subscription door's outcomes, or — when it is shut and the evidence allows — the probe's.</returns>
    internal static IObservable<PreWarmOutcome> WhenTheSubscriptionDoorIsShut(
        IObservable<PreWarmOutcome> subscriptionDoor,
        IMessageHub mesh,
        string fingerprint,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        ILogger? logger)
        => subscriptionDoor.Catch((BuildCoordinationUnreachableException unreachable) =>
            mesh.ReadBuildGoReading(fingerprint, logger)
                .SelectMany(reading => reading.Witness switch
                {
                    // Warning, not Information: readiness is granted on durable evidence, but the
                    // transport fault that forced this door open is REAL and unfixed. It is already
                    // logged in full by RetryUnreachableCoordination; this line says what the
                    // process did about it, so the two are not confused for one another.
                    BuildGoWitness.Go => Log(logger, LogLevel.Warning, () => logger!.LogWarning(
                            "BuildProtocol: '{Path}' is UNREACHABLE from this process, but the "
                            + "DURABLE witness already carries the GO for framework {Fingerprint} "
                            + "(ready at {ReadyAt:O}) — the build this image needs was approved "
                            + "before this process asked for it. Readiness follows the durable "
                            + "verdict instead of refusing a build that is already approved. The "
                            + "unreachability above is NOT cleared by this and remains the signal "
                            + "that the path from this pod to the '{Path}' hub is broken.",
                            BuildNodeType.RootPath, fingerprint, reading.Go!.ReadyAt,
                            BuildNodeType.RootPath),
                        ProbeTheShare(mesh, definitions, store, logger)),

                    // A REAL NEGATIVE: the witness answered, and the answer is that no build has
                    // been approved for this image. Fail closed with the original exception — the
                    // health payload reads it through DescribesUnreachableCoordination, and the
                    // hosted service logs the refusal.
                    BuildGoWitness.NoGo => Log(logger, LogLevel.Error, () => logger!.LogError(
                            "BuildProtocol: '{Path}' is unreachable AND the durable witness ANSWERED "
                            + "that it carries no GO for framework {Fingerprint} ({Detail}) — BOTH "
                            + "doors are shut, so this process has verified NOTHING and readiness "
                            + "stays REFUSED. The rollout holds the previous image; a restart "
                            + "re-attempts.",
                            BuildNodeType.RootPath, fingerprint, reading.Detail),
                        Observable.Throw<PreWarmOutcome>(unreachable)),

                    _ => WhenTheWitnessCannotBeRead(
                        mesh, fingerprint, reading, definitions, store, unreachable, logger),
                }));

    /// <summary>
    /// Runs a log line, then returns <paramref name="next"/> — so the three-way branch above stays a
    /// switch expression instead of a statement body that hides the symmetry of its arms.
    /// </summary>
    private static IObservable<T> Log<T>(
        ILogger? logger, LogLevel level, Action write, IObservable<T> next)
    {
        if (logger?.IsEnabled(level) == true)
            write();
        return next;
    }

    /// <summary>
    /// 🚨 THE THIRD STATE'S VERDICT (#3404). Neither coordination door ANSWERED — the subscription
    /// was unreachable and the durable witness could not be read — so the process knows nothing
    /// about the build from the mesh. It does not have to guess: it holds a witness the broken
    /// transport cannot touch.
    ///
    /// <para><b>The third witness.</b> <see cref="IAssemblyStore"/> is a blob container (production)
    /// or a mounted volume (monolith, dev, test) — never a hub message — and
    /// <c>NodeTypeBakeStatus.Probe</c> asks it, per NodeType, whether bytes exist for the LIVE
    /// framework identity. That is a MEASUREMENT of exactly the thing the readiness gate is about,
    /// taken outside the failure domain that shut both doors.</para>
    ///
    /// <para>🚨 <b>Fail-open vs fail-closed, decided rather than assumed.</b> The rule is: grant only
    /// on <c>GateRelevant.IsEmpty</c> — every NodeType that was HEALTHY before this image is
    /// <c>Baked</c> for this framework identity — and refuse otherwise. That is STRICTER than the GO
    /// branch above, not laxer: a GO grants and then reports still-pending types as
    /// <see cref="PreWarmStatus.TimedOut"/>, which <see cref="IsGatingFailure"/> deliberately does
    /// not gate on, so a half-baked share passes WITH a GO and is refused here WITHOUT one. The
    /// asymmetry is the point — a GO is another process's certification, and this door has none, so
    /// it may only grant on evidence it measured itself.</para>
    ///
    /// <para>🚨 <b>"Finding nothing is not passing" is preserved verbatim.</b> A probe that finds a
    /// previously-healthy type still needing a bake refuses, because nobody reachable is going to
    /// build it: this process could not claim and could not follow. What changed is only that the
    /// refusal now rests on something the process established, and SAYS that the witness was
    /// unreadable rather than that there is no GO.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> WhenTheWitnessCannotBeRead(
        IMessageHub mesh,
        string fingerprint,
        BuildGoReading reading,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        BuildCoordinationUnreachableException unreachable,
        ILogger? logger)
        => ProbeTheShareReport(mesh, definitions, store, logger)
            .SelectMany(fresh =>
            {
                if (!fresh.GateRelevant.IsEmpty)
                {
                    logger?.LogError(
                        "BuildProtocol: '{Path}' is unreachable AND the durable witness could not be "
                        + "READ ({Detail}) — so nothing is known about framework {Fingerprint}'s "
                        + "build, and this pod's own share still needs {Pending} previously-healthy "
                        + "NodeType(s) ({Summary}). Nobody reachable is going to build them: this "
                        + "process could neither claim nor follow. Readiness stays REFUSED, the "
                        + "rollout holds the previous image, and a restart re-attempts. 🚨 This is "
                        + "an UNDETERMINED witness, NOT a witness that said there is no GO.",
                        BuildNodeType.RootPath, reading.Detail, fingerprint,
                        fresh.GateRelevant.Count, fresh.Summary);
                    return Observable.Throw<PreWarmOutcome>(unreachable);
                }

                logger?.LogWarning(
                    "BuildProtocol: '{Path}' is UNREACHABLE and the durable witness could not be "
                    + "READ ({Detail}) — so the GO for framework {Fingerprint} is UNDETERMINED, "
                    + "which is not the same as absent. This pod therefore decided on the one "
                    + "witness the broken transport cannot touch: its own assembly store, which "
                    + "holds a build for EVERY previously-healthy NodeType on this framework "
                    + "identity ({Summary}). Readiness follows that measurement. The "
                    + "unreachability above is NOT cleared by this and remains the signal that the "
                    + "path from this pod to the '{Path}' hub is broken.",
                    BuildNodeType.RootPath, reading.Detail, fingerprint, fresh.Summary,
                    BuildNodeType.RootPath);

                return OutcomesOf(
                        fresh,
                        bakedDetail: "on this pod's own share, probed after BOTH coordination doors "
                            + "went silent",
                        pendingDetail: "still pending on this pod's own share, and never healthy "
                            + "before this image")
                    .ToObservable();
            });

    // ── the winner ──────────────────────────────────────────────────────────────────────────────

    private static IObservable<PreWarmOutcome> BakeAsMaster(
        IMessageHub mesh,
        string holder,
        string fingerprint,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        Func<IObservable<PreWarmOutcome>> bake,
        ILogger? logger)
    {
        var chunks = PlanChunks(definitions.Keys);
        logger?.LogInformation(
            "BuildProtocol: claim granted to {Holder} for framework {Fingerprint} — {Chunks} chunk(s): {Names}",
            holder, fingerprint, chunks.Count, string.Join(", ", chunks.Keys));

        var outcomes = new List<PreWarmOutcome>();

        // Heartbeat for the claim's lifetime: a bake measured in minutes must not read as a dead
        // holder to the arbiter. Disposed with the sweep by the Using below.
        IDisposable StartHeartbeat() => Observable.Interval(BuildNodeType.HeartbeatInterval)
            .SelectMany(_ => mesh.BeatBuildClaim(holder))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "BuildProtocol: heartbeat failed for {Holder}", holder));

        // 🚨 The heartbeat covers the WHOLE master path, chunk opening included (#2076). It used to
        // start only after `.ToList()` — i.e. after every chunk had been materialised, claimed and
        // given an activity — and `OpenChunk` waits up to `GrantWindow` for each chunk's own grant.
        // With 37 chunks that is minutes of silence on a claim whose only liveness stamp is the
        // grant instant, and `Observable.Interval` does not beat until one full
        // `HeartbeatInterval` after it is subscribed. Across clusters the holder's identity is
        // Unknown BY CONSTRUCTION (Orleans membership is per-cluster), so the peer's arbiter judges
        // it on `ClaimStaleAfter` alone — and a builder that is working perfectly well can age past
        // that budget before its first beat, licensing a second builder. Starting the beat first
        // makes the stamp track the work rather than trail it; the Using still disposes it with the
        // sweep, so a released claim stops beating exactly as before.
        return Observable.Using(
            StartHeartbeat,
            _ => mesh
                // Record the plan on the root, then materialize + claim + open an activity per chunk.
                .UpdateBuildAsHolder(holder, s => s with
                {
                    Status = BuildStatus.Building,
                    Chunks = chunks.Keys.OrderBy(k => k, StringComparer.Ordinal).ToImmutableList(),
                })
                .SelectMany(_ => chunks
                    .OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => OpenChunk(mesh, holder, fingerprint, c.Key, c.Value, logger))
                    .Concat())
                .ToList()
                .SelectMany(openedChunks => bake()
                    .Do(outcomes.Add)
                    .Concat(Observable.Defer(() =>
                        CloseOut(mesh, holder, fingerprint, chunks, openedChunks.ToList(), outcomes, logger)
                            .IgnoreElements()
                            .Select(__ => default(PreWarmOutcome)!)))));
    }

    private static IObservable<OpenedChunk> OpenChunk(
        IMessageHub mesh, string holder, string fingerprint, string name,
        IReadOnlyList<string> members, ILogger? logger)
    {
        var chunkPath = $"{BuildNodeType.RootPath}/{name}";
        var activityId = Guid.NewGuid().ToString("N");
        var activityPath = $"{chunkPath}/_Activity/{activityId}";
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();

        return mesh.EnsureBuildNode(chunkPath, new BuildState
            {
                Queries = ImmutableList.Create(
                    $"namespace:{name} scope:subtree nodeType:{MeshNode.NodeTypePath}"),
            })
            // The chunk builds for the SAME fingerprint as the root — the field is the framework
            // identity, never a place to smuggle the chunk name (the first prod run did, and the
            // node read `frameworkVersion: "Chess"`).
            .SelectMany(_ => mesh.RequestBuildClaim(holder, fingerprint, chunkPath))
            .SelectMany(_ => mesh.ObserveBuildClaim(holder, chunkPath)
                .Take(1).Timeout(GrantWindow))
            .SelectMany(_ => meshService.CreateNode(new MeshNode(activityId, $"{chunkPath}/_Activity")
            {
                NodeType = "Activity",
                MainNode = chunkPath,
                Content = new ActivityLog("ChunkBuild")
                {
                    Id = activityId,
                    HubPath = chunkPath,
                    Status = ActivityStatus.Running,
                },
            }))
            .SelectMany(_ => mesh.UpdateBuildAsHolder(
                holder,
                s => s with { Status = BuildStatus.Building, ActivityPath = activityPath },
                chunkPath))
            .Select(_ => new OpenedChunk(name, chunkPath, activityPath, members))
            .Catch((Exception ex) =>
            {
                // A chunk whose bookkeeping cannot open must not stop the BUILD — the sweep is
                // what readiness depends on; the chunk node is its observability. Loud, then on.
                logger?.LogWarning(ex,
                    "BuildProtocol: could not open chunk {Chunk} — building without its bookkeeping",
                    name);
                return Observable.Empty<OpenedChunk>();
            });
    }

    private static IObservable<System.Reactive.Unit> CloseOut(
        IMessageHub mesh,
        string holder,
        string fingerprint,
        IReadOnlyDictionary<string, IReadOnlyList<string>> chunks,
        IReadOnlyList<OpenedChunk> openedChunks,
        IReadOnlyList<PreWarmOutcome> outcomes,
        ILogger? logger)
    {
        var byType = outcomes
            .GroupBy(o => o.TypePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        // The release paths each chunk's compiles minted live on the POST-bake definition stamps.
        // Read them per node via the LIVE stream, never a query: the query index is eventually
        // consistent and, right after the bake's own writes, provably lagged — the first version
        // of this method re-enumerated and closed chunks with EMPTY written paths.
        var workspace = mesh.GetWorkspace();
        var successful = openedChunks
            .SelectMany(c => c.Members)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => byType.TryGetValue(m, out var o) && o.ReachedUsableBuild)
            .ToList();
        return successful
            .Select(m => workspace.GetMeshNodeStream(m)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .Select(n => (Type: m,
                    Release: n!.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions)?.LatestReleasePath))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "BuildProtocol: could not read the release stamp of {Type} — its chunk closes without it",
                        m);
                    return Observable.Return((Type: m, Release: (string?)null));
                }))
            .Concat()
            .ToList()
            .Select(stamps => stamps
                .Where(s => !string.IsNullOrEmpty(s.Release))
                .GroupBy(s => s.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Release, StringComparer.OrdinalIgnoreCase))
            .SelectMany(releases => openedChunks
                .Select(chunk => CloseChunk(mesh, holder, chunk, byType, releases, logger))
                .Concat()
                .ToList()
                .SelectMany(_ =>
                {
                    var gating = outcomes.Where(IsGatingFailure).Select(o => o.TypePath).ToList();
                    if (gating.Count > 0)
                    {
                        logger?.LogWarning(
                            "BuildProtocol: NOT publishing GO for {Fingerprint} — {Count} gating regression(s): {Types}",
                            fingerprint, gating.Count, string.Join(", ", gating));
                        return mesh.FailBuild(
                            holder,
                            $"{gating.Count} regression(s) on this image: {string.Join(", ", gating)}");
                    }
                    logger?.LogInformation(
                        "BuildProtocol: publishing GO for framework {Fingerprint} ({Outcomes} outcome(s), {Chunks} chunk(s))",
                        fingerprint, outcomes.Count, chunks.Count);
                    return mesh.CompleteBuild(holder, new BuildGo(
                        fingerprint,
                        DateTime.UtcNow,
                        Detail: $"{outcomes.Count(o => o.ReachedUsableBuild)}/{outcomes.Count} usable across {chunks.Count} chunk(s)"));
                }))
            .Select(_ => System.Reactive.Unit.Default);
    }

    private static IObservable<MeshNode> CloseChunk(
        IMessageHub mesh,
        string holder,
        OpenedChunk chunk,
        IReadOnlyDictionary<string, PreWarmOutcome> byType,
        IReadOnlyDictionary<string, string?> releases,
        ILogger? logger)
    {
        var memberOutcomes = chunk.Members
            .Select(m => byType.TryGetValue(m, out var o) ? o : null)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToList();
        var failed = memberOutcomes.Where(IsGatingFailure).ToList();
        var written = chunk.Members
            .Where(m => byType.TryGetValue(m, out var o) && o.ReachedUsableBuild)
            .Select(m => releases.TryGetValue(m, out var r) ? r : null)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .ToImmutableList();

        return mesh.UpdateBuildAsHolder(
                holder,
                s => s with
                {
                    Status = failed.Count > 0 ? BuildStatus.Failed : BuildStatus.Ready,
                    Error = failed.Count > 0
                        ? string.Join("; ", failed.Select(f => $"{f.TypePath}: {f.Detail}"))
                        : null,
                    WrittenPaths = written,
                    ClaimedBy = null, ClaimedAt = null, HeartbeatAt = null,
                },
                chunk.Path)
            // This close-out clears the claim fields itself instead of going through
            // CompleteBuild/FailBuild, so it has to drop the chunk's LOCK explicitly — clearing
            // ClaimedBy on the node alone would free the chunk in this cluster only.
            .SelectMany(node => mesh.ReleaseBuildClaim(holder, chunk.Path).Select(_ => node))
            .SelectMany(_ => FinishActivity(
                mesh, chunk.ActivityPath,
                failed.Count > 0 ? ActivityStatus.Failed : ActivityStatus.Succeeded))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "BuildProtocol: could not close chunk {Chunk}", chunk.Name);
                return Observable.Empty<MeshNode>();
            });
    }

    // internal, not private: MeshWeaver.Graph.Test drives this directly to pin the #3117
    // release. Nothing outside the test assembly can see it.
    internal static IObservable<MeshNode> FinishActivity(
        IMessageHub mesh, string activityPath, ActivityStatus status) =>
        mesh.GetWorkspace().GetMeshNodeStream(activityPath).Update(node =>
            {
                var log = node?.ContentAs<ActivityLog>(mesh.JsonSerializerOptions);
                if (node is null || log is null || log.Status.IsTerminal()) return node!;
                return node with { Content = log.Finish(log.Version + 1, status) };
            })
            // #3117 — a build-protocol completion is a terminal _Activity write that bypasses
            // ActivityLogAppender.Append, so it never retired the activity's per-node hub. On
            // COMPLETION, never on the emission — the write is still in flight when its value is
            // emitted. An already-terminal log takes the no-op arm above and is released here too,
            // which is correct: the status IS terminal, whoever wrote it.
            .Do(_ => { },
                () => ActivityLogAppender.ReleaseMirrorWhenFinal(mesh, activityPath, status));

    // ── the follower ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The path taken when the claim is held elsewhere. It ends on one of TWO real events, and the
    /// point of #1440 is that it used to have neither reliably:
    ///
    /// <list type="number">
    /// <item><b>The GO becomes visible.</b> Not "is announced" — <em>visible</em>. The old code
    /// subscribed to <c>ObserveBuildGo</c> alone, which is a projection of THIS cluster's mirror of
    /// <c>Admin/Build</c>. A builder in another process writes the same durable row; the
    /// <c>PostgreSqlChangeListener</c> feed does cross the process boundary (the partitioned
    /// overloads start it since #1816 — that "registered and never started" was #1440, the middle
    /// leg of #1814), but a <c>NOTIFY</c> reaches only a LIVE <c>LISTEN</c> session and is never
    /// replayed, so a mirror that activates after that write is still never told and a wait on
    /// the announcement alone is a wait for an event that may already have passed. So the GO is
    /// now also READ off the durable witness — the same record, and for the same reason, the claim
    /// arbiter decides on (<c>BuildNodeType.ArbitrateDurably</c>).</item>
    /// <item><b>The arbiter hands US the claim.</b> Registering as a candidate is not free to
    /// abandon: the registration outlives the grant wait, and the arbiter grants it the moment the
    /// build falls free — whether the builder finished or died. The old follower had stopped
    /// listening, so that grant went to a process that would never act on it, leaving the build
    /// locked to a live holder the takeover rule then defends forever. Observing it instead turns
    /// "the builder went away" into a real event that ends the wait, and turns the grant into the
    /// level-trigger on which the durable witness is re-read.</item>
    /// </list>
    ///
    /// <para>🚨 No timer, and no bound bolted onto the wait. A bound would end the wait by GUESSING,
    /// and a follower that guesses "the build finished" certifies a share it never saw — a silent
    /// wrong answer where the hang at least announced itself. Both doors above are level-triggered
    /// on durable state, exactly like the arbiter (#1437, and #1366 for why the poll clock went).</para>
    /// </summary>
    internal static IObservable<PreWarmOutcome> FollowGo(
        IMessageHub mesh,
        string holder,
        string fingerprint,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        Func<IObservable<PreWarmOutcome>> bake,
        ILogger? logger)
    {
        logger?.LogInformation(
            "BuildProtocol: claim held elsewhere — {Holder} follows the build for framework "
            + "{Fingerprint} (durable witness + this cluster's GO + its own claim candidacy)",
            holder, fingerprint);

        // Door 1 — the GO is visible: on the durable row (the witness a peer cluster shares with
        // the builder) or on this cluster's mirror, whichever answers first.
        var go = mesh.ReadBuildGo(fingerprint, logger)
            .Where(g => g is not null)
            .Select(g => g!)
            .Merge(mesh.ObserveBuildGo(fingerprint))
            .Select(g => (Go: (BuildGo?)g, Granted: false));

        // Door 2 — the build fell free and the arbiter granted it to us. Re-subscribing here also
        // closes the race where the grant landed microseconds after the GrantWindow expired.
        var granted = mesh.ObserveBuildClaim(holder)
            .Take(1)
            .Select(_ => (Go: (BuildGo?)null, Granted: true));

        return go.Merge(granted)
            .Take(1)
            .SelectMany(end => end.Granted
                ? OnGranted(mesh, holder, fingerprint, definitions, store, bake, logger)
                : ProbeAfterGo(mesh, holder, fingerprint, end.Go!, definitions, store, logger));
    }

    /// <summary>
    /// The follower was handed the claim. That means the build fell free — but NOT which way: the
    /// builder may have finished (published its GO and released) or gone away mid-bake. Only the
    /// durable witness distinguishes them, and asking it here is what makes a peer cluster's GO
    /// actually spare this process the bake instead of merely arriving too late to matter.
    /// </summary>
    private static IObservable<PreWarmOutcome> OnGranted(
        IMessageHub mesh,
        string holder,
        string fingerprint,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        Func<IObservable<PreWarmOutcome>> bake,
        ILogger? logger)
        => mesh.ReadBuildGo(fingerprint, logger)
            .SelectMany(go =>
            {
                if (go is not null)
                {
                    logger?.LogInformation(
                        "BuildProtocol: {Holder} was granted the claim, but the durable witness "
                        + "already carries the GO for framework {Fingerprint} (ready at {ReadyAt:O}) "
                        + "— standing down instead of re-baking",
                        holder, fingerprint, go.ReadyAt);
                    return ProbeAfterGo(mesh, holder, fingerprint, go, definitions, store, logger);
                }

                logger?.LogInformation(
                    "BuildProtocol: {Holder} was granted the claim while following framework "
                    + "{Fingerprint} — the previous builder released it without a GO, so this "
                    + "process bakes",
                    holder, fingerprint);
                return BakeAsMaster(mesh, holder, fingerprint, definitions, bake, logger);
            });

    /// <summary>
    /// The GO says the build finished; the share says what actually landed. Probing (rather than
    /// trusting) keeps the follower level-triggered on reality — the same property the sweep itself
    /// has. A type still pending after GO is reported as not-evaluated (non-gating): the follower
    /// has no verdict about it.
    ///
    /// <para>Standing down FIRST is not bookkeeping. A candidate that reached its answer without a
    /// grant still has a live registration, and the arbiter will hand it the next free build — a
    /// build this process has already finished with and will never run. See
    /// <c>WithdrawBuildClaim</c> for why that wedges the following image's rollout.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> ProbeAfterGo(
        IMessageHub mesh,
        string holder,
        string fingerprint,
        BuildGo go,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        ILogger? logger)
    {
        logger?.LogInformation(
            "BuildProtocol: GO visible for framework {Fingerprint} (ready at {ReadyAt:O}) — "
            + "{Holder} stands down and probes the share",
            fingerprint, go.ReadyAt, holder);

        return mesh.WithdrawBuildClaim(holder)
            .SelectMany(_ => ProbeTheShare(mesh, definitions, store, logger));
    }

    /// <summary>
    /// What a GO actually buys: the share is PROBED rather than trusted, so the verdict stays
    /// level-triggered on reality — the same property the sweep itself has. A type still pending
    /// after GO is reported as not-evaluated (<see cref="PreWarmStatus.TimedOut"/>, non-gating):
    /// this process has no verdict about it, which is different from a verdict against it.
    ///
    /// <para>Deliberately contains NO claim bookkeeping. <see cref="ProbeAfterGo"/> stands down
    /// first because it registered as a candidate; <see cref="WhenTheSubscriptionDoorIsShut"/> has
    /// nothing to stand down from and must not post into an unreachable hub to find that out. Split
    /// out so the two share the probe without sharing the stand-down.</para>
    /// </summary>
    private static IObservable<PreWarmOutcome> ProbeTheShare(
        IMessageHub mesh,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        ILogger? logger)
        => ProbeTheShareReport(mesh, definitions, store, logger)
            .SelectMany(fresh => OutcomesOf(
                fresh,
                bakedDetail: "on the share after GO",
                pendingDetail: "still pending on the share after the build published GO"));

    /// <summary>
    /// The probe itself, kept separate from the outcome mapping so a caller that must DECIDE on the
    /// report — <see cref="WhenTheWitnessCannotBeRead"/>, which needs
    /// <c>NodeTypeBakeReport.GateRelevant</c> — reads the same measurement the outcomes are built
    /// from, in ONE probe. Two probes would let the decision and the report disagree about a share
    /// another process is writing to.
    /// </summary>
    private static IObservable<NodeTypeBakeReport> ProbeTheShareReport(
        IMessageHub mesh,
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        ILogger? logger)
        => NodeTypeBakeStatus.Probe(definitions, store, logger: logger,
            liveDependencyIdOf: NodeTypeCompilationHelpers.DependencyIdResolverOf(mesh),
            liveToolchainId: NodeTypeCompilationHelpers.ProcessToolchainId);

    /// <summary>
    /// One probe report as pre-warm outcomes. A type the share still needs is
    /// <see cref="PreWarmStatus.TimedOut"/> — NOT evaluated, and therefore non-gating per
    /// <see cref="IsGatingFailure"/> — because a probe is not a compile and this process has no
    /// verdict about that type, which is different from a verdict against it.
    /// </summary>
    private static IEnumerable<PreWarmOutcome> OutcomesOf(
        NodeTypeBakeReport fresh, string bakedDetail, string pendingDetail)
        => fresh.Entries.Select(e => new PreWarmOutcome(
            e.TypePath,
            e.NeedsBake ? PreWarmStatus.TimedOut : PreWarmStatus.AlreadyBaked,
            e.NeedsBake ? pendingDetail : bakedDetail)
        {
            WasHealthyBeforeBake = e.WasHealthy,
        });

    // ── shared ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="handshake"/>, re-attempting it while the failure means the coordination
    /// node could not be REACHED (#1635) — and giving up loudly when it does not clear.
    ///
    /// <para>Pure Rx over an injected factory and scheduler so the policy is unit-testable without
    /// a mesh: the property that matters is not the happy path but that exhausting the attempts
    /// still ERRORS. A readiness gate whose evidence is unreachable must fail closed; the retry
    /// exists only so a grain that is still activating does not count as unreachable.</para>
    ///
    /// <para>Only "unreachable" is retried. Any other error — a malformed state, a rejected write,
    /// a bug — surfaces on the first attempt, unwrapped, because retrying it would just delay the
    /// same verdict by minutes and bury the cause under three identical stack traces.</para>
    /// </summary>
    /// <typeparam name="T">The handshake's emission.</typeparam>
    /// <param name="handshake">Cold factory for one attempt. Re-invoked per attempt.</param>
    /// <param name="attempts">Total attempts, including the first. Values below 1 mean one attempt.</param>
    /// <param name="backoff">Delay before the next attempt, given how many have failed (1-based).</param>
    /// <param name="scheduler">Scheduler for the backoff delay.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<T> RetryUnreachableCoordination<T>(
        Func<IObservable<T>> handshake,
        int attempts,
        Func<int, TimeSpan> backoff,
        IScheduler scheduler,
        ILogger? logger)
    {
        var total = Math.Max(1, attempts);

        IObservable<T> Attempt(int attemptNumber) => Observable
            .Defer(handshake)
            .Catch((Exception ex) =>
            {
                if (!IsUnreachable(ex))
                    return Observable.Throw<T>(ex);

                if (attemptNumber >= total)
                {
                    // Loud, and it NAMES what it could not reach. The bare TimeoutException this
                    // replaces said "no response ... → target Admin/Build" and was read as a
                    // compile problem for as long as anyone looked at it.
                    var unreachable = new BuildCoordinationUnreachableException(
                        $"BuildProtocol: could not reach the build coordination node "
                        + $"'{BuildNodeType.RootPath}' in {total} attempt(s) — the pre-warm sweep "
                        + "never started, so this process has verified NOTHING about its NodeTypes "
                        + "on this image. This is a refusal, not a pass: readiness stays refused "
                        + "and the rollout holds the previous image. A restart re-attempts.",
                        ex);
                    logger?.LogError(unreachable, "{Message}", unreachable.Message);
                    return Observable.Throw<T>(unreachable);
                }

                var wait = backoff(attemptNumber);
                logger?.LogWarning(ex,
                    "BuildProtocol: attempt {Attempt}/{Total} could not reach the coordination "
                    + "node '{Path}' — its hub is most likely still activating. Retrying in "
                    + "{Wait}. If every attempt fails the sweep FAULTS and readiness is refused.",
                    attemptNumber, total, BuildNodeType.RootPath, wait);

                return wait <= TimeSpan.Zero
                    ? Observable.Defer(() => Attempt(attemptNumber + 1))
                    : Observable.Timer(wait, scheduler)
                        .SelectMany(_ => Attempt(attemptNumber + 1));
            });

        return Attempt(1);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> means the coordination node was never reached, as
    /// opposed to having answered something we did not like. A hub request that gets no response
    /// dies as a <see cref="TimeoutException"/>, and it can arrive wrapped (an
    /// <see cref="AggregateException"/> from a merged inner stream), so the walk is over the whole
    /// chain rather than the outermost type.
    /// </summary>
    /// <summary>
    /// Whether a faulted warm-up stream failed because the coordination node was never reached,
    /// rather than because the sweep produced a bad verdict. Consumed by the readiness refusal so
    /// the two are distinguishable in the health payload — see #1635.
    /// </summary>
    public static bool DescribesUnreachableCoordination(Exception? exception) => exception switch
    {
        null => false,
        BuildCoordinationUnreachableException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(DescribesUnreachableCoordination),
        { InnerException: { } inner } => DescribesUnreachableCoordination(inner),
        _ => false,
    };

    internal static bool IsUnreachable(Exception exception) => exception switch
    {
        TimeoutException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsUnreachable),
        { InnerException: { } inner } => IsUnreachable(inner),
        _ => false,
    };


    /// <summary>
    /// Derive the chunk plan: one chunk per first path segment (the partition — which is exactly a
    /// plugin's footprint for plugin content). Deterministic, order-independent, and derived rather
    /// than invented, per the design doc.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> PlanChunks(
        IEnumerable<string> typePaths) =>
        typePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(
                p => { var i = p.IndexOf('/'); return i > 0 ? p[..i] : p; },
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mirrors <c>NodeTypeBakeGateState.MarkOutcome</c>'s gating rules: only a measured failure of
    /// a previously-healthy type blocks the GO. "I don't know" (timeouts, unevaluated upstreams)
    /// and content verdicts (sources deleted) never do — the same leniency the readiness gate
    /// applies, kept in one shape here so the GO and the gate cannot disagree about what a
    /// regression is.
    /// </summary>
    internal static bool IsGatingFailure(PreWarmOutcome outcome) =>
        !outcome.ReachedUsableBuild
        && outcome.WasHealthyBeforeBake
        && outcome.Status is not (PreWarmStatus.TimedOut
            or PreWarmStatus.UpstreamUnevaluated
            or PreWarmStatus.NoSources
            or PreWarmStatus.UpstreamContentBroken);

    private sealed record OpenedChunk(
        string Name, string Path, string ActivityPath, IReadOnlyList<string> Members);
}

/// <summary>
/// The build coordination node (<c>Admin/Build</c>) could not be reached, so the pre-warm sweep
/// never ran (#1635).
///
/// <para>Distinct from every failure the sweep itself can produce: those are verdicts about
/// whether this image builds its NodeTypes, this one is the absence of a verdict. Both refuse
/// readiness — a pod that verified nothing must not claim it did — but only this one is worth
/// restarting on, which is why it has its own name in the log instead of arriving as a bare
/// <see cref="TimeoutException"/> whose target an operator has to decode.</para>
/// </summary>
public sealed class BuildCoordinationUnreachableException(string message, Exception innerException)
    : Exception(message, innerException);
