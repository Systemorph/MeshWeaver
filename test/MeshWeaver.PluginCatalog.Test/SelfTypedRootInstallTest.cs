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
using MeshWeaver.Messaging;
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
}
