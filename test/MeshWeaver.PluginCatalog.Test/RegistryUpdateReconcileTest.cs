#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The CONSUMER half of plugin self-update (#1318): an installation that installs over HTTP from a
/// registry — no GitHub credential, no webhooks — must be able to learn that an installed module
/// changed.
///
/// <para><b>What was missing.</b> The only mechanism was <see cref="PluginUpdateWatcher"/>, which
/// subscribes to a <c>BuildCompletion</c> node. That node is constructed in exactly ONE place in the
/// whole tree — a GitHub <c>workflow_run</c> webhook — and the watcher only subscribes at all once a
/// catalog node names a source repo. A registry-only consumer has neither, so the watcher was
/// registered, live, and completely inert, and <see cref="PackageManifest.AutoUpdate"/> was a flag
/// nothing ever fired.</para>
///
/// <para>The tests below pin the decision — which is now ONE decision for both paths — and the
/// wiring that makes it run on a consumer.</para>
/// </summary>
public class RegistryUpdateReconcileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // 🚨 A record's AutoUpdate is NOT taken from the manifest handed to the installer — it is
    // SEEDED at install time from the deployment's default and then owned by the record
    // (PackageInstaller.SeedAutoUpdate: `existingRecord?.AutoUpdate ?? options?.AutoUpdateByDefault`).
    // Without this registration every record here would be created opted-OUT, which would make the
    // reminder test below pass for the wrong reason — it would assert "nothing was installed"
    // against a path that was never eligible to install.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton(
                new PluginCatalogOptions { AutoUpdateByDefault = true }));

    private const string ModuleV1 = "cccccccccccccc01";
    private const string ModuleV2 = "cccccccccccccc02";

    private static IReadOnlyList<PackageFile> FilesAt(string moduleVersion, string notes, string commit) =>
    [
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Widget/Notes.md", notes),
        new("Widget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Widget","moduleVersion":"{{{moduleVersion}}}","sourceCommit":"{{{commit}}}","files":{"Widget/index.json":"h-root-1","Widget/Notes.md":"h-notes-{{{moduleVersion}}}"}}"""),
    ];

    /// <summary>A stand-in for the registry feed: it serves a package list and records every fetch,
    /// so a test can prove that NOTHING traveled.</summary>
    private sealed class FeedSource(
        IReadOnlyList<PackageManifest> catalog, IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public readonly List<IReadOnlyCollection<string>?> Fetches = [];

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return(catalog);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
        {
            Fetches.Add(null);
            return Observable.Return(files);
        }

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths)
        {
            Fetches.Add(paths);
            var wanted = paths is null ? null : new HashSet<string>(paths, StringComparer.Ordinal);
            return Observable.Return<IReadOnlyList<PackageFile>>(
                wanted is null ? files : files.Where(f => wanted.Contains(f.RelativePath)).ToList());
        }
    }

    private static PackageManifest Pkg(string moduleVersion) => new()
    {
        Id = "Widget",
        Name = "Widget Plugin",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Widget",
        SourceFolder = "Widget",
        Version = "commit-1",
        ModuleVersion = moduleVersion,
    };

    private IObservable<MeshNode?> ReadRecord() =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/Widget", Mesh.JsonSerializerOptions)
            .Take(1);

    /// <summary>
    /// 🚨 THE POINT OF THE ISSUE. A module whose content moved on at the registry, on a record that
    /// opted into unattended updates, is brought up to date by a read of the feed alone — no
    /// webhook, no <c>BuildCompletion</c>, no GitHub credential anywhere in the path.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOutdatedInstalledModule_IsBroughtUpToDate_FromTheFeedAlone()
    {
        await PackageInstaller.Install(
                Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        var source = new FeedSource([Pkg(ModuleV2)], FilesAt(ModuleV2, "# Notes v2", "c2"));

        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, source, "HEAD", [Pkg(ModuleV2)],
                "Served by registry 'test'", null)
            .FirstAsync().ToTask();

        var record = await ReadRecord().ToTask();
        record.Should().NotBeNull();
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.ModuleVersion
            .Should().Be(ModuleV2,
                "reading the registry's own feed is how a consumer learns — before this, the only "
                + "signal was a GitHub webhook that such an installation can never receive, so the "
                + "record stayed on v1 until a human clicked Provision");

        source.Fetches.Should().NotBeEmpty("the module changed, so the delta had to travel");
        source.Fetches.Should().NotContain(f => f is null,
            "a full-package fetch means the manifest diff was bypassed");
    }

    /// <summary>
    /// The NEGATIVE property, and the one that matters most: the gate is content identity, not the
    /// event. A boot reconcile runs on EVERY start, so a feed read that changed nothing must cost
    /// nothing — otherwise every restart would re-install every package.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFeedThatServesTheSameContent_FetchesNothing()
    {
        await PackageInstaller.Install(
                Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        var source = new FeedSource([Pkg(ModuleV1)], FilesAt(ModuleV1, "# Notes v1", "c2"));

        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, source, "HEAD", [Pkg(ModuleV1)],
                "Served by registry 'test'", null)
            .FirstAsync().ToTask();

        source.Fetches.Should().BeEmpty(
            "the module version matched, so the decision was made without fetching a single file — "
            + "a boot-time reconcile that keyed off the READ instead of the CONTENT would re-install "
            + "everything on every restart");
    }

    /// <summary>
    /// Reminder is the default. A record that did NOT opt in must not be updated behind the
    /// operator's back just because the reconcile now runs — the opt-in is the whole contract.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecordThatDidNotOptIn_IsRemindedRatherThanUpdated()
    {
        // Install under a deployment that does NOT opt in, so the record itself carries the
        // opt-out — which is the thing under test, and the only thing the reconcile consults.
        Mesh.ServiceProvider.GetRequiredService<PluginCatalogOptions>().AutoUpdateByDefault = false;

        await PackageInstaller.Install(
                Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        var source = new FeedSource([Pkg(ModuleV2)], FilesAt(ModuleV2, "# Notes v2", "c2"));

        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, source, "HEAD", [Pkg(ModuleV2)], "Served by registry 'test'", null)
            .FirstAsync().ToTask();

        source.Fetches.Should().BeEmpty("no opt-in ⇒ nothing is installed, so nothing is fetched");

        var record = await ReadRecord().ToTask();
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.ModuleVersion
            .Should().Be(ModuleV1, "the installed content is untouched — the user is reminded instead");
    }

    /// <summary>
    /// A registry lists far more packages than any one instance installs. A package with no install
    /// record here is not an update — it is somebody else's package, and must produce nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APackageThatIsNotInstalledHere_ProducesNothing()
    {
        var source = new FeedSource([Pkg(ModuleV2)], FilesAt(ModuleV2, "# Notes v2", "c2"));

        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, source, "HEAD", [Pkg(ModuleV2)],
                "Served by registry 'test'", null)
            .FirstAsync().ToTask();

        source.Fetches.Should().BeEmpty("nothing is installed here, so there is nothing to reconcile");
        (await ReadRecord().ToTask()).Should().BeNull("a reconcile must never INSTALL a new package");
    }

    /// <summary>
    /// The wiring, pinned the same way the watcher's is. Registration alone does not start it — the
    /// host starts only <c>IHostedService</c> registrations, and the hosted registration must
    /// resolve the SAME mesh singleton rather than a second inert copy.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheConsumerReconcilerIsRegisteredHostedAndScopedToTheMesh()
    {
        var reconciler = Mesh.ServiceProvider.GetService<RegistryUpdateReconciler>();
        reconciler.Should().NotBeNull("AddPluginCatalog registers the consumer-side reconciler");

        Mesh.ServiceProvider.GetService<RegistryUpdateReconciler>()
            .Should().BeSameAs(reconciler,
                "mesh-scoped singleton — its subscriptions die with the mesh, never leak into the next test");

        Mesh.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().Contain(s => ReferenceEquals(s, reconciler),
                "the IHostedService forward is what actually runs the boot reconcile");

        // This mesh has no registry configured, which is the git-only / registry-instance shape:
        // the service must be present and do nothing, so a deployment that never had a registry
        // keeps working exactly as before.
        Mesh.ServiceProvider.GetService<PluginCatalogOptions>()?.EffectiveRegistries.Count
            .Should().Be(0);
    }
}
