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
/// The manifest-diff incremental update: a package shipping a CI <c>manifest.lock</c> installs with
/// the manifest baseline on its record; a later update with a differing module version fetches ONLY
/// the manifest + the changed files, upserts only those nodes (untouched nodes keep their exact
/// Version/LastModified — no churn, no re-broadcast, no recompile), prunes removed nodes, and
/// re-stamps the record. An update at the SAME module version fetches nothing at all.
/// </summary>
public class IncrementalUpdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    private const string V1Module = "aaaaaaaaaaaaaaa1";
    private const string V2Module = "aaaaaaaaaaaaaaa2";

    private static readonly IReadOnlyList<PackageFile> V1Files =
    [
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin.","minMeshVersion":"1.0.0"}}"""),
        new("Widget/Thing.json",
            """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>()","includeGlobalTypes":true}}"""),
        new("Widget/Thing/Source/Thing.cs",
            "public record Thing { public string Name { get; init; } = string.Empty; }"),
        new("Widget/Notes.md", "# Notes v1"),
        new("Widget/Extra.md", "# Extra — removed in v2"),
        new("Widget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Widget","moduleVersion":"{{{V1Module}}}","sourceCommit":"c1","files":{"Widget/index.json":"h-root-1","Widget/Thing.json":"h-type-1","Widget/Thing/Source/Thing.cs":"h-src-1","Widget/Notes.md":"h-notes-1","Widget/Extra.md":"h-extra-1"}}"""),
    ];

    // v2: Notes.md changed, Extra.md removed; root/type/source byte-identical.
    private static readonly IReadOnlyList<PackageFile> V2Files =
    [
        V1Files[0], V1Files[1], V1Files[2],
        new("Widget/Notes.md", "# Notes v2 — updated"),
        new("Widget/manifest.lock",
            $$$"""{"schema":"mw-manifest/1","module":"Widget","moduleVersion":"{{{V2Module}}}","sourceCommit":"c2","files":{"Widget/index.json":"h-root-1","Widget/Thing.json":"h-type-1","Widget/Thing/Source/Thing.cs":"h-src-1","Widget/Notes.md":"h-notes-2"}}"""),
    ];

    /// <summary>A package source that records every fetch (full or subset) so the test can pin
    /// exactly what traveled.</summary>
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
    public async Task ManifestDiffUpdatesOnlyChangedNodes_PrunesRemoved_AndSkipsWhenUpToDate()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var options = Mesh.JsonSerializerOptions;

        // ── Listing surfaces the module version from manifest.lock ──
        var listSource = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-1",
                V1Files.Select(f => new RepoFile(f.RelativePath, f.Content)).ToList())),
            "https://github.com/acme/plugins");
        var listed = await listSource.ListPackages("HEAD").FirstAsync().ToTask();
        listed.Should().ContainSingle();
        listed[0].ModuleVersion.Should().Be(V1Module, "the catalog entry carries the manifest's module version");
        listed[0].Version.Should().Be("commit-1", "the commit sha stays for display/traceability");

        // ── Full install of v1 stamps the manifest baseline on the install record ──
        var v1 = await PackageInstaller.Install(Mesh, Pkg(V1Module, "commit-1"), V1Files, "commit-1")
            .FirstAsync().ToTask();
        v1.Written.Should().Be(5, "5 nodes (manifest.lock is no node)");

        var record = await ReadRecord(persistence, options);
        record.ModuleVersion.Should().Be(V1Module);
        record.InstalledFiles.Should().NotBeNull().And.HaveCount(5);
        record.InstalledFiles!["Widget/Notes.md"].Should().Be("h-notes-1");

        // Let the NodeType's live compile settle so its enrichment writes can't race the
        // version snapshot below.
        var nt = await Mesh.GetMeshNodeStream("Widget/Thing")
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        ((NodeTypeDefinition)nt.Content!).CompilationStatus.Should().Be(CompilationStatus.Ok);

        // The compile's enrichment writes persist through the DEBOUNCED pipeline and trail the
        // stream's Ok — snapshot only once each node's persisted Version has quiesced, or the
        // enrichment landing mid-delta would masquerade as churn.
        var rootBefore = await ReadStable(persistence, options, "Widget");
        var typeBefore = await ReadStable(persistence, options, "Widget/Thing");
        var srcBefore = await ReadStable(persistence, options, "Widget/Thing/Source/Thing");

        // ── Incremental update to v2: only manifest.lock + the changed file travel ──
        var v2Source = new RecordingSource(V2Files);
        var v2 = await CatalogLayoutAreas.InstallOrUpdate(Mesh, v2Source, "commit-2", Pkg(V2Module, "commit-2"), null)
            .FirstAsync().ToTask();
        v2.Written.Should().Be(1, "only Notes.md changed");

        v2Source.Fetches.Should().HaveCount(2, "one manifest fetch + one changed-files fetch — never a full fetch");
        v2Source.Fetches[0].Should().Equal("Widget/manifest.lock");
        v2Source.Fetches[1].Should().Equal("Widget/Notes.md");

        // The changed node updated; the removed node pruned (poll — the delete's persistence can
        // trail the delta's completion through the debounced pipeline).
        (await Read(persistence, options, "Widget/Notes")).Should().NotBeNull();
        await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => persistence.Exists("Widget/Extra"))
            .Where(exists => !exists)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        // Untouched nodes: EXACT same Version + LastModified — no churn, no history row, no
        // re-broadcast, and (for the NodeType) no recompile.
        var rootAfter = await Read(persistence, options, "Widget");
        var typeAfter = await Read(persistence, options, "Widget/Thing");
        var srcAfter = await Read(persistence, options, "Widget/Thing/Source/Thing");
        rootAfter!.Version.Should().Be(rootBefore!.Version);
        rootAfter.LastModified.Should().Be(rootBefore.LastModified);
        typeAfter!.Version.Should().Be(typeBefore!.Version);
        typeAfter.LastModified.Should().Be(typeBefore.LastModified);
        srcAfter!.Version.Should().Be(srcBefore!.Version);
        srcAfter.LastModified.Should().Be(srcBefore.LastModified);

        // The record now carries the v2 baseline.
        var recordAfter = await ReadRecord(persistence, options);
        recordAfter.ModuleVersion.Should().Be(V2Module);
        recordAfter.InstalledFiles.Should().HaveCount(4);
        recordAfter.InstalledFiles!.Should().NotContainKey("Widget/Extra.md");

        // ── Same module version again: nothing fetched, nothing written, record untouched ──
        var skipSource = new RecordingSource(V2Files);
        var skip = await CatalogLayoutAreas.InstallOrUpdate(Mesh, skipSource, "commit-3", Pkg(V2Module, "commit-3"), null)
            .FirstAsync().ToTask();
        skip.Written.Should().Be(0);
        skipSource.Fetches.Should().BeEmpty("an up-to-date module fetches not a single file");
    }

    private static async Task<PackageManifest> ReadRecord(
        IStorageAdapter persistence, System.Text.Json.JsonSerializerOptions options)
    {
        var node = await Read(persistence, options, $"{PackageInstaller.InstalledPartition}/Widget");
        node.Should().NotBeNull();
        var manifest = node!.ContentAs<PackageManifest>(options);
        manifest.Should().NotBeNull();
        return manifest!;
    }

    private static async Task<MeshNode?> Read(
        IStorageAdapter persistence, System.Text.Json.JsonSerializerOptions options, string path) =>
        await persistence.Read(path, options).FirstAsync().ToTask();

    // Reads until the persisted Version is unchanged across 4 consecutive samples (~1.2s quiet) —
    // the debounced enrichment persists have settled by then.
    private static async Task<MeshNode> ReadStable(
        IStorageAdapter persistence, System.Text.Json.JsonSerializerOptions options, string path)
    {
        MeshNode? last = null;
        var stable = 0;
        for (var i = 0; i < 100 && stable < 4; i++)
        {
            var current = await Read(persistence, options, path);
            stable = current is not null && last is not null && current.Version == last.Version
                ? stable + 1
                : 0;
            last = current ?? last;
            await Task.Delay(300);
        }
        last.Should().NotBeNull($"node {path} must be persisted");
        return last!;
    }
}
