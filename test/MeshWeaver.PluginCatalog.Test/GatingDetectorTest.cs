#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE COVER-GRANT DEADLOCK DETECTOR — that it fires, that it stays quiet, and that it is CHEAP.
///
/// <para>🚨 <b>The defect these pin.</b> The installer waited up to 30 s for a cover grant its own
/// doc comment called optional — "a partition whose node type does not gate never writes it". On
/// that shape the query can never emit, so EVERY install of such a package burned the entire budget
/// and then reported success, leaving one Information line that read exactly like the healthy case.
/// Measured in this very suite before the fix: 30.25 s
/// (<c>SelfTypedRootInstallTest.RootTypedByAnInPackageNodeType_Installs</c>), 30.33 s and 30.29 s
/// (<c>StaleStampRootBindingTest</c>), 30.24 s
/// (<c>InstallReleaseOrderingTest.DeferredNodeTypeReleases_AreRequestedAfterTheRootRecycle</c>) and
/// 60.28 s for the one that installs twice
/// (<c>SelfTypedRootInstallTest.SelfTypedRoot_ReinstallImmediately_WritesNothing</c>) — 181 s of a
/// 336 s suite, one 30 s stall per install. <c>PackageInstaller.Install</c> is the PRODUCTION
/// install path, so that is dead wall-clock on a live mesh too, not a test artefact.</para>
///
/// <para><b>Why a test and not just a smaller constant.</b> A detector nobody exercises is a
/// detector nobody knows is broken — which is precisely how this one came to report the wedged case
/// and the normal case with the same message at the same level. So all three outcomes are pinned
/// here: nothing owed (instant), the grant lands (milliseconds), and the grant never comes (bounded,
/// and LOUD).</para>
/// </summary>
public class GatingDetectorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    /// <summary>The budget the detector is documented to hold — mirrored, not imported: a test that
    /// read the production constant would pass at any value, including the 30 s this fixes.</summary>
    private static readonly TimeSpan DocumentedBudget = TimeSpan.FromSeconds(5);

    // ────────────────────────────── the discriminator (pure) ──────────────────────────────

    /// <summary>
    /// A COMMERCIAL package is the one shape where <see cref="PackageInstaller.EnsureDeclaredAccess"/>
    /// deliberately writes nothing: the partition lands gated and only a gating pass's cover grant
    /// can make it — including its own Subscribe cover — reachable. That is what is worth watching.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ACommercialPackage_OwesACoverGrant()
    {
        PackageInstaller.CoverGrantExpected(Manifest("Priced", price: 49m), "Priced")
            .Should().BeTrue("a priced package installs gated — nothing else can open it");
        PackageInstaller.CoverGrantExpected(Manifest("Coupon", price: -1m), "Coupon")
            .Should().BeTrue("a negative price is coupon-only, and gated exactly the same way");
        PackageInstaller.CoverGrantExpected(Manifest("Sales", contactEmail: "sales@acme.test"), "Sales")
            .Should().BeTrue("contact-sales is commercial too — IsCommercial counts it");
    }

    /// <summary>
    /// Every shape the installer PUBLISHES itself owes nothing. This is the whole fix: on these the
    /// old code waited 30 s for a node that was never coming, while the partition's readability was
    /// already established — and already verified, one phase earlier, by
    /// <c>VerifyDeclaredAccess</c>'s storage read of the marker.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void APackageTheInstallerPublishes_OwesNothing()
    {
        PackageInstaller.CoverGrantExpected(Manifest("Free"), "Free")
            .Should().BeFalse("a free package gets {partition}/_Policy · PublicRead = true — readable "
                + "with or without a cover grant");
        PackageInstaller.CoverGrantExpected(
                Manifest("Scoped", publicSegments: ["Docs"]), "Scoped")
            .Should().BeFalse("the scoped shape's root Public grant IS the cover grant — the "
                + "installer writes it itself, so waiting on it waits on its own write");
        PackageInstaller.CoverGrantExpected(
                Manifest("Baseline", price: 49m, preInstalled: true), "Baseline")
            .Should().BeFalse("pre-installed overrides price: platform baseline is public by "
                + "definition, so EnsureDeclaredAccess publishes it");
    }

    /// <summary>
    /// A root this package wrote into but does not TARGET is one core has made no access statement
    /// about — so it has no honest verdict to offer and must stay silent rather than invent one.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ARootTheManifestDoesNotTarget_GetsNoVerdict()
    {
        var manifest = Manifest("Priced", price: 49m);
        PackageInstaller.CoverGrantExpected(manifest, "SomeOtherPartition").Should().BeFalse();
        PackageInstaller.CoverGrantExpected(manifest, "").Should().BeFalse();
        PackageInstaller.CoverGrantExpected(manifest, null).Should().BeFalse();
    }

    // ────────────────────────────── the detector (live mesh) ──────────────────────────────

    /// <summary>
    /// 🚨 THE MAINTAINER'S ASK — prove the detector actually fires. A partition that installs GATED
    /// and never receives its cover grant is not "normal": every viewer is denied, including on the
    /// page that would sell the package. The detector must SAY SO, inside its budget, and never
    /// fail the install.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AGrantThatNeverArrives_IsReported_WithinTheBudget()
    {
        var logger = new RecordingLogger();
        var elapsed = Stopwatch.StartNew();
        var outcome = await Detect(Manifest("Wedged", price: 49m), "Wedged", logger).Await();
        elapsed.Stop();
        Output.WriteLine($"wedged partition reported after {elapsed.ElapsedMilliseconds} ms");

        outcome.Should().Be(PackageInstaller.GatingOutcome.Stalled,
            "a gated partition with no cover grant is the one outcome this detector exists to catch");

        // BOUNDED — the defect was that this cost 30 s.
        elapsed.Elapsed.Should().BeLessThan(DocumentedBudget + TimeSpan.FromSeconds(10),
            "the detector budget is 5 s; a 30 s wait is the stall this closes");
        // …and it really WATCHED. A detector that answers instantly has stopped detecting.
        elapsed.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3),
            "it must hold the query for its budget, not short-circuit on the empty first answer");

        // LOUD — the half that made the defect invisible. Before this, the wedged case and the
        // normal case emitted the same Information line.
        var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("a wedged gating pass is reported exactly once, loudly");
        warnings[0].Message.Should().Contain(PackageInstaller.CoverGrantPath("Wedged"),
            "the report must name the exact path that is missing, or nobody can act on it");
        warnings[0].Message.Should().Contain("DENIES",
            "it must name the consequence — the partition denies every viewer — not just the fact");
        logger.Records.Should().NotContain(r => r.Level >= LogLevel.Error,
            "never fatal: the install itself is complete, and failing it would trade an unreadable "
            + "partition for no partition at all");
    }

    /// <summary>
    /// The healthy half — and the MEASUREMENT the 5 s budget is chosen against: once a gating pass
    /// writes the grant, the detector sees it in milliseconds. The hub is already warm by the time
    /// this phase starts (phase 1 paid the activation), so the only outstanding work is one
    /// access-table write.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Detector_sees_a_cover_grant_that_lands()
    {
        var logger = new RecordingLogger();
        // 🚨 Merged, never a pre-started Task (no ToTask anywhere — ObservableToTaskBridgeGuard).
        // Merge subscribes its sources IN ORDER, so the detector's query is in place before the
        // write starts — the real gating-pass shape, and the query is empty-on-absent so being
        // early costs nothing. The write contributes no element of its own.
        var elapsed = Stopwatch.StartNew();
        var outcome = await Detect(Manifest("Gated", price: 49m), "Gated", logger)
            .Merge(WriteCoverGrant("Gated").IgnoreElements()
                .Select(_ => PackageInstaller.GatingOutcome.NotExpected))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();
        elapsed.Stop();
        Output.WriteLine($"cover grant written and observed in {elapsed.ElapsedMilliseconds} ms");

        outcome.Should().Be(PackageInstaller.GatingOutcome.Landed);
        elapsed.Elapsed.Should().BeLessThan(DocumentedBudget,
            "the write→observe latency is what the budget has to cover, and it is orders of "
            + "magnitude smaller — that is why 5 s is enough and 30 s was never the point");
        logger.Records.Should().NotContain(r => r.Level >= LogLevel.Warning,
            "a partition that IS readable must produce no alarm");
    }

    /// <summary>
    /// THE STALL ITSELF, at unit scale: the shape that used to cost a full 30 s per install now
    /// costs nothing, because core knows from its OWN declared-access decision that no gating pass
    /// owes this partition anything.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APartitionTheInstallerPublishes_IsNotWaitedOnAtAll()
    {
        var logger = new RecordingLogger();
        var elapsed = Stopwatch.StartNew();
        var outcome = await Detect(Manifest("Open"), "Open", logger).Await();
        elapsed.Stop();
        Output.WriteLine($"non-gating partition answered in {elapsed.ElapsedMilliseconds} ms");

        outcome.Should().Be(PackageInstaller.GatingOutcome.NotExpected);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "this is the shape that burned 30 s on every install — it must not wait at all");
        logger.Records.Should().NotContain(r => r.Level >= LogLevel.Warning,
            "there is nothing wrong here: the installer published the partition itself");
    }

    // ────────────────────────────────────── helpers ──────────────────────────────────────

    /// <summary>
    /// Runs the detector exactly as the install phase does — under SYSTEM via
    /// <c>RunAsSystem</c> (never <c>Observable.Using</c>, which latches the subscriber's identity,
    /// #1790), because the <c>_Access</c> query is only readable that way.
    /// </summary>
    private IObservable<PackageInstaller.GatingOutcome> Detect(
        PackageManifest manifest, string root, ILogger logger) =>
        Mesh.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => PackageInstaller.DetectGatingStall(Mesh, manifest, root, logger))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60));

    /// <summary>The node a plugin-side gating pass writes to cover a gated partition — the exact
    /// path <see cref="PackageInstaller.CoverGrantPath"/> names and
    /// <c>InstallGatingHandshakeTest</c> pins against <c>PluginGate</c>.</summary>
    private IObservable<Unit> WriteCoverGrant(string root)
    {
        var node = new MeshNode(WellKnownUsers.Public + "_Access", $"{root}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = "Public — Viewer",
            State = MeshNodeState.Active,
            MainNode = root,
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = WellKnownUsers.Public,
                Roles = [new RoleAssignment { Role = "Viewer" }],
            },
        };
        node.Path.Should().Be(PackageInstaller.CoverGrantPath(root),
            "the fixture must write the very path the detector watches, or it proves nothing");

        // RunAsSystem, never Observable.Using(ImpersonateAsSystem) — the latter opens the scope on
        // the subscribing thread and closes it on the terminating one (#1790).
        return Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateOrUpdateNode(node))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(_ => Unit.Default);
    }

    private static PackageManifest Manifest(
        string id,
        decimal? price = null,
        string? contactEmail = null,
        bool preInstalled = false,
        IEnumerable<string>? publicSegments = null) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = "v1",
            Price = price,
            ContactEmail = contactEmail,
            PreInstalled = preInstalled,
            PublicSegments = publicSegments is null ? [] : [.. publicSegments],
        };

    /// <summary>Captures every record written through it, so a test can assert on log LEVEL and
    /// content — the two things that told the wedged case apart from the normal one only after
    /// this fix.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> records = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (records)
                records.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
