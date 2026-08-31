using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Reactive.Testing;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A portal must not be able to serve one build while reporting another</b> —
/// Systemorph/MeshWeaver#2471.
///
/// <para>Measured on memex, 2026-08-26: the source node carried the change, <c>get_diagnostics</c>
/// said <c>Ok</c>/compiled, and the rendered <c>Tests</c> area produced the OLD suite. It survived
/// two NodeType recycles, four instance recycles and a forced compile over 30+ minutes, and the
/// <c>$Banner</c> stale-build adornment — the one adornment whose entire job is to say "you are
/// looking at an old build" — was EMPTY throughout.</para>
///
/// <para><b>Why the adornment could not fire, and why no recycle helped.</b> The watcher compared
/// <see cref="NodeTypeDefinition.LatestAssemblyPath"/> against the path the instance bound. A path
/// is a STORE KEY — <c>(nodeTypePath, LastCompiledVersion)</c> — which each pod resolves through
/// its own local cache, and a recompile of an already-<c>Ok</c> type does not advance the node's
/// version. So the key can be identical on both sides while the bytes behind it differ, and a
/// recycle simply re-resolves the same key from the same local copy. A path comparison is
/// structurally incapable of observing this state, and every remedy built on it is inert by
/// construction.</para>
///
/// <para><b>What makes the claim checkable.</b> An assembly's MVID is minted per emission, so it
/// answers the question the path cannot: <i>are these the bytes this node is talking about?</i>
/// Recording it on the node and reading it off the bound file turns "compiled Ok" from something
/// believed into something COMPARED — the same postcondition-not-hope move the platform already
/// makes with <c>framework-mvid.txt</c> beside a bake and the <c>_complete</c> sentinel beside a
/// publication.</para>
/// </summary>
public class ServedBuildIsVerifiableTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NodeTypePath = "TestData/ServedBuildType";
    private const string InstancePath = "TestData/ServedBuildType/instance1";
    private const string BoundAssembly = "TestData_ServedBuildType/v10-abc-111111111111.dll";
    private const string BoundMvid = "1111111111111111111111111111111a";
    private const string PublishedMvid = "2222222222222222222222222222222b";

    // ── the pure identity surface ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The MVID read from the file must be the assembly's REAL one — otherwise the whole detector
    /// compares two numbers it made up. Ground truth is the loaded assembly's own
    /// <see cref="Module.ModuleVersionId"/>; the reader must reach the same value WITHOUT loading.
    /// </summary>
    [Fact]
    public void OfFile_ReadsTheAssemblysRealMvid_WithoutLoadingIt()
    {
        var assembly = typeof(ServedBuildIdentity).Assembly;

        ServedBuildIdentity.OfFile(assembly.Location)
            .Should().Be(assembly.ManifestModule.ModuleVersionId.ToString("N"));
    }

    /// <summary>Two different assemblies have two different identities — the property the whole
    /// comparison rests on. A reader that returned a constant would pass every "it reads something"
    /// assertion and detect nothing.</summary>
    [Fact]
    public void OfFile_DistinguishesTwoDifferentAssemblies()
    {
        var mine = ServedBuildIdentity.OfFile(typeof(ServedBuildIdentity).Assembly.Location);
        var bcl = ServedBuildIdentity.OfFile(typeof(object).Assembly.Location);

        mine.Should().NotBeNullOrEmpty();
        bcl.Should().NotBeNullOrEmpty();
        mine.Should().NotBe(bcl);
    }

    /// <summary>
    /// The same bytes through the in-memory reader (the shape a bundle adoption has in hand — no
    /// file exists yet) must agree with the file reader, or an adopted build would be stamped with
    /// an identity nothing else computes.
    /// </summary>
    [Fact]
    public void OfBytes_AgreesWithOfFile()
    {
        var path = typeof(ServedBuildIdentity).Assembly.Location;

        ServedBuildIdentity.OfBytes(File.ReadAllBytes(path))
            .Should().Be(ServedBuildIdentity.OfFile(path));
    }

    /// <summary>
    /// 🚨 A DETECTOR MUST NEVER FAULT AN ACTIVATION. It runs on the hot path of every per-instance
    /// hub binding, so an absent, unreadable or non-managed file has to degrade to "I do not know"
    /// — a throw here would turn a reporting improvement into an outage.
    /// </summary>
    [Fact]
    public void EveryUnreadableInputDegradesToNull_NeverAThrow()
    {
        var notAnAssembly = Path.Combine(Path.GetTempPath(),
            $"served-build-{Guid.NewGuid():N}.dll");
        File.WriteAllText(notAnAssembly, "this is not a PE file");
        try
        {
            ServedBuildIdentity.OfFile(null).Should().BeNull();
            ServedBuildIdentity.OfFile("").Should().BeNull();
            ServedBuildIdentity.OfFile("/does/not/exist.dll").Should().BeNull();
            ServedBuildIdentity.OfFile(notAnAssembly).Should().BeNull();
            ServedBuildIdentity.OfBytes(null).Should().BeNull();
            ServedBuildIdentity.OfBytes([]).Should().BeNull();
            ServedBuildIdentity.OfBytes([1, 2, 3]).Should().BeNull();
        }
        finally
        {
            File.Delete(notAnAssembly);
        }
    }

    /// <summary>
    /// 🚨 UNKNOWN IS NOT A MISMATCH. Every node stamped before <c>LatestAssemblyMvid</c> existed
    /// carries a null on one side; treating that as evidence would fire the banner on every legacy
    /// node on the first boot after this ships, and a detector that cries wolf is switched off
    /// within the day — taking the real signal with it.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(PublishedMvid, null)]
    [InlineData(null, BoundMvid)]
    [InlineData("", BoundMvid)]
    [InlineData(PublishedMvid, "")]
    [InlineData(PublishedMvid, PublishedMvid)]
    public void Mismatch_IsNullWheneverThereIsNoEvidenceOfOne(string? published, string? served)
    {
        ServedBuildIdentity.Mismatch(published, served, NodeTypePath).Should().BeNull();
        if (published is null or "" || served is null or "")
            ServedBuildIdentity.Unverifiable(published, served).Should().BeTrue(
                "a comparison that could not be taken must be reportable as such, or an all-green "
                + "count over an all-unknown fleet reads as proof");
    }

    /// <summary>
    /// …and when both sides ARE known and differ, the message must name both builds and say the
    /// thing the reader most needs: the node's status is not evidence, and a recycle will not help.
    /// </summary>
    /// <summary>
    /// 🚨 The bind seam REFUSES a stale bind rather than reporting one (#2471 reopened,
    /// 2026-08-31): with evidence of a mismatch and retry budget left, the activation goes to
    /// TriggerRecompileAndRetry — a fresh compile mints new bytes AND a new published MVID —
    /// instead of binding bytes the type did not publish. Once bound, nothing downstream can fix
    /// it: the banner is report-only, and the instance splits the type family across two
    /// collectible assemblies (As&lt;T&gt; case 3), which is what degraded StoreContent, every
    /// TierContent and the order in the paid-fulfilment run this pins.
    /// </summary>
    [Theory]
    [InlineData(PublishedMvid, BoundMvid, 0, true)]   // mismatch, budget left → refuse
    [InlineData(PublishedMvid, BoundMvid, 1, false)]  // budget exhausted → bind + banner (degraded beats parked)
    [InlineData(PublishedMvid, PublishedMvid, 0, false)] // identical bytes → bind
    [InlineData(null, BoundMvid, 0, false)]           // legacy node, no published MVID → no evidence → bind
    [InlineData(PublishedMvid, null, 0, false)]       // unreadable local file → no evidence → bind
    public void StaleBind_IsRefusedExactlyOnEvidence_WithinTheRetryBudget(
        string? published, string? bound, int attempts, bool refused)
        => Assert.Equal(refused,
            NodeTypeEnrichmentHelpers.ShouldRefuseStaleBind(published, bound, NodeTypePath, attempts));

    [Fact]
    public void Mismatch_NamesBothBuilds_AndSaysARecycleWillNotHelp()
    {
        var detail = ServedBuildIdentity.Mismatch(PublishedMvid, BoundMvid, NodeTypePath);

        detail.Should().NotBeNull();
        detail.Should().Contain(PublishedMvid);
        detail.Should().Contain(BoundMvid);
        detail.Should().Contain(NodeTypePath);
        detail.Should().Contain("NOT evidence");
        detail.Should().Contain("A recycle re-binds the same local copy");
    }

    // ── the watcher: the state a PATH comparison cannot see ───────────────────────────────────

    private IMessageHub BuildHub() =>
        Mesh.GetHostedHub(new Address(InstancePath), c => c.WithTypes(typeof(NodeTypeDefinition)));

    private (IMessageHub Hub, List<StaleBuildOffer?> Offers, IObservable<bool> Disposed) BuildInstanceHub()
    {
        var hub = BuildHub();
        var subject = new BehaviorSubject<StaleBuildOffer?>(null);
        hub.Set(subject);
        var observed = new List<StaleBuildOffer?>();
        subject.Subscribe(observed.Add);
        var disposed = new ReplaySubject<bool>(1);
        hub.RegisterForDisposal(_ =>
        {
            disposed.OnNext(true);
            disposed.OnCompleted();
        });
        return (hub, observed, disposed);
    }

    private static MeshNode TypeNode(long version, string assemblyPath, string? mvid) =>
        new MeshNode("ServedBuildType", "TestData")
        {
            NodeType = MeshNode.NodeTypePath,
            Version = version,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = assemblyPath,
                LatestAssemblyMvid = mvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            }
        };

    private static void Settle(TestScheduler scheduler) =>
        scheduler.AdvanceBy(TimeSpan.FromSeconds(30).Ticks);

    private static async Task<bool> DisposedWithinAsync(IObservable<bool> disposed, TimeSpan window)
    {
        try { return await disposed.FirstAsync().Timeout(window).Await(); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// 🚨 THE #2471 SIGNAL: the type republishes at the SAME assembly path with DIFFERENT bytes.
    /// The path comparison sees nothing — this emission is byte-for-byte the "unrelated node write"
    /// the anti-bounce rule deliberately ignores — so before this the banner stayed empty exactly
    /// when it was needed.
    /// </summary>
    [Fact]
    public void SameAssemblyPath_DifferentBytes_IsOffered_AsAServedBuildMismatch()
    {
        var (hub, offers, _) = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler,
            boundAssemblyMvid: BoundMvid);

        // The instance's OWN build republished — same path, same bytes. Still silent.
        typeStream.OnNext(TypeNode(version: 11, BoundAssembly, BoundMvid));
        Settle(scheduler);
        offers.Should().NotContain(o => o != null,
            "republishing the very bytes this instance is running is not a mismatch");

        // Same store key, DIFFERENT bytes behind it.
        typeStream.OnNext(TypeNode(version: 12, BoundAssembly, PublishedMvid));
        Settle(scheduler);

        var offer = offers.LastOrDefault(o => o is not null);
        offer.Should().NotBeNull(
            "the served bytes are not the published bytes — the one state $Banner exists for");
        offer!.Kind.Should().Be(StaleBuildKind.ServedBuildIsNotPublished);
        offer.PublishedAssemblyMvid.Should().Be(PublishedMvid);
        offer.BoundAssemblyMvid.Should().Be(BoundMvid);
    }

    /// <summary>
    /// 🚨 …and it must NOT be auto-recycled, even on a portal that opted into convergence. The
    /// path did not move, so the recycle re-resolves the same store key from the same local cache
    /// and lands on the same bytes — which is precisely what was measured six times over. Worse,
    /// the watcher is <c>Take(1)</c>: spending that one shot on a recycle that changes nothing
    /// leaves the instance in the same state with no banner and nothing left to fire.
    /// </summary>
    [Fact]
    public async Task ASameKeyMismatch_IsNeverAutoRecycled_EvenWhenConvergenceIsOn()
    {
        var (hub, offers, disposed) = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler,
            autoRecycle: true, boundAssemblyMvid: BoundMvid);

        typeStream.OnNext(TypeNode(version: 12, BoundAssembly, PublishedMvid));
        Settle(scheduler);

        (await DisposedWithinAsync(disposed, TimeSpan.FromMilliseconds(300))).Should().BeFalse(
            "recycling re-binds the same local copy of the same store key — it is not convergence");
        offers.LastOrDefault(o => o is not null).Should().NotBeNull(
            "the banner is what the viewer gets instead, and it must not be swallowed by the "
            + "auto-recycle branch");
    }

    /// <summary>
    /// The unchanged half, guarded: a genuine PATH advance is still an OFFER — a newer build the
    /// user can take with a recycle — and still converges when the deployment asked for it. The
    /// #2471 detection is ADDED to the watcher, never substituted for what it already did.
    /// </summary>
    [Fact]
    public async Task ARealNewBuild_IsStillAnOffer_AndStillConverges()
    {
        var (offerHub, offers, offerDisposed) = BuildInstanceHub();
        var offerStream = new Subject<MeshNode>();
        var offerScheduler = new TestScheduler();
        using var offerWatcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            offerStream, offerHub, NodeTypePath, BoundAssembly, logger: null, offerScheduler,
            boundAssemblyMvid: BoundMvid);

        offerStream.OnNext(TypeNode(
            version: 12, "TestData_ServedBuildType/v12-abc-222222222222.dll", PublishedMvid));
        Settle(offerScheduler);

        var offer = offers.LastOrDefault(o => o is not null);
        offer.Should().NotBeNull();
        offer!.Kind.Should().Be(StaleBuildKind.NewerBuildAvailable,
            "a new build at a new key IS takeable with a recycle — that offer must not be "
            + "downgraded into the #2471 message, which tells the user the button will not help");
        (await DisposedWithinAsync(offerDisposed, TimeSpan.FromMilliseconds(300)))
            .Should().BeFalse("the default is an offer, never a self-recycle");
    }

    /// <summary>
    /// A node stamped before <c>LatestAssemblyMvid</c> existed carries a null published identity.
    /// Every such instance must behave EXACTLY as it did before this change — silent on a same-path
    /// republication — or the first boot after this ships banners the whole mesh.
    /// </summary>
    [Fact]
    public void ALegacyNodeWithNoRecordedMvid_BehavesExactlyAsBefore()
    {
        var (hub, offers, _) = BuildInstanceHub();
        var typeStream = new Subject<MeshNode>();
        var scheduler = new TestScheduler();
        using var watcher = NodeTypeEnrichmentHelpers.ArmStaleAssemblySelfHeal(
            typeStream, hub, NodeTypePath, BoundAssembly, logger: null, scheduler,
            boundAssemblyMvid: BoundMvid);

        for (var version = 11; version <= 20; version++)
            typeStream.OnNext(TypeNode(version, BoundAssembly, mvid: null));
        Settle(scheduler);

        offers.Should().NotContain(o => o != null,
            "unknown is not a mismatch — a null published mvid must never fire the banner");
    }
}
