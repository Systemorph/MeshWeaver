using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A build-triggered GitSync import reads the commit THAT BUILD proved — never the branch tip
/// at the moment the fetch happens.</b> Systemorph/MeshWeaver.Plugins#1430, measured on production.
///
/// <para><b>What went wrong.</b> The <c>workflow_run</c> green-build trigger selected its candidate
/// sync sources against the run's <c>head_sha</c> and then asked for "update to latest", which
/// resolves <c>GitHubSyncConfig.Branch</c> inside the fetch. Those are the same tree only while
/// nothing merges in between. On 2026-09-06 a MeshWeaver.Plugins <c>main</c> run for
/// <c>8d4920c93</c> finished at 22:38:18Z with <c>main</c> already past #1413 (the Payments split,
/// merged 22:17:21Z). Both production portals imported #1413's <c>Store/*</c> sources — against a
/// platform carrying neither <c>IPaymentProvider</c> nor the Payments module — and
/// <c>Store/Catalog</c>, <c>Order</c>, <c>Plugin</c> and <c>Maintenance</c> sat in compile
/// <c>Error</c> for roughly five hours, with the catalog and the checkout path dark on the
/// commercial portal.</para>
///
/// <para><b>What this test measures, and why it could fail.</b> The assertion is on the ref the
/// import actually asks GitHub for — <see cref="IGitHubRepoClient.Fetch(string,string,string?,string)"/>'s
/// <c>commitish</c> — not on a log line or a decision function, because the defect was precisely
/// that the decision and the fetch disagreed. Against the pre-fix code the recorded ref is the
/// branch name <c>main</c>; against the fix it is the sha the payload carried. The second assertion
/// (<c>NotBe("main")</c>) is not redundant: it is the one that states the failure mode in the words
/// the incident used, so a future regression reads as itself rather than as a mismatched string.</para>
///
/// <para>The fake repo client is the IO boundary — a GitHub transport, not a mesh interface — which
/// is the one seam this codebase does substitute. Everything else is the real mesh: the real
/// webhook processor, the real sync service, the real activity runner.</para>
/// </summary>
public class BuildTriggeredSyncPinsTheBuiltCommitTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/pinned-build";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>A real-shaped 40-hex sha, so nothing downstream can mistake it for a branch name.</summary>
    private const string BuiltSha = "8d4920c93a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private readonly RecordingRepoClient repoClient = new();

    /// <summary>The DevLogin user the test base logs in — read directly, since
    /// <c>AccessService.Context</c> is circuit-scoped and null on the test-method thread.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/Octokit transport,
                // so the test observes the ref without ever reaching the network.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private GitHubWebhookProcessor Webhooks =>
        Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a
    // constant, and the inner waits below already carry the adaptive bound. This outer one
    // only has to stop a WEDGE.
    [Fact(Timeout = 120_000)]
    public async Task GreenBuild_ImportsAtTheBuiltCommit_NeverAtTheBranchTip()
    {
        var space = "GbPin" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Pinned build space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await();

        // The import authenticates as the sync config's CREATOR — read it off the node rather than
        // assuming which identity the write landed under, so the credential below is seeded for the
        // user the production path will actually resolve.
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        Output.WriteLine($"sync config {configNode.Path} createdBy={syncOwner}");
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await();

        // Arm the observation BEFORE the trigger: the fetch is the thing under test and it happens
        // on a background activity, so the assertion must already be subscribed when it fires.
        var fetchedRef = repoClient.FetchedRefs.Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the build-triggered import must reach GitHub with a ref");

        // 🚨 The webhook request is ANONYMOUS — its authorization is the verified HMAC signature.
        // Drop every ambient identity so the processor's own System impersonation is what carries
        // the lookups and the write, exactly as it must on an access-gated portal.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        int triggered;
        try
        {
            triggered = await Webhooks.Process("workflow_run", GreenBuildPayload(BuiltSha))
                .Timeout(TestTimeouts.Convergence).Await();
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }

        triggered.Should().Be(1, "the one sync source of this repository is selected by the green build");

        var requested = await fetchedRef;
        Output.WriteLine($"import asked GitHub for '{requested}' (built sha {BuiltSha})");

        requested.Should().Be(BuiltSha,
            "the import must read the tree the build proved, not whatever the branch points at when "
            + "the fetch happens (MeshWeaver.Plugins#1430)");
        requested.Should().NotBe("main",
            "resolving the configured branch a second time is the defect: main had moved past the "
            + "built commit, and two live portals received sources no build had ever compiled");
    }

    /// <summary>
    /// The unattended import has NO branch-HEAD fallback: a caller that cannot name the proven
    /// commit fails instead of degrading to the behaviour #1430 removed. This is the third state
    /// stated as a test — "we could not read a proven commit" must never collapse into "then use
    /// the branch", which is the shape that turns an unread value into a confident wrong answer.
    /// </summary>
    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a
    // constant, and the inner waits below already carry the adaptive bound. This outer one
    // only has to stop a WEDGE.
    [Fact(Timeout = 120_000)]
    public async Task UnattendedImport_WithoutAProvenCommit_Refuses_RatherThanFallingBackToTheBranch()
    {
        var space = "GbNoSha" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "No proven commit",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Mesh.UpdateToProvenCommitFromGitHub(space, UserId, commitSha: "")
                .Timeout(TestTimeouts.Convergence).Await());

        // Nothing was asked of GitHub — the refusal is BEFORE any fetch, not a failed fetch.
        await repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick);
    }

    /// <summary>
    /// A green workflow that did not compile the repository's content is not a weaker build signal;
    /// it is no build signal at all. This is the live #3978 shape: the scheduled PR updater wrote
    /// twenty build completions for a commit whose real content CI was red.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GreenUnrelatedWorkflow_DoesNotRecordOrTriggerAnImport()
    {
        var noFetch = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick);

        var triggered = await Webhooks.Process(
                "workflow_run",
                GreenBuildPayload(BuiltSha, ".github/workflows/auto-update-green-prs.yml"))
            .Timeout(TestTimeouts.Convergence).Await();

        triggered.Should().Be(0,
            "a workflow's trigger says how it started, not that it compiled this repository's content");
        await noFetch;
    }

    /// <summary>
    /// 🚨 <b>The POSITIVE control for #3978, and it needs all THREE trigger kinds to be one.</b>
    ///
    /// <para>Keying the publish signal on the repository's content CI is only safe while every way
    /// that CI can START still publishes. Narrowing the admitted triggers is the mistake this fleet
    /// has ALREADY made, in the opposite direction: on 2026-09-02 the test was <c>event == "push"</c>
    /// alone, <c>MeshWeaver.Reinsurance</c>'s three green <c>repository_dispatch</c> runs at
    /// <c>636ebd5</c> were all discarded, and <c>Underwriting/_GitSync</c> sat <b>38 hours</b> behind
    /// a merged main with every delivery answering 200 OK and nothing reporting a fault
    /// (MeshWeaver.Plugins#1194). A content-CI run reaches this webhook as <c>push</c> when the
    /// branch moves, as <c>repository_dispatch</c> when a platform release re-verifies the satellite
    /// with no commit to push, and as <c>schedule</c> on its own cron — so all three are asserted,
    /// separately, at their own shas.</para>
    ///
    /// <para><b>And it carries its own negative control, in the same mesh.</b> The fourth delivery
    /// is a green <c>schedule</c> run of the PR-updater — the live #3978 shape — which must record
    /// nothing and fetch nothing. An absence assertion goes vacuous the moment the setup around it
    /// stops working, so the three deliveries that DID import are what stop
    /// <c>NotEmit</c> from passing on a mesh where no import could have happened at all.</para>
    /// </summary>
    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a
    // constant, and the inner waits below already carry the adaptive bound.
    [Fact(Timeout = 120_000)]
    public async Task AGreenContentCiRun_Publishes_UnderEveryAdmittedTriggerKind()
    {
        var space = "GbTrig" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Every admitted trigger",
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

        // One distinct sha per trigger kind, so no delivery can be satisfied by another's import and
        // #3945's "already attempted these exact bytes" skip cannot mask a dropped signal.
        var shas = new (string Trigger, string Sha, string Why)[]
        {
            ("push", "1111111111111111111111111111111111111111",
                "the branch moved and its content CI ran — the original case"),
            ("repository_dispatch", "2222222222222222222222222222222222222222",
                "a platform release re-verifies the satellite with no commit to push — the exact "
                + "signal MeshWeaver.Plugins#1194 discarded for 38 hours"),
            ("schedule", "3333333333333333333333333333333333333333",
                "the content CI's own cron run, which only ever exists on the default branch"),
        };

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        foreach (var (trigger, sha, why) in shas)
        {
            // Arm the observation BEFORE the delivery: the fetch happens on a background activity.
            var fetched = repoClient.FetchedRefs.Where(r => r == sha)
                .Should().Within(TestTimeouts.Convergence * 2)
                .Emit($"a green content-CI run started by '{trigger}' is a publish signal — {why}");

            // The webhook request is ANONYMOUS; drop every ambient identity so the processor's own
            // System impersonation is what carries the lookups and the write.
            accessService.ClearHostIdentity();
            accessService.SetHostIdentity(null);
            accessService.SetContext(null);
            int triggered;
            try
            {
                triggered = await Webhooks
                    .Process("workflow_run", GreenBuildPayload(sha, trigger: trigger))
                    .Timeout(TestTimeouts.Convergence).Await();
            }
            finally
            {
                accessService.SetHostIdentity(
                    new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
            }

            triggered.Should().Be(1,
                $"the one sync source of this repository must be selected by a green content-CI run "
                + $"started by '{trigger}' — {why}");
            (await fetched).Should().Be(sha,
                $"the '{trigger}' delivery must import the tree ITS run proved");
            Output.WriteLine($"{trigger} @ {sha[..8]} → triggered={triggered}, fetched={sha[..8]}");
        }

        // ── the negative half, on a mesh the three deliveries above have proven can import ──
        const string UpdaterSha = "4444444444444444444444444444444444444444";
        var neverFetched = repoClient.FetchedRefs.Where(r => r == UpdaterSha)
            .Should().NotEmit(within: TestTimeouts.Quick);

        var refused = await Webhooks
            .Process("workflow_run", GreenBuildPayload(
                UpdaterSha, ".github/workflows/auto-update-green-prs.yml", trigger: "schedule"))
            .Timeout(TestTimeouts.Convergence).Await();

        refused.Should().Be(0,
            "a green scheduled PR updater checks out nothing and builds nothing; the SAME trigger "
            + "that just published a real content-CI run must not publish this one — the trigger "
            + "says how a workflow started, never what it proved (#3978)");
        await neverFetched;
    }

    private static JsonElement GreenBuildPayload(
        string headSha,
        string workflowPath = ".github/workflows/ci.yml",
        string trigger = "push") => JsonDocument.Parse($$"""
        {
          "action": "completed",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "workflow_run": {
            "conclusion": "success", "head_branch": "main", "head_sha": "{{headSha}}",
            "id": 34061098155, "run_number": 2026, "name": "Content CI", "event": "{{trigger}}",
            "path": "{{workflowPath}}",
            "updated_at": "2026-09-06T22:38:18Z"
          }
        }
        """).RootElement;

    /// <summary>
    /// The GitHub transport, reduced to the one question this test asks: which ref did the import
    /// request? Everything else throws, so a future caller that starts depending on another
    /// operation is told rather than silently served a fake answer.
    /// </summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private readonly ReplaySubject<string> fetched = new();

        /// <summary>Every commitish a fetch has asked for, replayed to a late subscriber.</summary>
        public IObservable<string> FetchedRefs => fetched;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            fetched.OnNext(commitish);
            // An empty snapshot at the requested sha: the import then has nothing to write, which is
            // exactly what this test wants — the ref is the measurement, the content is not.
            return Observable.Return(new RepoSnapshot(commitish, Array.Empty<RepoFile>()));
        }

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
                "BuildTriggeredSyncPinsTheBuiltCommitTest's repo client answers only Fetch."));
    }
}
