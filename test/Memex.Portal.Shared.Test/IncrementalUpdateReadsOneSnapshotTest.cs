using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6718 — an incremental update reads its manifest and its delta from ONE snapshot
/// (policy <c>record-served-commit-on-install</c>).
///
/// <para><b>The defect.</b> The incremental path reads twice: <c>manifest.lock</c> at the moving
/// ref, then the changed files at the moving ref AGAIN. When the ref advances from commit A to B
/// between the two reads, the record pairs A's module version and file map with B's files and B's
/// <c>installedFromRef</c>. The fixture's ref advances exactly there, deterministically: the first
/// read of <c>main</c> during the update resolves to A, every later one to B.</para>
///
/// <para>Two sources, one fact apart: one honours a commit it is asked for (a git source, a
/// registry that honours the snapshot token), one ignores it and serves its current tip (a
/// registry that predates the token). The first must land A entirely; the second must refuse the
/// mixed read and install B entirely, from one read. On the code before the fix both record a
/// commit whose content they do not hold.</para>
/// </summary>
public class IncrementalUpdateReadsOneSnapshotTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "OneSnapshotPkg";
    private const string OtherPath = $"{Package}/Other";

    private static readonly string Installed = new('0', 40);
    private static readonly string CommitA = new('a', 40);
    private static readonly string CommitB = new('b', 40);

    private const string VersionInstalled = "v0000000000000000";
    private const string VersionA = "va000000000000000";
    private const string VersionB = "vb000000000000000";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    /// <summary>
    /// The source honours the commit: the delta is asked for AT the manifest's commit A, so the
    /// update lands A's files under A's manifest and states A — although <c>main</c> already
    /// resolves to B by the time the delta is read.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheDelta_IsReadAtTheManifestsCommit_ThoughTheRefAdvanced()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new AdvancingSource(honoursCommit: true);
        await InstallInstalledVersion(source, ct);

        source.Advance();
        await Update(source, VersionA, ct);

        var record = await Record();
        record.ModuleVersion.Should().Be(VersionA, "the manifest was read at A");
        record.InstalledFromRef.Should().Be(CommitA,
            "THE assertion: the record names the commit its manifest was read at. Before the fix "
            + "the delta re-resolved 'main' and the record named B over A's manifest");
        (await OtherContent()).Should().Contain("Generation A",
            "and the changed file is A's, not the B the moving ref pointed at by then");
        source.Requested.Should().Equal(["main", "main", CommitA],
            "the first install reads main, the update's manifest read reads main, and its delta "
            + "read asks for the manifest's commit — never 'main' a second time");
    }

    /// <summary>
    /// The source IGNORES the commit and serves its tip (B): the mixed read is refused, and the
    /// update falls back to a full install, which reads manifest and files at once — so the record
    /// is B throughout, its module version and its commit agreeing.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeltaServedAtAnotherCommit_IsRefused_AndTheUpdateInstallsOneSnapshotInFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new AdvancingSource(honoursCommit: false);
        await InstallInstalledVersion(source, ct);

        source.Advance();
        await Update(source, VersionA, ct);

        var record = await Record();
        record.InstalledFromRef.Should().Be(CommitB, "the full install read B");
        record.ModuleVersion.Should().Be(VersionB,
            "THE assertion: module version and commit come from ONE read. Before the fix the record "
            + "carried A's module version over B's commit");
        (await OtherContent()).Should().Contain("Generation B");
        source.Requested.Should().Equal(["main", "main", CommitA, "main"],
            "the delta was asked for at A, served at B and refused; the fall-back reads main once");
    }

    /// <summary>
    /// A manifest read that names NO commit (an old registry mid-rollout) cannot pin the delta,
    /// so the delta reads the moving ref — and if THAT read names a commit, the record must not
    /// state it: nothing ties it to the manifest, which may have come from an earlier tip. The
    /// record keeps the requested ref. Before the fix it stated B over A's manifest.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUnnamedManifestRead_NeverStampsTheDeltasCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        // Read 0 = the first install (names its commit), read 1 = the update's manifest read
        // (names NONE), read 2 = the delta (names B).
        var source = new AdvancingSource(honoursCommit: true, unnamedRead: 1);
        await InstallInstalledVersion(source, ct);

        source.Advance();
        await Update(source, VersionA, ct);

        var record = await Record();
        record.InstalledFromRef.Should().Be("main",
            "THE assertion: the manifest read named no commit, so the delta's commit B proves "
            + "nothing about the manifest (A) and the record must not claim it");
        source.Requested.Should().Equal(["main", "main", "main"],
            "with no snapshot the delta reads the moving ref, as before #6718");
    }

    // ── The registry's half: the snapshot token ─────────────────────────────────────────────────

    /// <summary>A full commit sha reachable from the configured ref is read; the configured ref
    /// is read for everything else.</summary>
    [Fact]
    public async Task TheRegistry_ReadsAReachableCommit_AndItsConfiguredRefOtherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = new ConfiguredPackageSource(new AncestrySource(reachable: true), "main", "Plugins");
        var unpublished = new ConfiguredPackageSource(new AncestrySource(reachable: false), "main", "Plugins");

        (await PluginRegistryEndpoints.ReadRef(published, CommitA).Await(ct)).Should().Be(CommitA,
            "a commit published on the configured ref is the snapshot the consumer asked for");
        (await PluginRegistryEndpoints.ReadRef(unpublished, CommitA).Await(ct)).Should().Be("main",
            "a commit NOT reachable from the configured ref (an unmerged branch) is never read");
        (await PluginRegistryEndpoints.ReadRef(published, "feature/x").Await(ct)).Should().Be("main",
            "a branch name is no snapshot token — the registry serves its own ref, as before");
        (await PluginRegistryEndpoints.ReadRef(published, null).Await(ct)).Should().Be("main");
    }

    /// <summary>NEGATIVE CONTROL — a source that cannot establish ancestry (the interface default)
    /// never honours a token, so a registry over it behaves exactly as before.</summary>
    [Fact]
    public async Task ASourceThatCannotEstablishAncestry_NeverHonoursAToken()
    {
        var source = new ConfiguredPackageSource(new AdvancingSource(honoursCommit: true), "main", "Plugins");

        (await PluginRegistryEndpoints.ReadRef(source, CommitA).Await(TestContext.Current.CancellationToken))
            .Should().Be("main");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<IncrementalUpdateReadsOneSnapshotTest>();

    private async Task InstallInstalledVersion(AdvancingSource source, CancellationToken ct)
    {
        await Update(source, VersionInstalled, ct);
        var record = await Record();
        record.InstalledFromRef.Should().Be(Installed,
            "the first install is the baseline the update diffs against");
        record.InstalledFiles.Should().ContainKey($"{Package}/Other.md",
            "without a file map the update is a full install and the incremental path is not tested");
    }

    private Task Update(IPackageSource source, string candidateVersion, CancellationToken ct) =>
        CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "main", Candidate(candidateVersion), Logger)
            .Timeout(TimeSpan.FromSeconds(180))
            .Await(ct);

    private async Task<PackageManifest> Record()
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{Package}", Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
        var record = node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
        record.Should().NotBeNull("the installer stamps an install record");
        return record!;
    }

    private async Task<string> OtherContent()
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(OtherPath, Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
        node.Should().NotBeNull($"'{OtherPath}' is installed by every version");
        return JsonSerializer.Serialize(node!.Content, Mesh.JsonSerializerOptions);
    }

    private static PackageManifest Candidate(string moduleVersion) => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
        ModuleVersion = moduleVersion,
    };

    private static ImmutableList<PackageFile> Tree(string moduleVersion, string otherHash, string generation) =>
    [
        new PackageFile($"{Package}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{Package}}",
              "moduleVersion": "{{moduleVersion}}",
              "version": "1.0.0",
              "files": {
                "{{Package}}/index.json": "aaa",
                "{{Package}}/Other.md": "{{otherHash}}"
              }
            }
            """),
        new PackageFile($"{Package}/index.json", $$"""
            { "id": "{{Package}}", "path": "{{Package}}", "nodeType": "Space", "name": "{{Package}}", "state": "Active" }
            """),
        new PackageFile($"{Package}/Other.md", $"# Other\n\nGeneration {generation}."),
    ];

    /// <summary>
    /// A repository whose <c>main</c> ADVANCES: before <see cref="Advance"/> it resolves to the
    /// installed commit; after it, the FIRST read of <c>main</c> resolves to A and every later one
    /// to B — the race, made deterministic. A commit asked for by sha is served at that commit
    /// when <c>honoursCommit</c>, and at the current tip otherwise (a registry that ignores the
    /// requested ref).
    /// </summary>
    private sealed class AdvancingSource(bool honoursCommit, int unnamedRead = -1) : IPackageSource
    {
        private static readonly ImmutableDictionary<string, ImmutableList<PackageFile>> Trees =
            ImmutableDictionary<string, ImmutableList<PackageFile>>.Empty
                .Add(Installed, Tree(VersionInstalled, "h0", "zero"))
                .Add(CommitA, Tree(VersionA, "hA", "A"))
                .Add(CommitB, Tree(VersionB, "hB", "B"));

        private ImmutableQueue<string> tips = ImmutableQueue.Create(Installed);
        private ImmutableList<string> requested = [];

        public ImmutableList<string> Requested => requested;

        public void Advance() => tips = ImmutableQueue.Create(CommitA, CommitB);

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([Candidate(VersionA)]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            FetchPackage(package, gitRef, null).Select(f => (IReadOnlyList<PackageFile>)f.Files);

        public IObservable<PackageFetch> FetchPackage(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
            Observable.Defer(() =>
            {
                var index = requested.Count;
                ImmutableInterlocked.Update(ref requested, r => r.Add(gitRef));
                var commit = honoursCommit && Trees.ContainsKey(gitRef) ? gitRef : Tip();
                var files = Trees[commit];
                return Observable.Return(new PackageFetch(
                    paths is null ? files : files.Where(f => paths.Contains(f.RelativePath)).ToImmutableList(),
                    index == unnamedRead ? null : commit));
            });

        /// <summary>The commit <c>main</c> resolves to on this read; the last tip stays.</summary>
        private string Tip()
        {
            var tip = tips.Peek();
            var rest = tips.Dequeue();
            if (!rest.IsEmpty)
                tips = rest;
            return tip;
        }
    }

    /// <summary>A source whose ancestry answer is fixed — the registry's ref choice is about the
    /// answer, never the transport.</summary>
    private sealed class AncestrySource(bool reachable) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return<IReadOnlyList<PackageFile>>([]);

        public IObservable<bool> PublishesCommit(string commit, string gitRef) => Observable.Return(reachable);
    }
}
