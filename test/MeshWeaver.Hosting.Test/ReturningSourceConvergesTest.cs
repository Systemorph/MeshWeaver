using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A historical success marker cannot describe the current partition after another source was
/// imported. Exercises the real GitSync service and importer; only GitHub's transport is scripted.
/// </summary>
public class ReturningSourceConvergesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/returning-source";
    private const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly ScriptedRepoClient repoClient = new();
    private static string UserId => TestUsers.Admin.ObjectId!;
    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGitHubSyncTypes().ConfigureServices(services =>
        {
            services.AddGitHubSyncServices();
            services.AddSingleton<IGitHubRepoClient>(repoClient);
            return services;
        });

    [Theory(Timeout = 240_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningToAnEarlierImportedSource_RestoresChangedAndPrunedNodes_ThenSkips(bool rootOnly)
    {
        repoClient.RootOnly = rootOnly;
        var space = await Prepare(cancellationToken: TestContext.Current.CancellationToken);
        await Import(space, RevisionB, TestContext.Current.CancellationToken);
        var rollback = await Import(space, RevisionA, TestContext.Current.CancellationToken);
        if (rootOnly)
            (await RootName(space, TestContext.Current.CancellationToken)).Should().Be("Root revision A");
        else
        {
            rollback.PrunedPaths.Should().Contain($"{space}/Added");
            (await Body($"{space}/Existing", TestContext.Current.CancellationToken)).Should().Contain("revision A");
        }

        var restored = await Import(space, RevisionB, TestContext.Current.CancellationToken);
        restored.Outcome.Should().Be("Imported",
            "the historical B marker survived the A import, but the current manifest describes A");
        if (rootOnly)
            (await RootName(space, TestContext.Current.CancellationToken)).Should().Be("Root revision B");
        else
        {
            restored.WrittenPaths.Should().Contain($"{space}/Existing");
            restored.WrittenPaths.Should().Contain($"{space}/Added");
            await AssertRevisionB(space, TestContext.Current.CancellationToken);
        }
        await AssertUnchangedRepeat(space, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 240_000)]
    public async Task ScopedRootChange_CannotLeaveThePreviousRootsMarkerCurrent()
    {
        repoClient.RootOnly = true;
        var space = await Prepare(cancellationToken: TestContext.Current.CancellationToken);
        await Import(space, RevisionB, TestContext.Current.CancellationToken);
        repoClient.ScopedRootChanges = true;

        await Import(space, RevisionA, TestContext.Current.CancellationToken);
        (await RootName(space, TestContext.Current.CancellationToken)).Should().Be("Root revision A",
            "the scoped import really refreshed the root before the return to B");

        var restored = await Import(space, RevisionB, TestContext.Current.CancellationToken);
        (await RootName(space, TestContext.Current.CancellationToken)).Should().Be("Root revision B",
            "the A import evaluated its root even though Git's index.json path is outside the child scope");
        restored.Outcome.Should().Be("Imported");
        await AssertUnchangedRepeat(space, TestContext.Current.CancellationToken);
    }

    [Theory(Timeout = 240_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImportAtTheRecordedCommit_EvaluatesStaleNodesOutsideAnEmptyGitDiff(
        bool reconcile, bool protectHumanEdit)
    {
        var space = await Prepare(twoWay: protectHumanEdit, cancellationToken: TestContext.Current.CancellationToken);
        await Import(space, RevisionB, TestContext.Current.CancellationToken);
        await Import(space, RevisionA, TestContext.Current.CancellationToken);
        var configPath = GitHubSyncService.ConfigPath(space);
        // This is the persisted production shape after a false skip: Git says B was seen,
        // while the authoritative import manifest and actual source nodes still describe A.
        await Mesh.GetWorkspace().GetMeshNodeStream(configPath).Update(node => node with
        {
            Content = node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)! with
            {
                LastSyncCommitSha = RevisionB,
                LastSyncOutcome = "Skipped",
            },
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var before = await Config(space, TestContext.Current.CancellationToken);

        if (protectHumanEdit)
            await Mesh.GetWorkspace().GetMeshNodeStream($"{space}/Authored").Update(node => node with
            {
                Content = new MarkdownContent { Content = "A person's uncommitted revision" },
            }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var result = await (reconcile
                ? Sync.ReconcileAtCommit(space, RevisionB, UserId)
                : Sync.ReimportAtCommit(space, RevisionB, UserId))
            .Should().Within(TestTimeouts.Convergence * 2).Emit();
        result.WrittenPaths.Should().Contain($"{space}/Existing",
            "an empty B..B Git diff cannot describe drift in the live partition");
        result.WrittenPaths.Should().Contain($"{space}/Added");
        await AssertRevisionB(space, TestContext.Current.CancellationToken);
        if (protectHumanEdit)
        {
            result.Preserved.Should().Be(1);
            (await Body($"{space}/Authored", TestContext.Current.CancellationToken)).Should().Contain("uncommitted revision");
            (await Config(space, TestContext.Current.CancellationToken)).LastSyncedAt.Should().Be(before.LastSyncedAt,
                "reconciliation must keep the two-way conflict horizon while a person's edit remains");
        }
        else
            await AssertUnchangedRepeat(space, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 240_000)]
    public async Task Reconcile_EvaluatesLiveSourceEvenWhenItsManifestStillMatches()
    {
        var space = await Prepare(twoWay: true, cancellationToken: TestContext.Current.CancellationToken);
        await Import(space, RevisionB, TestContext.Current.CancellationToken);
        // Reproduce a system-written source changing after the ledger was recorded. This is
        // measured live drift, not a person's edit and not a change between Git commits.
        IObservable<MeshNode> write;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            write = Mesh.GetWorkspace().GetMeshNodeStream($"{space}/Existing").Update(node => node with
            {
                Content = new MarkdownContent { Content = "System-written stale revision A" },
            });
        await write.Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (await Body($"{space}/Existing", TestContext.Current.CancellationToken)).Should().Contain("revision A");

        var result = await Sync.ReconcileAtCommit(space, RevisionB, UserId)
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
        result.WrittenPaths.Should().Contain($"{space}/Existing",
            "reconciliation evaluates the measured live mismatch even when the source ledger matches B");
        await AssertRevisionB(space, TestContext.Current.CancellationToken);
        await AssertUnchangedRepeat(space, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 240_000)]
    public async Task VersionOnlySourceRevision_DoesNotRewriteIdenticalContentOrTheOwnerClock()
    {
        var partition = "VersionReturn" + Guid.NewGuid().ToString("N")[..8];
        var firstSource = new StaticSource(partition, Versioned: true,
            ImmutableList.Create(new MeshNode("Page", partition)
            {
                NodeType = "Markdown", Name = "Unchanged source", Version = 42,
                Content = new MarkdownContent { Content = "Identical authored content" },
            }));
        var first = await StaticRepoImporter.ImportSource(Mesh, firstSource)
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
        first.Outcome.Should().Be("Imported");
        var path = $"{partition}/Page";
        var before = await Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var nextSource = firstSource with
        {
            Nodes = firstSource.Nodes.SetItem(0, firstSource.Nodes[0] with { Version = 43 }),
        };
        var next = await StaticRepoImporter.ImportSource(Mesh, nextSource)
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
        next.Fingerprint.Should().NotBe(first.Fingerprint,
            "Versioned sources use their revision in the import fingerprint");
        next.Outcome.Should().Be("Imported");
        next.WrittenPaths.Should().BeEmpty(
            "source revision alone is not an authored content change or an owner-clock assignment");

        foreach (var source in ImmutableList.Create(firstSource, nextSource))
        {
            var repeat = await StaticRepoImporter.ImportSource(Mesh, source)
                .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
            repeat.Outcome.Should().Be("Skipped");
            var after = await Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
            after.Version.Should().Be(before.Version,
                "the owner clocks writes; imported source revisions must not create a write of identical content");
            after.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)!.Content
                .Should().Be("Identical authored content");
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task EmptyPathIgnoredByTheFingerprint_DoesNotInvalidateTheCurrentManifest()
    {
        var partition = "EmptyPathReturn" + Guid.NewGuid().ToString("N")[..8];
        var source = new StaticSource(partition, Versioned: false,
            ImmutableList.Create(new MeshNode("Page", partition)
            {
                NodeType = "Markdown", Name = "Valid source",
                Content = new MarkdownContent { Content = "Preserved source" },
            }));
        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
        first.Outcome.Should().Be("Imported");

        var repeat = await StaticRepoImporter.ImportSource(Mesh,
                source with { Nodes = source.Nodes.Add(new MeshNode(string.Empty)) })
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: TestContext.Current.CancellationToken);
        repeat.Fingerprint.Should().Be(first.Fingerprint,
            "the fingerprint and manifest writer both omit empty source paths");
        repeat.Outcome.Should().Be("Skipped");
        (await Body($"{partition}/Page", TestContext.Current.CancellationToken)).Should().Be("Preserved source");
    }

    private sealed record StaticSource(string Partition, bool Versioned, ImmutableList<MeshNode> Nodes)
        : IStaticRepoSource
    {
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
    }

    private async Task<string> Prepare(bool twoWay = false, CancellationToken cancellationToken = default)
    {
        var space = "Return" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space", Name = "Returning source", State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);
        var node = await Sync.SaveConfig(space, RepoUrl, "main", null, false, false,
                direction: SyncDirection.ImportOnly, twoWay: twoWay)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);
        var owner = node.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>()
            .Save(owner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);
        return space;
    }

    private async Task<StaticRepoImportResult> Import(
        string space, string revision, CancellationToken cancellationToken)
    {
        var result = await Sync.ReimportAtCommit(space, revision, UserId)
            .Should().Within(TestTimeouts.Convergence * 2).Emit(cancellationToken: cancellationToken);
        Output.WriteLine($"{revision[0]}: {result.Outcome}; written={string.Join(',', result.WrittenPaths)}; "
            + $"pruned={string.Join(',', result.PrunedPaths)}; preserved={result.Preserved}");
        result.Failed.Should().Be(0);
        return result;
    }

    private async Task AssertRevisionB(string space, CancellationToken cancellationToken)
    {
        (await Body($"{space}/Existing", cancellationToken)).Should().Contain("revision B");
        (await Body($"{space}/Added", cancellationToken)).Should().Contain("Only in B");
    }

    private async Task AssertUnchangedRepeat(string space, CancellationToken cancellationToken)
    {
        var repeated = await Import(space, RevisionB, cancellationToken);
        repeated.Outcome.Should().Be("Skipped");
        repeated.Count.Should().Be(0);
        repeated.WrittenPaths.Should().BeEmpty();
        repeated.PrunedPaths.Should().BeEmpty();
    }

    private async Task<string> Body(string path, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private async Task<string?> RootName(string space, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(space).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);
        return node.Name;
    }

    private async Task<GitHubSyncConfig> Config(string space, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: cancellationToken);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        public bool RootOnly { get; set; }
        public bool ScopedRootChanges { get; set; }
        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            if (RootOnly)
                return Observable.Return(new RepoSnapshot(commitish, new[]
                {
                    new RepoFile("index.json", $$"""{"nodeType":"Space","name":"Root revision {{(commitish == RevisionB ? "B" : "A")}}"}"""),
                    new RepoFile("Existing.md", "# Existing\n\nUnchanged source"),
                }));
            var files = ImmutableList.Create(
                new RepoFile("Existing.md", $"# Existing\n\nrevision {(commitish == RevisionB ? "B" : "A")}"),
                new RepoFile("Authored.md", "# Authored\n\nRepository copy"));
            if (commitish == RevisionB)
                files = files.Add(new RepoFile("Added.md", "# Added\n\nOnly in B"));
            return Observable.Return(new RepoSnapshot(commitish, files));
        }

        public IObservable<IReadOnlyList<string>?> GetChangedPaths(
            string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
            => Observable.Return<IReadOnlyList<string>?>(baseSha == headSha
                ? Array.Empty<string>()
                : ScopedRootChanges ? new[] { "index.json" } : null);

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

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException(
                "ReturningSourceConvergesTest's repo client answers only Fetch."));
    }
}
