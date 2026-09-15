using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>ONE PARTITION, ONE BOOKKEEPING</b> — Systemorph/MeshWeaver#4355.
///
/// <para><b>The defect.</b> A partition can be BOTH a sync-governed space — a
/// <c>{partition}/_GitSync</c> whose imports are held to the commit sealed for the running framework
/// identity — AND the target of a registry-installed package with <c>autoUpdate: true</c>. The two
/// writers keep SEPARATE books, and neither can see the other's. The seal reconciler rewrites the
/// partition from git and leaves the install record untouched; the registry lane then computes its
/// next delta from that record, which has stopped describing the mesh, and writes only the files
/// whose hash moved SINCE THE RECORD — leaving every file it believes unchanged at the git tree's
/// content. The partition ends up a MIX of two commits that no CI ever compiled.</para>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-14.</b> <c>Store/_GitSync</c> held at the
/// sealed commit <c>627fb3cd</c> (Store 1.10.3); at 14:23Z a boot sweep's declined adoptions drove
/// <c>ReconcileAtProvenCommitFromGitHub</c> to re-import the whole partition at it while
/// <c>Plugins/Store</c> still said 1.10.14; at 20:25Z the registry served 1.11.1 and the delta
/// against that record found <c>Store/Core/Source/StoreTexts.cs</c> byte-identical between 1.10.14
/// and 1.11.1, declared it unchanged and never wrote it — while five sibling types WERE written at
/// 1.11.1. <c>Store/Catalog</c> recompiled against 1.10.3's texts and PARKED with
/// <c>CS1061 'StoreTexts' does not contain a definition for 'ExploreCta'</c>.</para>
///
/// <para><b>The invariant pinned here.</b> A partition has ONE content bookkeeping. Either the
/// installer owns the content — its record IS the description of the mesh, an unattended apply may
/// write and a delta may be diffed against it — or a sync source does, in which case the installer
/// never silently becomes the second writer and never diffs against a baseline it does not own.
/// The two halves are measured separately because they fail separately:</para>
/// <list type="number">
///   <item><see cref="TheUnattendedApply_HoldsOnASyncOwnedPartition_AndLandsOnTheOneItOwns"/> — the
///   registry auto-update must not write into a synced partition at all (which is also what stops
///   it landing sources this instance has no proven bundle for), and must SAY so rather than skip
///   in silence.</item>
///   <item><see cref="AnUpdate_NeverDiffsAgainstABaselineItDoesNotOwn"/> — every other lane that
///   still writes such a partition by design (the seal-pinned boot install, a human's Update click)
///   must install in FULL, because its delta baseline is a claim about a mesh a second writer
///   maintains. This is the arm that reproduces the MIX itself.</item>
/// </list>
///
/// <para>Both arms carry their opposite as a control in the same mesh, differing in exactly one
/// fact — whether the target partition carries a <c>_GitSync</c> naming a repository. A test that
/// only asserted the hold would pass just as well against a lane that had stopped updating
/// anything.</para>
/// </summary>
public class OnePartitionOneBookkeepingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The mesh a deployment of this fleet actually runs: the catalog (so an install records
    /// itself), GitHub sync (so <see cref="IPartitionSourceTracking"/> is answered by the REAL
    /// provider over a REAL config node rather than a stand-in — the seam the gate depends on is
    /// part of what is under test) and <c>AutoUpdateByDefault</c>, which is what stamps an install
    /// record with <see cref="PackageUpdatePolicy.Auto"/> here as on every portal.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton(new PluginCatalogOptions { AutoUpdateByDefault = true });
                return services;
            });

    private const string V1 = "1111111111111111";
    private const string V2 = "2222222222222222";

    // ── Half 1: the unattended apply ────────────────────────────────────────────────────────────
    private const string SyncOwned = "SyncOwnedPkg";
    private const string InstallerOwned = "InstallerOwnedPkg";

    // ── Half 2: the delta baseline ──────────────────────────────────────────────────────────────
    private const string MixSynced = "MixSyncedPkg";
    private const string MixOwned = "MixOwnedPkg";

    /// <summary>The name a second writer puts on a node the installer believes it owns — the
    /// content DRIFT that #4259's presence check cannot see, because the node is still there.</summary>
    private const string DriftedName = "rewritten by the other writer";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<OnePartitionOneBookkeepingTest>();

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Half 1 — the unattended apply is not the second writer
    // ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>THE REGRESSION, half one.</b> Two installed packages that differ in exactly one fact:
    /// one's target partition carries a <c>_GitSync</c>, the other's does not. The registry serves a
    /// newer content identity for both, and both records are on the unattended policy.
    ///
    /// <para><b>Fails on main.</b> There the apply is decided by the package's update policy alone,
    /// so BOTH packages are fetched and written — the sync-owned partition included, at a content
    /// identity its sync source will revert at the next seal reconcile, and against a record its own
    /// content no longer matches. The witness is the source's request log: which package's files
    /// were asked for is the one thing this lane cannot fake.</para>
    ///
    /// <para>The installer-owned package is the control in both directions: it proves the pass
    /// really ran (a green result cannot come from a reconcile that did nothing) and that the gate
    /// discriminates rather than switching the whole lane off.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheUnattendedApply_HoldsOnASyncOwnedPartition_AndLandsOnTheOneItOwns()
    {
        // ── The starting state: both packages installed at V1, by the installer, for real. ──────
        var first = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [SyncOwned] = Files(SyncOwned, V1, "ccc", "# Other\n\nGeneration one."),
            [InstallerOwned] = Files(InstallerOwned, V1, "ccc", "# Other\n\nGeneration one."),
        });
        await Install(first, SyncOwned, V1);
        await Install(first, InstallerOwned, V1);

        (await Record(SyncOwned))!.EffectiveUpdatePolicy.Should().Be(PackageUpdatePolicy.Auto,
            "the whole premise is an UNATTENDED apply — on any other policy this lane would not "
            + "write even on main, and the assertion below would pass for the wrong reason");
        (await Record(InstallerOwned))!.EffectiveUpdatePolicy.Should().Be(PackageUpdatePolicy.Auto);

        // ── The one fact that differs: a sync source now keeps SyncOwned's partition current. ───
        await TrackPartition(SyncOwned);
        await AssertOwnership(InstallerOwned, PartitionContentOwner.Installer);

        // ── The registry serves V2 of both. This is the exact call RegistryUpdateReconciler
        //    .ReconcileFromFeed makes on a boot pass, a drained deferral and a safety-net tick. ──
        var second = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [SyncOwned] = Files(SyncOwned, V2, "ddd", "# Other\n\nGeneration TWO."),
            [InstallerOwned] = Files(InstallerOwned, V2, "ddd", "# Other\n\nGeneration TWO."),
        });
        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, second, "HEAD",
                // SyncOwned FIRST: the candidates are reconciled sequentially, so the control's
                // arrival strictly follows the decision under test and cannot race it.
                [Candidate(SyncOwned, V2), Candidate(InstallerOwned, V2)],
                "Served by registry 'test'", Logger)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the reconcile pass must complete before anything about it is asserted");

        // ── The control: the partition the installer owns advanced, content and record. ─────────
        second.Fetched.Should().Contain(r => r.PackageId == InstallerOwned,
            "the installer-owned package must still auto-update — if it did not, the assertion "
            + "below would be measuring a lane that had stopped working rather than a gate");
        (await Record(InstallerOwned))!.ModuleVersion.Should().Be(V2,
            "and its record moved forward, which is what 'the apply landed' means");

        // ── THE assertion. ──────────────────────────────────────────────────────────────────────
        second.Fetched.Should().NotContain(r => r.PackageId == SyncOwned,
            "THE assertion: an unattended apply must not fetch — let alone write — into a partition "
            + "a sync source keeps current. On main it does, which is how memex.systemorph.com's "
            + "Store partition became a mix of 1.10.3 and 1.11.1 (MeshWeaver#4355)");

        var held = await Record(SyncOwned);
        held!.ModuleVersion.Should().Be(V1,
            "the record did not move either — an apply that was held must not stamp a version it "
            + "did not install, or the next delta is computed against a second lie");
        held.NotifiedModuleVersion.Should().Be(V2,
            "and the hold is TOLD, once per candidate: an auto-update that will never land here is "
            + "exactly the quiet 'my plugin never updates' this lane must not become");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Half 2 — the delta baseline belongs to whoever owns the content
    // ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>THE REGRESSION, half two — the MIX itself.</b> Install V1; let a SECOND WRITER rewrite
    /// one node (the shape a seal reconcile's re-import has, and the one #4259's presence check
    /// cannot see: the node is still there, its CONTENT drifted); then update to V2, in which that
    /// node's own file hash has NOT moved while a sibling's has.
    ///
    /// <para><b>Fails on main.</b> The incremental path fetches exactly
    /// <c>newManifest.DiffFrom(record.InstalledFiles)</c> — two DECLARATIONS, neither of which
    /// observes the mesh — so the drifted node is never fetched, never written, and the partition is
    /// left half at the second writer's content and half at V2. That is
    /// <c>StoreTexts.cs</c> at 1.10.3 beside <c>Catalog</c> at 1.11.1, verbatim.</para>
    ///
    /// <para>The control is the same package on a partition NOTHING syncs: there the installer IS
    /// the only writer, its record does describe the mesh, and the delta stays a delta — so the
    /// drift survives, exactly as today. One fact differs between the two arms, and only one.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUpdate_NeverDiffsAgainstABaselineItDoesNotOwn()
    {
        var first = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [MixSynced] = Files(MixSynced, V1, "ccc", "# Other\n\nGeneration one."),
            [MixOwned] = Files(MixOwned, V1, "ccc", "# Other\n\nGeneration one."),
        });
        await Install(first, MixSynced, V1);
        await Install(first, MixOwned, V1);

        (await Record(MixSynced))!.InstalledFiles.Should().NotBeNull()
            .And.ContainKey($"{MixSynced}/Guide.md",
                "the record's file map IS the incremental path's baseline — with no map the "
                + "installer falls back to a full install, which repairs by accident and would "
                + "make this test vacuous");

        await TrackPartition(MixSynced);
        await AssertOwnership(MixOwned, PartitionContentOwner.Installer);

        // ── The second writer. A seal reconcile re-imports the whole partition at the sealed
        //    commit; here one node is enough, and the node stays PRESENT, which is precisely why
        //    #4259's absence repair cannot reach this.
        await Drift($"{MixSynced}/Guide");
        await Drift($"{MixOwned}/Guide");

        // ── The update: the module hash moves, Other.md's content moves, Guide.md's does NOT. ───
        var second = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [MixSynced] = Files(MixSynced, V2, "ddd", "# Other\n\nGeneration TWO."),
            [MixOwned] = Files(MixOwned, V2, "ddd", "# Other\n\nGeneration TWO."),
        });
        await Install(second, MixSynced, V2);
        await Install(second, MixOwned, V2);

        // ── THE assertion: on a partition the installer does not own, the update is a FULL
        //    install, so every declared file travels and the partition is one content identity. ──
        second.Fetched.Should().Contain(r => r.PackageId == MixSynced && r.Paths is null,
            "THE assertion: an update whose baseline describes a mesh a second writer maintains "
            + "must ask for the WHOLE package, not a delta. On main it asks for the changed file "
            + "alone (MeshWeaver#4355)");
        (await NameOf($"{MixSynced}/Guide")).Should().NotBe(DriftedName,
            "and the node whose file hash never moved is back at the package's content — the mix "
            + "is what parked Store/Catalog with CS1061");
        (await NameOf($"{MixSynced}/Other")).Should().NotBe(DriftedName,
            "the control inside the arm: the file that DID change landed too, so the full install "
            + "really ran");
        (await Record(MixSynced))!.ModuleVersion.Should().Be(V2,
            "and the record describes the partition again");

        // ── The control arm: nothing syncs MixOwned, so the delta stays a delta. ────────────────
        second.Fetched.Should().Contain(
            r => r.PackageId == MixOwned && r.Paths is not null
                 && r.Paths.Contains($"{MixOwned}/Other.md") && !r.Paths.Contains($"{MixOwned}/Guide.md"),
            "the control: where the installer IS the only writer its record does describe the mesh, "
            + "so the incremental path is kept — the gate discriminates, it does not turn the fast "
            + "path off for everyone");
        (await NameOf($"{MixOwned}/Guide")).Should().Be(DriftedName,
            "and the unfetched file's node is untouched there, which is today's behaviour and the "
            + "proof that the arm above differs by the sync source and by nothing else");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  The decision, driven offline — every arm, including the two that are not passes
    // ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 "I could not tell" is not "the installer owns it". The three-way split is the whole point:
    /// folding <see cref="PartitionContentOwner.Undetermined"/> into
    /// <see cref="PartitionContentOwner.Installer"/> would make a seam that faulted read exactly
    /// like a partition nothing syncs — and the caller would then write into a partition it has no
    /// evidence about, which is the defect one level in.
    /// </summary>
    [Fact]
    public void NotAnsweredIsNeverInstallerOwned()
    {
        PartitionContentOwnership.Decide("Store", tracked: true, providerCount: 1)
            .Owner.Should().Be(PartitionContentOwner.SyncSource);
        PartitionContentOwnership.Decide("Store", tracked: true, providerCount: 1)
            .InstallerOwnsTheContent.Should().BeFalse();

        PartitionContentOwnership.Decide("Store", tracked: false, providerCount: 1)
            .InstallerOwnsTheContent.Should().BeTrue(
                "a mesh WITH a sync layer that says this partition tracks nothing is a real "
                + "negative — the one case where the installer may write");

        PartitionContentOwnership.Decide("Store", tracked: null, providerCount: 2)
            .Owner.Should().Be(PartitionContentOwner.Undetermined,
                "a seam that faulted or did not answer inside its budget was NOT checked");
        PartitionContentOwnership.Decide("Store", tracked: null, providerCount: 2)
            .InstallerOwnsTheContent.Should().BeFalse("and that is not a pass");

        PartitionContentOwnership.Decide("Store", tracked: null, providerCount: 0)
            .InstallerOwnsTheContent.Should().BeTrue(
                "a mesh that registers NO tracking provider has no notion of a second writer — a "
                + "local mesh, a CI mesh, the bake host — and must keep the pre-#4355 behaviour "
                + "rather than hold every install on every such deployment");

        PartitionContentOwnership.Decide("Store", tracked: true, providerCount: 1)
            .Because.Should().Contain("Store",
                "every arm's reason names the partition — a hold nobody can attribute is a hold "
                + "nobody can act on");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private static PackageManifest Candidate(string id, string moduleVersion) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
        ModuleVersion = moduleVersion,
    };

    /// <summary>
    /// The package at one generation. <paramref name="otherHash"/> / <paramref name="otherBody"/>
    /// are the ONLY things that move between generations — <c>Guide.md</c>'s hash is deliberately
    /// identical in both locks, which is the premise of half two: the diff cannot name it, so only
    /// a full install reaches it.
    /// </summary>
    private static IReadOnlyList<PackageFile> Files(
        string id, string moduleVersion, string otherHash, string otherBody) =>
    [
        new PackageFile($"{id}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{id}}",
              "moduleVersion": "{{moduleVersion}}",
              "version": "1.0.0",
              "files": {
                "{{id}}/index.json": "aaa",
                "{{id}}/Guide.md": "bbb",
                "{{id}}/Other.md": "{{otherHash}}"
              }
            }
            """),
        new PackageFile($"{id}/index.json", $$"""
            {
              "id": "{{id}}",
              "path": "{{id}}",
              "nodeType": "Space",
              "name": "{{id}}",
              "state": "Active"
            }
            """),
        new PackageFile($"{id}/Guide.md", "# Guide\n\nThe node a second writer rewrites. Its hash "
            + "never moves, so the manifest diff never names it."),
        new PackageFile($"{id}/Other.md", otherBody),
    ];

    private async Task Install(IPackageSource source, string id, string moduleVersion)
    {
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Candidate(id, moduleVersion), Logger)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit($"installing {id} at {moduleVersion} is a precondition of what follows");
        (await Record(id))!.ModuleVersion.Should().Be(moduleVersion,
            "an install that did not stamp its record leaves every later measurement meaningless");
    }

    /// <summary>A real <c>{partition}/_GitSync</c> naming a repository — what the settings tab
    /// writes when a Space is connected — read back through the very decision under test, so a
    /// false negative in the arm cannot be mistaken for a defect in the gate.</summary>
    private async Task TrackPartition(string partition)
    {
        await MeshService.CreateNode(new MeshNode(GitHubSyncService.ConfigId, partition)
            {
                Name = "GitHub Sync",
                NodeType = GitHubSyncService.ConfigNodeType,
                State = MeshNodeState.Active,
                Content = new GitHubSyncConfig
                {
                    RepositoryUrl = "https://github.com/Systemorph/Example",
                    Branch = "main",
                },
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await AssertOwnership(partition, PartitionContentOwner.SyncSource);
    }

    private async Task AssertOwnership(string partition, PartitionContentOwner expected)
    {
        var verdict = await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .SelectMany(_ => PartitionContentOwnership.Observe(Mesh, partition))
            .Where(v => v.Owner == expected)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await();
        verdict.Owner.Should().Be(expected);
    }

    /// <summary>The SECOND writer: a node the installer believes it owns, rewritten behind its
    /// back. The node stays present — the whole difference from #4259, whose repair keys on
    /// absence.</summary>
    private async Task Drift(string path)
    {
        await Mesh.GetMeshNodeStream(path)
            .Update(node => node with { Name = DriftedName })
            .Should().Within(TestTimeouts.WriteConvergence).Emit($"drifting {path} is a precondition");
        (await NameOf(path)).Should().Be(DriftedName,
            "the drift must have landed, or the arm below measures nothing");
    }

    private async Task<string?> NameOf(string path)
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
        node.Should().NotBeNull($"'{path}' must exist for its name to say anything");
        return node!.Name;
    }

    private async Task<PackageManifest?> Record(string packageId)
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{packageId}", Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
        return node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
    }

    /// <summary>What a fetch asked for. <c>Paths</c> is null for a FULL install and a set for the
    /// incremental delta — the difference the second arm is entirely about.</summary>
    private sealed record Fetch(string PackageId, IReadOnlySet<string>? Paths);

    /// <summary>
    /// The package source as a transport, recording what was asked of it — the witness both arms
    /// turn on, and the one thing a lane under test cannot fake. Serves several packages, because
    /// the reconcile pass visits a list.
    /// </summary>
    private sealed class RecordingSource(IReadOnlyDictionary<string, IReadOnlyList<PackageFile>> byPackage)
        : IPackageSource
    {
        private ImmutableList<Fetch> fetched = ImmutableList<Fetch>.Empty;

        public ImmutableList<Fetch> Fetched => fetched;

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>(
                byPackage.Keys.Select(id => Candidate(id, V1)).ToList());

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) =>
            FetchPackageFiles(package, gitRef, null);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths)
        {
            var wanted = paths?.ToImmutableHashSet(StringComparer.Ordinal);
            ImmutableInterlocked.Update(ref fetched, f => f.Add(new Fetch(package.Id, wanted)));
            var files = byPackage.TryGetValue(package.Id, out var all) ? all : [];
            return Observable.Return(wanted is null
                ? files
                : (IReadOnlyList<PackageFile>)files.Where(f => wanted.Contains(f.RelativePath)).ToList());
        }
    }
}
