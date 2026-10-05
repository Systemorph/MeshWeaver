using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
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
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Sources sync on PUSH, at the pushed commit — a red build of the branch holds nothing, and a
/// lost delivery is caught by the branch reconcile</b> (policy <c>sources-sync-on-push</c>;
/// <c>Doc/Architecture/SourcesSyncOnPush</c>).
///
/// <para><b>What went wrong (measured on the control instance, 2026-10-04/05).</b> The import was
/// triggered only by a GREEN build of the branch. MeshWeaver.Plugins' main was red on every push from
/// 14:39Z, so <c>AI/_GitSync</c> and <c>Hosting/_GitSync</c> last attempted at 14:56Z and then
/// nothing for 15 h while main moved twice; a manual update at 05:29Z imported at once with 18
/// NodeTypes recompiled and 0 compile errors. The green-build trigger had re-introduced the
/// whole-repository hold policy <c>module-sync-per-manifest-hash</c> removed.</para>
///
/// <para><b>What every assertion measures.</b> The ref the import asks GitHub for — the
/// <c>commitish</c> of <see cref="IGitHubRepoClient.Fetch(string,string,string?,string)"/> — because
/// the defects here are all "the fetch did or did not happen, at that ref". The class keeps its name
/// for the #1430 half it still pins: a machine trigger imports the commit it NAMES, never the branch
/// tip resolved at fetch time.</para>
///
/// <para>The fake repo client is the IO boundary — a GitHub transport, not a mesh interface — which
/// is the one seam this codebase does substitute. Everything else is the real mesh.</para>
/// </summary>
public class BuildTriggeredSyncPinsTheBuiltCommitTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/pinned-build";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>A real-shaped 40-hex sha, so nothing downstream can mistake it for a branch name.</summary>
    private const string PushedSha = "8d4920c93a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private readonly RecordingRepoClient repoClient = new();

    /// <summary>The DevLogin user the test base logs in.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/Octokit transport.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private GitHubWebhookProcessor Webhooks =>
        Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// 🚨 <b>A push imports at the pushed commit, and a RED build of that commit does not hold it.</b>
    ///
    /// <para>The red build is delivered FIRST, under a <c>NotEmit</c>: it must fetch nothing (a
    /// failed run is no signal of any kind). Then the push — which before this change logged and
    /// imported nothing — must reach GitHub at its own sha. Against the old code this test fails on
    /// the second assertion: the push answered 0 and nothing was ever fetched.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APush_ImportsAtThePushedCommit_EvenThoughTheBranchsBuildIsRed()
    {
        await Arrange("PushRed", TestContext.Current.CancellationToken);

        var redFetchesNothing = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick,
            cancellationToken: TestContext.Current.CancellationToken);
        var red = await Anonymously(() => Webhooks.Process("workflow_run",
            BuildPayload(PushedSha, conclusion: "failure")));
        red.Should().Be(0, "a red build is no publish signal and records nothing");
        await redFetchesNothing;

        var fetched = repoClient.FetchedRefs.Where(r => r == PushedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the push must import although the branch's CI is red (policy sources-sync-on-push)");
        var triggered = await Anonymously(() => Webhooks.Process("push", PushPayload(PushedSha)));

        triggered.Should().Be(1, "the one sync source of this repository is selected by the push");
        var requested = await fetched;
        requested.Should().Be(PushedSha,
            "the import must read the commit the push named, never the branch resolved at fetch time "
            + "(MeshWeaver.Plugins#1430)");
        requested.Should().NotBe("main");
    }

    /// <summary>
    /// 🚨 <b>A green build RECORDS the build and imports nothing</b> — under every admitted trigger.
    /// A green build of an older commit finishing after a newer push had landed would otherwise move
    /// the Space backwards, and the push already brought the sources.
    ///
    /// <para><b>Positive control in the same mesh:</b> a push at one of the very same shas DOES
    /// fetch, so the <c>NotEmit</c> above it cannot pass on a mesh that could not import at all. The
    /// scheduled PR-updater (#3978) is still refused outright: no build record.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AGreenBuild_RecordsTheBuild_ButNoLongerImports()
    {
        await Arrange("Green", TestContext.Current.CancellationToken);

        var shas = new (string Trigger, string Sha)[]
        {
            ("push", "1111111111111111111111111111111111111111"),
            ("repository_dispatch", "2222222222222222222222222222222222222222"),
            ("schedule", "3333333333333333333333333333333333333333"),
        };

        var noImport = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick,
            cancellationToken: TestContext.Current.CancellationToken);
        foreach (var (trigger, sha) in shas)
        {
            var recorded = await Anonymously(() => Webhooks.Process("workflow_run",
                BuildPayload(sha, trigger: trigger)));
            recorded.Should().Be(1,
                $"a green content-CI run started by '{trigger}' is still RECORDED as a build fact");
        }
        var updater = await Anonymously(() => Webhooks.Process("workflow_run", BuildPayload(
            "4444444444444444444444444444444444444444",
            workflowPath: ".github/workflows/auto-update-green-prs.yml", trigger: "schedule")));
        updater.Should().Be(0, "a green PR updater proves no content and is not recorded (#3978)");
        await noImport;

        // ── the positive control ──
        var pushFetches = repoClient.FetchedRefs.Where(r => r == shas[2].Sha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a push at the same sha must import — the mesh above COULD import, it was told not to");
        (await Anonymously(() => Webhooks.Process("push", PushPayload(shas[2].Sha))))
            .Should().Be(1);
        (await pushFetches).Should().Be(shas[2].Sha);
    }

    /// <summary>
    /// 🚨 <b>A lost push delivery is caught by the branch reconcile</b> — and a source already on the
    /// head costs the reconcile one ref lookup and no fetch.
    ///
    /// <para>No webhook is delivered at all (the lost delivery). The reconcile resolves the branch
    /// head through <see cref="IGitHubRepoClient.GetHeadSha"/> and imports AT that sha. Then, once
    /// the source records the head, a second pass must NOT fetch — the negative control that keeps
    /// "reconcile" from meaning "re-clone every repository every ten minutes".</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALostPushDelivery_IsCaughtByTheBranchReconcile_AndASettledSourceIsNotFetchedAgain()
    {
        var space = await Arrange("Lost", TestContext.Current.CancellationToken);
        repoClient.Head = PushedSha;

        var fetched = repoClient.FetchedRefs.Where(r => r == PushedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the reconcile must bring the source to the head the lost push would have");
        var triggered = await Anonymously(() => Webhooks.ReconcileBranches());
        triggered.Should().Be(1, "the one source of this repository is behind the branch head");
        (await fetched).Should().Be(PushedSha,
            "the reconcile imports AT the sha it resolved, never at the branch (#1430)");

        // The source records the head once the import lands; the reconcile selects from the
        // eventually-consistent config QUERY, so wait for the query too, not just the node.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Any(n =>
                n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)?.LastSyncCommitSha == PushedSha))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence * 2)
            .Await(TestContext.Current.CancellationToken);

        var noRefetch = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick,
            cancellationToken: TestContext.Current.CancellationToken);
        var again = await Anonymously(() => Webhooks.ReconcileBranches());
        again.Should().Be(0, "the source is already at the head");
        await noRefetch;
    }

    /// <summary>
    /// The pushed-commit surface has NO branch-HEAD fallback: a caller that cannot name the commit
    /// fails before any fetch instead of degrading to the behaviour #1430 removed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnattendedImport_WithoutACommit_Refuses_RatherThanFallingBackToTheBranch()
    {
        var space = "PushNoSha" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "No commit",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var nothingFetched = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick,
            cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Mesh.UpdateToPushedCommitFromGitHub(space, UserId, commitSha: "", trigger: "push")
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Mesh.UpdateToProvenCommitFromGitHub(space, UserId, commitSha: "")
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken));
        await nothingFetched;
    }

    /// <summary>
    /// A green workflow that did not compile the repository's content is not a build signal at all
    /// (#3978): no record, no import.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GreenUnrelatedWorkflow_DoesNotRecordOrTriggerAnImport()
    {
        var noFetch = repoClient.FetchedRefs.Should().NotEmit(within: TestTimeouts.Quick,
            cancellationToken: TestContext.Current.CancellationToken);

        var triggered = await Webhooks.Process(
                "workflow_run",
                BuildPayload(PushedSha, ".github/workflows/auto-update-green-prs.yml"))
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        triggered.Should().Be(0,
            "a workflow's trigger says how it started, not that it compiled this repository's content");
        await noFetch;
    }

    /// <summary>
    /// 🚨 <b>An incompatible module is declined ALONE while its siblings sync</b> — the per-module
    /// judgement every push-triggered import runs (policy <c>module-sync-per-manifest-hash</c>), read
    /// off a pushed tree exactly as the import reads it.
    ///
    /// <para>The decision is pure, so it is pinned with an explicit running platform: the test
    /// process itself runs a local <c>-ci.0</c> build, which the one floor comparator treats as
    /// advisory by design (policy <c>package-min-mesh-version</c>), so an end-to-end decline cannot be
    /// produced here without overriding a process-wide environment variable every parallel test
    /// would see. The negative control is the same tree on a platform that meets the floor: nothing
    /// is declined.</para>
    /// </summary>
    [Fact]
    public void AnIncompatibleModuleIsDeclinedAlone_WhileItsSiblingSyncs()
    {
        IEnumerable<(string Path, string Content)> PushedTree() =>
        [
            ("Hosting/manifest.lock", """{ "module": "Hosting", "moduleVersion": "1f75ade77bdf2fa5" }"""),
            ("Hosting/index.json", """{ "nodeType": "Space", "content": { "minMeshVersion": "3.0.0-ci.9500" } }"""),
            ("Hosting/Babysitter.md", "---\nNodeType: Markdown\n---\n"),
            ("AI/manifest.lock", """{ "module": "AI", "moduleVersion": "b5c88490aa11bb22" }"""),
            ("AI/index.json", """{ "nodeType": "Space", "content": { "minMeshVersion": "3.0.0-ci.9000" } }"""),
        ];
        var held = new Dictionary<string, string> { ["AI"] = "178ff85c00112233", ["Hosting"] = "39aa00bb11cc22dd" };

        var onAnOldPlatform = ModuleSyncDecision.Decide(
            ModuleSyncDecision.Read(PushedTree()), held, "3.0.0-ci.9218", reconcile: false);
        Output.WriteLine(string.Join(Environment.NewLine, onAnOldPlatform.Select(m => $"{m.Module}: {m.Outcome} — {m.Reason}")));

        var hosting = onAnOldPlatform.Single(m => m.Module == "Hosting");
        hosting.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined,
            "Hosting declares platform ≥ 3.0.0-ci.9500 and this instance runs 3.0.0-ci.9218");
        hosting.Reason.Should().Contain("3.0.0-ci.9500").And.Contain("3.0.0-ci.9218",
            "the decline names BOTH versions, so an operator knows what roll releases it");
        onAnOldPlatform.Single(m => m.Module == "AI").Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
            "a sibling module's floor holds nothing else — the AI module changed and syncs");

        // ── negative control: the platform meets every floor ⇒ nothing is declined ──
        ModuleSyncDecision.Decide(ModuleSyncDecision.Read(PushedTree()), held, "3.0.0-ci.9600", reconcile: false)
            .Select(m => m.Outcome)
            .Should().AllBe(ModuleSyncOutcomeKind.Synced);
    }

    // ── arrangement ──────────────────────────────────────────────────────────

    private async Task<string> Arrange(string prefix, CancellationToken cancellationToken)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = prefix,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        // The import authenticates as the sync config's CREATOR — seed the credential for exactly
        // the identity the production path will resolve.
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        // The processor selects from an eventually-consistent QUERY of the configs.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Count > 0)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        return space;
    }

    /// <summary>
    /// 🚨 The webhook request is ANONYMOUS — its authorization is the verified HMAC signature — and the
    /// reconcile runs on a timer with no user. Every ambient identity is dropped so the processor's
    /// own System impersonation is what carries the lookups and the writes.
    /// </summary>
    private async Task<int> Anonymously(Func<IObservable<int>> work)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            return await work().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    private static JsonElement PushPayload(string afterSha) => JsonDocument.Parse($$"""
        {
          "ref": "refs/heads/main",
          "before": "0123456789abcdef0123456789abcdef01234567",
          "after": "{{afterSha}}",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "commits": [ { "added": ["Page.md"], "modified": [], "removed": [] } ],
          "size": 1
        }
        """).RootElement;

    private static JsonElement BuildPayload(
        string headSha,
        string workflowPath = ".github/workflows/ci.yml",
        string trigger = "push",
        string conclusion = "success") => JsonDocument.Parse($$"""
        {
          "action": "completed",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "workflow_run": {
            "conclusion": "{{conclusion}}", "head_branch": "main", "head_sha": "{{headSha}}",
            "id": 34061098155, "run_number": 2026, "name": "Content CI", "event": "{{trigger}}",
            "path": "{{workflowPath}}",
            "updated_at": "2026-10-04T14:39:00Z"
          }
        }
        """).RootElement;

    /// <summary>
    /// The GitHub transport, reduced to the two questions these tests ask: which ref did an import
    /// fetch, and what does the branch point at. Everything else throws.
    ///
    /// <para>🚨 <see cref="FetchedRefs"/> is HOT, not replaying: the negative controls assert that NO
    /// fetch follows a step, and a replaying subject would hand them an earlier step's fetch.</para>
    /// </summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetched = new();

        /// <summary>Every commitish a fetch asks for, as it happens.</summary>
        public IObservable<string> FetchedRefs => fetched;

        /// <summary>What the branch points at, for the reconcile's ref lookup.</summary>
        public string Head { get; set; } = PushedSha;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            // Defer: the fetch is what SUBSCRIBING costs, so it is recorded there.
            => Observable.Defer(() =>
            {
                fetched.OnNext(commitish);
                // One real page at the requested sha, so the import LANDS and records the commit.
                return Observable.Return(new RepoSnapshot(commitish, ImmutableList.Create(
                    new RepoFile("Page.md", "---\nNodeType: Markdown\nName: Page\n---\n\nA page.\n"))));
            });

        public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
            => Observable.Return(Head);

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
                "BuildTriggeredSyncPinsTheBuiltCommitTest's repo client answers only Fetch and GetHeadSha."));
    }
}
