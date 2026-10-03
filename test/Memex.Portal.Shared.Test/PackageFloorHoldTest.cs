#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>2026-09-27, reproduced end to end on the SOURCE-CONTENT lane</b> (policy
/// <c>package-min-mesh-version</c>).
///
/// <para>Store 1.16 used <c>IPaymentProvider</c> billing-portal members and Hosting used
/// <c>DeploymentContent.AnnouncementKeySecret</c>; both synced onto instances running
/// <c>3.0.0-ci.9412</c>/<c>9414</c> — images predating those members — and 14 NodeTypes were left
/// with no usable assembly. The content lane decided an update by the manifest hash alone; the
/// declared floor was advisory everywhere, and the module lane's link probe cannot see NodeType
/// SOURCE, which compiles in the mesh after it has landed.</para>
///
/// <para>The fixture: a package installed at v1 whose v2 sources reference a platform member the
/// running platform lacks, declaring a floor above the running platform. Driven through the real
/// paths — <see cref="PackageUpdateReconciler"/> (the unattended registry reconcile),
/// <see cref="CatalogLayoutAreas.InstallOrUpdate"/> (the click and the boot install) and
/// <see cref="PackageInstaller.Install"/> (a fresh install) — with the source's own fetch log as
/// the witness that the held sources never travelled, so nothing could compile them.</para>
///
/// <para><b>Why the suite that existed did not catch 09-27.</b> Every floor test asserted the
/// OPPOSITE (#3648's "advisory everywhere" fixtures — <c>ModuleFloorAdvisoryTest</c>), and none
/// drove the content lane with a floor at all: the reconcile/click tests carry no
/// <c>MinMeshVersion</c>, and no test compared a package's declared floor with the running version
/// on that lane, so "a newer package lands on an older platform" was the tested, intended
/// behaviour.</para>
/// </summary>
public class PackageFloorHoldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly FloorHoldFixture.RecordingDispatch dispatch = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(FloorFixture.Pin)
                .AddSingleton<IPackageHoldDispatch>(dispatch));

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<PackageFloorHoldTest>();

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    /// <summary>
    /// THE reproduction, on the unattended lane: held → the installed version's sources stay and
    /// nothing is fetched → ONE blocking ticket, not one per tick → floor satisfied → the update
    /// lands and the hold clears.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheReconcile_HoldsAnUpdateAboveTheFloor_KeepsTheInstalledSources_TicketsOnce_ThenLands()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        await Installer.Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);

        await FloorHoldFixture.InstallV1(Mesh, Logger, ct);
        await PackageInstaller.SetUpdatePolicy(Mesh, FloorHoldFixture.Package, PackageUpdatePolicy.Auto, Logger)
            .Timeout(60.Seconds()).Await(ct);

        // ── HELD ────────────────────────────────────────────────────────────────────────────
        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), Logger, ct);

        var held = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdateDispatchedAt is not null, ct);
        source.Fetches.Should().Be(0,
            "THE assertion of 09-27: the held version's sources are never fetched, so nothing can compile them");
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text,
            "the installed version's source keeps serving (R1)");
        held.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash, "the record still describes the installed version");
        held.HeldUpdate.Should().Contain(FloorFixture.Above).And.Contain(FloorFixture.Running)
            .And.Contain("held").And.Contain(FloorHoldFixture.V2Version);
        held.HeldUpdateDispatch.Should().StartWith("blocking ticket dispatched");

        var ticket = dispatch.Tickets.Should().ContainSingle("one held state, one blocking ticket").Subject;
        ticket.Package.Should().Be(FloorHoldFixture.Package);
        ticket.HeldVersion.Should().Be(FloorHoldFixture.V2Version);
        ticket.Floor.Should().Be(FloorFixture.Above);
        ticket.Running.Should().Be(FloorFixture.Running);
        ticket.InstalledVersion.Should().Be(FloorHoldFixture.V1Version);
        ticket.Unblocks.Should().Contain("rolls to a platform ≥ " + FloorFixture.Above);

        // ── the same held state, ticked again: nothing new is sent, nothing is fetched ─────────
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), Logger, ct);
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), Logger, ct);
        dispatch.Tickets.Should().ContainSingle("repeated ticks over one held state send ONE ticket, not one per tick");
        source.Fetches.Should().Be(0);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text);

        // ── the floor satisfied: the update lands and the hold clears ─────────────────────────
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Below), Logger, ct);
        var landed = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text,
            "the positive control: with the floor met, the same update lands");
        landed.HeldUpdate.Should().BeNull("an install of the candidate ends the hold");
        landed.HeldUpdateDispatch.Should().BeNull();
        landed.HeldUpdateDispatchedAt.Should().BeNull();
        dispatch.Tickets.Should().ContainSingle("a satisfied floor sends no ticket");
    }

    /// <summary>The click / boot-install lane: the ONE orchestrator holds too, before a file travels.</summary>
    [Fact(Timeout = 300_000)]
    public async Task InstallOrUpdate_HoldsTheUpdate_AndReturnsNothingWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        await Installer.Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);
        await FloorHoldFixture.InstallV1(Mesh, Logger, ct);

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        var result = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", FloorHoldFixture.V2(FloorFixture.Above), Logger)
            .Timeout(120.Seconds()).Await(ct);

        result.Should().Be(new InstallResult(0, 0));
        source.Fetches.Should().Be(0);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text);
        var record = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdate is not null, ct);
        record.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash);
        dispatch.Tickets.Should().ContainSingle();
    }

    /// <summary>A FRESH install of a version whose floor is unmet is refused, clearly, and writes
    /// nothing — not a node, not a record, not a fetched file.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AFreshInstallAboveTheFloor_IsRefused_NamingBothVersions_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        await Installer.Completed.FirstAsync().Timeout(180.Seconds()).Await(ct);

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        var refusal = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", FloorHoldFixture.V2(FloorFixture.Above), Logger)
            .Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext).FirstAsync()
            .Timeout(120.Seconds()).Await(ct);

        refusal.Exception.Should().BeOfType<PackagePlatformFloorException>();
        refusal.Exception!.Message.Should().Contain(FloorHoldFixture.Package).And.Contain(FloorFixture.Above)
            .And.Contain(FloorFixture.Running).And.Contain("not installed");
        source.Fetches.Should().Be(0, "refused before a single file is fetched");
        (await ReassertFixture.Read(Mesh, FloorHoldFixture.RecordPath)).Should().BeNull();
        (await ReassertFixture.Read(Mesh, FloorHoldFixture.SourcePath)).Should().BeNull();

        // The installer itself refuses a direct caller the same way (the enforcement half).
        var direct = await PackageInstaller
            .Install(Mesh, FloorHoldFixture.V2(FloorFixture.Above), FloorHoldFixture.V2Files(), "HEAD", Logger)
            .Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext).FirstAsync()
            .Timeout(120.Seconds()).Await(ct);
        direct.Exception.Should().BeOfType<PackagePlatformFloorException>();
        (await ReassertFixture.Read(Mesh, FloorHoldFixture.SourcePath)).Should().BeNull();

        // Negative control: the same package with a satisfied floor installs.
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", FloorHoldFixture.V2(FloorFixture.Below), Logger)
            .Timeout(180.Seconds()).Await(ct);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text);
        dispatch.Tickets.Should().BeEmpty("a refused fresh install is not a held update — nothing is kept running");
    }
}

/// <summary>
/// A host with NO dispatch registered: the hold still happens and is still visible — the record
/// says the ticket was NOT dispatched and why; nothing crashes.
/// </summary>
public class PackageFloorHoldWithoutDispatchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(FloorFixture.Pin));

    [Fact(Timeout = 300_000)]
    public async Task TheHold_IsRecorded_AndSaysItWasNotDispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PackageFloorHoldWithoutDispatchTest>();
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        await FloorHoldFixture.InstallV1(Mesh, logger, ct);

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), logger, ct);

        var record = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdateDispatch is not null, ct);
        record.HeldUpdate.Should().Contain(FloorFixture.Above);
        record.HeldUpdateDispatch.Should().StartWith("NOT dispatched").And.Contain(nameof(IPackageHoldDispatch));
        record.HeldUpdateDispatchedAt.Should().BeNull("a ticket nobody accepted is retried, never marked sent");
        source.Fetches.Should().Be(0);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text);
    }
}

/// <summary>
/// 🚨 The ticket through the REAL channel: <see cref="PackageHoldHandover"/> over
/// <see cref="SelfUpdateHandover"/> — the signed POST a self-update announcement takes — into a
/// fake control inbox. One held state, one signed event whose body says what is held and what
/// unblocks it; repeated ticks send nothing more.
/// </summary>
public class PackageFloorHoldTicketTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Deployment = "unit-floor-instance";
    private const string InboxUrl = "https://control.example/api/hooks/Hosting/PlatformBuilds";
    private const string Secret = "unit-floor-inbox-secret";

    private readonly FloorHoldFixture.FakeControlInbox inbox = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [SelfUpdateHandover.SecretKey] = Secret }).Build())
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(FloorFixture.Pin)
                .AddSingleton<IPackageHoldDispatch>(new PackageHoldHandover
                {
                    Handover = (hub, logger) => new SeamedHandover(hub,
                        new SelfUpdateHandover.Settings(Deployment, InboxUrl, SecretPresent: true,
                            LocalTargetListed: false, LocalSecretPresent: false, "https://unit.example"),
                        inbox),
                }));

    [Fact(Timeout = 300_000)]
    public async Task OneHeldState_SendsOneSignedBlockingEvent_ThroughTheSelfUpdateChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PackageFloorHoldTicketTest>();
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        await FloorHoldFixture.InstallV1(Mesh, logger, ct);

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), logger, ct);
        var record = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdateDispatchedAt is not null, ct);
        record.HeldUpdateDispatch.Should().StartWith("blocking ticket dispatched").And.Contain(InboxUrl);

        var delivery = inbox.Deliveries.Should().ContainSingle().Subject;
        WebhookInbox.VerifyHmacSha256(delivery.Signature, delivery.Body, Secret).Should().BeTrue(
            "the control inbox verifies the same X-Hub-Signature-256 a self-update announcement carries");
        using var doc = JsonDocument.Parse(delivery.Body);
        var root = doc.RootElement;
        root.GetProperty("event").GetString().Should().Be(SelfUpdateHandover.PackageHeldEvent);
        root.GetProperty("severity").GetString().Should().Be(PackageHoldHandover.Blocking);
        root.GetProperty("deployment").GetString().Should().Be(Deployment);
        root.GetProperty("package").GetString().Should().Be(FloorHoldFixture.Package);
        root.GetProperty("heldVersion").GetString().Should().Be(FloorHoldFixture.V2Version);
        root.GetProperty("floor").GetString().Should().Be(FloorFixture.Above);
        root.GetProperty("currentVersion").GetString().Should().Be(FloorFixture.Running);
        root.GetProperty("installedVersion").GetString().Should().Be(FloorHoldFixture.V1Version);
        root.GetProperty("unblocks").GetString().Should().Contain("rolls to a platform ≥ " + FloorFixture.Above);
        root.GetProperty("reporter").GetString().Should().Be(PackageHoldHandover.Reporter);

        await FloorHoldFixture.Reconcile(Mesh, source, FloorHoldFixture.V2(FloorFixture.Above), logger, ct);
        inbox.Deliveries.Should().ContainSingle("the same held state is not re-sent on the next tick");
    }

    private sealed class SeamedHandover(IMessageHub hub, SelfUpdateHandover.Settings settings, HttpMessageHandler inbox)
        : SelfUpdateHandover(hub, null, new HttpClient(inbox))
    {
        public override Settings ReadSettings() => settings;
    }
}

/// <summary>
/// The real <see cref="PackageHoldHandover"/> on an instance that declares NO control inbox: the
/// absence is the finding — recorded as "NOT dispatched" naming what is missing, never a crash.
/// </summary>
public class PackageFloorHoldNoInboxTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(FloorFixture.Pin)
                .AddSingleton<IPackageHoldDispatch>(new PackageHoldHandover()));

    [Fact(Timeout = 300_000)]
    public async Task NoControlInbox_IsRecordedAsNotDispatched_NamingWhatIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PackageFloorHoldNoInboxTest>();
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        await FloorHoldFixture.InstallV1(Mesh, logger, ct);

        await FloorHoldFixture.Reconcile(Mesh, new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files()),
            FloorHoldFixture.V2(FloorFixture.Above), logger, ct);

        var record = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdateDispatch is not null, ct);
        record.HeldUpdateDispatch.Should().StartWith("NOT dispatched").And.Contain("Hosting:");
        record.HeldUpdateDispatchedAt.Should().BeNull();
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text);
    }
}

/// <summary>The pure de-duplication rule and the ticket's shape.</summary>
public class PackageHoldTicketRulesTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static PlatformFloorVerdict Held() => PlatformFloor.Evaluate("3.0.0-ci.9494", "3.0.0-ci.9412");

    private static PackageManifest Candidate() => new() { Id = "Store", Version = "1.16.0", MinMeshVersion = "3.0.0-ci.9494" };

    [Fact]
    public void ATicketIsSentOncePerHeldState_AndAgainOnlyWhenItChanges_OrADayLater()
    {
        var sentence = PackagePlatformFloorGate.HeldSentence(Candidate(), Held());
        var fresh = new PackageManifest { Id = "Store", Version = "1.15.0" };
        PackagePlatformFloorGate.ShouldDispatch(fresh, sentence, Now).Should().BeTrue("a new held state");

        var sent = fresh with { HeldUpdate = sentence, HeldUpdateDispatchedAt = Now.AddHours(-1) };
        PackagePlatformFloorGate.ShouldDispatch(sent, sentence, Now).Should().BeFalse("the same state, sent an hour ago");
        PackagePlatformFloorGate.ShouldDispatch(sent, sentence, Now.AddHours(24)).Should().BeTrue("a day later it is re-raised");

        var rolled = PackagePlatformFloorGate.HeldSentence(Candidate(), PlatformFloor.Evaluate("3.0.0-ci.9494", "3.0.0-ci.9450"));
        PackagePlatformFloorGate.ShouldDispatch(sent, rolled, Now).Should().BeTrue(
            "the running platform moved but not far enough — a changed held state is a new ticket");

        var failed = fresh with { HeldUpdate = sentence, HeldUpdateDispatchedAt = null, HeldUpdateDispatch = "NOT dispatched: x" };
        PackagePlatformFloorGate.ShouldDispatch(failed, sentence, Now).Should().BeTrue("an attempt nobody accepted is retried");
    }

    [Fact]
    public void TheTicket_SaysWhatIsHeld_WhatKeepsRunning_AndWhatUnblocksIt()
    {
        var ticket = PackagePlatformFloorGate.Ticket(
            Candidate(), new PackageManifest { Id = "Store", Version = "1.15.0" }, Held());
        ticket.Package.Should().Be("Store");
        ticket.HeldVersion.Should().Be("1.16.0");
        ticket.Floor.Should().Be("3.0.0-ci.9494");
        ticket.Running.Should().Be("3.0.0-ci.9412");
        ticket.InstalledVersion.Should().Be("1.15.0");
        ticket.Unblocks.Should().Contain("rolls to a platform ≥ 3.0.0-ci.9494").And.Contain("1.15.0 keeps running");

        var announcement = PackageHoldHandover.AnnouncementOf(ticket, Now);
        var body = SelfUpdateHandover.Body(announcement);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("event").GetString().Should().Be("package-update-held");
        doc.RootElement.GetProperty("severity").GetString().Should().Be("blocking");
        doc.RootElement.TryGetProperty("newVersion", out _).Should().BeFalse(
            "a held-package ticket carries no release fields a self-update router could misread");
    }
}

/// <summary>One package: v1 installed, v2 referencing a platform member the running platform lacks.</summary>
internal static class FloorHoldFixture
{
    public const string Package = "FloorHoldPkg";
    public const string SourcePath = $"{Package}/BillingSource";
    public static readonly string RecordPath = $"{PackageInstaller.InstalledPartition}/{Package}";

    public const string V1Version = "1.15.0";
    public const string V2Version = "1.16.0";
    public const string V1Hash = "mv-floorhold-v1";
    public const string V2Hash = "mv-floorhold-v2";

    public const string V1Text = "IPaymentProvider.CreateCheckout(order) — v1, compiles on this platform";

    /// <summary>The 09-27 shape: v2's source calls a member the running platform does not have.</summary>
    public const string V2Text = "IPaymentProvider.CreateBillingPortalSession(customer) — v2, needs a newer platform";

    public static PackageManifest V1() => Candidate(V1Version, V1Hash, floor: null);

    public static PackageManifest V2(string floor) => Candidate(V2Version, V2Hash, floor);

    private static PackageManifest Candidate(string version, string hash, string? floor) => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = version,
        ModuleVersion = hash,
        MinMeshVersion = floor,
    };

    public static IReadOnlyList<PackageFile> V1Files() => Files(V1Version, V1Hash, V1Text);

    public static IReadOnlyList<PackageFile> V2Files() => Files(V2Version, V2Hash, V2Text);

    private static IReadOnlyList<PackageFile> Files(string version, string hash, string text) =>
    [
        new PackageFile($"{Package}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{Package}}",
              "moduleVersion": "{{hash}}",
              "version": "{{version}}",
              "files": {
                "{{Package}}/index.json": "idx",
                "{{Package}}/BillingSource.md": "{{hash}}-src"
              }
            }
            """),
        new PackageFile($"{Package}/index.json", $$"""
            {
              "id": "{{Package}}",
              "path": "{{Package}}",
              "nodeType": "Space",
              "name": "Floor hold package",
              "state": "Active"
            }
            """),
        new PackageFile($"{Package}/BillingSource.md", text),
    ];

    /// <summary>A source that counts every fetch — the witness that held sources never travel.</summary>
    public sealed class CountingSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        private int fetches;

        public int Fetches => Volatile.Read(ref fetches);

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Defer(() =>
            {
                Interlocked.Increment(ref fetches);
                return Observable.Return(files);
            });
    }

    /// <summary>Records every ticket the catalog hands over.</summary>
    public sealed class RecordingDispatch : IPackageHoldDispatch
    {
        private ImmutableList<PackageHoldTicket> tickets = ImmutableList<PackageHoldTicket>.Empty;

        public ImmutableList<PackageHoldTicket> Tickets => tickets;

        public IObservable<string> Dispatch(IMessageHub hub, PackageHoldTicket ticket) =>
            Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref tickets, t => t.Add(ticket));
                return Observable.Return("accepted by the test dispatch");
            });
    }

    /// <summary>The control inbox, substituted: records every delivery and answers accepted/verified.</summary>
    public sealed class FakeControlInbox : HttpMessageHandler
    {
        private ImmutableList<Delivery> deliveries = ImmutableList<Delivery>.Empty;

        public ImmutableList<Delivery> Deliveries => deliveries;

        public sealed record Delivery(string Url, string Body, string? Signature);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            request.Headers.TryGetValues(WebhookInbox.SignatureHeader, out var signatures);
            ImmutableInterlocked.Update(ref deliveries, d => d.Add(
                new Delivery(request.RequestUri!.ToString(), body, signatures?.FirstOrDefault())));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"accepted\",\"signature\":\"verified\"}"),
            };
        }
    }

    /// <summary>Installs v1 through the ONE orchestrator and waits until its source is in the mesh.</summary>
    public static async Task InstallV1(IMessageHub mesh, ILogger logger, CancellationToken ct)
    {
        await CatalogLayoutAreas.InstallOrUpdate(mesh, new CountingSource(V1Files()), "HEAD", V1(), logger)
            .Timeout(180.Seconds()).Await(ct);
        (await ReassertFixture.WaitForNode(mesh, SourcePath, present: true)).Should().BeTrue(
            "v1 must be installed before an update can be held");
        (await Text(mesh)).Should().Be(V1Text);
        (await AwaitRecord(mesh, r => r.ModuleVersion == V1Hash, ct)).Version.Should().Be(V1Version);
    }

    /// <summary>One unattended reconcile pass over one candidate — the call every entry point makes.</summary>
    public static Task Reconcile(IMessageHub mesh, IPackageSource source, PackageManifest candidate, ILogger logger, CancellationToken ct) =>
        PackageUpdateReconciler.ReconcileInstalled(mesh, source, "HEAD", [candidate], "Served by the test", logger)
            .Timeout(180.Seconds()).Await(ct);

    /// <summary>The source node's markdown body, read authoritatively off storage.</summary>
    public static async Task<string?> Text(IMessageHub mesh)
    {
        var node = await ReassertFixture.Read(mesh, SourcePath);
        if (node?.Content is null)
            return null;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(node.Content, mesh.JsonSerializerOptions));
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("content", out var body)
               && body.ValueKind == JsonValueKind.String
            ? body.GetString()
            : doc.RootElement.ToString();
    }

    /// <summary>Polls the install record off storage until <paramref name="predicate"/> holds.</summary>
    public static Task<PackageManifest> AwaitRecord(IMessageHub mesh, Func<PackageManifest, bool> predicate, CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(RecordPath, mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null))
            .Select(n => n?.ContentAs<PackageManifest>(mesh.JsonSerializerOptions))
            .Where(r => r is not null && predicate(r))
            .Select(r => r!)
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await(ct);
}
