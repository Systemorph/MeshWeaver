using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The build protocol's coordination contract (<c>Doc/Architecture/BuildCoordination.md</c>):
/// the root materializes once, claims are granted by the node's own hub (never taken), a
/// non-holder's writes land on nothing, completing a build publishes a GO that later builds
/// never revoke, and releasing a claim hands the node to the next queued candidate.
/// </summary>
public class BuildCoordinationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);

    // The protocol-bake test drives a full cold Roslyn kickoff compile plus a protocol sweep —
    // the same budget reasoning as NodeTypeReleaseTest's override: align the base class's dispose
    // watchdog with the longest [Fact] timeout in the class so it cannot kill the test before its
    // own declared budget.
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(240);

    [Fact(Timeout = 60_000)]
    public async Task ClaimQueue_Go_And_HolderGuard()
    {
        var hub = Mesh;
        var workspace = hub.GetWorkspace();

        // Materialize once — a second Ensure must resolve to the same node, not a second create.
        var first = await hub.EnsureBuildNode().FirstAsync().ToTask();
        var second = await hub.EnsureBuildNode().FirstAsync().ToTask();
        first.Path.Should().Be(BuildNodeType.RootPath);
        second.Path.Should().Be(first.Path);

        // Candidate A registers and is granted by the arbiter (nobody holds the node).
        await hub.RequestBuildClaim("holder-a", "fp-a").FirstAsync().ToTask();
        var grantedA = await hub.ObserveBuildClaim("holder-a")
            .FirstAsync().Timeout(WaitBudget).ToTask();
        grantedA.FrameworkVersion.Should().Be("fp-a");
        grantedA.Status.Should().Be(BuildStatus.Planning);

        // Candidate B registers while A holds — it must QUEUE, not preempt.
        await hub.RequestBuildClaim("holder-b", "fp-b").FirstAsync().ToTask();
        var whileAHolds = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s?.RequestedClaims?.ContainsKey("holder-b") == true)
            .FirstAsync().Timeout(WaitBudget).ToTask();
        whileAHolds!.ClaimedBy.Should().Be("holder-a");

        // A non-holder's guarded write is a no-op — a superseded builder cannot corrupt state.
        await hub.UpdateBuildAsHolder("holder-b", s => s with { Error = "must not land" })
            .FirstAsync().ToTask();
        var afterGuardedWrite = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Where(n => n is not null).FirstAsync().ToTask();
        afterGuardedWrite.ContentAs<BuildState>(hub.JsonSerializerOptions)!
            .Error.Should().BeNull();

        // A completes: GO for fp-a lands, the claim is released, and the arbiter grants B.
        await hub.CompleteBuild("holder-a", new BuildGo("fp-a", DateTime.UtcNow))
            .FirstAsync().ToTask();
        var goA = await hub.ObserveBuildGo("fp-a").FirstAsync().Timeout(WaitBudget).ToTask();
        goA.FrameworkVersion.Should().Be("fp-a");
        var grantedB = await hub.ObserveBuildClaim("holder-b")
            .FirstAsync().Timeout(WaitBudget).ToTask();
        grantedB.FrameworkVersion.Should().Be("fp-b");

        // B completes: fp-b's GO is ADDED — fp-a's GO must survive. Old-image silos stay ready.
        await hub.CompleteBuild("holder-b", new BuildGo("fp-b", DateTime.UtcNow))
            .FirstAsync().ToTask();
        var final = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s?.Ready?.ContainsKey("fp-b") == true)
            .FirstAsync().Timeout(WaitBudget).ToTask();
        final!.Ready!.Should().ContainKey("fp-a");
        final.Ready.Should().ContainKey("fp-b");
        final.ClaimedBy.Should().BeNull();
        final.Status.Should().Be(BuildStatus.Ready);
    }

    /// <summary>
    /// The executor path end-to-end: a protocol-driven sweep claims the root, opens a chunk per
    /// partition with its own <c>_Activity</c>, records the release paths the build produced, and
    /// publishes the GO — including when the share already holds every build (that re-publish IS
    /// the crashed-before-GO healing path).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ProtocolDrivenBake_PublishesGo_AndChunkReportsReleases()
    {
        var workspace = Mesh.GetWorkspace();
        const string partition = "TestBuildProto";
        const string typeId = "Sample";
        var typePath = $"{partition}/{typeId}";

        await NodeFactory.CreateNode(new MeshNode(typeId, partition)
        {
            Name = "Sample Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Sample for the protocol bake test.",
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        }).Should().Emit();

        // The kickoff compile (per-NodeType hub watcher) settles first — cold Roslyn on a 2-core
        // CI runner can take 60-90s.
        _ = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));

        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, buildProtocol: true)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(120))
            .ToTask();
        (outcomes.Count > 0).Should().Be(true);

        var root = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(Mesh.JsonSerializerOptions))
            .Where(s => s?.Ready is { Count: > 0 })
            .FirstAsync().Timeout(WaitBudget).ToTask();
        root!.Status.Should().Be(BuildStatus.Ready);
        root.ClaimedBy.Should().BeNull();

        var chunk = await workspace.GetMeshNodeStream($"{BuildNodeType.RootPath}/{partition}")
            .Select(n => n?.ContentAs<BuildState>(Mesh.JsonSerializerOptions))
            .Where(s => s is { Status: BuildStatus.Ready })
            .FirstAsync().Timeout(WaitBudget).ToTask();
        // The sweep may have re-baked the type (a host without an assembly store re-compiles by
        // design), so the chunk's written path can be a NEWER release than the kickoff one — what
        // is contractual is that the paths it reports are release nodes of this chunk's types.
        chunk!.WrittenPaths.Should().NotBeNull();
        (chunk.WrittenPaths!.Count > 0).Should().Be(true);
        chunk.WrittenPaths.All(p => p.StartsWith($"{typePath}/Release/", StringComparison.Ordinal))
            .Should().Be(true);
        chunk.ActivityPath.Should().NotBeNull();

        var activity = await workspace.GetMeshNodeStream(chunk.ActivityPath!)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Status.IsTerminal())
            .FirstAsync().Timeout(WaitBudget).ToTask();
        activity!.Status.Should().Be(ActivityStatus.Succeeded);
    }

    // ---- The follower (#1440): a wait that ends on a real event, never on a notification ----
    //
    // Both tests below drive the PUBLIC entry point BuildProtocolDriver.Run, so they compile
    // unchanged against the pre-fix implementation — and both hang there until their [Fact] timeout.
    // What they pin is that a follower has TWO doors out, and that it closes them behind it:
    //
    //   • the GO becomes VISIBLE — read off the durable witness, not merely awaited on this
    //     cluster's mirror, which a builder in another process can never reach; and
    //   • the arbiter hands US the claim, because the builder went away.
    //
    // The 15 s each test spends before its trigger is BuildProtocolDriver.GrantWindow — a declared
    // bound of the protocol that the candidate must run out before it becomes a follower at all,
    // not a guess at how long some propagation takes. Trigger earlier and the arbiter grants the
    // candidate during its grant wait, which exercises the master path instead.

    private static readonly IReadOnlyDictionary<string, NodeTypeDefinition?> NoDynamicTypes =
        new Dictionary<string, NodeTypeDefinition?>();

    private static IObservable<Unit> AfterTheGrantWindow() =>
        Observable.Timer(BuildProtocolDriver.GrantWindow + TimeSpan.FromSeconds(2))
            .Select(_ => Unit.Default);

    private IObservable<BuildState> Root() =>
        Mesh.GetWorkspace().GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null)
            .Select(s => s!);

    /// <summary>
    /// The builder goes away WITHOUT ever publishing a GO. Nothing will ever emit the event the
    /// follower was waiting for — the only process that could publish it has stopped — and the
    /// protocol has already handed the build to the follower, which is the event it should have
    /// been watching. Before the fix the follower had unsubscribed from its own claim, so the grant
    /// went to a process that would never act on it and this test runs out its timeout.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Follower_TakesOverTheBake_WhenTheBuildFallsFreeWithoutAGo()
    {
        var hub = Mesh;
        const string fingerprint = "fp-falls-free";

        // A rival wins the build and starts baking.
        await hub.RequestBuildClaim("rival", fingerprint).FirstAsync().ToTask();
        await hub.ObserveBuildClaim("rival").FirstAsync().Timeout(WaitBudget).ToTask();

        var baked = 0;
        var run = BuildProtocolDriver.Run(
                hub,
                NodeTypeBakeReport.Empty(fingerprint),
                NoDynamicTypes,
                NullAssemblyStore.Instance,
                () => Observable.Defer(() =>
                {
                    Interlocked.Increment(ref baked);
                    return Observable.Return(
                        new PreWarmOutcome("Test/FallsFree", PreWarmStatus.Compiled, "baked by the follower"));
                }),
                logger: null)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask();

        // Past the grant window our candidate is a follower. Now the builder dies: the claim is
        // dropped and no GO is ever published for this fingerprint.
        await AfterTheGrantWindow().FirstAsync().ToTask();
        await hub.FailBuild("rival", "builder process gone").FirstAsync().ToTask();

        var outcomes = await run;

        baked.Should().Be(1, "the follower was handed the build and must run it");
        outcomes.Should().ContainSingle().Which.TypePath.Should().Be("Test/FallsFree");

        // …and it finished the job properly: the GO it published is what every other silo is waiting on.
        var final = await Root().Where(s => s.Ready?.ContainsKey(fingerprint) == true)
            .FirstAsync().Timeout(WaitBudget).ToTask();
        final.ClaimedBy.Should().BeNull();
    }

    /// <summary>
    /// The builder finishes normally. The follower must complete on the GO — and STAND DOWN, which
    /// is the half that used to be missing: its registration outlived the wait, so the arbiter
    /// handed it the freed build, it never acted on it, and the claim stuck to a live holder the
    /// takeover rule defends by design. The next image's fingerprint could then never be claimed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Follower_StandsDown_SoTheNextBuildCanStillBeClaimed()
    {
        var hub = Mesh;
        const string fingerprint = "fp-stand-down";

        await hub.RequestBuildClaim("rival", fingerprint).FirstAsync().ToTask();
        await hub.ObserveBuildClaim("rival").FirstAsync().Timeout(WaitBudget).ToTask();

        var baked = 0;
        var run = BuildProtocolDriver.Run(
                hub,
                NodeTypeBakeReport.Empty(fingerprint),
                NoDynamicTypes,
                NullAssemblyStore.Instance,
                () => Observable.Defer(() =>
                {
                    Interlocked.Increment(ref baked);
                    return Observable.Empty<PreWarmOutcome>();
                }),
                logger: null)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask();

        await AfterTheGrantWindow().FirstAsync().ToTask();
        await hub.CompleteBuild("rival", new BuildGo(fingerprint, DateTime.UtcNow))
            .FirstAsync().ToTask();

        await run;
        baked.Should().Be(0, "the build was done — the follower had nothing to bake");

        // The build must be free for the NEXT candidate: no holder, and nobody queued who has
        // already walked away. A single pending registration here is the wedge.
        var settled = await Root()
            .Where(s => s.ClaimedBy is null && s.RequestedClaims is null or { Count: 0 })
            .FirstAsync().Timeout(WaitBudget).ToTask();
        settled.Ready.Should().ContainKey(fingerprint);

        // …and it really is claimable: a fresh candidate is granted rather than queued forever.
        await hub.RequestBuildClaim("next-image", "fp-next").FirstAsync().ToTask();
        var grantedNext = await hub.ObserveBuildClaim("next-image")
            .FirstAsync().Timeout(WaitBudget).ToTask();
        grantedNext.FrameworkVersion.Should().Be("fp-next");
    }

    // ---- Arbitrate as a pure decision procedure: the staleness rules without wall-clock ----

    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode BuildNode(BuildState state) =>
        new("Build", "Admin") { NodeType = BuildNodeType.NodeType, Content = state };

    [Fact]
    public void Arbitrate_GrantsEarliestRequest_WhenUnclaimed()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("late", new BuildClaimRequest("fp-late", t0.AddSeconds(5)))
                .Add("early", new BuildClaimRequest("fp-early", t0)),
        });

        var result = BuildNodeType.Arbitrate(node, Options, t0.AddSeconds(10))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("early");
        result.FrameworkVersion.Should().Be("fp-early");
        result.Status.Should().Be(BuildStatus.Planning);
        result.RequestedClaims.Should().NotContainKey("early");
        result.RequestedClaims.Should().ContainKey("late");
    }

    // ---- The cross-cluster election (#1424): priority, convergence window, determinism ----
    //
    // One arbiter runs per Orleans cluster that touches the node, each over its own mirror of the
    // same durable row — so the grant must be a deterministic election over converged data, never
    // "first arbiter to look wins" (the pilot's double bake).

    /// <summary>
    /// A dedicated bake process beats every serving pod, however much earlier the pod registered —
    /// this is what makes the pods stand down exactly when a bake Job is present.
    /// </summary>
    [Fact]
    public void Arbitrate_PriorityOutranksAnEarlierRegistration()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("pod", new BuildClaimRequest("fp", t0))
                .Add("bake-job", new BuildClaimRequest(
                    "fp", t0.AddSeconds(3), Priority: BuildClaimRequest.BakePriority)),
        });

        var result = BuildNodeType.Arbitrate(node, Options, t0.AddSeconds(10))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("bake-job");
        result.RequestedClaims.Should().ContainKey("pod");
    }

    /// <summary>
    /// A fresh root election inside the settle window grants NOTHING — same reference back, no
    /// write — because a concurrent registration from another cluster may still be propagating.
    /// The re-check timer, not a new emission, is what revisits it.
    /// </summary>
    [Fact]
    public void Arbitrate_FreshRootElection_WaitsOutTheSettleWindow()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("pod", new BuildClaimRequest("fp", t0)),
        });

        BuildNodeType.Arbitrate(
                node, Options, t0 + BuildNodeType.GrantSettleWindow - TimeSpan.FromSeconds(1))
            .Should().BeSameAs(node);

        BuildNodeType.Arbitrate(
                node, Options, t0 + BuildNodeType.GrantSettleWindow + TimeSpan.FromSeconds(1))
            .ContentAs<BuildState>(Options)!.ClaimedBy.Should().Be("pod");
    }

    /// <summary>
    /// Chunk claims are NOT window-gated: the root election already decided the builder, and 37
    /// chunks × 5 s of settle would put minutes of pure waiting into a ~2-minute bake.
    /// </summary>
    [Fact]
    public void Arbitrate_ChunkClaims_GrantWithoutTheWindow()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var chunk = new MeshNode("Chess", "Admin/Build")
        {
            NodeType = BuildNodeType.NodeType,
            Content = new BuildState
            {
                RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                    .Add("winner", new BuildClaimRequest("fp", t0)),
            },
        };

        BuildNodeType.Arbitrate(chunk, Options, t0.AddSeconds(1))
            .ContentAs<BuildState>(Options)!.ClaimedBy.Should().Be("winner");
    }

    /// <summary>
    /// The property the whole fix rests on: two arbiters looking at the SAME converged data
    /// compute the SAME winner, so their concurrent grant writes are idempotent instead of
    /// last-write-wins. Asserted over both discriminators (priority and timestamp) at once.
    /// </summary>
    [Fact]
    public void Arbitrate_IsDeterministic_AcrossArbiters()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var state = new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("pod-a", new BuildClaimRequest("fp", t0.AddMilliseconds(1)))
                .Add("pod-b", new BuildClaimRequest("fp", t0.AddMilliseconds(2)))
                .Add("bake-job", new BuildClaimRequest(
                    "fp", t0.AddMilliseconds(3), Priority: BuildClaimRequest.BakePriority)),
        };
        var now = t0.AddSeconds(10);

        var one = BuildNodeType.Arbitrate(BuildNode(state), Options, now)
            .ContentAs<BuildState>(Options)!;
        var two = BuildNodeType.Arbitrate(BuildNode(state), Options, now)
            .ContentAs<BuildState>(Options)!;

        one.ClaimedBy.Should().Be("bake-job");
        two.ClaimedBy.Should().Be(one.ClaimedBy);
        two.ClaimedAt.Should().Be(one.ClaimedAt);
    }

    [Fact]
    public void Arbitrate_LeavesLiveClaimAlone()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            ClaimedBy = "current",
            ClaimedAt = t0,
            HeartbeatAt = t0,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("challenger", new BuildClaimRequest("fp-x", t0.AddSeconds(1))),
        });

        // Heartbeat is fresh: the challenger stays queued, the node is returned UNCHANGED
        // (same reference — a redundant arbitration tick must write nothing).
        var result = BuildNodeType.Arbitrate(
            node, Options, t0 + BuildNodeType.ClaimStaleAfter - TimeSpan.FromSeconds(1));
        result.Should().BeSameAs(node);
    }

    [Fact]
    public void Arbitrate_StealsStaleClaim_ForNextCandidate()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            ClaimedBy = "dead-builder",
            ClaimedAt = t0,
            HeartbeatAt = t0,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("successor", new BuildClaimRequest("fp-next", t0.AddMinutes(1))),
        });

        var result = BuildNodeType.Arbitrate(
                node, Options, t0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromSeconds(1))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("successor");
        result.FrameworkVersion.Should().Be("fp-next");
    }

    [Fact]
    public void Arbitrate_NoPendingClaims_IsUntouched()
    {
        var node = BuildNode(new BuildState { Status = BuildStatus.Ready });
        BuildNodeType.Arbitrate(node, Options, DateTime.UtcNow).Should().BeSameAs(node);
    }

    // ---- Takeover by cluster MEMBERSHIP rather than by the clock (#1355) ----
    //
    // The four tests above pass no membership at all — the no-cluster host — so they pin the
    // FALLBACK rule and must keep passing verbatim. These pin the rule that supersedes it, and
    // each one is written so it FAILS if the verdict is ignored and the clock is consulted:
    // the dead holder's heartbeat is fresh, and the live holder's is ancient.

    private static readonly DateTime T0 = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static MeshNode ClaimHeldBy(string holder, string? identity, DateTime beat) =>
        BuildNode(new BuildState
        {
            ClaimedBy = holder,
            ClaimedByIdentity = identity,
            ClaimedAt = beat,
            HeartbeatAt = beat,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("successor", new BuildClaimRequest("fp-next", T0.AddMinutes(1), "silo-b")),
        });

    /// <summary>
    /// The point of #1355: a holder the cluster has positively recorded as departed is replaced at
    /// once. Its heartbeat here is ONE SECOND old — so if the staleness clock still had any say,
    /// the claim would be defended and this test would fail.
    /// </summary>
    [Fact]
    public void Arbitrate_TakesOverAGoneHolderImmediately_WithoutWaitingOutTheClock()
    {
        var node = ClaimHeldBy("dead-builder", "silo-a", T0);
        var cluster = new FakeCluster("silo-b", gone: ["silo-a"]);

        var result = BuildNodeType.Arbitrate(node, Options, T0.AddSeconds(1), cluster)
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("successor");
        result.ClaimedByIdentity.Should().Be("silo-b",
            "the successor's own identity must be stamped, or the NEXT takeover decision is blind");
        result.FrameworkVersion.Should().Be("fp-next");
    }

    /// <summary>
    /// The other half, and the one that prevents the storm: a holder the cluster still sees running
    /// is never taken over, however old its heartbeat looks. A stopped heartbeat on a live process
    /// means busy, starved or descheduled — not dead — and evicting it puts two builders on one
    /// compile. The heartbeat here is an hour past <c>ClaimStaleAfter</c>.
    /// </summary>
    [Fact]
    public void Arbitrate_NeverTakesOverALiveHolder_EvenWhenTheHeartbeatLooksAncient()
    {
        var node = ClaimHeldBy("slow-builder", "silo-a", T0);
        var cluster = new FakeCluster("silo-b", live: ["silo-a"]);

        var result = BuildNodeType.Arbitrate(
            node, Options, T0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromHours(1), cluster);

        result.Should().BeSameAs(node);
    }

    /// <summary>
    /// A claim written before identities were stamped — and equally, one whose identity this cluster
    /// cannot resolve — carries no verdict, so the clock governs it exactly as before. Upgrade
    /// safety: the first pod on the new image must not treat an old-format claim as abandoned.
    /// </summary>
    [Theory]
    [InlineData(null)]           // pre-membership claim: no identity was ever written
    [InlineData("silo-unknown")] // an identity this cluster has no record of — absence is not death
    public void Arbitrate_WithNoMembershipVerdict_IsGovernedByTheClock(string? identity)
    {
        var cluster = new FakeCluster("silo-b");

        BuildNodeType.Arbitrate(
                ClaimHeldBy("builder", identity, T0),
                Options,
                T0 + BuildNodeType.ClaimStaleAfter - TimeSpan.FromSeconds(1),
                cluster)
            .ContentAs<BuildState>(Options)!.ClaimedBy
            .Should().Be("builder", "inside the budget the claim still stands");

        BuildNodeType.Arbitrate(
                ClaimHeldBy("builder", identity, T0),
                Options,
                T0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromSeconds(1),
                cluster)
            .ContentAs<BuildState>(Options)!.ClaimedBy
            .Should().Be("successor", "past it, the clock is all we have and it hands the claim on");
    }

    /// <summary>
    /// One pod's VIEW of the cluster: the members it can see running, the ones it has seen depart,
    /// and anything else — which is <see cref="ClusterMemberState.Unknown"/>, exactly as an
    /// un-hydrated Orleans snapshot answers for a silo it does not list.
    /// </summary>
    private sealed class FakeCluster(
        string localIdentity,
        IEnumerable<string>? live = null,
        IEnumerable<string>? gone = null) : IClusterMembership
    {
        private readonly ImmutableHashSet<string> live =
            (live ?? []).ToImmutableHashSet().Add(localIdentity);

        private readonly ImmutableHashSet<string> gone = (gone ?? []).ToImmutableHashSet();

        public string LocalIdentity { get; } = localIdentity;

        public ClusterMemberState StateOf(string identity) =>
            gone.Contains(identity) ? ClusterMemberState.Gone
            : this.live.Contains(identity) ? ClusterMemberState.Alive
            : ClusterMemberState.Unknown;
    }
}
