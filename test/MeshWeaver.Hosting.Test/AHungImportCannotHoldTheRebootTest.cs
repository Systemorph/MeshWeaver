using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#6625 — a reboot request sat non-terminal for ~21 h (build) and in
/// <c>Preparing</c>/<c>Sync</c> (memex), and while ANY reboot request is non-terminal the watchdog
/// suppresses every pod's self-reboot. The Sync step is <see cref="ModuleSourceUpdate.UpdateAll"/>,
/// whose per-source budget (<see cref="ModuleSourceUpdate.ActivityBudget"/>, "how long one source's
/// import may take before its row reads Failed") was composed AFTER the import's activity path was
/// emitted — and <c>RunActivity</c> emits that path only when the run is TERMINAL. An import that never
/// finished therefore held the step, and the request, forever: the budget bounded nothing.
///
/// <para>Reproduced with the one IO seam substituted: a GitHub transport whose fetch NEVER answers
/// (and ignores cancellation), exactly the shape of a hung import. The row must come back Failed,
/// naming the budget, within the budget — and the fetch having been subscribed is the control that
/// the import really ran and really hung, so the row is not a refusal from before the import.</para>
/// </summary>
public class AHungImportCannotHoldTheRebootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/hung-import";
    private readonly HangingRepoClient repoClient = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services.AddSingleton<IGitHubRepoClient>(repoClient);
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private async Task<string> GivenAHungModuleSource(string prefix, CancellationToken ct)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Hung-import space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        // The reboot imports as System (ModuleSourceUpdate passes WellKnownUsers.System), so the
        // System principal is the one whose credential the fetch resolves.
        await Credentials
            .Save(WellKnownUsers.System, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        // A MODULE source: it has recorded a module-bearing tree, so a reboot re-imports it.
        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Update(node => node with
            {
                Content = node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)! with
                {
                    ModuleVersions = ImmutableDictionary<string, string>.Empty.Add("MeshWeaver.Test.Hung", "1.0.0"),
                },
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);

        return space;
    }

    [Fact(Timeout = 240_000)]
    public async Task AnImportThatNeverFinishes_EndsItsRowFailed_WithinTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await GivenAHungModuleSource("Hung", ct);

        var fetched = repoClient.Fetches.FirstAsync().Replay();
        using var connection = fetched.Connect();

        var budget = TimeSpan.FromSeconds(3);
        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, budget)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a hung import must end its row within the budget — the reboot's Sync step waits on it", ct);

        Output.WriteLine(string.Join(Environment.NewLine, rows.Select(r => $"{r.ConfigPath}: {r.Outcome} — {r.Detail}")));
        await fetched.Should().Within(TestTimeouts.Quick)
            .Emit("the control: the import really ran and its fetch really hung", ct);
        var row = rows.Single(r => r.Space == space);
        row.Outcome.Should().Be(ModuleSourceUpdateOutcome.Failed);
        row.Detail.Should().Contain("did not finish within 3 s").And.Contain("NOT established");

        // The row's promise — "its cancellation was requested" — is checked on the activity node.
        var activity = row.Detail[(row.Detail.IndexOf("(activity ", StringComparison.Ordinal) + "(activity ".Length)..];
        activity = activity[..activity.IndexOf(')')];
        await Mesh.GetWorkspace().GetMeshNodeStream(activity)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)?.RequestedStatus == ActivityStatus.Cancelled,
                because: "the timed-out import's cancellation is requested on its activity", cancellationToken: ct);
    }

    /// <summary>
    /// 🚨 #6625 review: an import that outlived its budget may still be running, and several sources
    /// can target one Space — so no later import is started behind it. The later source reads Skipped,
    /// naming the import it waits behind, and only ONE fetch ever starts (the control).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task NoLaterImportStarts_BehindOneThatOutlivedItsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await GivenAHungModuleSource("HungA", ct);
        var second = await GivenAHungModuleSource("HungB", ct);
        var fetchCount = 0;
        using var counting = repoClient.Fetches.Subscribe(_ => Interlocked.Increment(ref fetchCount));

        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromSeconds(3))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batch must end once one import outlives its budget", ct);

        Output.WriteLine(string.Join(Environment.NewLine, rows.Select(r => $"{r.ConfigPath}: {r.Outcome} — {r.Detail}")));
        var mine = rows.Where(r => r.Space == first || r.Space == second).ToArray();
        mine.Should().HaveCount(2);
        mine[0].Outcome.Should().Be(ModuleSourceUpdateOutcome.Failed);
        mine[1].Outcome.Should().Be(ModuleSourceUpdateOutcome.Skipped);
        mine[1].Detail.Should().Contain("not started");
        Volatile.Read(ref fetchCount).Should().Be(1, "the second import must never have started");
    }

    /// <summary>The transport reduced to a fetch that never answers, recorded on subscribe.</summary>
    private sealed class HangingRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetches = new();

        public IObservable<string> Fetches => fetches;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Defer(() =>
            {
                fetches.OnNext(commitish);
                return Observable.Never<RepoSnapshot>();
            });

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
            new NotSupportedException("AHungImportCannotHoldTheRebootTest's repo client answers only Fetch."));
    }
}
