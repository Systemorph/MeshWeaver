using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

        return mesh.RequestBuildClaim(holder, fingerprint, priority: priority)
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
                    : FollowGo(mesh, holder, fingerprint, definitions, store, bake, logger)));
    }

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

        return mesh
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
            .SelectMany(openedChunks => Observable.Using(
                StartHeartbeat,
                _ => bake()
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

    private static IObservable<MeshNode> FinishActivity(
        IMessageHub mesh, string activityPath, ActivityStatus status) =>
        mesh.GetWorkspace().GetMeshNodeStream(activityPath).Update(node =>
        {
            var log = node?.ContentAs<ActivityLog>(mesh.JsonSerializerOptions);
            if (node is null || log is null || log.Status.IsTerminal()) return node!;
            return node with { Content = log.Finish(log.Version + 1, status) };
        });

    // ── the follower ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The path taken when the claim is held elsewhere. It ends on one of TWO real events, and the
    /// point of #1440 is that it used to have neither reliably:
    ///
    /// <list type="number">
    /// <item><b>The GO becomes visible.</b> Not "is announced" — <em>visible</em>. The old code
    /// subscribed to <c>ObserveBuildGo</c> alone, which is a projection of THIS cluster's mirror of
    /// <c>Admin/Build</c>. A builder in another process writes the same durable row, but no change
    /// feed crosses the process boundary (<c>PostgreSqlChangeListener</c> is registered and never
    /// started in either partitioned-PG overload), so a mirror that activated before that write is
    /// never told and the wait was for an event that could not be delivered. So the GO is now also
    /// READ off the durable witness — the same record, and for the same reason, the claim arbiter
    /// decides on (<c>BuildNodeType.ArbitrateDurably</c>).</item>
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
            .SelectMany(_ => NodeTypeBakeStatus.Probe(definitions, store, logger: logger))
            .SelectMany(fresh => fresh.Entries
                .Select(e => new PreWarmOutcome(
                    e.TypePath,
                    e.NeedsBake ? PreWarmStatus.TimedOut : PreWarmStatus.AlreadyBaked,
                    e.NeedsBake
                        ? "still pending on the share after the build published GO"
                        : "on the share after GO")
                {
                    WasHealthyBeforeBake = e.WasHealthy,
                }));
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────

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
