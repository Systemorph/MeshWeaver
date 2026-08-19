using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Reactive.Testing;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using NSubstitute;
using NSubstitute.Extensions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for the overlay self-heal watcher
/// (<see cref="NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal"/>) — the fix for
/// the memex 2026-07-25/26 incident where an instance hub enriched while its
/// NodeType's compile was unsettled bound the compilation-error overlay (or
/// the silent default-config fallback) PERMANENTLY, even after the type
/// compiled green seconds later, until an operator posted a manual
/// DisposeRequest per instance.
///
/// <para>The contract pinned here:</para>
/// <list type="bullet">
///   <item>The watcher posts a self-<see cref="DisposeRequest"/> (targeting
///     the instance hub's OWN address — the RecycleLayoutArea idiom)
///     <b>exactly once</b>, on the first emission that is a genuinely usable
///     build (<see cref="NodeTypeCompilationHelpers.HasUsableBuild"/>) past
///     the version-at-overlay gate.</item>
///   <item>The replayed at-overlay state — including the usable-but-
///     bytes-missing shape where HasUsableBuild is ALREADY true — must NOT
///     fire instantly; only a Version ADVANCE may recycle (the anti-hot-loop
///     gate).</item>
///   <item>A null gate (slow-path timeout / probe-missing: no type node in
///     hand) fires on the FIRST usable emission, ignoring unsettled states
///     and non-NodeTypeDefinition content.</item>
///   <item>Disposing the watcher (the hub's RegisterForDisposal hook) stops
///     it — no post after teardown.</item>
/// </list>
/// </summary>
public class OverlaySelfHealWatcherTest
{
    private const string NodeTypePath = "TestData/SelfHealType";
    private const string InstancePath = "TestData/SelfHealType/instance1";

    private static IMessageHub BuildInstanceHub(out Func<Func<PostOptions, PostOptions>?> capturedOptions)
    {
        var hub = Substitute.For<IMessageHub>();
        hub.Address.Returns(new Address(InstancePath));
        Func<PostOptions, PostOptions>? captured = null;
        // Configure() records the Post spec WITHOUT running NSubstitute's
        // auto-value provider — IMessageDelivery carries an INTERNAL member
        // (ChangeState), so auto-substituting Post's return type throws
        // TypeLoadException at proxy generation. Pinning the result to null
        // covers the fire-time call too (the watcher ignores the delivery).
        hub.Configure()
            .Post(Arg.Any<DisposeRequest>(),
                Arg.Do<Func<PostOptions, PostOptions>>(f => captured = f))
            .Returns((IMessageDelivery<DisposeRequest>?)null);
        capturedOptions = () => captured;
        return hub;
    }

    /// <summary>
    /// A NodeType MeshNode emission at <paramref name="version"/>.
    /// <paramref name="usable"/> == the HasUsableBuild shape: assembly fields
    /// populated AND CompiledFrameworkVersion matching the CURRENT framework
    /// (the only state a successful compile write-back produces).
    /// </summary>
    private static MeshNode TypeNode(long version, bool usable) =>
        new MeshNode("SelfHealType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = usable ? CompilationStatus.Ok : CompilationStatus.Compiling,
                LatestAssemblyCollection = usable ? "assemblies" : null,
                LatestAssemblyPath = usable ? $"{NodeTypePath}/Release/v{version}.dll" : null,
                CompiledFrameworkVersion = usable
                    ? NodeTypeCompilationHelpers.FrameworkVersion
                    : null,
            }
        };

    private static void AssertNoDispose(IMessageHub hub) =>
        hub.DidNotReceive().Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    private static void AssertDisposedExactlyOnce(IMessageHub hub) =>
        hub.Received(1).Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    [Fact]
    public void VersionGated_FiresExactlyOnce_OnVersionAdvancingUsableEmission()
    {
        var hub = BuildInstanceHub(out var capturedOptions);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 5, logger: null);

        // The replayed at-overlay state: usable but NOT past the gate. This is
        // the pinned-release-missing / bytes-missing shape where HasUsableBuild
        // is already true at overlay time — firing here would be the hot
        // recycle → re-overlay → recycle loop the gate exists to prevent.
        typeStream.OnNext(TypeNode(version: 5, usable: true));
        AssertNoDispose(hub);

        // Version advanced but the state is not usable (a compile in flight) —
        // must not recycle onto a build that isn't there yet.
        typeStream.OnNext(TypeNode(version: 6, usable: false));
        AssertNoDispose(hub);

        // Version-advancing usable emission — THE heal signal: exactly one
        // self-DisposeRequest.
        typeStream.OnNext(TypeNode(version: 7, usable: true));
        AssertDisposedExactlyOnce(hub);

        // The post targets the instance's OWN address (the RecycleLayoutArea
        // idiom). Derive expected from the SAME base options so the
        // auto-generated MessageId doesn't perturb record equality.
        var options = capturedOptions();
        options.Should().NotBeNull("the DisposeRequest must be posted with explicit options");
        var baseOptions = new PostOptions(new Address("TestData/sender"));
        options!(baseOptions).Should().Be(baseOptions.WithTarget(new Address(InstancePath)),
            "the self-heal recycle must target the instance hub's own address");

        // Take(1): a later usable emission must never fire a second recycle.
        typeStream.OnNext(TypeNode(version: 8, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// The SECOND sticky class (two-pod memex evidence, 2026-07-26): the
    /// bytes-missing default-config fallback arms the watcher while
    /// HasUsableBuild is ALREADY TRUE (only the byte resolution missed on this
    /// pod). The at-overlay state must NOT fire instantly — the version gate is
    /// mandatory there, not optional — and a version-advancing usable emission
    /// (any later compile write) fires exactly once.
    /// </summary>
    [Fact]
    public void UsableButBytesMissingAtOverlay_DoesNotFireInstantly_VersionAdvanceFiresOnce()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 41, logger: null);

        // Replay of the exact at-overlay state: usable metadata, same version.
        typeStream.OnNext(TypeNode(version: 41, usable: true));
        typeStream.OnNext(TypeNode(version: 41, usable: true));
        AssertNoDispose(hub);

        // Any later write to the NodeType node bumps Version; with a usable
        // build that is the heal signal — at most ONE self-recycle per write.
        typeStream.OnNext(TypeNode(version: 42, usable: true));
        AssertDisposedExactlyOnce(hub);

        typeStream.OnNext(TypeNode(version: 43, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    [Fact]
    public void NullGate_FiresOnFirstUsableEmission_IgnoringUnsettledAndForeignContent()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null);

        // Non-NodeTypeDefinition content never fires, whatever the version.
        typeStream.OnNext(new MeshNode("SelfHealType", "TestData") { Version = 100 });
        AssertNoDispose(hub);

        // Unsettled compile state never fires.
        typeStream.OnNext(TypeNode(version: 101, usable: false));
        AssertNoDispose(hub);

        // First usable emission fires — with a null gate there is no version
        // floor (the timeout/probe-missing sites hold no type node, and a
        // currently-usable state would have settled instead of timing out).
        typeStream.OnNext(TypeNode(version: 102, usable: true));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// 🚨 THE 2026-07-27 memex OUTAGE. A CD deploy replaced both portal pods; every NodeType had to
    /// recompile on first activation; <c>Store/Plugin</c> missed the 60s settle window, so every
    /// plugin root (AgenticEngineering, Claims, DataModelling, Reinsurance, Underwriting, …) bound
    /// the "did not settle" overlay. The recompile then SUCCEEDED — but recompiling an
    /// already-<c>Ok</c> type does not rewrite its node, so the type stayed at the SAME version the
    /// overlay had captured (v796, untouched since 08:34). The version-only gate could therefore
    /// never fire: the overlay outlived the condition it described, five manual recycles changed
    /// nothing (each re-activation re-armed the same unsatisfiable predicate), and only a
    /// scale-to-zero brought the site back.
    ///
    /// <para>The grace path is the fix: a usable build at an UNCHANGED version heals the instance
    /// once the window elapses. Time is driven by a TestScheduler, so this pins the behaviour
    /// deterministically rather than by sleeping.</para>
    /// </summary>
    [Fact]
    public void UsableBuildAtUnchangedVersion_HealsAfterTheGrace()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new ReplaySubject<MeshNode>(1);
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 796, logger: null, scheduler);

        // The post-restart reality: the type reports a usable build at EXACTLY the version the
        // overlay captured. Nothing advances, ever — this is what stayed broken all day.
        typeStream.OnNext(TypeNode(version: 796, usable: true));

        // Still nothing immediately: the anti-hot-loop guarantee is preserved, so an instance that
        // re-overlays because it genuinely cannot build does not spin on recycle.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);
        AssertNoDispose(hub);

        // Once the grace elapses the stuck instance heals ITSELF — no operator, no pod restart.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(20).Ticks);
        AssertDisposedExactlyOnce(hub);

        // Take(1) still holds across both routes — one recycle, never a storm.
        typeStream.OnNext(TypeNode(version: 796, usable: true));
        scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// The grace must not paper over a type that is still broken: if no usable build exists when
    /// the window elapses, nothing is recycled — the overlay stays and keeps telling the truth.
    /// </summary>
    [Fact]
    public void StillUnusableWhenTheGraceElapses_DoesNotRecycle()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new ReplaySubject<MeshNode>(1);
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: 796, logger: null, scheduler);

        typeStream.OnNext(TypeNode(version: 796, usable: false));
        scheduler.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);
        AssertNoDispose(hub);

        // …and when the build finally lands, the still-armed watcher heals on the next read.
        typeStream.OnNext(TypeNode(version: 796, usable: true));
        scheduler.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);
        AssertDisposedExactlyOnce(hub);
    }

    [Fact]
    public void DisposedWatcher_NeverFires()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null);

        // The hub's RegisterForDisposal hook: tearing the hub down disposes the
        // watcher — a late heal emission must not post to a dead address.
        watcher.Dispose();
        typeStream.OnNext(TypeNode(version: 7, usable: true));
        AssertNoDispose(hub);
    }

    // ---------------------------------------------------------------------------------------
    // Issue #1814 defect B — the LATCHING fault card.
    //
    // Every test above drives the watcher by pushing onto typeStream, which quietly assumes the
    // thing that failed on memex on 2026-08-17: that the stream emits at all. It did not. Both
    // heal routes above read meshHub.GetWorkspace().GetMeshNodeStream(nodeType) — the same stream
    // whose silence made the enrichment slow path time out in the first place — so the heal signal
    // and the fault shared a single point of failure, and the grace route's re-subscribe replays
    // the workspace's cached snapshot rather than re-reading anything. Result: twelve plugin roots
    // served the fault card for 1 h 24 m AFTER Store/Plugin had compiled successfully on both
    // pods, with no overlay and no "did not settle" event logged in the preceding 30–40 minutes.
    // Nothing was retrying; the card was cached on the instance hub and served. Recycling by hand
    // was the only remedy, twelve times.
    //
    // The invariant these pin: an instance serving the overlay for a type whose build subsequently
    // settles to Ok stops serving it WITHOUT external intervention, within a bounded time — and a
    // type that is still broken keeps its card.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 🚨 THE 2026-08-17 memex OUTAGE, as a unit. The type stream is PERMANENTLY SILENT after the
    /// at-overlay emission (a cross-process write this reader's mirror is never told about), while
    /// the authoritative record has since settled to a usable build. Neither the version-advance
    /// route nor the grace route can ever see it — they are both fed by that stream. The instance
    /// must still stop serving the card, on its own, inside the first ladder rung.
    /// </summary>
    [Fact]
    public void SilentTypeStream_ReEvaluatesFromStorage_AndHealsWithoutIntervention()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        var reads = 0;
        // What the store holds: Store/Plugin, compiled, Ok, assembly published, framework matching
        // — the exact node read off prod at 22:19 while every cover was still showing the card.
        var authoritative = TypeNode(version: 3259, usable: true);

        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null, scheduler,
            guards: null,
            reRead: () =>
            {
                reads++;
                return Observable.Return<MeshNode?>(authoritative);
            });

        // The at-overlay state, and then silence for ever — nothing else is ever pushed.
        typeStream.OnNext(TypeNode(version: 3259, usable: false));

        // Before the first rung the card stands: re-evaluation is bounded, not a per-render probe.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(44).Ticks);
        reads.Should().Be(0, "the ladder must not read before its first rung");
        AssertNoDispose(hub);

        // First rung: the instance recycles ITSELF off the card — no operator, no recycle by hand,
        // no emission on the type stream. This is the assertion the 2026-08-17 code cannot satisfy.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);
        AssertDisposedExactlyOnce(hub);
        reads.Should().Be(1, "one read per rung — never a poll");

        // Take(1) still holds: the hub is going away, so exactly one recycle is posted.
        scheduler.AdvanceBy(TimeSpan.FromHours(1).Ticks);
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// The other half of the invariant: a type that is GENUINELY broken keeps its card. The ladder
    /// must not paper over a parked type — and it must stay a ladder: over a full hour of a silent
    /// stream and a never-usable record it costs single-digit reads, not a poll.
    /// </summary>
    [Fact]
    public void StillBrokenType_KeepsItsCard_AndTheLadderStaysBounded()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        var reads = 0;

        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null, scheduler,
            guards: null,
            reRead: () =>
            {
                reads++;
                // Still no usable build — the source is broken and the type is parked.
                return Observable.Return<MeshNode?>(TypeNode(version: 3259, usable: false));
            });

        scheduler.AdvanceBy(TimeSpan.FromHours(1).Ticks);

        AssertNoDispose(hub);
        reads.Should().BeGreaterThan(3, "the ladder keeps looking — giving up is how the card latched");
        reads.Should().BeLessThan(12,
            "45s/90s/3m/6m then 10m: an hour of a broken type costs single-digit reads, "
            + "never a retry storm on a mesh that is already struggling");

        // …and the moment the record does become usable, the very next rung heals it.
        var beforeHeal = reads;
        scheduler.AdvanceBy(TimeSpan.FromMinutes(11).Ticks);
        reads.Should().BeGreaterThan(beforeHeal);
        AssertNoDispose(hub);
    }

    /// <summary>
    /// A read that cannot be answered is not a verdict. The ladder logs it and asks again at the
    /// next rung — the opposite of the swallow-and-stop that made the card permanent.
    /// </summary>
    [Fact]
    public void FaultedReRead_DoesNotEndTheLadder()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        var reads = 0;

        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null, scheduler,
            guards: null,
            reRead: () => ++reads == 1
                // First rung: the mesh hub is busy and the read faults.
                ? Observable.Throw<MeshNode?>(new TimeoutException("query did not answer"))
                // Second rung: it answers, and the answer is a usable build.
                : Observable.Return<MeshNode?>(TypeNode(version: 3259, usable: true)));

        scheduler.AdvanceBy(TimeSpan.FromSeconds(46).Ticks);
        reads.Should().Be(1);
        // A faulted read must not be read as "still broken" — nor as "healed".
        AssertNoDispose(hub);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(91).Ticks);
        reads.Should().Be(2);
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// An EMPTY authoritative read — no node at that path — is not a heal signal either.
    /// </summary>
    [Fact]
    public void EmptyReRead_IsNotAHealSignal()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();

        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null, scheduler,
            guards: null,
            reRead: Observable.Empty<MeshNode?>);

        scheduler.AdvanceBy(TimeSpan.FromMinutes(30).Ticks);
        AssertNoDispose(hub);
    }

    /// <summary>
    /// 🚨 The recycle destroys the watcher that ordered it, so the anti-loop bound cannot live in
    /// the watcher. <see cref="OverlayHealBudget"/> is the memory that survives: a pair that has
    /// already recycled itself several times has its next recycle DEFERRED — never cancelled — so a
    /// non-converging instance costs one activation per widening step instead of one per rung.
    /// </summary>
    [Fact]
    public void RepeatedlyStuckInstance_HasItsNextRecycleDeferred_NeverCancelled()
    {
        var hub = BuildInstanceHub(out _);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        var budget = new OverlayHealBudget();

        // This instance has already self-healed three times against this type inside the window —
        // i.e. it recycled, re-enriched, and landed right back on the card. Spacing after three
        // heals is three minutes.
        budget.RecordHeal(InstancePath, NodeTypePath, scheduler.Now);
        budget.RecordHeal(InstancePath, NodeTypePath, scheduler.Now);
        budget.RecordHeal(InstancePath, NodeTypePath, scheduler.Now);

        using var watcher = NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
            typeStream, hub, NodeTypePath, typeVersionAtOverlay: null, logger: null, scheduler,
            guards: null,
            reRead: () => Observable.Return<MeshNode?>(TypeNode(version: 3259, usable: true)),
            budget: budget);

        // The ladder's first rung finds a usable build at 45s — but the budget holds the recycle.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(46).Ticks);
        AssertNoDispose(hub);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(133).Ticks);
        // Still inside the 3-minute spacing that three prior heals earned.
        AssertNoDispose(hub);

        // DEFERRED, never dropped: the recycle lands the instant the spacing elapses.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);
        AssertDisposedExactlyOnce(hub);
        budget.HealsSoFar(InstancePath, NodeTypePath, scheduler.Now).Should().Be(4);
    }

    /// <summary>
    /// The property that matters operationally: an instance whose re-enrichment keeps faulting
    /// (recycle → re-overlay → recycle) must not spin. Drives the real loop — every posted
    /// DisposeRequest tears the watcher down and a fresh one is armed, exactly as a hub recycle
    /// does — and counts the recycles over a virtual hour.
    /// </summary>
    [Fact]
    public void NonConvergingInstance_RecyclesAreSpaced_NotOncePerLadderRung()
    {
        WithBudget(new OverlayHealBudget()).Should().BeLessThan(12,
            "a pair that never converges backs off to one recycle per ten minutes");
        WithBudget(null).Should().BeGreaterThan(40,
            "the control: with no cross-recycle memory every fresh watcher restarts at the first "
            + "rung, which is the storm the budget exists to prevent");

        static int WithBudget(OverlayHealBudget? budget)
        {
            var scheduler = new TestScheduler();
            var recycles = 0;
            IDisposable? current = null;
            var hub = Substitute.For<IMessageHub>();
            hub.Address.Returns(new Address(InstancePath));

            IDisposable Arm() => NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal(
                // Silent for ever, and the record always reports a usable build the instance
                // nevertheless cannot bind — the 2026-08-17 shape.
                new Subject<MeshNode>(), hub, NodeTypePath,
                typeVersionAtOverlay: null, logger: null, scheduler, guards: null,
                reRead: () => Observable.Return<MeshNode?>(TypeNode(version: 3259, usable: true)),
                budget: budget);

            hub.Configure()
                .Post(Arg.Any<DisposeRequest>(),
                    Arg.Do<Func<PostOptions, PostOptions>>(_ =>
                    {
                        recycles++;
                        var dying = current;
                        // The recycle: this hub (and its watcher) goes away, the next access
                        // re-enriches, faults again, and arms a FRESH watcher. Deferred by one
                        // virtual tick so the watcher is not disposed from inside its own OnNext.
                        scheduler.Schedule(TimeSpan.Zero, () =>
                        {
                            dying?.Dispose();
                            current = Arm();
                        });
                    }))
                .Returns((IMessageDelivery<DisposeRequest>?)null);

            current = Arm();
            scheduler.AdvanceBy(TimeSpan.FromHours(1).Ticks);
            current?.Dispose();
            return recycles;
        }
    }
}
