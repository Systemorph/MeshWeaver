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
/// Pins the FRESH-MESH install ordering for the two node-repo shapes the plugins repo ships
/// that used to be refused "NodeType … is not registered":
/// <list type="number">
///   <item>A typed INSTANCE nested under its leaf-shaped type's path
///     (<c>ClaimsDeepfield/Cedent.json</c> + <c>ClaimsDeepfield/Cedent/NSV.json</c>) — the old
///     ordering classified everything under a type path as the type's "Source/Test/docs" and
///     wrote the instance BEFORE the type node.</item>
///   <item>A package ROOT whose CONTENT is a <c>NodeTypeDefinition</c> on a Space root
///     (UWDeepfield) — the root's path then prefixes the whole package, pulling every instance
///     into the before-the-types bucket.</item>
/// </list>
/// On a portal these only surfaced when the type had never been installed before — a fresh
/// mesh (the CI gate, a new deployment) hits them deterministically.
/// </summary>
public class NodeRepoInstanceOrderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    // Shape 1 + 2 combined: the root carries NodeTypeDefinition content (UWDeepfield quirk),
    // the type is leaf-shaped (Type.json + Type/Source), and one instance lives under the type
    // path while another lives beside it.
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Pack/index.json",
            """{"$type":"MeshNode","id":"Pack","namespace":"","path":"Pack","mainNode":"Pack","name":"Pack","nodeType":"Space","state":"Active","content":{"$type":"NodeTypeDefinition","description":"a Space root carrying NodeTypeDefinition content (the UWDeepfield shape)"}}"""),
        new("Pack/Widget.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"Pack","path":"Pack/Widget","mainNode":"Pack/Widget","name":"Widget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"the type"}}"""),
        new("Pack/Widget/Source/Widget.cs",
            "public record Widget { public string Name { get; init; } = string.Empty; }"),
        new("Pack/Widget/Nested.json",
            """{"$type":"MeshNode","id":"Nested","namespace":"Pack/Widget","path":"Pack/Widget/Nested","mainNode":"Pack/Widget/Nested","name":"Nested","nodeType":"Pack/Widget","state":"Active"}"""),
        new("Pack/Sibling.json",
            """{"$type":"MeshNode","id":"Sibling","namespace":"Pack","path":"Pack/Sibling","mainNode":"Pack/Sibling","name":"Sibling","nodeType":"Pack/Widget","state":"Active"}"""),
    };

    [Fact(Timeout = 120_000)]
    public async Task TypedInstances_UnderAndBesideTheirInPackageType_InstallOnFreshMesh()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-order", Repo));
        var source = new NodeRepoPackageSource(fetch, "https://github.com/acme/pack");
        var manifest = new PackageManifest
        {
            Id = "Pack",
            Name = "Pack",
            Kind = PackageKind.NodeRepo,
            TargetPartition = "Pack",
            SourceFolder = "Pack",
            Version = "commit-order",
        };
        var files = await source.FetchPackageFiles(manifest, "HEAD").FirstAsync().ToTask();
        files.Count.Should().Be(5);

        var result = await PackageInstaller.Install(Mesh, manifest, files, "commit-order")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(90)).ToTask();
        result.Written.Should().Be(5,
            "every node must land — the type before its instances, on a mesh that never saw the type");

        // Both instances landed TYPED — proof the type node was persistence-visible first.
        (await Read("Pack/Widget/Nested")).NodeType.Should().Be("Pack/Widget");
        (await Read("Pack/Sibling")).NodeType.Should().Be("Pack/Widget");
        (await Read("Pack")).NodeType.Should().Be("Space");
    }

    private async Task<MeshNode> Read(string path) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).Select(n => n!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
}
