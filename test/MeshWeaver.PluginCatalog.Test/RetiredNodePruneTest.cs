#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Systemorph/MeshWeaver#2473 — a node the source repo RETIRES must not survive in the mesh.
///
/// <para>The manifest-diff incremental update refuses to touch a package whose SHARED
/// <c>Source/</c>/<c>Test/</c> changed (the blast radius is every type in the package) and falls
/// back to a full install (<c>CatalogLayoutAreas.IncrementalUpdate</c>, "changed shared Source/Test
/// files; full install required"). Before #2473's fix, the full install path
/// (<c>PackageInstaller.InstallNodeRepo</c>) upserted only the nodes the package still ships and
/// pruned NOTHING, so a NodeType the repo deleted — <c>Thing</c>, whose <c>index.json</c> and
/// <c>Source/*.cs</c> both left the repo in the same commit that also touched the package's shared
/// <c>Source/</c> — kept serving its last-built assembly forever. It only detonated on the NEXT
/// framework-identity flip, which recompiles every dynamic NodeType: with zero sources left to
/// compile against, it fails, parks at <c>CompileError</c>, and holds every instance hub for the
/// full 60 s activation budget (see NodeTypeCompilation.md).</para>
///
/// <para>🚨 A live orphan of exactly this shape was found on memex-cloud (<c>LinkedIn/Skill</c>) with
/// its ENTIRE <c>Release/*</c> compile-history subtree still attached under the retired type's path —
/// so the second thing this test pins is that the prune removes the SUBTREE, not just the retired
/// node itself. It does, for free: <c>PruneRemovedNodes</c> prunes via
/// <c>IMeshService.DeleteNode</c>, which <c>MeshService.DeleteNode</c> always issues with
/// <c>Recursive = true</c>; the recursive delete's path collection
/// (<c>IStorageAdapter.ListDescendantPaths</c>) is a prefix scan under the deleted root regardless of
/// which physical table a descendant lives in (the production Postgres adapter UNIONs
/// <c>mesh_nodes</c> with every satellite table). A compile's <c>Release/{version}</c> history node
/// is never routed to a satellite table at all (<c>PartitionDefinition.StandardTableMappings</c>
/// never routed it — see Memex.Database.Migration's <c>V19_DeleteLegacyReleaseNodes</c>), so it is an
/// ordinary <c>mesh_nodes</c> row whose namespace is prefixed by the type's path — squarely inside
/// the deleted subtree. <c>ReleaseHistorySurvivesUnderThePrunedType</c> below plants exactly such a
/// node directly (bypassing the package installer, the way a live compile would create one) and
/// pins it disappears along with its owning type.</para>
/// </summary>
public class RetiredNodePruneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private const string V1Module = "bbbbbbbbbbbbbbb1";
    private const string V2Module = "bbbbbbbbbbbbbbb2";

    // v1: a root, a NodeType (Thing) with its own Source, and the package's SHARED Source/ (code
    // every type in the package may reference — not nested under any one type).
    private static readonly IReadOnlyList<PackageFile> V1Files =
    [
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Widget/Thing.json",
            """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>()","includeGlobalTypes":true}}"""),
        new("Widget/Thing/Source/Thing.cs",
            "public record Thing { public string Name { get; init; } = string.Empty; }"),
        new("Widget/Source/Shared.cs",
            "public static class SharedV1 { public const string Value = \"v1\"; }"),
        new("Widget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Widget","moduleVersion":"{{{V1Module}}}","sourceCommit":"c1","files":{"Widget/index.json":"h-root-1","Widget/Thing.json":"h-type-1","Widget/Thing/Source/Thing.cs":"h-src-1","Widget/Source/Shared.cs":"h-shared-1"}}"""),
    ];

    // v2: the repo RETIRES Thing (its NodeType node AND its Source both drop out) in the same
    // commit that also touches the package's SHARED Source/ — the exact combination that forces
    // the incremental path to refuse and fall back to a full install.
    private static readonly IReadOnlyList<PackageFile> V2Files =
    [
        V1Files[0],
        new("Widget/Source/Shared.cs",
            "public static class SharedV1 { public const string Value = \"v2\"; }"),
        new("Widget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Widget","moduleVersion":"{{{V2Module}}}","sourceCommit":"c2","files":{"Widget/index.json":"h-root-1","Widget/Source/Shared.cs":"h-shared-2"}}"""),
    ];

    /// <summary>A package source that records every fetch (full or subset) so the test can pin
    /// exactly which install path ran — mirrors IncrementalUpdateTest's.</summary>
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

    private static PackageManifest Pkg(string moduleVersion, string version) => new()
    {
        Id = "Widget",
        Name = "Widget Plugin",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Widget",
        SourceFolder = "Widget",
        Version = version,
        ModuleVersion = moduleVersion,
    };

    [Fact(Timeout = 120_000)]
    public async Task SharedSourceChange_FallsBackToFullInstall_AndPrunesTheRetiredNodeType()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var options = Mesh.JsonSerializerOptions;

        // ── v1: full install stamps the manifest baseline, including the now-doomed Thing ──
        var v1 = await PackageInstaller.Install(Mesh, Pkg(V1Module, "commit-1"), V1Files, "commit-1")
            .FirstAsync().ToTask();
        v1.Written.Should().Be(4, "4 nodes: root, Thing, Thing/Source/Thing, Source/Shared");

        var nt = await Mesh.GetMeshNodeStream("Widget/Thing")
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        ((NodeTypeDefinition)nt.Content!).CompilationStatus.Should().Be(CompilationStatus.Ok);

        (await persistence.Exists("Widget/Thing").FirstAsync().ToTask()).Should().BeTrue();
        (await persistence.Exists("Widget/Thing/Source/Thing").FirstAsync().ToTask()).Should().BeTrue();

        // ── v2: the repo retires Thing AND touches the shared Source/ in the same update ──
        var v2Source = new RecordingSource(V2Files);
        var v2 = await CatalogLayoutAreas.InstallOrUpdate(Mesh, v2Source, "commit-2", Pkg(V2Module, "commit-2"), null)
            .FirstAsync().ToTask();

        // Pin that this really is the full-install FALLBACK, not the ordinary delta path: the
        // incremental attempt fetches the manifest first, discovers the shared-Source conflict and
        // throws, and the catch falls back to a FULL fetch (paths: null).
        v2Source.Fetches.Should().Contain(paths => paths is null,
            "the shared Source/ change must force a full re-fetch, not a manifest-diff delta");

        v2.Written.Should().BeGreaterThan(0, "the root's re-publish and Source/Shared's new content are both writes");

        // The RED PROOF: before #2473's fix, InstallNodeRepo's full path pruned nothing, so the
        // retired NodeType and its Source node survived the update forever.
        await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => persistence.Exists("Widget/Thing"))
            .Where(exists => !exists)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => persistence.Exists("Widget/Thing/Source/Thing"))
            .Where(exists => !exists)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        // The record's baseline no longer names the retired files either — the NEXT update's diff
        // must not think Thing is still installed.
        var record = await ReadRecord(persistence, options);
        record.ModuleVersion.Should().Be(V2Module);
        record.InstalledFiles.Should().NotBeNull();
        record.InstalledFiles!.Should().NotContainKey("Widget/Thing.json");
        record.InstalledFiles!.Should().NotContainKey("Widget/Thing/Source/Thing.cs");

        // The surviving root is untouched in content — only Thing left, nothing else broke.
        (await persistence.Exists("Widget").FirstAsync().ToTask()).Should().BeTrue();
    }

    [Fact(Timeout = 120_000)]
    public async Task ReleaseHistorySurvivesUnderThePrunedType_ThenIsPrunedWithIt()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // ── v1: install, then plant a Release-history child the way a live compile does —
        // NEVER through the package installer (compile history is never shipped by a repo; it is
        // written directly by MeshDataSource.TryCreateReleaseNode against the type's own path). ──
        await PackageInstaller.Install(Mesh, Pkg(V1Module, "commit-1"), V1Files, "commit-1")
            .FirstAsync().ToTask();
        await Mesh.GetMeshNodeStream("Widget/Thing")
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        var release = MeshNode.FromPath("Widget/Thing/Release/v1-abc123") with
        {
            NodeType = "Markdown",
            Name = "Release v1",
            State = MeshNodeState.Active,
            Content = "# compiled at v1",
        };
        await meshService.CreateNode(release).FirstAsync().ToTask();
        (await persistence.Exists("Widget/Thing/Release/v1-abc123").FirstAsync().ToTask()).Should().BeTrue(
            "the planted release-history node must actually be there before the prune runs");

        // ── v2: the repo retires Thing (and its compile history is never part of any manifest —
        // the diff never names it, so ONLY a recursive delete of Thing's subtree removes it) ──
        var v2Source = new RecordingSource(V2Files);
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, v2Source, "commit-2", Pkg(V2Module, "commit-2"), null)
            .FirstAsync().ToTask();

        // The RED-adjacent proof for the coordinator's concern: pruning Thing must take its
        // Release history with it, not leave it orphaned under a now-nonexistent parent.
        await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => persistence.Exists("Widget/Thing"))
            .Where(exists => !exists)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        (await persistence.Exists("Widget/Thing/Release/v1-abc123").FirstAsync().ToTask()).Should().BeFalse(
            "the retired type's Release history must be pruned WITH it (recursive delete), " +
            "not left orphaned under a path nothing owns any more");
    }

    private static async Task<PackageManifest> ReadRecord(
        IStorageAdapter persistence, System.Text.Json.JsonSerializerOptions options)
    {
        var node = await persistence.Read($"{PackageInstaller.InstalledPartition}/Widget", options)
            .FirstAsync().ToTask();
        node.Should().NotBeNull();
        var manifest = node!.ContentAs<PackageManifest>(options);
        manifest.Should().NotBeNull();
        return manifest!;
    }
}
