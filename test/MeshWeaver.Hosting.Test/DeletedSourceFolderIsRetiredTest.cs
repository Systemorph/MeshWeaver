#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A source folder the repository DELETED is retired, not refused forever</b>
/// (<c>Doc/Architecture/SourceRetirement</c>).
///
/// <para><b>Measured on both production meshes, 2026-10-06.</b> The <c>DeepSign</c> package was
/// renamed to <c>Signature</c> (MeshWeaver.Plugins c3262d1e9). <c>DeepSign/_GitSync</c> kept
/// following <c>subdirectory: DeepSign</c>, found nothing there, and refused on every commit since
/// (<c>lastSyncOutcome: Refused</c>, 1,442 versions on memex.systemorph.com) — so the two menu
/// contributions it had imported stayed in every node's ⋯ More beside their Signature replacements.
/// The refusal exists for a mistyped subdirectory (#1326); a deleted folder produces the same empty
/// listing, and the two were never told apart.</para>
///
/// <para><b>What would falsify the fix:</b> the deletion must be PROVEN from git (the folder had
/// files at the last imported commit) — a source whose folder never had files is still refused
/// (<see cref="RefusedSourceSaysItIsRefusedTest"/>), and what goes is only what the source put there:
/// a node created in the partition at runtime, the partition root and the sync source all survive.</para>
/// </summary>
public class DeletedSourceFolderIsRetiredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string Folder = "OldPackage";
    private const string WithFolder = "a1a1a1a1000000000000000000000000000000aa";
    private const string FolderDeleted = "b2b2b2b2000000000000000000000000000000bb";
    private const string LaterCommit = "c3c3c3c3000000000000000000000000000000cc";

    private readonly PerCommitRepoClient repoClient = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins — the fetch answers per commit and never reaches the network.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private static string UserId => TestUsers.Admin.ObjectId!;

    [Fact(Timeout = 180_000)]
    public async Task ADeletedFolder_RetiresWhatTheSourceImported_AndKeepsEverythingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArmedSpace(ct);
        repoClient.Commits[WithFolder] =
        [
            Markdown("Guide", "# The old package's guide"),
            Markdown("Menu", "# Stands in for a UiContribution the package shipped"),
        ];
        repoClient.Commits[FolderDeleted] = [];
        repoClient.Commits[LaterCommit] = [];

        // ── the package is imported while its folder exists ─────────────────
        await Sync.ReimportAtCommit(space, WithFolder, UserId)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await NodeAppears($"{space}/Guide", ct);
        await NodeAppears($"{space}/Menu", ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == WithFolder, ct);

        // A node the partition gained at RUNTIME — never in any import manifest.
        await NodeFactory.CreateNode(new MeshNode("Runtime", space)
        {
            NodeType = "Markdown",
            Name = "Runtime state",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "written at runtime" },
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        // ── the repository deletes the folder ───────────────────────────────
        var result = await Sync.ReimportAtCommit(space, FolderDeleted, UserId)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"outcome={result.Outcome} pruned=[{string.Join(", ", result.PrunedPaths)}]");

        result.Outcome.Should().Be(GitHubSyncService.RetiredOutcome,
            "the folder had files at the last imported commit and has none now — git deleted it, "
            + "which is a retirement, not the mistyped subdirectory #1326 refuses");
        repoClient.ChangedPathsCalls.Should().Be(0,
            "an empty source is classified before diff scoping, even when the client supports comparisons");
        string.Join(",", result.PrunedPaths.OrderBy(p => p, StringComparer.Ordinal))
            .Should().Be($"{space}/Guide,{space}/Menu",
                "exactly the nodes the source imported go — the manifest is the provenance");

        await NodeGone($"{space}/Guide", ct);
        await NodeGone($"{space}/Menu", ct);
        (await Exists($"{space}/Runtime", ct)).Should().BeTrue(
            "a node created in the partition at runtime was never the source's to delete");
        (await Exists(space, ct)).Should().BeTrue(
            "the partition root stays: deleting it is recursive and is the governed package removal's job");
        (await Exists(GitHubSyncService.ConfigPath(space), ct)).Should().BeTrue(
            "the sync source stays — if the folder returns, the next sync imports it again");

        var retired = await ConfigWhen(space,
            c => c.LastSyncOutcome == GitHubSyncService.RetiredOutcome && c.LastSyncCommitSha == FolderDeleted, ct);
        retired.LastSyncNote.Should().Contain("no longer carries",
            "the conclusion is stated where an operator reads the source's state");

        // ── a later commit: still retired, no refusal ───────────────────────
        // Its folder is empty at the new base too, so only the recorded retirement can say so.
        var later = await Sync.ReimportAtCommit(space, LaterCommit, UserId)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        later.Outcome.Should().Be(GitHubSyncService.RetiredOutcome,
            "a retired source must not fall back to refusing on every commit after its retirement");
        later.PrunedPaths.Should().BeEmpty("there is nothing left of what the source imported");
    }

    [Fact(Timeout = 180_000)]
    public async Task AFolderThatWasNeverThere_IsStillRefused_EvenWithABaseCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArmedSpace(ct);
        repoClient.Commits[WithFolder] = [Markdown("Guide", "# A real page")];
        repoClient.Commits[FolderDeleted] = [];
        await Sync.ReimportAtCommit(space, WithFolder, UserId)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == WithFolder, ct);

        // The operator re-points the source at a folder that exists at NEITHER commit — the typo
        // #1326 guards against. The base it imported under the old folder proves nothing about this one.
        repoClient.EmptyFolders.Add("OldPackagee");
        await Sync.SaveConfig(space, RepoUrl, "main", "OldPackagee",
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var refusal = await Sync.ReimportAtCommit(space, FolderDeleted, UserId)
            .Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        refusal.Exception.Should().BeOfType<SyncSubdirectoryEmptyException>(
            "a folder with no files at the last imported commit either is a configuration fault, "
            + "and an empty snapshot imported under FullReplace would mirror the Space away");
        (await Exists($"{space}/Guide", ct)).Should().BeTrue("a refusal deletes nothing");
    }

    private static RepoFile Markdown(string id, string text)
        => new($"{id}.json",
            $$$"""{"id":"{{{id}}}","nodeType":"Markdown","name":"{{{id}}}","content":{"$type":"MarkdownContent","content":"{{{text}}}"}}""");

    private async Task<string> ArmedSpace(CancellationToken ct)
    {
        var space = "Retired" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Retired-source space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", Folder, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    private Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Select(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null && predicate(c))
            .Select(c => c!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<MeshNode> NodeAppears(string path, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Select(n => n!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>Waits on the store's own answer (a children listing of the parent) for the path to be absent.</summary>
    private Task<bool> NodeGone(string path, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Present(path))
            .Where(present => !present)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<bool> Exists(string path, CancellationToken ct)
        => Present(path).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private IObservable<bool> Present(string path)
        => MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Take(1)
            .Select(change => change.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The transport reduced to per-commit listings of the configured folder.</summary>
    private sealed class PerCommitRepoClient : IGitHubRepoClient
    {
        public Dictionary<string, IReadOnlyList<RepoFile>> Commits { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmptyFolders { get; } = new(StringComparer.Ordinal);
        public int ChangedPathsCalls { get; private set; }

        public IObservable<IReadOnlyList<string>?> GetChangedPaths(
            string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        {
            ChangedPathsCalls++;
            return Observable.Return<IReadOnlyList<string>?>(["Guide.json", "Menu.json"]);
        }

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(new RepoSnapshot(commitish,
                subdirectory is not null && EmptyFolders.Contains(subdirectory.Trim('/'))
                    ? Array.Empty<RepoFile>()
                    : Commits.TryGetValue(commitish, out var files) ? files : Array.Empty<RepoFile>()));

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
            string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubPullRequestDetail>();
        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("this test's transport answers only Fetch"));
    }
}
