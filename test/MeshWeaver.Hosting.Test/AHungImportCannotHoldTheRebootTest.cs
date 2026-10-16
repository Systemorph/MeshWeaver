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

    private Task<string> GivenAHungModuleSource(string prefix, CancellationToken ct) => GivenASource(prefix, moduleSource: true, ct);

    private async Task<string> GivenASource(string prefix, bool moduleSource, CancellationToken ct)
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
        if (!moduleSource)
            return space;
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
    /// 🚨 #6625 review: the reboot's progress is judged against its declared execution window, and the
    /// Sync step imports its sources one after another — so each source's row is REPORTED the moment it
    /// ends, before the next source starts (<see cref="ModuleSourceUpdate.UpdateAllReporting"/>). The
    /// first row is reported before the second source's fetch began; every row is reported, in order,
    /// before the batch emits.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task EachSourceIsReported_AsItEnds_BeforeTheNextStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await GivenASource("ProgA", moduleSource: false, ct);
        var second = await GivenAHungModuleSource("ProgB", ct);
        var fetchCount = 0;
        using var counting = repoClient.Fetches.Subscribe(_ => Interlocked.Increment(ref fetchCount));
        var reported = ImmutableList<(ModuleSourceUpdateRow Row, int FetchesThen)>.Empty;

        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromSeconds(3),
                activity => Mesh.GetMeshNodeStream(activity)
                    .Select(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
                    .Where(log => log is not null)
                    .Select(log => log!),
                row => Observable.Defer(() =>
                {
                    ImmutableInterlocked.Update(ref reported, r => r.Add((row, Volatile.Read(ref fetchCount))));
                    return Observable.Return(true);
                }))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batch ends once the hung import outlived its budget", ct);

        var mine = rows.Where(r => r.Space == first || r.Space == second).ToArray();
        mine.Should().HaveCount(2);
        var mineReported = reported.Where(x => x.Row.Space == first || x.Row.Space == second).ToArray();
        mineReported.Select(x => x.Row).Should().Equal(mine, "every finished source is reported, in order, before the batch emits");
        mineReported[0].Row.Outcome.Should().Be(ModuleSourceUpdateOutcome.Skipped);
        mineReported[0].FetchesThen.Should().Be(0, "the first source is reported before the next source's import started");
        mineReported[1].Row.Outcome.Should().Be(ModuleSourceUpdateOutcome.Failed);
    }

    /// <summary>
    /// 🚨 #6625 review: the caller's work ENDED (a reboot that is no longer running) while a source was
    /// still pending — its answer is <c>false</c>, and no further import starts beside whatever replaced
    /// it. The pending source reads Skipped, saying so, and its fetch never starts (the control).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ACallerThatEnded_StartsNoFurtherImport()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await GivenASource("StopA", moduleSource: false, ct);
        var second = await GivenAHungModuleSource("StopB", ct);
        var fetchCount = 0;
        using var counting = repoClient.Fetches.Subscribe(_ => Interlocked.Increment(ref fetchCount));

        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromSeconds(3),
                _ => Observable.Never<ActivityLog>(),
                row => Observable.Return(row.Space != first))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batch ends when the caller stops it", ct);

        var mine = rows.Where(r => r.Space == first || r.Space == second).ToArray();
        mine.Should().HaveCount(2);
        mine[1].Outcome.Should().Be(ModuleSourceUpdateOutcome.Skipped);
        mine[1].Detail.Should().Contain("the caller stopped the batch");
        Volatile.Read(ref fetchCount).Should().Be(0, "no import starts after the caller's work ended");
    }

    /// <summary>
    /// 🚨 #6625 review: a fault from the caller's reporting reaches the caller — it is never folded into
    /// the "could not be listed — NOTHING was synced" row over a source that did run.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AReportingFault_ReachesTheCaller_NotTheListingFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await GivenASource("FaultR", moduleSource: false, ct);

        var outcome = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromSeconds(3),
                _ => Observable.Never<ActivityLog>(),
                _ => Observable.Throw<bool>(new InvalidOperationException("progress could not be recorded")))
            .Materialize()
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        outcome.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            outcome.Kind == System.Reactive.NotificationKind.OnNext
                ? string.Join(" | ", outcome.Value.Select(r => r.Detail))
                : "");
        outcome.Exception!.Message.Should().Be("progress could not be recorded");
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

    /// <summary>
    /// 🚨 MeshWeaver#6753 — the watch on the import's activity FAULTS after the activity exists (the
    /// gate query faulting, or the point read terminating with a routing error). That proves nothing
    /// about the import, which may still be writing — so the row must name the activity as still
    /// running and no later import may start. The fault used to reach <c>One</c>'s catch, which reported
    /// "nothing running", and the second source's import started beside the first: the fetch count is
    /// the control that it did not.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AWatchFaultAfterTheActivityExists_KeepsTheImportRunning_AndStartsNoLaterImport()
    {
        var ct = TestContext.Current.CancellationToken;
        await AFaultedWatch_EndsTheBatch_WithOneImportStarted("FaultA", "FaultB",
            _ => Observable.Throw<ActivityLog>(new InvalidOperationException("the activity's point read met a routing NotFound")),
            ct);
    }

    /// <summary>
    /// 🚨 #6625 review — the same fault, raised by the watch delegate ITSELF while it is being built
    /// (a synchronous throw, not an <c>Observable.Throw</c>). Built outside the watch's own catch, it
    /// reached <c>One</c>'s catch, which answers "nothing running", and the second import started beside
    /// the first: the fetch count is the control.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AWatchThatThrowsWhileBeingBuilt_KeepsTheImportRunning_AndStartsNoLaterImport()
    {
        var ct = TestContext.Current.CancellationToken;
        await AFaultedWatch_EndsTheBatch_WithOneImportStarted("ThrowA", "ThrowB",
            _ => throw new InvalidOperationException("the watch could not be built"),
            ct);
    }

    private async Task AFaultedWatch_EndsTheBatch_WithOneImportStarted(
        string firstPrefix, string secondPrefix, Func<string, IObservable<ActivityLog>> watch, CancellationToken ct)
    {
        var first = await GivenAHungModuleSource(firstPrefix, ct);
        var second = await GivenAHungModuleSource(secondPrefix, ct);
        var fetchCount = 0;
        using var counting = repoClient.Fetches.Subscribe(_ => Interlocked.Increment(ref fetchCount));
        var firstFetch = repoClient.Fetches.FirstAsync().Replay();
        using var firstFetchConnection = firstFetch.Connect();

        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromMinutes(5), watch)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a faulted watch must still end the batch with a row per source", ct);

        Output.WriteLine(string.Join(Environment.NewLine, rows.Select(r => $"{r.ConfigPath}: {r.Outcome} — {r.Detail}")));
        // The control: the first import really started (the watch faults from onActivityCreated, so the
        // rows can arrive before the import's fetch is subscribed — wait for it, never accept zero).
        await firstFetch.Should().Within(TestTimeouts.Convergence)
            .Emit("the control: the first import really started", ct);
        var mine = rows.Where(r => r.Space == first || r.Space == second).ToArray();
        mine.Should().HaveCount(2);
        mine[0].Outcome.Should().Be(ModuleSourceUpdateOutcome.Failed);
        mine[0].Detail.Should().Contain("faulted").And.Contain("may still be running").And.Contain("NOT established");
        mine[1].Outcome.Should().Be(ModuleSourceUpdateOutcome.Skipped);
        mine[1].Detail.Should().Contain("not started");
        Volatile.Read(ref fetchCount).Should().Be(1, "the first import started and the second must never have");
    }

    /// <summary>#6625 review — a non-positive budget is refused at the boundary, before any source is
    /// listed or any import started (it used to surface only after the first activity existed).</summary>
    [Fact]
    public void ANonPositiveBudget_IsRefused_BeforeAnyImportStarts()
    {
        var fetchCount = 0;
        using var counting = repoClient.Fetches.Subscribe(_ => Interlocked.Increment(ref fetchCount));

        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromSeconds(-1)));
        Volatile.Read(ref fetchCount).Should().Be(0);
    }

    /// <summary>
    /// 🚨 MeshWeaver#6753 (adjacent) — the watch COMPLETES without a terminal status. The batch still
    /// emits its rows: the first source reads Failed (still running), the later one Skipped. It used
    /// to emit NOTHING — the empty import dropped its row, and the recursive <c>Sequence</c> dropped
    /// every row with it, breaking <see cref="ModuleSourceUpdate.UpdateAll"/>'s "emits once".
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AWatchThatEndsWithoutATerminalStatus_StillEmitsEveryRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await GivenAHungModuleSource("EmptyA", ct);
        var second = await GivenAHungModuleSource("EmptyB", ct);

        var rows = await ModuleSourceUpdate.UpdateAllWithin(Mesh, TimeSpan.FromMinutes(5),
                _ => Observable.Empty<ActivityLog>())
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batch emits once, whatever the watch did", ct);

        Output.WriteLine(string.Join(Environment.NewLine, rows.Select(r => $"{r.ConfigPath}: {r.Outcome} — {r.Detail}")));
        var mine = rows.Where(r => r.Space == first || r.Space == second).ToArray();
        mine.Should().HaveCount(2);
        mine[0].Outcome.Should().Be(ModuleSourceUpdateOutcome.Failed);
        mine[0].Detail.Should().Contain("before it reported a terminal status");
        mine[1].Outcome.Should().Be(ModuleSourceUpdateOutcome.Skipped);
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
