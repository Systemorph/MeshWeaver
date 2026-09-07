using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A sync source's node must be able to say WHEN it last synced — and the two-way conflict
/// horizon is not that answer</b> (Systemorph/MeshWeaver#3581).
///
/// <para><b>What was measured.</b> On 2026-09-07 both AKS portals carried an <c>Edu/_GitSync</c>
/// whose <c>lastSyncCommitSha</c> was that morning's Plugins <c>main</c> (<c>e8f315bf</c>) beside a
/// <c>lastSyncedAt</c> of 2026-07-11 (memex) and 2026-08-07 (memex-cloud), on nodes rewritten to
/// v250 / v432 minutes after that sha landed. The settings tab printed the pair on one line as
/// "Last synced: &lt;July&gt; — commit &lt;September&gt;", so the node contradicted itself and the
/// first question of every "why is this partition compiling instead of adopting" investigation had
/// to be answered by comparing node timestamps against pair tags in a container registry.</para>
///
/// <para><b>Neither field was lying.</b> <c>LastSyncedAt</c> is the two-way CONFLICT HORIZON — the
/// last instant mesh and repo were RECONCILED — and it is deliberately held back by a
/// fingerprint-matched no-op (#677), by an import that preserved server-newer nodes (#675) and by
/// one that did not land everything (#2229 item C). Advancing it on those outcomes moves it past
/// pending uncommitted server changes and lets a later push prune them, which is the data loss
/// those issues are about. <c>LastSyncCommitSha</c> legitimately advances on a no-op. The gap was
/// that NOTHING recorded the third fact — that a sync ran at all.</para>
///
/// <para><b>So this test asserts both halves at once</b>, because a fix for one that breaks the
/// other is the likely regression: a no-op re-sync must record WHEN it ran and WHAT it concluded,
/// and must NOT move the horizon. Before the fix the first half is impossible (the fields do not
/// exist and the branch wrote nothing at all); a later "simplification" that folds the recency
/// stamp into <c>LastSyncedAt</c> fails the second.</para>
///
/// <para>The fake repo client is the IO boundary — a GitHub transport, not a mesh interface — which
/// is the one seam this codebase substitutes. Everything else is the real mesh: the real sync
/// service, the real importer, the real node streams.</para>
/// </summary>
public class SyncNodeSaysWhenItLastSyncedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/says-when";

    /// <summary>Two real-shaped 40-hex shas: the repo moves forward while its CONTENT does not,
    /// which is exactly the shape that produces a no-op import.</summary>
    private const string FirstSha = "1111111122223333444455556666777788889999";

    private const string SecondSha = "aaaaaaaabbbbccccddddeeeeffff000011112222";

    private readonly ScriptedRepoClient repoClient = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the scripted client replaces the git/Octokit transport.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, and
    // the inner waits below already carry the adaptive bound. This outer one only stops a WEDGE.
    [Fact(Timeout = 240_000)]
    public async Task ANoOpReSync_RecordsWhenItRan_WhileTheConflictHorizonStaysPut()
    {
        var space = "Sw" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Says when",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await();

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await();

        // ── 1. A real import. Every field moves together, which is the state the second import
        //       must then be measured against.
        repoClient.Sha = FirstSha;
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"import 1: outcome={first.Outcome} count={first.Count}");
        first.Outcome.Should().Be("Imported",
            "the first import of this content really does materialize it — if it does not, the "
            + "second import below is not measuring a NO-OP and the test proves nothing");

        var afterFirst = await ConfigWhen(space, c => c.LastSyncCommitSha == FirstSha);
        Output.WriteLine(
            $"after 1: attemptAt={afterFirst.LastSyncAttemptAt:O} outcome={afterFirst.LastSyncOutcome} "
            + $"sha={afterFirst.LastSyncCommitSha} horizon={afterFirst.LastSyncedAt:O}");

        afterFirst.LastSyncAttemptAt.Should().NotBeNull(
            "a sync that ran must say when it ran — that is the whole of #3581");
        afterFirst.LastSyncOutcome.Should().Be("Imported");
        afterFirst.LastSyncedAt.Should().NotBeNull(
            "a clean reconciling import DOES advance the conflict horizon; only the suppressed "
            + "outcomes hold it back");
        var horizon = afterFirst.LastSyncedAt!.Value;

        // A wall-clock reading taken AFTER the first import's write landed. The second import's
        // stamp is compared against this rather than against the first stamp, so the assertion is
        // "it was written again", not "two timestamps happened to differ" — no clock is waited on.
        var betweenImports = DateTimeOffset.UtcNow;

        // ── 2. THE MEASURED SHAPE. The repository moves to a new commit carrying the SAME content,
        //       so the importer short-circuits on its own content fingerprint and reports Skipped.
        //       This is what Edu did every day: the sha advances, the horizon must not.
        repoClient.Sha = SecondSha;
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"import 2: outcome={second.Outcome} count={second.Count}");
        second.Outcome.Should().Be("Skipped",
            "identical content at a new commit is the no-op the fingerprint gate exists for — if "
            + "this reads 'Imported' the fixture changed and the rest of the assertions are moot");

        var afterSecond = await ConfigWhen(space, c => c.LastSyncCommitSha == SecondSha);
        Output.WriteLine(
            $"after 2: attemptAt={afterSecond.LastSyncAttemptAt:O} outcome={afterSecond.LastSyncOutcome} "
            + $"sha={afterSecond.LastSyncCommitSha} horizon={afterSecond.LastSyncedAt:O}");

        // Half one — the gap #3581 reported. Before the fix this branch wrote ONLY the sha, so the
        // node carried no record that anything ran and no record of what it concluded.
        afterSecond.LastSyncAttemptAt.Should().NotBeNull();
        afterSecond.LastSyncAttemptAt!.Value.Should().BeOnOrAfter(betweenImports,
            "the no-op re-sync must stamp its OWN run — a stamp left at the first import's value "
            + "is the frozen field this issue is about");
        afterSecond.LastSyncOutcome.Should().Be("Skipped",
            "'nothing to import' is an outcome a reader needs, and it is the one the Edu rows could "
            + "not report");

        // Half two — the invariant a careless fix breaks. The horizon may NOT ride along.
        afterSecond.LastSyncedAt.Should().Be(horizon,
            "a fingerprint-matched no-op verified NOTHING against the live partition, so advancing "
            + "the conflict horizon would move it past pending uncommitted server changes and let a "
            + "later push prune them (#675/#677). The recency stamp is a SEPARATE field precisely "
            + "so that recording 'a sync ran' cannot disarm that protection");
    }

    /// <summary>
    /// The sync config as the authoritative node stream reports it, once it satisfies
    /// <paramref name="predicate"/> — never a query (eventually consistent, and this reads right
    /// after a write), and never a bare first emission (the cache can replay the pre-write value).
    /// </summary>
    private async Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate)
    {
        var path = GitHubSyncService.ConfigPath(space);
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    /// <summary>
    /// The GitHub transport reduced to the two questions this test asks: what does the branch hold,
    /// and at which commit. The CONTENT is constant while <see cref="Sha"/> moves — the fixture for
    /// "the repo advanced, the tree did not". Everything else throws, so a future caller that starts
    /// depending on another operation is told rather than silently served a fake answer.
    /// </summary>
    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        /// <summary>The commit the next fetch reports. Written by the test between imports.</summary>
        public string Sha { get; set; } = "0000000000000000000000000000000000000000";

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(new RepoSnapshot(Sha, new[]
            {
                new RepoFile("A.md", "# A\n\nThe one page this repository carries.\n"),
            }));

        // The default GetChangedPaths (null → full import) is deliberately inherited: a client with
        // no compare support is the documented safe fallback, and a scoped run would not stamp the
        // content fingerprint this test needs the second import to match.

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
                "SyncNodeSaysWhenItLastSyncedTest's repo client answers only Fetch."));
    }
}
