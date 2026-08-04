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
}
