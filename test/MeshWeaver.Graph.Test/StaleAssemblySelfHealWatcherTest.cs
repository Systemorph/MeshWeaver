using System;
using System.Collections.Generic;
using System.Linq;
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
/// Unit contract for the stale-assembly self-heal watcher
/// (<see cref="NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal"/>) — the fix for the memex
/// 2026-08-12 deploy, where a GitSync update recompiled three NodeTypes SUCCESSFULLY and every
/// instance carried on executing the previous assembly until each hub was recycled by hand.
///
/// <para>The case is the one nobody was watching because it is the one that WORKED: an instance
/// that binds a good assembly takes no overlay, so <see cref="NodeTypeEnrichmentHelpers.ArmOverlaySelfHeal"/>
/// — armed only on degraded branches — never applied to it, and the binding is then cached for the
/// grain's lifetime.</para>
///
/// <para>The contract pinned here:</para>
/// <list type="bullet">
///   <item>A newly published assembly path recycles the instance <b>exactly once</b>, via a
///     self-<see cref="DisposeRequest"/> to its OWN address (the RecycleLayoutArea idiom).</item>
///   <item>Republishing the SAME assembly must NOT fire — that is the unrelated-node-write case
///     (a release-request stamp, release notes, a pin edit), and firing on it would bounce every
///     instance of the type for nothing.</item>
///   <item>The gate is the assembly PATH, not the node Version: a new build at the SAME version
///     must heal (the 2026-07-27 version-gate failure, where a recompile that does not advance the
///     version left the watcher waiting for a bump that never came), and an ADVANCING version
///     carrying the same assembly must not.</item>
///   <item>Unsettled builds and non-<see cref="NodeTypeDefinition"/> content are ignored.</item>
///   <item>Disposing the watcher stops it — no post after teardown.</item>
/// </list>
/// </summary>
public class StaleAssemblySelfHealWatcherTest
{
    private const string NodeTypePath = "TestData/StaleAssemblyType";
    private const string InstancePath = "TestData/StaleAssemblyType/instance1";
    private const string BoundAssembly = "TestData_StaleAssemblyType/v10-abc-111111111111.dll";

    /// <summary>
    /// The instance hub, plus the log of offers the watcher publishes. The watcher reads its
    /// <see cref="BehaviorSubject{T}"/> back off the hub's property bag (<c>IMessageHub.Get</c>) —
    /// the same bag <c>WithStaleAssemblySelfHeal</c> seeds — so stubbing Get is all the wiring the
    /// contract needs. The subject seeds <c>null</c> (no offer), exactly as production does, which
    /// is why the assertions below count only NON-null entries.
    /// </summary>
    private static IMessageHub BuildInstanceHub(out List<StaleBuildOffer?> offers)
    {
        var hub = Substitute.For<IMessageHub>();
        hub.Address.Returns(new Address(InstancePath));
        var subject = new BehaviorSubject<StaleBuildOffer?>(null);
        hub.Get<BehaviorSubject<StaleBuildOffer?>>().Returns(subject);
        var observed = new List<StaleBuildOffer?>();
        subject.Subscribe(observed.Add);
        offers = observed;
        return hub;
    }

    /// <summary>
    /// The offer must NAME the build it is about. A bare "an offer was published" assertion passes
    /// just as well when the offer carries the wrong type or a null path, which would render a
    /// banner the user cannot act on.
    /// </summary>
    private static void AssertOfferNames(List<StaleBuildOffer?> offers, string expectedPublished)
    {
        var offer = offers.LastOrDefault(o => o is not null);
        offer.Should().NotBeNull("the watcher must publish an offer");
        offer!.NodeType.Should().Be(NodeTypePath);
        offer.BoundAssemblyPath.Should().Be(BoundAssembly, "the banner explains what is running now");
        offer.PublishedAssemblyPath.Should().Be(expectedPublished, "…and what supersedes it");
    }

    /// <summary>
    /// A NodeType emission. <paramref name="assemblyPath"/> null ⇒ the unsettled shape (no usable
    /// build); otherwise the exact shape a successful compile write-back produces.
    /// </summary>
    private static MeshNode TypeNode(long version, string? assemblyPath) =>
        new MeshNode("StaleAssemblyType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = assemblyPath is null
                    ? CompilationStatus.Compiling
                    : CompilationStatus.Ok,
                LatestAssemblyCollection = assemblyPath is null ? null : "assemblies",
                LatestAssemblyPath = assemblyPath,
                CompiledFrameworkVersion = assemblyPath is null
                    ? null
                    : NodeTypeCompilationHelpers.FrameworkVersion,
            }
        };

    /// <summary>Let the settle window elapse — the watcher only acts once the type goes quiet.</summary>
    private static void Settle(TestScheduler scheduler) =>
        scheduler.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);

    private static void AssertNoOffer(List<StaleBuildOffer?> offers) =>
        offers.Should().NotContain(o => o != null,
            "nothing may be offered until a genuinely different build has settled");

    private static void AssertOfferedExactlyOnce(List<StaleBuildOffer?> offers) =>
        offers.Count(o => o != null).Should().Be(1,
            "Take(1): one offer per bound assembly — the banner does not re-fire per publication");

    /// <summary>
    /// 🚨 The offer must NEVER recycle by itself. This is the whole point of the change: the
    /// watcher used to post a self-DisposeRequest, so every live instance restarted on every
    /// publish. Recycling is now the USER'S click (RecycleLayoutArea), so a DisposeRequest from
    /// the watcher is a regression back to the restart storm.
    /// </summary>
    private static void AssertNeverRecycledItself(IMessageHub hub) =>
        hub.DidNotReceive().Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    /// <summary>
    /// 🚨 The #1669 regression: the publication arrives in UN-MATERIALIZED JSON shape — the normal
    /// shape for a node that just crossed a sync stream, and exactly how the emission reached the
    /// watcher in the ThinkInStreams/Subscribe + post-roll Store incidents. The old
    /// <c>is NodeTypeDefinition</c> predicate was blind here, so the instance kept its stale
    /// (worst case zero-areas) activation until a manual recycle. The watcher must recover the
    /// definition via <c>ContentAs</c> and fire.
    /// </summary>
    [Fact]
    public void UnmaterializedJsonEmission_StillOffersTheNewBuild()
    {
        var hub = BuildInstanceHub(out var offers);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        hub.JsonSerializerOptions.Returns(options);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        // The same publication NewlyPublishedAssembly_… fires on — but serialized to a raw
        // JsonElement, as the sync stream delivers it before materialization re-types it.
        var typed = TypeNode(version: 12, assemblyPath: "TestData_StaleAssemblyType/v12-abc-222222222222.dll");
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            (NodeTypeDefinition)typed.Content!, options);
        typeStream.OnNext(typed with { Content = json });

        Settle(scheduler);
        AssertOfferedExactlyOnce(offers);
        AssertOfferNames(offers, "TestData_StaleAssemblyType/v12-abc-222222222222.dll");
        AssertNeverRecycledItself(hub);
    }

    [Fact]
    public void NewlyPublishedAssembly_OffersTheNewBuild_ExactlyOnce()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        // The replayed at-bind state: the very assembly this instance is running. Firing here
        // would banner every instance on every emission of its own current state.
        typeStream.OnNext(TypeNode(version: 10, assemblyPath: BoundAssembly));
        AssertNoOffer(offers);

        // A compile in flight — no build to rebind onto yet.
        typeStream.OnNext(TypeNode(version: 11, assemblyPath: null));
        AssertNoOffer(offers);

        // 🚨 THE signal: a DIFFERENT assembly is published. This is the emission that was ignored
        // before the fix, leaving the instance executing the old DLL indefinitely.
        typeStream.OnNext(TypeNode(version: 12, assemblyPath: "TestData_StaleAssemblyType/v12-abc-222222222222.dll"));
        // …but NOT instantly: the type may still be publishing (an install compiles the type, then
        // recompiles when its Source/ lands). Offering on each one flaps the banner mid-install.
        AssertNoOffer(offers);

        Settle(scheduler);
        AssertOfferedExactlyOnce(offers);
        AssertOfferNames(offers, "TestData_StaleAssemblyType/v12-abc-222222222222.dll");
        AssertNeverRecycledItself(hub);

        // Take(1): further publications do not re-offer. The banner already says "a newer build is
        // available", which stays true; the user's recycle rebinds to whatever is newest then.
        typeStream.OnNext(TypeNode(version: 13, assemblyPath: "TestData_StaleAssemblyType/v13-abc-333333333333.dll"));
        Settle(scheduler);
        AssertOfferedExactlyOnce(offers);
    }

    /// <summary>
    /// 🚨 The anti-bounce half. A NodeType node is written for reasons that produce no new
    /// assembly — a <c>RequestedReleaseAt</c> stamp, release notes, a pin edit. Those advance the
    /// Version while republishing the SAME assembly path, and must leave every instance alone (no banner).
    /// </summary>
    [Fact]
    public void RepublishingTheSameAssembly_DoesNotOffer_EvenAsTheVersionAdvances()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        for (var version = 11; version <= 20; version++)
            typeStream.OnNext(TypeNode(version, assemblyPath: BoundAssembly));
        Settle(scheduler);

        AssertNoOffer(offers);
    }

    /// <summary>
    /// 🚨 The regression the gate choice exists for. memex 2026-07-27: the overlay watcher was
    /// gated on the node VERSION advancing, and a recompile of an already-<c>Ok</c> type need not
    /// advance it — so the watcher waited for a bump that never came and the instance stayed stuck
    /// until the pods were restarted. A new assembly at an UNCHANGED version must heal.
    /// </summary>
    [Fact]
    public void NewAssemblyAtTheSameVersion_StillOffers()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        typeStream.OnNext(TypeNode(version: 10, assemblyPath: "TestData_StaleAssemblyType/v10-abc-999999999999.dll"));
        Settle(scheduler);

        AssertOfferedExactlyOnce(offers);
        AssertOfferNames(offers, "TestData_StaleAssemblyType/v10-abc-999999999999.dll");
        AssertNeverRecycledItself(hub);
    }

    /// <summary>Non-definition content and unsettled builds are ignored, never fired on.</summary>
    [Fact]
    public void UnsettledAndForeignContent_AreIgnored()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        typeStream.OnNext(TypeNode(version: 11, assemblyPath: null));
        typeStream.OnNext(new MeshNode("StaleAssemblyType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = 12,
            Content = "not a definition"
        });
        Settle(scheduler);
        AssertNoOffer(offers);
    }

    /// <summary>
    /// 🚨 THE REGRESSION (#1343, caught by NodeRepoInstanceOrderingTest failing 6 runs in 8 while
    /// the same commit with this watcher disabled passed 8/8).
    ///
    /// <para>An INSTALL publishes more than once — the type node lands and compiles, then its
    /// <c>Source/</c> lands and it compiles AGAIN, seconds apart — all while its instances are
    /// being activated and READ. Recycling on each publication put a <c>DisposeRequest</c> through
    /// those hubs mid-read ("Hub Pack/Widget/Nested is shutting down") and the post-install read
    /// timed out.</para>
    ///
    /// <para>A burst must therefore cost exactly ONE offer, and only once the type goes quiet.</para>
    /// </summary>
    [Fact]
    public void APublicationBurst_CostsExactlyOneOffer_AndOnlyAfterItGoesQuiet()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        // The install shape: several builds in quick succession, instances live throughout.
        for (var version = 11; version <= 15; version++)
        {
            typeStream.OnNext(TypeNode(version,
                assemblyPath: $"TestData_StaleAssemblyType/v{version}-abc-{version}00000000.dll"));
            // A short hop — far inside the settle window. Nothing may be disposed yet.
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            AssertNoOffer(offers);
        }

        // Quiet at last: exactly one offer, naming the NEWEST build.
        Settle(scheduler);
        AssertOfferedExactlyOnce(offers);
    }

    /// <summary>Disposal (the hub's RegisterForDisposal hook) stops the watcher.</summary>
    [Fact]
    public void Disposing_StopsTheWatcher()
    {
        var hub = BuildInstanceHub(out var offers);
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler);

        watcher.Dispose();
        typeStream.OnNext(TypeNode(version: 12, assemblyPath: "TestData_StaleAssemblyType/v12-abc-222222222222.dll"));
        Settle(scheduler);

        AssertNoOffer(offers);
    }
}
