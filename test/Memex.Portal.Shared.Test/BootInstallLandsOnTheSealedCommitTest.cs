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
/// 🚨 <b>The boot default install was the last unattended branch-tip import, and it was writing
/// partitions a sync entry already held at the sealed commit</b> (Systemorph/MeshWeaver#4259).
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

    [Fact(Timeout = 300_000)]
    public async Task TheBootInstall_LandsOnTheCommitTheSealNames_NeverTheBranch()
    {
        // ── Boot 1: nothing sealed for this identity ─────────────────────────────────────────
        // The residue the contract names — a repository this instance runs no publication of —
        // keeps today's behaviour: the configured branch. This is also the precondition that
        // makes every later boot a measurement: the partition holds the TIP's tree.
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await(TestContext.Current.CancellationToken);
        first.Packages.Should().Equal(new[] { Package });
        first.Failed.Should().Be(0);
        (await Record())!.InstalledFromRef.Should().Be("main",
            "with no publication of the repository sealed for this identity, the lane resolves the branch (the residue)");
        (await Lesson()).Should().Be("# Lesson, as of main");
        (await Read(QuizPath)).Should().NotBeNull("the branch tip carries a node the sealed commit does not");
        // A boot fetches at least twice — the listing and the package's files — so the measurement
        // is the SET of refs asked for, never a count.
        repoClient.FetchedRefs.Should().NotBeEmpty().And.OnlyContain(r => r == "main");
        var fetchesAfterFirstBoot = repoClient.FetchedRefs.Count;

        // ── Boot 2: the repository is sealed at an OLDER commit for this identity ────────────
        StageSeal(RepoFullName, SealedSha, complete: true);
        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
        second.Packages.Should().Equal(new[] { Package });
        second.Failed.Should().Be(0);
        second.ListingIncomplete.Should().BeFalse();
        (await Record())!.InstalledFromRef.Should().Be(SealedSha,
            "an unattended import lands on the commit whose bytes this instance runs, and the record says which");
        (await Lesson()).Should().Be("# Lesson, as sealed",
            "the partition holds the SEALED tree — the same tree the partition's sync entry is held to");
        (await Read(QuizPath)).Should().BeNull(
            "a node only the branch tip carries is pruned when the lane lands on the seal; the two "
            + "unattended writers of this partition now agree on its tree, so nothing is left for the "
            + "sealed reconciler to remove behind the installer's back");
        repoClient.FetchedRefs.Skip(fetchesAfterFirstBoot).Should().NotBeEmpty("the sealed commit was fetched")
            .And.NotContain("main", "the branch is never resolved by an unattended install once the repository is sealed here")
            .And.OnlyContain(r => r == SealedSha);

        // ── Boot 3: the seal is TORN (no completion sentinel) ────────────────────────────────
        // "Cannot tell" is a hold, never "clear to resolve the branch": the very shape being
        // removed would otherwise come back the moment a publication was half-written.
        var fetchesBeforeHold = repoClient.FetchedRefs.Count;
        StageSeal(RepoFullName, SealedSha, complete: false);
        var third = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
        third.Packages.Should().BeEmpty("a held source contributes no candidates this boot");
        third.Failed.Should().Be(0, "a hold is not a failure — a retry cannot change a seal, the seal landing can");
        third.ListingIncomplete.Should().BeTrue(
            "the pass must not read its own silence as 'that source refuses nothing' (#4097)");
        repoClient.FetchedRefs.Count.Should().Be(fetchesBeforeHold, "a held source is not fetched at any ref");
        (await Record())!.InstalledFromRef.Should().Be(SealedSha, "the record is untouched by a hold");
        (await Lesson()).Should().Be("# Lesson, as sealed");

        // ── Boot 4: a seal of ANOTHER repository — not this lane's business ──────────────────
        StageSeal(OtherRepoFullName, SealedSha, complete: true);
        var fetchesBeforeFourth = repoClient.FetchedRefs.Count;
        var fourth = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
        fourth.Packages.Should().Equal(new[] { Package });
        fourth.ListingIncomplete.Should().BeFalse();
        (await Record())!.InstalledFromRef.Should().Be("main",
            "another repository's seal holds nothing here — this repository is back to 'no publication sealed', the residue");
        (await Read(QuizPath)).Should().NotBeNull("the branch tip's tree is back, so its extra node is too");
        repoClient.FetchedRefs.Skip(fetchesBeforeFourth).Should().NotBeEmpty().And.OnlyContain(r => r == "main");
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
    /// <para><b>The control is in the same test and differs by one fact</b>: once the repository IS
    /// sealed for this identity the same sync-owned partition installs again, at the sealed commit.
    /// The gate discriminates on whether the REF was proven, never on whether the partition has a
    /// second writer — so it cannot become "the boot install stopped installing".</para>
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

        // ── ARM 2: a PROVEN ref, into a partition synced from ANOTHER repository. ──────────────
        // 🚨 This arm used to be the "control", and it was UNSOUND — which Copilot's review of
        // #4619 said in as many words: the fixture connects the partition to `Systemorph/Example`
        // while the package is sealed from `test/boot-seal`, so "the two agree" was never true of
        // it. Both writers are pinned, and they are pinned to two different repositories. #4619
        // left that as MeshWeaver#4625 because nothing compared them. Gate 1d now does, so the same
        // fixture is no longer a control: it IS the #4625 case, and it must HOLD.
        StageSeal(RepoFullName, SealedSha, complete: true);
        var mismatched = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120))
            .Await(TestContext.Current.CancellationToken);
        var repoHold = mismatched.Held.Should().ContainSingle(h => h.Package == Package,
            "a proven ref is not enough: this partition imports from a DIFFERENT repository, so the "
            + "two pinned writers still land two trees (MeshWeaver#4625)").Subject;
        repoHold.Reason.Should().Contain("MeshWeaver#4625",
            "the hold names the gap it is about, or nobody can act on it");
        repoHold.Reason.Should().Contain("two pinned writers, two repositories, one partition");
        mismatched.Packages.Should().BeEmpty("a held package is not a package this pass landed");
        (await Record())!.InstalledFromRef.Should().Be("main",
            "and the record is still not re-stamped");

        // ── ARM 3, THE CONTROL: a proven ref into the partition's OWN repository INSTALLS. ─────
        // 🚨 This is the arm that must never break. It is the fleet's normal shape — a package
        // sealed from a repository, installed into a partition synced from that repository's own
        // folder — and holding it would take #4259's lane offline on every instance, which is the
        // stated cost that kept #4625 open rather than folded into #4619. Only the REPOSITORY
        // changes between arm 2 and arm 3: same seal, same proven ref, same partition. So what this
        // pins is the comparison and nothing else.
        await PointTheSyncAt($"https://github.com/{RepoFullName}", Package);
        var third = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120))
            .Await(TestContext.Current.CancellationToken);
        third.Held.Should().BeEmpty(
            "the control: a PROVEN ref into the partition's OWN repository lands the same tree the "
            + "partition's writer is held to, so the two agree and nothing is held — the gate must "
            + "not read 'this partition has a second writer' as 'never install here again'");
        third.Packages.Should().Equal(new[] { Package });
        (await Record())!.InstalledFromRef.Should().Be(SealedSha);
        (await Lesson()).Should().Be("# Lesson, as sealed",
            "and the control really WROTE — the partition now holds the sealed tree, the same one "
            + "its sync entry is held to, which is the state the two writers may share");
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
