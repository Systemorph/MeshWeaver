#pragma warning disable CS1591

using System;
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
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE COMBO READER — one read that states what an instance is actually running, so a deploy gate
/// can verify that exact combination before shipping (<c>Doc/Architecture/CandidateReleaseProtocol</c>).
///
/// <para><b>The property under test is the FOLD.</b> A module's coordinate is recorded in two
/// shapes — per-Space <c>{Space}/_GitSync</c> entries and <c>Plugins/{id}</c> install records — and
/// a reader that handles only the install records returns almost nothing on a real portal <b>while
/// looking like a healthy empty set</b>. Measured live on memex 2026-08-10: <b>42 sync configs</b>,
/// and <c>Plugins/*</c> holding only <c>_Policy</c> — <b>zero install records</b>. So the headline
/// test is the GitSync-only instance, not the installer one.</para>
///
/// <para>🚨 <b>The honesty property.</b> <c>lastSyncCommitSha</c> pins what the Space was last
/// SYNCED FROM. Content authored in the mesh afterwards is not in that commit, and the reader must
/// never imply otherwise — so it reports the ref as PROVENANCE and says so in the answer itself.
/// <see cref="TheRefIsProvenance_NotAClaimThatTheMeshStillMatchesIt"/> pins that a post-sync edit
/// does not move (or invalidate) the ref, and that the answer carries the caveat.</para>
/// </summary>
public class InstanceComboReaderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/Systemorph/MeshWeaver.SocialMedia";

    /// <summary>A real 40-hex commit sha — the shape a sync records, and the shape the reader must
    /// grade as an EXACT coordinate.</summary>
    private const string CommitSha = "d19534d605ed24c348178803bb1241744c39091b";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private InstanceComboReader Reader =>
        Mesh.ServiceProvider.GetRequiredService<InstanceComboReader>();

    private GitHubSyncService Sync =>
        Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  The shape that actually carries production
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 THE REGRESSION THIS TYPE EXISTS FOR. An instance whose modules ALL arrived through
    /// <c>{Space}/_GitSync</c> — the memex shape, 42 configs against zero install records — must
    /// report those modules, with their repo and their pinned commit. A Package-only reader answers
    /// "0 modules" here and that reads as "nothing installed, all clear".
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AGitSyncOnlyInstance_ReportsItsModules_NotAnEmptySet()
    {
        await Provision("SocialMedia", "SocialMedia", CommitSha);
        await Provision("LinkedIn", "LinkedIn", CommitSha);

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        combo.IsComplete.Should().BeTrue("both sources were readable");
        combo.Modules.Select(m => m.ModuleId).Should().Contain(["LinkedIn", "SocialMedia"],
            "these modules arrived the way modules ACTUALLY arrive — a reader that only knows "
            + "install records reports nothing here and looks healthy doing it");

        var social = combo.Modules.Single(m => m.ModuleId == "SocialMedia");
        social.Origin.Should().Be(ModuleOrigin.GitSync);
        social.GitSync.Should().NotBeNull();
        social.GitSync!.RepositoryUrl.Should().Be(RepoUrl);
        social.GitSync.Subdirectory.Should().Be("SocialMedia");
        social.GitSync.Branch.Should().Be("main");
        social.GitSync.LastSyncCommitSha.Should().Be(CommitSha);
        social.GitSync.ConfigPath.Should().Be("SocialMedia/_GitSync",
            "the answer must name WHERE it read the coordinate, so an operator can go look");

        social.ProvenanceRef.Should().Be(CommitSha);
        social.ProvenanceKind.Should().Be(ProvenanceKind.SyncedCommit);
        social.Fidelity.Should().Be(RefFidelity.Exact, "a commit sha identifies one immutable tree");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The installer shape, and the fold
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The other shape: a real install through <see cref="PackageInstaller"/> writes
    /// <c>Plugins/{id}</c>, and the combo reports its <c>ModuleVersion</c> — a hash over the
    /// module's OWN files, which is why it outranks a whole-repo commit sha as a coordinate.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnInstalledPackage_IsReportedWithItsModuleVersion()
    {
        await Install("data-analyst", moduleVersion: "mv-abc123");

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        var module = combo.Modules.Single(m => m.ModuleId == "data-analyst");
        module.Origin.Should().Be(ModuleOrigin.PackageInstall);
        module.Package.Should().NotBeNull();
        module.Package!.RecordPath.Should().Be($"{PackageInstaller.InstalledPartition}/data-analyst");
        module.Package.ModuleVersion.Should().Be("mv-abc123");
        module.ProvenanceKind.Should().Be(ProvenanceKind.ModuleVersion);
        module.Fidelity.Should().Be(RefFidelity.Exact,
            "a content hash over the module's own files identifies its tree exactly");
        module.MaterializedAt.Should().NotBeNull("an install records when it landed");
    }

    /// <summary>
    /// BOTH shapes, folded into ONE list: a module that is installed AND kept in sync appears
    /// exactly once, carrying both coordinates. Reporting it twice would double-count the combo;
    /// dropping one coordinate would lose the repo the gate has to check out.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AModuleRecordedBothWays_AppearsOnce_CarryingBothCoordinates()
    {
        await Install("SocialMedia", moduleVersion: "mv-both");
        // The installer already provisioned the partition — wire the sync entry onto it rather than
        // re-creating a root that is there (CreateNode is create-only).
        await Provision("SocialMedia", "SocialMedia", CommitSha, createSpace: false);

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        combo.Modules.Count(m => m.ModuleId == "SocialMedia").Should().Be(1,
            "one module is one entry — folding the two shapes is the whole job");

        var module = combo.Modules.Single(m => m.ModuleId == "SocialMedia");
        module.Origin.Should().Be(ModuleOrigin.Both);
        module.GitSync!.LastSyncCommitSha.Should().Be(CommitSha,
            "the sync coordinate must survive the fold — it is what a gate checks out");
        module.Package!.ModuleVersion.Should().Be("mv-both");
        module.ProvenanceRef.Should().Be("mv-both",
            "the module's own content hash outranks a whole-repo commit sha, which moves whenever "
            + "an unrelated sibling module changes");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  🚨 Honesty
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <c>lastSyncCommitSha</c> is PROVENANCE. After content is authored in the mesh the Space no
    /// longer matches the commit it was synced from — and the reader neither notices nor claims
    /// otherwise. So the answer must SAY so rather than let a caller read the ref as an identity.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheRefIsProvenance_NotAClaimThatTheMeshStillMatchesIt()
    {
        await Provision("SocialMedia", "SocialMedia", CommitSha);

        var before = await Reader.Read().Should().Within(120.Seconds()).Emit();
        before.Caveats.Should().Contain(InstanceComboReader.ProvenanceCaveat,
            "an answer that reports a sync sha must state, in the answer, that it records where the "
            + "content CAME FROM — never that the live mesh is byte-identical to it");

        var module = before.Modules.Single(m => m.ModuleId == "SocialMedia");
        module.MaterializedAt.Should().NotBeNull(
            "the drift boundary must be readable: anything authored after it is not in the ref");

        // Author content in the mesh AFTER the sync — the normal case, since the mesh is authored
        // as well as synced. Written as SYSTEM because wiring a _GitSync makes the partition
        // repo-owned and retracts the human creator's grant (SystemOwnedAccessRetractionHandler) —
        // that is the production shape, and not what this test is about.
        await AsSystem(() => NodeFactory.CreateNode(new MeshNode("HandWritten", "SocialMedia")
        {
            NodeType = "Markdown",
            Name = "Written here, after the sync",
            State = MeshNodeState.Active,
            Content = "# Not in that commit",
        })).Timeout(60.Seconds()).ToTask();

        var after = await Reader.Read().Should().Within(120.Seconds()).Emit();
        after.Modules.Single(m => m.ModuleId == "SocialMedia").ProvenanceRef.Should().Be(CommitSha,
            "the ref still records the last sync — it is provenance, and an in-mesh edit neither "
            + "moves it nor is detected by it. That is exactly why it must never be read as identity");
        after.Caveats.Should().Contain(InstanceComboReader.ProvenanceCaveat);
    }

    /// <summary>
    /// A Space wired to a repo that has never synced is pinned to a BRANCH — which moves. Two runs
    /// of that combo can resolve it to different content, so the answer must grade it
    /// <see cref="RefFidelity.Moving"/>, refuse <see cref="InstanceCombo.IsReproducible"/>, and name
    /// the module in a caveat. Silently treating a branch as a coordinate is how a gate reports
    /// "verified" for something it cannot reproduce.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AModulePinnedOnlyToABranch_IsGradedMoving_AndBlocksReproducibility()
    {
        await Provision("Chess", "Chess", lastSyncCommitSha: null);

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        var module = combo.Modules.Single(m => m.ModuleId == "Chess");
        module.ProvenanceKind.Should().Be(ProvenanceKind.Branch);
        module.ProvenanceRef.Should().Be("main");
        module.Fidelity.Should().Be(RefFidelity.Moving, "a branch is not a coordinate — it moves");

        combo.IsReproducible.Should().BeFalse(
            "one un-pinned module makes the whole combo unrepeatable, and a gate must be told");
        combo.Caveats.Should().Contain(c => c.Contains("'Chess'") && c.Contains("moving"),
            "the caveat must NAME the module, not just say something somewhere is unpinned");
    }

    /// <summary>
    /// With no <see cref="ModuleDiscovery"/> record — <c>Admin/_Discovery</c> is empty on memex —
    /// the reader can only see what this instance HAS. Modules a configured repo ships but this
    /// instance lacks are invisible, and that limit has to be stated rather than passed off as a
    /// complete set.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task WithNoDiscoveryRecord_TheAnswerSaysAbsentModulesAreInvisible()
    {
        await Provision("SocialMedia", "SocialMedia", CommitSha);

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        combo.NotCarried.Should().BeEmpty();
        combo.Caveats.Should().Contain(InstanceComboReader.NoDiscoveryCaveat,
            "an empty NotCarried must not be readable as 'the repo ships nothing else'");
    }

    /// <summary>
    /// When a discovery scan HAS run, the modules it found and this instance does not carry come
    /// back as <see cref="InstanceCombo.NotCarried"/> — so a gate can tell "this combo is the whole
    /// set" from "this combo is what happens to be here". A module the instance demonstrably carries
    /// is never reported as a gap, however stale the scan.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task WithADiscoveryRecord_ModulesTheRepoShipsButThisInstanceLacks_AreNamed()
    {
        await Provision("SocialMedia", "SocialMedia", CommitSha);
        await WriteDiscoveryRecord();

        var combo = await Reader.Read().Should().Within(120.Seconds()).Emit();

        combo.NotCarried.Select(g => g.ModuleId).Should().Equal(["Planning"],
            "Planning is shipped by the repo and absent here; SocialMedia is carried, so it is not "
            + "a gap even though the scan predates nothing in particular");
        combo.NotCarried.Single().Status.Should().Be(ModuleDiscoveryStatus.Discovered);
        combo.NotCarried.Single().RepositoryUrl.Should().Be(RepoUrl);
        combo.Caveats.Should().NotContain(InstanceComboReader.NoDiscoveryCaveat);

        combo.Modules.Single(m => m.ModuleId == "SocialMedia").Name.Should().Be("Social Media",
            "the discovery record is where a GitSync-only module's display name comes from");
    }

    /// <summary>
    /// An instance that carries nothing reports an empty list — and is NOT reproducible, because
    /// "no modules" is not a verified combo. The distinction that matters is against an UNREADABLE
    /// source, which sets <see cref="InstanceCombo.IsComplete"/> false; an empty read leaves it
    /// true.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnInstanceCarryingNothing_IsCompleteButNotReproducible()
    {
        var combo = await Reader.Read().Should().Within(90.Seconds()).Emit();

        combo.Modules.Should().BeEmpty();
        combo.IsComplete.Should().BeTrue("both sources were readable — they were just empty");
        combo.IsReproducible.Should().BeFalse(
            "an empty combo verifies nothing; only IsComplete says the read succeeded");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Grading, without a mesh
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The fidelity grading is the gate's whole go/no-go, so it is pinned directly: a sha is exact,
    /// a branch name in the same field is not, a module hash is exact, and nothing recorded is
    /// <see cref="RefFidelity.Unrecorded"/> rather than optimistically "fine".
    /// </summary>
    [Theory]
    [InlineData(CommitSha, null, ProvenanceKind.SyncedCommit, RefFidelity.Exact)]
    [InlineData("main", null, ProvenanceKind.SyncedCommit, RefFidelity.Moving)]
    [InlineData(null, "mv-abc", ProvenanceKind.ModuleVersion, RefFidelity.Exact)]
    [InlineData(null, null, ProvenanceKind.None, RefFidelity.Unrecorded)]
    public void FidelityGrading_TellsAnImmutableTreeFromAMovingOne(
        string? syncedSha, string? moduleVersion, ProvenanceKind kind, RefFidelity fidelity)
    {
        var module = new ModuleCoordinate
        {
            ModuleId = "X",
            GitSync = syncedSha is null ? null : new GitSyncCoordinate { LastSyncCommitSha = syncedSha },
            Package = moduleVersion is null ? null : new PackageCoordinate { ModuleVersion = moduleVersion },
        };

        module.ProvenanceKind.Should().Be(kind);
        module.Fidelity.Should().Be(fidelity);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Helpers — the REAL write paths, never a stand-in
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Provisions a module the way production does: a Space, its <c>_GitSync</c> entry through
    /// <see cref="GitHubSyncService.SaveConfig"/>, and — when the module has actually synced — the
    /// commit merged onto the config node exactly as <c>RecordLastSync</c> merges it.
    /// </summary>
    private async Task Provision(
        string space, string subdirectory, string? lastSyncCommitSha, bool createSpace = true)
    {
        if (createSpace)
            await NodeFactory.CreateNode(new MeshNode(space)
            {
                NodeType = GitHubSyncService.SpaceNodeType,
                Name = space,
                State = MeshNodeState.Active,
                Content = new Space(),
            }).Timeout(60.Seconds()).ToTask();

        await Sync.SaveConfig(
                space, RepoUrl, "main", subdirectory,
                createBranchIfMissing: false, createRepoIfMissing: false,
                direction: SyncDirection.ImportOnly)
            .Timeout(60.Seconds()).ToTask();

        if (lastSyncCommitSha is null)
            return;

        // The same two-field merge the sync operation performs when an import lands.
        var now = DateTimeOffset.UtcNow;
        await Mesh.GetWorkspace()
            .GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Update(node => node with
            {
                Content = (node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)
                           ?? new GitHubSyncConfig()) with
                {
                    LastSyncedAt = now,
                    LastSyncCommitSha = lastSyncCommitSha,
                },
            })
            .Timeout(60.Seconds()).ToTask();
    }

    /// <summary>Installs a package for real — <see cref="PackageInstaller"/> writes the
    /// <c>Plugins/{id}</c> record this reader then reads.</summary>
    private Task<InstallResult> Install(string id, string moduleVersion) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = id,
                    Name = id,
                    Kind = PackageKind.Content,
                    TargetPartition = id,
                    SourceFolder = id,
                    Version = "1.0.0",
                    ModuleVersion = moduleVersion,
                },
                [new PackageFile($"{id}/Doc.md", $"# {id}")],
                "HEAD")
            .FirstAsync().Timeout(120.Seconds()).ToTask();

    /// <summary>
    /// The record a <see cref="ModuleDiscoveryService"/> scan writes: the repo ships two modules,
    /// one of which this instance carries.
    /// </summary>
    private async Task WriteDiscoveryRecord()
    {
        var path = ModuleDiscovery.PathFor(RepoUrl);
        var slash = path.LastIndexOf('/');
        var node = new MeshNode(path[(slash + 1)..], path[..slash])
        {
            NodeType = ModuleDiscovery.NodeType,
            Name = "Modules of SocialMedia",
            State = MeshNodeState.Active,
            Content = new ModuleDiscovery
            {
                RepositoryUrl = RepoUrl,
                SourceName = "plugins",
                GitRef = "main",
                LastScannedAt = DateTimeOffset.UtcNow,
                Modules =
                [
                    new DiscoveredModule
                    {
                        Id = "SocialMedia",
                        Name = "Social Media",
                        Status = ModuleDiscoveryStatus.Synced,
                    },
                    new DiscoveredModule
                    {
                        Id = "Planning",
                        Name = "Planning",
                        Status = ModuleDiscoveryStatus.Discovered,
                        Detail = "not on this instance",
                    },
                ],
            },
        };

        await AsSystem(() => Mesh.Observe<CreateOrUpdateNodeResponse>(
                new CreateOrUpdateNodeRequest(node)))
            .FirstAsync().Timeout(60.Seconds()).ToTask();
    }

    /// <summary>Establishes System on the write's OWN subscribe — an ambient impersonation does not
    /// survive a scheduler hop, which is exactly why production wraps every such write this way.</summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> write)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(() => accessService.ImpersonateAsSystem(), _ => write());
    }
}
