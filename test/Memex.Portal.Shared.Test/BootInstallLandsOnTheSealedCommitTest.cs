#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
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

    [Fact(Timeout = 300_000)]
    public async Task TheBootInstall_LandsOnTheCommitTheSealNames_NeverTheBranch()
    {
        // ── Boot 1: nothing sealed for this identity ─────────────────────────────────────────
        // The residue the contract names — a repository this instance runs no publication of —
        // keeps today's behaviour: the configured branch. This is also the precondition that
        // makes every later boot a measurement: the partition holds the TIP's tree.
        var first = await Installer.Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await();
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
        var second = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
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
        var third = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
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
        var fourth = await Installer.RunDefaultInstall().Timeout(TimeSpan.FromSeconds(120)).Await();
        fourth.Packages.Should().Equal(new[] { Package });
        fourth.ListingIncomplete.Should().BeFalse();
        (await Record())!.InstalledFromRef.Should().Be("main",
            "another repository's seal holds nothing here — this repository is back to 'no publication sealed', the residue");
        (await Read(QuizPath)).Should().NotBeNull("the branch tip's tree is back, so its extra node is too");
        repoClient.FetchedRefs.Skip(fetchesBeforeFourth).Should().NotBeEmpty().And.OnlyContain(r => r == "main");
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
            .Take(1).Timeout(TestTimeouts.Convergence).Await();

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
