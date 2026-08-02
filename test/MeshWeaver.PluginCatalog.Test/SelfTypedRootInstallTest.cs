#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
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
/// Pins the SELF-TYPED-ROOT install (the Store shape): a node-repo package whose ROOT carries a
/// DYNAMIC node type that ships IN THE SAME package (root <c>Shop</c> is nodeType
/// <c>Shop/Front</c>, defined by the child NodeType node) must install: the installer runs under
/// System (PartitionWriteGuard's static-only OwnsPartition check cannot pass for a dynamic type),
/// orders the type node before the root (the not-registered probe needs the type NODE to exist),
/// keeps a type's Source before the type, and lands underscore satellites last (a satellite must
/// anchor under an existing owner).
/// </summary>
public class SelfTypedRootInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Shop/index.json",
            """{"$type":"MeshNode","id":"Shop","namespace":"","path":"Shop","mainNode":"Shop","name":"Shop","nodeType":"Shop/Front","state":"Active","content":{"$type":"FrontContent","intro":"hello"}}"""),
        new("Shop/Front.json",
            """{"$type":"MeshNode","id":"Front","namespace":"Shop","path":"Shop/Front","mainNode":"Shop/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The shop front.","configuration":"config => config.WithContentType<FrontContent>()","includeGlobalTypes":true}}"""),
        new("Shop/Front/Source/FrontContent.cs",
            "public record FrontContent { public string? Intro { get; init; } }"),
        new("Shop/_Access/Anonymous_Access.json",
            """{"$type":"MeshNode","id":"Anonymous_Access","namespace":"Shop/_Access","name":"Anonymous — Viewer","nodeType":"AccessAssignment","mainNode":"Shop","content":{"$type":"AccessAssignment","accessObject":"Anonymous","displayName":"Anonymous","roles":[{"$type":"RoleAssignment","role":"Viewer"}]}}"""),
    };

    [Fact(Timeout = 120000)]
    public async Task RootTypedByAnInPackageNodeType_Installs()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-shop", Repo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/shop");

        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
        // The self-typed root is NOT a listed root type (only Space / Store/Plugin /
        // Store/Catalog are) — synthesize the manifest the way a curated registry entry would.
        packages.Should().BeEmpty();
        var manifest = new PackageManifest
        {
            Id = "Shop",
            Name = "Shop",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "Shop",
            SourceFolder = "Shop",
            Version = "commit-shop",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();

        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
        result.Written.Should().Be(4);

        // The root landed, TYPED — the in-package type node existed before the root's create.
        var root = await Mesh.GetWorkspace().GetMeshNodeStream("Shop")
            .Where(n => n is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        root!.NodeType.Should().Be("Shop/Front");

        // The satellite landed under its (existing) owner.
        var grant = await Mesh.GetWorkspace().GetMeshNodeStream("Shop/_Access/Anonymous_Access")
            .Where(n => n is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        grant!.MainNode.Should().Be("Shop");
    }

    /// <summary>
    /// The PERSISTENCE-visibility guarantee behind re-install idempotence: install completion is a
    /// happens-before for the STORAGE ADAPTER too, not just the shared node stream — the decisions
    /// a later install makes (PlaceholderNeeded, UpsertIfChanged) read persistence, and the owning
    /// hub's persist is debounced. Without the RootRetypePersisted barrier, an IMMEDIATE re-install
    /// intermittently read the Space placeholder from storage, re-ran the placeholder dance and
    /// rewrote the root — the flapping "re-install wrote 1 node(s)" idempotence failure in the
    /// plugins repo's gate. Pin both halves: the retype is storage-visible the moment the first
    /// install reports done, and the back-to-back re-install writes ZERO nodes.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task SelfTypedRoot_ReinstallImmediately_WritesNothing()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-shop2", Repo2));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/shop2");
        var manifest = new PackageManifest
        {
            Id = "Shop2",
            Name = "Shop2",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "Shop2",
            SourceFolder = "Shop2",
            Version = "commit-shop2",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();

        var first = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
        first.Written.Should().Be(3);

        // (a) The retype is visible via the STORAGE ADAPTER at completion — the exact read the
        //     next install's PlaceholderNeeded performs.
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var persisted = await persistence.Read("Shop2", Mesh.JsonSerializerOptions)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();
        persisted.Should().NotBeNull("install completion must be a happens-before for persistence");
        persisted!.NodeType.Should().Be("Shop2/Front2",
            "the root's final type — not the Space placeholder — must be the persisted state the "
            + "moment the install reports done");

        // (b) The back-to-back re-install of the unchanged snapshot writes NOTHING.
        var second = await PackageInstaller.Install(Mesh, manifest, files, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
        second.Written.Should().Be(0,
            $"an unchanged re-install must be a no-op; wrote: [{string.Join(", ", second.WrittenPaths)}]");
    }

    private static readonly IReadOnlyList<RepoFile> Repo2 = new List<RepoFile>
    {
        new("Shop2/index.json",
            """{"$type":"MeshNode","id":"Shop2","namespace":"","path":"Shop2","mainNode":"Shop2","name":"Shop2","nodeType":"Shop2/Front2","state":"Active","content":{"$type":"Front2Content","intro":"hello"}}"""),
        new("Shop2/Front2.json",
            """{"$type":"MeshNode","id":"Front2","namespace":"Shop2","path":"Shop2/Front2","mainNode":"Shop2/Front2","name":"Front2","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The shop front.","configuration":"config => config.WithContentType<Front2Content>()","includeGlobalTypes":true}}"""),
        new("Shop2/Front2/Source/Front2Content.cs",
            "public record Front2Content { public string? Intro { get; init; } }"),
    };
}
