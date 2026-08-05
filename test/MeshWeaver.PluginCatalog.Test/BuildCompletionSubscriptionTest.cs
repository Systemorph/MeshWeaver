#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
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
/// The reactive half of plugin self-update: a repo's green CI build is recorded as a
/// <see cref="BuildCompletion"/> node, and the catalog reacts to that node — per MODULE, gated on
/// content identity.
///
/// <para>The property that matters most is the NEGATIVE one. The build node is rewritten on every
/// green run, including runs that changed nothing in a given module (doc-only commits, reverts,
/// re-runs of an unchanged tree). If the reaction keyed off the EVENT rather than the CONTENT, every
/// green build would push an install to every installation. So the first test asserts that an
/// unchanged module produces NOTHING — no notification, and not one file fetched.</para>
///
/// <para>See <c>Doc/Architecture/PluginUpdateOnGreenBuild</c>.</para>
/// </summary>
public class BuildCompletionSubscriptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private const string ModuleV1 = "bbbbbbbbbbbbbbb1";
    private const string ModuleV2 = "bbbbbbbbbbbbbbb2";

    private static IReadOnlyList<PackageFile> FilesAt(string moduleVersion, string notes, string commit) =>
    [
        new("Gadget/index.json",
            """{"$type":"MeshNode","id":"Gadget","namespace":"","path":"Gadget","mainNode":"Gadget","name":"Gadget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A gadget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Gadget/Notes.md", notes),
        new("Gadget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Gadget","moduleVersion":"{{{moduleVersion}}}","sourceCommit":"{{{commit}}}","files":{"Gadget/index.json":"h-root-1","Gadget/Notes.md":"h-notes-{{{moduleVersion}}}"}}"""),
    ];

    /// <summary>Records every fetch so a test can prove that NOTHING traveled.</summary>
    private sealed class RecordingSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public readonly List<IReadOnlyCollection<string>?> Fetches = [];

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([]);

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
        Id = "Gadget",
        Name = "Gadget Plugin",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Gadget",
        SourceFolder = "Gadget",
        Version = "commit-1",
        ModuleVersion = moduleVersion,
    };

    [Fact(Timeout = 120_000)]
    public async Task AGreenBuildThatChangedNothingInTheModuleFetchesNothing()
    {
        // Install v1 so there IS an install record to compare against.
        await PackageInstaller.Install(Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        // A green build lands, but the module's content hash is UNCHANGED — the same situation as a
        // doc-only commit elsewhere in the repo, or a re-run of the same tree.
        var source = new RecordingSource(FilesAt(ModuleV1, "# Notes v1", "c2"));
        var result = await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "c2", Pkg(ModuleV1), null)
            .FirstAsync().ToTask();

        result.Written.Should().Be(0, "an unchanged module must write nothing");
        source.Fetches.Should().BeEmpty(
            "the module version matched, so the decision was made without fetching a single file — "
            + "keying off the build EVENT instead of the CONTENT would have pushed an install here");
    }

    [Fact(Timeout = 120_000)]
    public async Task AGreenBuildThatChangedTheModuleFetchesOnlyTheChangedFiles()
    {
        await PackageInstaller.Install(Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        var source = new RecordingSource(FilesAt(ModuleV2, "# Notes v2 — updated", "c2"));
        var result = await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "c2", Pkg(ModuleV2), null)
            .FirstAsync().ToTask();

        result.Written.Should().BeGreaterThan(0, "the module changed, so something must land");

        // The manifest is fetched first to compute the diff, then ONLY the changed files — never
        // the whole package.
        source.Fetches.Should().NotBeEmpty();
        source.Fetches.Should().NotContain(f => f is null,
            "a full-package fetch means the manifest diff was bypassed");
        source.Fetches.SelectMany(f => f!).Should().NotContain("Gadget/index.json",
            "the root was byte-identical between the two manifests");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheBuildNodePathIsStableAndAnchoredInTheAdminPartition()
    {
        // The producer (GitSync) and the consumer (this project) never reference each other, so the
        // ONLY thing keeping them in agreement is this path. Pin it.
        BuildCompletion.PathFor("Systemorph", "MeshWeaver")
            .Should().Be("Admin/_Build/Systemorph.MeshWeaver");

        // Untrusted webhook input must never fabricate node hierarchy.
        BuildCompletion.PathFor("acme/evil", "repo")
            .Should().Be("Admin/_Build/acme-evil.repo");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheWatcherIsRegisteredAndScopedToTheMesh()
    {
        var watcher = Mesh.ServiceProvider.GetService<PluginUpdateWatcher>();
        watcher.Should().NotBeNull("AddPluginCatalog registers the build-completion subscriber");

        Mesh.ServiceProvider.GetService<PluginUpdateWatcher>()
            .Should().BeSameAs(watcher, "it is a mesh-scoped singleton, so its subscriptions die with the mesh");
    }

    /// <summary>The opt-in rule: unattended only for a record that opted in. An absent flag (every
    /// record written before the flag existed) stays on the reminder path — the platform default.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ReminderIsTheDefault_AutoUpdateIsAnExplicitOptIn()
    {
        PluginUpdateWatcher.ShouldAutoApply(new PackageManifest { Id = "Gadget" })
            .Should().BeFalse("a record that says nothing gets the reminder — the platform default");
        PluginUpdateWatcher.ShouldAutoApply(new PackageManifest { Id = "Gadget", AutoUpdate = true })
            .Should().BeTrue("an opted-in record installs unattended");
    }

    /// <summary>
    /// The install-time seed: a FRESH record takes the deployment's default
    /// (<c>PluginCatalog:AutoUpdateByDefault</c>, absent = explicit opt-in — our Helm deployments
    /// set it true so everything they install tracks its repo); an EXISTING record's own choice
    /// wins over any deployment default, in BOTH directions — the config is a seed, never a live
    /// override.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheDeploymentDefaultSeedsFreshInstalls_TheRecordsOwnChoiceWinsThereafter()
    {
        // Fresh install: no options / default options → reminder-only; opted-in deployment → auto-update.
        PackageInstaller.SeedAutoUpdate(null, null)
            .Should().BeFalse("a deployment that configures nothing is review-first");
        PackageInstaller.SeedAutoUpdate(null, new PluginCatalogOptions())
            .Should().BeFalse("the option's own default is explicit opt-in");
        PackageInstaller.SeedAutoUpdate(null, new PluginCatalogOptions { AutoUpdateByDefault = true })
            .Should().BeTrue("an opted-in deployment (our Helm portals) seeds fresh installs auto-updating");

        // Existing record: its explicit choice survives every re-stamp, whatever the deployment says.
        var optedIn = new PackageManifest { Id = "Gadget", AutoUpdate = true };
        PackageInstaller.SeedAutoUpdate(optedIn, new PluginCatalogOptions())
            .Should().BeTrue("an update re-stamp must not silently drop a package's opt-in");
        var reminderOnly = new PackageManifest { Id = "Gadget" };
        PackageInstaller.SeedAutoUpdate(reminderOnly, new PluginCatalogOptions { AutoUpdateByDefault = true })
            .Should().BeFalse("flipping the deployment default later must not opt in already-installed packages");
    }

    /// <summary>
    /// The integration half of the seed rule: an opted-in record stays opted in through a real
    /// update. The record built on a re-stamp starts from the CATALOG manifest — which never
    /// carries the policy field — so without the carry-forward the very FIRST auto-update would
    /// have silently dropped the opt-in and every deployment would fall back to reminders after
    /// one round.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOptedInRecordStaysOptedInThroughAnUpdate()
    {
        await PackageInstaller.Install(Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        var recordPath = $"{PackageInstaller.InstalledPartition}/Gadget";
        await Mesh.GetWorkspace().GetMeshNodeStream(recordPath)
            .Update(n => n with
            {
                Content = (n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions) ?? new PackageManifest())
                    with { AutoUpdate = true }
            })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        // Wait until the flag is STORAGE-visible — that is the read the re-stamp's carry-forward
        // does, and the hub write can flush a beat later than the stream emission.
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(recordPath, Mesh.JsonSerializerOptions))
            .Where(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)?.AutoUpdate == true)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var source = new RecordingSource(FilesAt(ModuleV2, "# Notes v2 — updated", "c2"));
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "c2", Pkg(ModuleV2), null)
            .FirstAsync().ToTask();

        // The re-stamp really happened (baseline moved to v2) AND the opt-in survived it.
        var record = await Mesh.GetWorkspace().GetMeshNodeStream(recordPath)
            .Select(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .Where(m => m is not null && m.ModuleVersion == ModuleV2)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        record!.AutoUpdate.Should().BeTrue(
            "the update re-stamped the baseline from the policy-less catalog manifest — the record's "
            + "own opt-in must be carried forward, not rebuilt as false");
    }

    /// <summary>
    /// One of the fences that makes DEFAULT-ON auto-update safe: a node the user claimed (any
    /// non-Include SyncBehavior — the deliberate act after modifying it) is skipped by the update's
    /// upsert, so a green build can never clobber a claimed local edit.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AClaimedNodeSurvivesAnUpdateUntouched()
    {
        await PackageInstaller.Install(Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        // The user modifies the node and CLAIMS it — the documented way to take a node over.
        await Mesh.GetWorkspace().GetMeshNodeStream("Gadget/Notes")
            .Update(n => n with { SyncBehavior = SyncBehavior.ExcludeThisOnly })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        // v2 changes exactly that node (the root is byte-identical between the manifests).
        var source = new RecordingSource(FilesAt(ModuleV2, "# Notes v2 — updated", "c2"));
        var result = await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "c2", Pkg(ModuleV2), null)
            .FirstAsync().ToTask();

        result.Written.Should().Be(0, "the only changed node is claimed, so nothing may land");
        var node = await Mesh.GetWorkspace().GetMeshNodeStream("Gadget/Notes")
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        node!.Content!.ToString().Should().Contain("v1",
            "the claimed node keeps the user's copy — the repo's v2 must not overwrite it");
    }

    /// <summary>
    /// The prune-side fence: the repo dropping a file revokes the PACKAGE's copy, never the user's
    /// claim — a claimed node survives the removed-file prune. (The unclaimed arm — a removed file
    /// IS pruned — is pinned in <c>IncrementalUpdateTest</c>.) Note the consequence: the skipped
    /// prune re-stamps the baseline without the file, so the node is permanently user-owned from
    /// here — consistent with claiming being the take-over act.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AClaimedNodeSurvivesThePrune()
    {
        await PackageInstaller.Install(Mesh, Pkg(ModuleV1), FilesAt(ModuleV1, "# Notes v1", "c1"), "c1")
            .FirstAsync().ToTask();

        await Mesh.GetWorkspace().GetMeshNodeStream("Gadget/Notes")
            .Update(n => n with { SyncBehavior = SyncBehavior.ExcludeThisOnly })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        // v2's manifest no longer lists Notes.md at all → the diff marks it removed.
        var source = new RecordingSource(FilesWithoutNotes(ModuleV2, "c2"));
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "c2", Pkg(ModuleV2), null)
            .FirstAsync().ToTask();

        var node = await Mesh.GetWorkspace().GetMeshNodeStream("Gadget/Notes")
            .Where(n => n?.Content is not null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        node.Should().NotBeNull("a claimed node must survive the removed-file prune");
    }

    private static IReadOnlyList<PackageFile> FilesWithoutNotes(string moduleVersion, string commit) =>
    [
        new("Gadget/index.json",
            """{"$type":"MeshNode","id":"Gadget","namespace":"","path":"Gadget","mainNode":"Gadget","name":"Gadget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A gadget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Gadget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Gadget","moduleVersion":"{{{moduleVersion}}}","sourceCommit":"{{{commit}}}","files":{"Gadget/index.json":"h-root-1"}}"""),
    ];
}
