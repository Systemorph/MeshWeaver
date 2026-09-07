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
/// 🚨 <b>A repository listing that came back INCOMPLETE is not a repository that deleted things</b>
/// (Systemorph/MeshWeaver#3589).
///
/// <para><b>The transport fact this is about.</b> GitHub answers the recursive-tree endpoint with
/// HTTP 200 and a PARTIAL blob list when the tree exceeds its response cap, flagging it only with
/// <c>truncated: true</c> in the body. Nothing else distinguishes that answer from a small
/// repository. <c>OctokitGitHubRepoClient.TreeOf</c> never read the flag, so a truncated tree flowed
/// into <c>Fetch</c> as "these are all the files there are" — and the import's prune, whose whole
/// premise is "absent from the source ⇒ deleted from the source", then deleted every mesh node whose
/// file GitHub had simply not returned. Under the default <c>FullReplace</c> that is the whole
/// Space.</para>
///
/// <para><b>Why the seam and not the unit.</b> <c>StaticRepoImporterIncompleteListingTest</c> pins
/// the DECISION in isolation. This one pins the WIRING: the flag has to survive
/// <c>RepoSnapshot</c> → <c>InMemoryStaticRepoSource</c> → <c>ComputePrunableNodes</c>, and every
/// #3589-shaped defect in this family has been a value that was read correctly somewhere and then
/// dropped on the way to the decision. The fake repo client is the IO boundary — a GitHub transport,
/// not a mesh interface — which is the one seam this codebase substitutes; everything else is the
/// real sync service, the real importer and the real node streams.</para>
///
/// <para><b>The two runs differ in ONE bit.</b> Same Space shape, same files, same commits, same
/// sync direction, same order — only <c>RepoSnapshot.ListingIsComplete</c> differs. The complete
/// run MUST still prune (it is the shipped mirror behaviour, and a guard that quietly disabled the
/// prune would be the #3589 symptom rather than its fix); the truncated run must prune nothing.
/// Before the fix both runs prune, which is exactly what makes this a regression test.</para>
/// </summary>
public class TruncatedRepoListingIsNotADeletionTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/truncated-tree";

    /// <summary>Two real-shaped 40-hex shas: the second import is at a NEW commit whose listing has
    /// lost a file, which is the only situation in which a prune is even considered.</summary>
    private const string FirstSha = "1111111122223333444455556666777788889999";

    private const string SecondSha = "aaaaaaaabbbbccccddddeeeeffff000011112222";

    /// <summary>The file the repository keeps carrying in both imports.</summary>
    private const string KeptFile = "Api.md";

    /// <summary>The file that is missing from the SECOND listing — the whole question being asked
    /// is whether "missing" means "retired" or "not returned".</summary>
    private const string MissingFile = "Retired.md";

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
    // (Two full Space imports run here, so the neighbouring sync tests' budget is used, not HubFact's.)
    [Fact(Timeout = 240_000)]
    public async Task ATruncatedListing_PrunesNothing_WhileTheSameImportWithACompleteOneStillPrunes()
    {
        // ── THE CONTROL FIRST. If this stops pruning, the "fix" has disabled the mirror rather than
        //    guarded it, and the assertion below would pass for the wrong reason.
        var control = await ImportThenDropAFile(listingIsComplete: true);
        Output.WriteLine($"complete listing: outcome={control.Outcome} pruned=[{string.Join(", ", control.PrunedPaths)}]");
        control.PrunedPaths.Should().Contain(control.MissingNodePath,
            "a COMPLETE listing that no longer carries the file is evidence the author retired it — "
            + "mirroring the partition to the repo is the shipped FullReplace behaviour");

        // ── THE GUARD. Byte-identical fixture; the transport says its answer was truncated.
        var truncated = await ImportThenDropAFile(listingIsComplete: false);
        Output.WriteLine($"truncated listing: outcome={truncated.Outcome} pruned=[{string.Join(", ", truncated.PrunedPaths)}]");
        truncated.PrunedPaths.Should().BeEmpty(
            "GitHub returns HTTP 200 with a truncated tree, so a file missing from the listing is an "
            + "UNREAD file, not a deleted one — pruning on it deletes live nodes on the strength of a "
            + "read that never happened, and the deletion is indistinguishable from an intended one");

        // …and the node is demonstrably still there. Read from the authoritative node stream, never
        // a query: this is read immediately after a write, and it exists (so no absent-node point read).
        var survivor = await Mesh.GetWorkspace().GetMeshNodeStream(truncated.MissingNodePath)
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
        survivor.Should().NotBeNull(
            "the guard is about the node still existing, not merely about a count being zero");
    }

    /// <summary>The outcome of one scripted two-import run against a fresh Space.</summary>
    private sealed record Run(string Outcome, IReadOnlyList<string> PrunedPaths, string MissingNodePath);

    /// <summary>
    /// Imports a Space twice from the scripted repo: first with BOTH files (materializing them), then
    /// at a new commit whose listing carries only <see cref="KeptFile"/> and declares itself complete
    /// or not. Returns what the second import concluded.
    /// </summary>
    private async Task<Run> ImportThenDropAFile(bool listingIsComplete)
    {
        var space = "Tt" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Truncated tree",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        // ImportOnly — a repo → mesh mirror. Bidirectional would additionally protect server-side
        // ADDITIONS from the prune (#604), which is a DIFFERENT guard: it would keep the node in both
        // runs and the control could no longer show the prune working.
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false,
                direction: SyncDirection.ImportOnly)
            .Timeout(TestTimeouts.Convergence).Await();

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await();

        // ── 1. Both files land. If they do not, the second import has nothing to prune and the
        //       measurement below is vacuous — so the count is asserted, not assumed.
        repoClient.Serve(FirstSha, complete: true, KeptFile, MissingFile);
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"{space} import 1: outcome={first.Outcome} count={first.Count}");
        first.Outcome.Should().Be("Imported",
            "the first import must really materialize both files — otherwise the second one is not "
            + "measuring a prune decision at all");
        first.WrittenPaths.Should().HaveCountGreaterThanOrEqualTo(2,
            "both repo files must have become nodes before the prune question can be asked");

        // ── 2. THE MEASURED SHAPE. A new commit whose listing has lost one file. Whether that is a
        //       deletion depends entirely on whether the listing can be trusted to be whole.
        repoClient.Serve(SecondSha, listingIsComplete, KeptFile);
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} count={second.Count}");
        second.Outcome.Should().NotBe("Skipped",
            "a dropped file changes the content fingerprint, so this import really runs — a Skipped "
            + "here would mean the prune phase was never reached and nothing was measured");

        return new Run(second.Outcome, second.PrunedPaths, $"{space}/{MissingFile[..^3]}");
    }

    /// <summary>
    /// The GitHub transport reduced to the three things this test scripts: which commit, which files,
    /// and — the whole subject — whether the listing that produced them was COMPLETE. Everything else
    /// throws, so a future caller that starts depending on another operation is told rather than
    /// silently served a fake answer.
    /// </summary>
    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        private RepoSnapshot snapshot = new("0000000000000000000000000000000000000000", [])
            { ListingIsComplete = true };

        /// <summary>Scripts the next fetch: the commit, the completeness verdict, and the files.</summary>
        public void Serve(string sha, bool complete, params string[] files) =>
            snapshot = new RepoSnapshot(
                sha,
                Array.ConvertAll(files, f => new RepoFile(f, $"# {f}\n\nContent of {f}.\n")))
            {
                // 🚨 THE ONE BIT UNDER TEST. Production sets this from GitHub's `truncated` flag on
                // the recursive-tree response — an HTTP 200 whose body is only part of the answer.
                ListingIsComplete = complete,
            };

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(snapshot);

        // The default GetChangedPaths (null → full import) is deliberately inherited: a client with no
        // compare support is the documented safe fallback, and a diff-scoped run would not reach the
        // prune phase this test is about.

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
                "TruncatedRepoListingIsNotADeletionTest's repo client answers only Fetch."));
    }
}
