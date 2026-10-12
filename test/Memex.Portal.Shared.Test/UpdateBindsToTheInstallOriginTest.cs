using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An unattended update is bound to the repository the install came from, never to the
/// package id alone</b> (MeshWeaver#6526).
///
/// <para>The update watcher hands a watched repository's listing to
/// <see cref="PackageUpdateReconciler"/>, which matched install records by id. A second watched
/// repository listing the same id could therefore update a package it never installed. Now the
/// record carries <see cref="PackageManifest.Origin"/>, and a record that predates it is decided
/// adopt-or-refuse: exactly one watched repository listing the package is adopted, two or more are
/// refused by name on the platform bell.</para>
///
/// <para>The records use the reminder policy, so every arm is observed through the reminder marker
/// (<see cref="PackageManifest.NotifiedModuleVersion"/>) and the platform bell, and nothing is
/// fetched. The marker is the positive signal that a pass TOOK a candidate.</para>
/// </summary>
public class UpdateBindsToTheInstallOriginTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Honest = "github.com/acme/plugins";
    private const string Impostor = "github.com/someone-else/plugins";
    private const string Installed = "mv-installed-6526";
    private const string Next = "mv-next-6526";
    private const string Later = "mv-later-6526";

    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog()
            // The running platform is pinned so a candidate declaring FloorFixture.Above is HELD —
            // the pinned arm below needs a candidate the floor gate would act on.
            .ConfigureServices(services => services.AddSingleton(FloorFixture.Pin));

    [Fact]
    public void TheOriginIsCanonical_WhicheverSpellingOfTheRepositoryBuiltTheSource()
    {
        InstallOrigin.ForRepo("https://github.com/Acme/Plugins.git/", null).Should().Be(Honest);
        InstallOrigin.ForRepo("https://github.com/acme/plugins", "/catalog/").Should().Be(Honest + "#catalog");
        InstallOrigin.ForRepo("", "x").Should().BeNull();
        InstallOrigin.ForRegistry("https://Memex.Example/").Should().Be("registry:https://memex.example");

        var none = new OriginCensus(ImmutableSortedSet<string>.Empty, ImmutableSortedSet<string>.Empty);
        InstallOrigin.Decide(Honest, Honest, null).Should().Be(OriginVerdict.Bound);
        InstallOrigin.Decide(Honest, Impostor, null).Should().Be(OriginVerdict.Foreign);
        InstallOrigin.Decide(null, Honest, none).Should().Be(OriginVerdict.Adopt);
        InstallOrigin.Decide(null, Honest, none with { Listing = [Impostor] }).Should().Be(OriginVerdict.Ambiguous);
        InstallOrigin.Decide(null, Honest, none with { Unread = [Impostor] }).Should().Be(OriginVerdict.Undetermined);
        InstallOrigin.Decide(null, Honest, null).Should().Be(OriginVerdict.Undetermined);
    }

    [Fact]
    public async Task ALegacyRecordAdoptsTheOnlyListingRepository_AndAnImpostorIsIgnoredAfterwards()
    {
        const string id = "OriginAdoptPkg";
        await Seed(id);

        // ── Adopt: the honest repository is the only watched one listing the package ─────────────
        var censusReads = 0;
        await Reconcile(id, Next, Bind(Honest, _ =>
        {
            Interlocked.Increment(ref censusReads);
            return Census();
        }));
        (await AwaitRecord(id, r => r.NotifiedModuleVersion == Next)).Origin.Should().Be(Honest,
            "the record names no origin and only one watched repository lists it, so it adopts that one");
        censusReads.Should().Be(1, "the census is read for a record with no origin");

        // ── An impostor listing the same id is ignored once the origin is recorded ──────────────
        await Reconcile(id, Later, Bind(Impostor, _ => throw new InvalidOperationException(
            "a record that names its origin must never need the census")));
        var afterImpostor = await Record(id);
        afterImpostor.Origin.Should().Be(Honest);
        afterImpostor.NotifiedModuleVersion.Should().Be(Next,
            "the impostor's candidate must not even be reminded about — it is not an update of this install");
        (await Reminders(id)).Should().ContainSingle("only the honest candidate raised a bell");

        // ── Control: the same candidate from the honest repository IS taken ─────────────────────
        // Without this the arm above could pass because the pass itself was broken.
        await Reconcile(id, Later, Bind(Honest, _ => throw new InvalidOperationException("not needed")));
        await AwaitRecord(id, r => r.NotifiedModuleVersion == Later);
    }

    [Fact]
    public async Task ALegacyRecordListedByTwoRepositories_IsRefusedByName_AndNothingIsAdopted()
    {
        const string id = "OriginAmbiguousPkg";
        await Seed(id);

        await Reconcile(id, Next, Bind(Honest, _ => Census(listing: [Impostor])));

        var refusals = await PlatformBell(id)
            .Where(ns => ns.Count > 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        var refusal = refusals.Should().ContainSingle().Subject.ContentAs<Notification>(Json)!;
        refusal.Title.Should().StartWith("Update refused");
        refusal.Message.Should().Contain(id).And.Contain(Honest).And.Contain(Impostor,
            "the refusal names the package and EVERY candidate repository");
        (await Record(id)).Origin.Should().BeNull("an ambiguous record adopts nothing");

        // An incomplete census neither adopts nor refuses — and the update is not taken either.
        const string other = "OriginUnreadPkg";
        await Seed(other);
        await Reconcile(other, Next, Bind(Honest, _ => Census(unread: [Impostor])));
        var unread = await Record(other);
        unread.Origin.Should().BeNull();
        unread.NotifiedModuleVersion.Should().BeNull();
    }

    [Fact]
    public async Task APinnedRecord_IsNotHeldByAForeignCandidate()
    {
        // A pinned record never reached the origin gate, and the floor gate ran before the policy
        // switch, so a FOREIGN candidate above the floor stamped a hold (and a blocking ticket) on a
        // package the administrator pinned (review on MeshWeaver#6541).
        const string id = "OriginPinnedPkg";
        await Seed(id, origin: Honest, policy: PackageUpdatePolicy.None);
        await Reconcile(id, Next, Bind(Impostor, _ => throw new InvalidOperationException(
            "a pinned record must never need the census")), minMeshVersion: FloorFixture.Above);
        var pinned = await Record(id);
        pinned.HeldUpdate.Should().BeNull("nothing moves for a pinned package — no hold from any repository");
        pinned.HeldUpdateDispatch.Should().BeNull();
        pinned.Origin.Should().Be(Honest);

        // Control: the same floor-held candidate on a NOTIFY record from its own repository IS held,
        // so the arm above cannot pass because the floor gate never fired.
        const string control = "OriginPinnedControlPkg";
        await Seed(control, origin: Honest);
        await Reconcile(control, Next, Bind(Honest, _ => throw new InvalidOperationException("not needed")),
            minMeshVersion: FloorFixture.Above);
        (await AwaitRecord(control, r => r.HeldUpdate is not null)).HeldUpdate.Should().Contain(FloorFixture.Above);
    }

    [Fact]
    public async Task AnAdoptionLostToAConcurrentInstall_TakesNothing()
    {
        // The census sees a record with NO origin; before the adoption writes, a concurrent install
        // stamps the impostor's origin. The adoption must decide on the record it WROTE, which now
        // names another repository — not on its pre-write snapshot (review on MeshWeaver#6541).
        const string id = "OriginRacePkg";
        await Seed(id);
        await Reconcile(id, Next, Bind(Honest, _ => Observable.Defer(() =>
            Mesh.ServiceProvider.GetRequiredService<AccessService>()
                .RunAsSystem(() => Mesh.GetMeshNodeStream(RecordPath(id))
                    .Update<PackageManifest>(current => current with { Origin = Impostor }))
                .Take(1)
                .SelectMany(_ => Census()))));
        var record = await Record(id);
        record.Origin.Should().Be(Impostor, "the concurrent install's origin wins; adoption fills an EMPTY origin only");
        record.NotifiedModuleVersion.Should().BeNull(
            "the candidate is bound to a repository the record no longer names, so it is not taken");
        (await Reminders(id, expectAny: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task NegativeControl_AnUnboundPassStillTakesTheImpostorsCandidate()
    {
        // The defect this fixture pins, reproduced: with no binding the reconciler matches by id
        // alone, so a record installed from the honest repository is reminded about the impostor's
        // build. If this ever stops raising, the arms above prove nothing.
        const string id = "OriginUnboundPkg";
        await Seed(id, origin: Honest);
        await Reconcile(id, Next, binding: null);
        (await AwaitRecord(id, r => r.NotifiedModuleVersion == Next)).Origin.Should().Be(Honest);
    }

    private static InstallOriginBinding Bind(string origin, Func<string, IObservable<OriginCensus>> census)
        => new(origin, census);

    private static IObservable<OriginCensus> Census(
        ImmutableSortedSet<string>? listing = null, ImmutableSortedSet<string>? unread = null)
        => Observable.Return(new OriginCensus(
            listing ?? ImmutableSortedSet<string>.Empty, unread ?? ImmutableSortedSet<string>.Empty));

    private async Task Reconcile(string id, string candidate, InstallOriginBinding? binding,
        string? minMeshVersion = null)
    {
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<UpdateBindsToTheInstallOriginTest>();
        await PackageUpdateReconciler
            .ReconcileInstalled(Mesh, new NoFetchSource(), "HEAD",
                [new PackageManifest { Id = id, Name = id, Version = "1.0.1", ModuleVersion = candidate, TargetPartition = id, MinMeshVersion = minMeshVersion }],
                "Built from test", logger, binding)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }

    private async Task Seed(string id, string? origin = null,
        PackageUpdatePolicy policy = PackageUpdatePolicy.Notify)
    {
        var manifest = new PackageManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            ModuleVersion = Installed,
            TargetPartition = id,
            Origin = origin,
            AutoUpdate = false,
            UpdatePolicy = policy,
            UpdatePolicySetAt = DateTimeOffset.UtcNow,
        };
        var record = MeshNode.FromPath(RecordPath(id)) with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = manifest,
        };
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record))
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }

    private static string RecordPath(string id) => $"{PackageInstaller.InstalledPartition}/{id}";

    private Task<PackageManifest> Record(string id) => AwaitRecord(id, _ => true);

    private async Task<PackageManifest> AwaitRecord(string id, Func<PackageManifest, bool> predicate) =>
        await Mesh.GetMeshNodeStream(RecordPath(id))
            .Select(n => n?.ContentAs<PackageManifest>(Json))
            .Where(r => r is not null && predicate(r))
            .Select(r => r!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>The record's "Update available" rows. When a reminder is EXPECTED, wait for the bell
    /// to show one: the query's initial snapshot can precede the index seeing the reminder, and an
    /// empty first emission would read as "none" (review on MeshWeaver#6541).</summary>
    private async Task<IReadOnlyList<MeshNode>> Reminders(string id, bool expectAny = true) =>
        (await PlatformBell(id).Where(ns => !expectAny || ns.Count > 0).FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken))
        .Where(n => n.ContentAs<Notification>(Json)?.Title?.StartsWith("Update available", StringComparison.Ordinal) == true)
        .ToList();

    /// <summary>This record's rows on the PLATFORM bell (the addressee of every package-update
    /// notification), narrowed to the ones it is the click target of.</summary>
    private IObservable<IReadOnlyList<MeshNode>> PlatformBell(string id) =>
        Mesh.GetWorkspace()
            .GetQuery($"notif|pkg|origin|{id}",
                $"path:{NotificationService.DeliveryNamespace(NotificationService.PlatformAddressee)} "
                + "scope:children nodeType:Notification")
            .Select(ns => (IReadOnlyList<MeshNode>)(ns ?? [])
                .Where(n => string.Equals(
                    n.ContentAs<Notification>(Json)?.TargetNodePath, RecordPath(id), StringComparison.Ordinal))
                .ToList());

    /// <summary>The reminder path fetches nothing; a fetch here is a branch the pass had no business taking.</summary>
    private sealed class NoFetchSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Throw<IReadOnlyList<PackageManifest>>(new InvalidOperationException("must not list"));

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Throw<IReadOnlyList<PackageFile>>(new InvalidOperationException("must not fetch"));
    }
}

/// <summary>
/// An install records the origin of the SOURCE it ran through — never an <c>origin</c> its manifest
/// authors (MeshWeaver#6526).
/// </summary>
public class AnInstallRecordsItsOriginTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Honest = "github.com/acme/plugins";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 300_000)]
    public async Task AManualInstall_RecordsTheSourcesOrigin_OverAnAuthoredOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<AnInstallRecordsItsOriginTest>();
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);

        // The catalog click's path: the funnel, through a source built for the honest repository,
        // with a manifest that AUTHORS a different origin.
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh,
                InstallOrigin.Bind(new ReassertFixture.Source(ReassertFixture.Files()), Honest), "HEAD",
                ReassertFixture.Candidate() with { Origin = "github.com/someone-else/plugins" }, logger)
            .Should().Within(180.Seconds()).Emit("the install must land", cancellationToken: ct);

        var record = await ReassertFixture.ReadRecord(Mesh);
        record.Should().NotBeNull("CONTROL — the install wrote its record");
        record!.Origin.Should().Be(Honest,
            "the record names the source the install ran through, never what the manifest authored");
    }
}
