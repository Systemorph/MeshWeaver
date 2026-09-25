#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Policy <c>module-sync-per-manifest-hash</c>:</b> the seal no longer pins the boot default
/// install's ref (the #4259 design below). Every boot lists the configured ref, and a partition a
/// sync source keeps current is written by that source alone (#4588) — which is what keeps the two
/// unattended writers of one partition from landing two trees now that the sync source follows
/// green builds rather than the seal. The history is kept because it is why #4588's hold exists.
///
/// <para>🚨 <b>The boot default install was the last unattended branch-tip import, and it was writing
/// partitions a sync entry already held at the sealed commit</b> (Systemorph/MeshWeaver#4259).</para>
///
/// <para>The Sync-Ref Contract says an UNATTENDED import reads a commit a build proved; only a
/// person clicking "Update to latest" reads a branch. Every GitSync path honours it and
/// <c>ModuleDiscoveryService.FirstImport</c> was pinned to the seal for exactly that reason
/// (MeshWeaver#3845). <see cref="InstanceAutoRegistrationService"/> still listed
/// <c>PluginCatalog:Sources:N:Ref</c> — <c>main</c> — resolved at fetch time on every boot, and
/// stamped <c>installedFromRef: main</c>. Measured on memex.meshweaver.cloud 2026-09-13: the boot
/// install put a 2026-09-13 tree of <c>Hosting</c> into a partition whose <c>Hosting/_GitSync</c>
/// is held to the 2026-09-12 commit sealed for the running framework; on the next boot the sealed
/// reconciler re-imported the partition at the seal and pruned the eleven nodes only the newer
/// tree has; the boot install then re-applied <c>main</c> as a diff against its own record and
/// left them absent. Eight NodeTypes sat at <c>compilationStatus: Error</c>.</para>
///
/// <para><b>What is measured here, on the production decision end to end.</b> A real
/// <see cref="InstanceAutoRegistrationService"/> over a real mesh, a configured URL source whose
/// transport answers a DIFFERENT tree per commitish — the branch tip carries a node the sealed
/// commit does not — and a real published-bundle root carrying the markers
/// <c>publish-bake-bundles.sh</c> writes. With no seal the lane keeps today's behaviour (the
/// residue the contract names: a repository this instance runs no publication of). Once the
/// repository is sealed for this identity, the lane lists and installs at that commit, never asks
/// the transport for the branch, and its record says so. A torn seal HOLDS — nothing is installed
/// at the branch instead, the pass reports its listing incomplete — and a seal of ANOTHER
/// repository is not this lane's business.</para>
/// </summary>
public class BootInstallLandsOnTheSealedCommitTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/boot-seal";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string OtherRepoFullName = "test/some-other-repo";

    /// <summary>The bake source directory the seal lives under — the name the Plugins lane uses.</summary>
    private const string SealedSourceName = "plugins";

    /// <summary>The commit the seal covers — an OLDER tree than the branch tip, as on memex-cloud.</summary>
    private const string SealedSha = "627fb3cd2f1bc7142226a2ae881c81f9d83cc430";

    private const string Package = "Course";
    private const string LessonPath = $"{Package}/Lesson";
    private const string QuizPath = $"{Package}/Quiz";
    private const string RecordPath = $"{PackageInstaller.InstalledPartition}/{Package}";

    // Written BEFORE the mesh is built: a derived class's field initializers run ahead of the base
    // constructor, which is what calls ConfigureMesh.
    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-boot-seal-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly TwoTreeRepoClient repoClient = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            // The tracking seam, REAL: the second test's gate is answered by the shipped GitHub
            // provider over a real {partition}/_GitSync node, not by a stand-in. With no such node
            // — every arm of the first test — it answers "nothing tracks this partition", which is
            // the pre-#4588 behaviour and is what keeps that test measuring what it always did.
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services
                .AddGitHubSyncServices())
            .ConfigureServices(services => services
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // The registry's OWN shape, exactly as the fleet configures it: a URL
                        // source at `main`, in node-repo format.
                        ["PluginCatalog:Sources:0:Name"] = "Plugins",
                        ["PluginCatalog:Sources:0:RepoPath"] = RepoUrl,
                        ["PluginCatalog:Sources:0:Ref"] = "main",
                        ["PluginCatalog:Sources:0:Format"] = "node-repo",
                        // Where this pod reads what was sealed for its own framework identity.
                        [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                    })
                    .Build())
                .AddSingleton<IGitHubRepoClient>(repoClient)
                // The platform baseline: the package declares `preInstalled: true`, so it is a
                // RECONCILED candidate that re-asserts on every boot — the lane under test.
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = true }));

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally
        {
            try { Directory.Delete(publishedRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private InstanceAutoRegistrationService Installer =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// 🚨 Policy <c>module-sync-per-manifest-hash</c> re-expresses this case: the seal no longer pins
    /// (or holds) the boot install's ref — the same answer the GitSync first import takes — so every
    /// boot lists the CONFIGURED ref whatever is sealed: nothing sealed, sealed at an older commit, a
    /// torn seal, another repository's seal. Against the #4259 code boot 2 fetched the sealed commit
    /// and boot 3 held; here every boot asks the transport for <c>main</c> only.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheBootInstall_ListsTheConfiguredRef_WhateverIsSealed()
    {
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await(TestContext.Current.CancellationToken);
        first.Packages.Should().Equal(new[] { Package });
        first.Failed.Should().Be(0);
        (await Record())!.InstalledFromRef.Should().Be("main");
        (await Lesson()).Should().Be("# Lesson, as of main");
        (await Read(QuizPath)).Should().NotBeNull("the branch tip carries a node the sealed commit does not");
        repoClient.FetchedRefs.Should().NotBeEmpty().And.OnlyContain(r => r == "main");

        foreach (var (repository, complete, what) in new[]
                 {
                     (RepoFullName, true, "sealed at an OLDER commit for this identity"),
                     (RepoFullName, false, "a TORN seal"),
                     (OtherRepoFullName, true, "another repository's seal"),
                 })
        {
            var fetchesBefore = repoClient.FetchedRefs.Count;
            StageSeal(repository, SealedSha, complete);
            var pass = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
            pass.Packages.Should().Equal(new[] { Package }, $"{what}: the package is still installed");
            pass.Failed.Should().Be(0);
            pass.ListingIncomplete.Should().BeFalse($"{what}: nothing is held, so the listing is whole");
            (await Record())!.InstalledFromRef.Should().Be("main",
                $"{what}: the seal does not choose the ref (policy module-sync-per-manifest-hash)");
            (await Read(QuizPath)).Should().NotBeNull($"{what}: the configured ref's tree stays");
            repoClient.FetchedRefs.Skip(fetchesBefore).Should().NotContain(SealedSha,
                $"{what}: the sealed commit is never asked for");
        }
    }

    /// <summary>
    /// 🚨 <b>THE RESIDUE, and what it may not do</b> — Systemorph/MeshWeaver#4588.
    ///
    /// <para>The test above removes the branch-tip install wherever a seal can be attributed to the
    /// source. It cannot be attributed to a REGISTRY source — <see cref="InstanceAutoRegistrationService.ProvenRef"/>
    /// answers "no repository to attribute a seal to" for a source with no <c>RepoPath</c> — and
    /// that residue is what the control instance runs. Measured on memex.systemorph.com
    /// 2026-09-17: <c>Plugins/Hosting</c> was installed at 14:34:30Z with
    /// <c>installedFromRef: HEAD</c> into <c>Hosting</c>, whose <c>Hosting/_GitSync</c> re-imports
    /// the same subdirectory at the commit sealed for the running framework (then <c>061976bc</c>,
    /// which does not carry <c>Hosting/Issue/Source/FleetWatchCadence.cs</c>). Thirteen minutes
    /// later the record claimed that file and the node was gone; three Hosting NodeTypes could not
    /// compile and the roll onto the 3.0.0 candidate could not converge.</para>
    ///
    /// <para><b>Fails on main.</b> There the residue writes the branch tip into a partition another
    /// writer keeps current, whatever that writer is held to. The witness is the transport's own
    /// request log — what a lane ASKED FOR is the one thing it cannot fake.</para>
    ///
    /// <para><b>Since policy <c>module-sync-per-manifest-hash</c> the seal proves no ref</b>, so the
    /// arms that used to install at the sealed commit (a partition synced from its own repository)
    /// defer to the partition's sync source as well: it follows green builds per module manifest hash
    /// and is the partition's one writer. A partition nothing syncs still installs (boot 1 here, and
    /// every boot of the case above), so this is not "the boot install stopped installing".</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheBootInstall_HoldsAnUnprovenRef_WhereAnotherWriterKeepsThePartitionCurrent()
    {
        // ── Boot 1: nothing sealed, and nothing syncs Course. The residue installs the branch
        //    tip — today's behaviour, and the precondition that makes the next pass a measurement.
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180))
            .Await(TestContext.Current.CancellationToken);
        first.Packages.Should().Equal(new[] { Package });
        first.Held.Should().BeEmpty(
            "nothing else writes this partition, so there is nothing to hold — a hold here would "
            + "make every assertion below pass for the wrong reason");
        (await Record())!.InstalledFromRef.Should().Be("main");
        (await Lesson()).Should().Be("# Lesson, as of main");

        // ── The one fact that changes: a sync source now keeps Course current. ──────────────────
        await TrackPartition();

        // ── And it does what a sealed reconcile does: it PRUNES the node its tree does not carry.
        //    This is the shape that loops on main — the installer's completeness check sees a
        //    declared node absent, installs in FULL to repair it, the partition's own writer
        //    removes it again at the next import, and the next boot detects the same shortfall.
        //    "A detection that REPEATS at an unchanged module version is a repair that did not
        //    hold" (CatalogLayoutAreas.SkipOrHeal), measured on memex.meshweaver.cloud over ELEVEN
        //    boots for Feedback/Feedback/Source/FeedbackHandover.
        await MeshService.DeleteNode(QuizPath)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the prune is the precondition of the repair this gate must not attempt",
                cancellationToken: TestContext.Current.CancellationToken);
        (await Read(QuizPath)).Should().BeNull("the prune must have landed, or nothing is measured");
        var fetchesBeforeTheHold = repoClient.FetchedRefs.Count;

        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120))
            .Await(TestContext.Current.CancellationToken);

        // ── THE assertion. ─────────────────────────────────────────────────────────────────────
        (await Read(QuizPath)).Should().BeNull(
            "THE assertion: an unattended install must not write a ref no publication sealed here "
            + "names back into a partition another writer keeps current — not even to repair a node "
            + "that writer deliberately does not carry. On main it writes it back every boot, which "
            + "is how Hosting/Issue/Source/FleetWatchCadence came to be claimed by a 13-minute-old "
            + "install and absent from the mesh (MeshWeaver#4588)");
        repoClient.FetchedRefs.Count.Should().Be(fetchesBeforeTheHold,
            "and it did not even ASK the transport — what a lane requested is the one thing it "
            + "cannot fake");
        second.Packages.Should().BeEmpty("a held package is not a package this pass landed");
        second.Failed.Should().Be(0,
            "a hold is not a failure — no retry can prove a ref, and advertising a defect where "
            + "there is a standing decision is the #2536 mistake one lane over");
        var held = second.Held.Should().ContainSingle(h => h.Package == Package,
            "the hold is RECORDED, once, with its reason — an install that will never land here is "
            + "exactly the quiet 'my plugin never updates' this lane must not become").Subject;
        second.Skipped.Should().BeEmpty(
            "and it is NOT spelt as a terminal authorization skip: the summary renders those "
            + "'authorization, not retried', while this one lifts by itself the moment a seal names "
            + "the source or the partition stops being written by anything else");
        held.Reason.Should().Contain(Package,
            "the reason names the partition it is about, or nobody can act on it");
        held.Reason.Should().Contain("MeshWeaver#4588");
        (await Record())!.InstalledFromRef.Should().Be("main",
            "and the record was NOT re-stamped — a hold that moved the record would leave the next "
            + "delta computed against a claim nothing wrote");

        // ── ARMS 2 and 3, under policy module-sync-per-manifest-hash. ──────────────────────────
        // The seal no longer PROVES the install's ref, so a seal of this repository changes nothing
        // here: whether the partition syncs from ANOTHER repository (arm 2, which #4625 held on the
        // repository mismatch) or from its OWN (arm 3, which used to install at the sealed commit),
        // the partition's content is kept current by its sync source — its ONE writer, following
        // green builds per module manifest hash — and the installer defers to it (#4588). Installing
        // the sealed tree here would put an OLDER tree into a partition that writer has moved past:
        // the two-writer mix #4355 describes, in the other direction.
        StageSeal(RepoFullName, SealedSha, complete: true);
        var mismatched = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120))
            .Await(TestContext.Current.CancellationToken);
        mismatched.Held.Should().ContainSingle(h => h.Package == Package,
            "a partition another writer keeps current is not written by the boot install").Subject
            .Reason.Should().Contain("MeshWeaver#4588");
        mismatched.Packages.Should().BeEmpty("a held package is not a package this pass landed");
        (await Record())!.InstalledFromRef.Should().Be("main", "and the record is still not re-stamped");

        await PointTheSyncAt($"https://github.com/{RepoFullName}", Package);
        var third = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120))
            .Await(TestContext.Current.CancellationToken);
        third.Held.Should().ContainSingle(h => h.Package == Package,
            "a partition synced from its OWN repository has one writer too — the sync source");
        third.Packages.Should().BeEmpty();
        repoClient.FetchedRefs.Should().NotContain(SealedSha, "the sealed commit is never asked for");
        (await Record())!.InstalledFromRef.Should().Be("main");
    }

    /// <summary>
    /// Re-points the partition's <c>_GitSync</c> at a repository and subdirectory — the one thing
    /// that differs between the #4625 hold and the control that must install.
    /// </summary>
    /// <param name="repositoryUrl">The repository the partition's own writer imports from.</param>
    /// <param name="subdirectory">The subdirectory within it.</param>
    private async Task PointTheSyncAt(string repositoryUrl, string? subdirectory)
    {
        var path = $"{Package}/{GitHubSyncService.ConfigId}";
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(node => node with
            {
                Content = new GitHubSyncConfig
                {
                    RepositoryUrl = repositoryUrl,
                    Branch = "main",
                    Subdirectory = subdirectory,
                },
            })
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the arm below turns on this edit, so it has to have landed",
                cancellationToken: TestContext.Current.CancellationToken);

        // 🚨 Read it back THROUGH the decision under test, never off the node: a write that landed
        // and a seam that re-read it are different facts, and the arm below is only meaningful once
        // the gate itself can see the new repository. Waiting on the gate is also what makes a
        // failure here read as "the seam never saw the edit" rather than as the arm's own verdict.
        await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .SelectMany(_ => PartitionContentOwnership.ImportedFromAnotherRepository(
                Mesh, Package, RepoFullName, Package))
            .Where(mismatch => mismatch is null)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The gate's decision, pinned offline so every arm stays separate — including the two that are
    /// not passes. Folding <see cref="PartitionContentOwner.Undetermined"/> into "proceed" would
    /// make a seam that faulted read exactly like a partition nothing syncs, and the lane would
    /// then write a branch tip into a partition it has no evidence about.
    /// </summary>
    [Fact]
    public void UnprovenRefHold_HoldsEverythingButARealNegative()
    {
        InstanceAutoRegistrationService.UnprovenRefHold(
                PartitionContentOwnership.Decide(Package, tracked: false, providerCount: 1), "main")
            .Should().BeNull(
                "a mesh WITH a sync layer that says nothing tracks this partition is a real "
                + "negative — the installer is its only writer and the residue is unchanged");

        InstanceAutoRegistrationService.UnprovenRefHold(
                PartitionContentOwnership.Decide(Package, tracked: null, providerCount: 0), "main")
            .Should().BeNull(
                "a mesh that registers NO tracking provider has no second writer at all — a local "
                + "mesh, a CI mesh, the bake host — and must keep the pre-#4588 behaviour");

        var tracked = InstanceAutoRegistrationService.UnprovenRefHold(
            PartitionContentOwnership.Decide(Package, tracked: true, providerCount: 1), "main");
        tracked.Should().NotBeNull().And.Contain(Package).And.Contain("main",
            "the hold names the partition AND the ref it refused to land there");

        InstanceAutoRegistrationService.UnprovenRefHold(
                PartitionContentOwnership.Decide(Package, tracked: null, providerCount: 2), "main")
            .Should().NotBeNull(
                "THE arm: a seam that did not answer was NOT checked, and 'I could not tell' is "
                + "never 'clear to write' — a hold that was wrong is lifted by the next boot, an "
                + "install that was wrong cannot be taken back");
    }

    /// <summary>A real <c>{partition}/_GitSync</c> naming a repository — what the settings tab
    /// writes when a Space is connected — read back through the very decision under test, so a
    /// false negative cannot be mistaken for a working gate. The repository is deliberately NOT
    /// this package's: the transport refuses it, exactly as a real client with no credential for it
    /// would, so the sync writes nothing and the arm measures the INSTALL alone.</summary>
    private async Task TrackPartition()
    {
        await MeshService
            .CreateNode(new MeshNode(GitHubSyncService.ConfigId, Package)
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
            .Should().Within(TestTimeouts.Convergence)
            .Emit("connecting the partition to a repository is the precondition of the hold",
                cancellationToken: TestContext.Current.CancellationToken);

        var verdict = await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .SelectMany(_ => PartitionContentOwnership.Observe(Mesh, Package))
            .Where(v => v.Owner == PartitionContentOwner.SyncSource)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(TestContext.Current.CancellationToken);
        verdict.InstallerOwnsTheContent.Should().BeFalse(
            "the seam must actually see the sync entry, or the pass below would be held by nothing");
    }

    /// <summary>
    /// The pure half, pinned offline so the three answers stay three: a plan naming a commit
    /// re-refs the source; a branch-tip plan returns the SAME instance (so "pinned" and "as
    /// configured" are told apart without comparing refs); a hold carries its reason.
    /// </summary>
    [Fact]
    public void ApplyPlan_KeepsTheThreeAnswersApart()
    {
        var configured = new ConfiguredPackageSource(repoClient.AsSource(), "main", "Plugins") { RepoPath = RepoUrl };

        var pinned = InstanceAutoRegistrationService.ApplyPlan(configured,
            new SealedSyncGate.FirstImportPlan(SealedSha, null, "sealed"));
        pinned.HoldReason.Should().BeNull();
        pinned.Source.GitRef.Should().Be(SealedSha);
        pinned.Source.Name.Should().Be("Plugins");
        pinned.Source.RepoPath.Should().Be(RepoUrl, "everything but the ref is carried");

        var tip = InstanceAutoRegistrationService.ApplyPlan(configured,
            new SealedSyncGate.FirstImportPlan(null, null, "nothing sealed"));
        tip.HoldReason.Should().BeNull();
        tip.Source.Should().BeSameAs(configured);

        var held = InstanceAutoRegistrationService.ApplyPlan(configured,
            new SealedSyncGate.FirstImportPlan(null, "torn", "torn"));
        held.HoldReason.Should().Be("torn");
    }

    /// <summary>
    /// <c>&lt;root&gt;/&lt;this process's identity&gt;/plugins/</c> with the markers a sealed
    /// publication carries; <paramref name="complete"/> false leaves out the completion sentinel —
    /// the torn shape. Re-callable, because re-sealing is what the publishing lane does.
    /// </summary>
    private void StageSeal(string repository, string commit, bool complete)
    {
        var sourceDirectory = Path.Combine(
            publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, SealedSourceName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), repository);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), commit);
        var sentinel = Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        if (complete)
            File.WriteAllText(sentinel, string.Empty);
        else if (File.Exists(sentinel))
            File.Delete(sentinel);
    }

    private async Task<PackageManifest?> Record() =>
        (await Read(RecordPath))?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);

    /// <summary>The lesson's markdown, as the markdown parser lands it (a <see cref="MarkdownContent"/>).</summary>
    private async Task<string?> Lesson() =>
        (await Read(LessonPath))?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content?.Trim();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// The transport, answering a DIFFERENT tree per commitish and recording what it was asked for:
    /// the branch carries a lesson "as of main" plus a Quiz node; the sealed commit carries the
    /// lesson "as sealed" and no Quiz. Every other commitish is a fault — an unattended install
    /// asking for anything else is the bug.
    /// </summary>
    private sealed class TwoTreeRepoClient : IGitHubRepoClient
    {
        private readonly List<string> fetched = [];

        public IReadOnlyList<string> FetchedRefs
        {
            get { lock (fetched) return fetched.ToList(); }
        }

        /// <summary>The same transport as a package source, for the offline pin.</summary>
        public IPackageSource AsSource() => new NodeRepoPackageSource(Fetch, RepoUrl);

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            // Another repository is not this transport's business, and saying so BEFORE recording
            // keeps the witness clean: the second test connects the partition to a different
            // repository, and a sync attempt of THAT one must neither appear in FetchedRefs nor
            // import anything over the package's content. A real client without a credential for it
            // fails in exactly this place.
            if (!string.Equals(repositoryUrl, RepoUrl, StringComparison.Ordinal))
                return Observable.Throw<RepoSnapshot>(new NotSupportedException(
                    $"This transport answers only for '{RepoUrl}'; '{repositoryUrl}' is another repository."));
            lock (fetched)
                fetched.Add(commitish);
            return commitish switch
            {
                "main" => Observable.Return(new RepoSnapshot("f00d" + new string('0', 36), Tree(
                    lesson: "# Lesson, as of main", quiz: "# Quiz, only on main",
                    moduleVersion: "tip-0000000000001", lessonHash: "L-main", quizHash: "Q-main"))),
                SealedSha => Observable.Return(new RepoSnapshot(SealedSha, Tree(
                    lesson: "# Lesson, as sealed", quiz: null,
                    moduleVersion: "seal-000000000001", lessonHash: "L-seal", quizHash: null))),
                _ => Observable.Throw<RepoSnapshot>(new InvalidOperationException(
                    $"The boot install asked the transport for '{commitish}', which is neither the branch nor the sealed commit.")),
            };
        }

        private static IReadOnlyList<RepoFile> Tree(
            string lesson, string? quiz, string moduleVersion, string lessonHash, string? quizHash)
        {
            var files = new List<RepoFile>
            {
                new($"{Package}/index.json", $$"""
                    {
                      "$type": "MeshNode", "id": "{{Package}}", "namespace": "", "path": "{{Package}}",
                      "mainNode": "{{Package}}", "name": "{{Package}}", "nodeType": "Space", "state": "Active",
                      "content": {
                        "$type": "PluginManifest", "description": "A course.", "minMeshVersion": "1.0.0",
                        "preInstalled": true
                      }
                    }
                    """),
                new($"{Package}/Lesson.md", lesson),
            };
            if (quiz is not null)
                files.Add(new RepoFile($"{Package}/Quiz.md", quiz));
            // The CI-maintained sidecar: its hashes are compared manifest-to-manifest only, so any
            // distinct strings do; what matters is that the sealed tree DECLARES no Quiz, which is
            // what makes the lane prune it when it lands on the seal.
            var quizEntry = quizHash is null ? "" : $", \"{Package}/Quiz.md\": \"{quizHash}\"";
            files.Add(new RepoFile($"{Package}/{ModuleManifest.FileName}", $$"""
                {
                  "module": "{{Package}}", "moduleVersion": "{{moduleVersion}}", "version": "1.0.0",
                  "files": {
                    "{{Package}}/index.json": "i-1", "{{Package}}/Lesson.md": "{{lessonHash}}"{{quizEntry}}
                  }
                }
                """));
            return files;
        }

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("BootInstallLandsOnTheSealedCommitTest's repo client answers only Fetch."));

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotUsed<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
            => NotUsed<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
            => NotUsed<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();
    }
}
