using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A GitHub sync trigger aimed at a path that is no GitSynced Space is REFUSED before anything
/// is written — it never becomes a System-owned Activity under that path.</b> MeshWeaver#4933.
///
/// <para><b>What happened.</b> memex-cloud logged
/// <c>42P01: relation "whatsnew.activities" does not exist</c> for
/// <c>WhatsNew/_Activity/cc667f2e</c> three times in eight days. The issue read it as a namespace
/// nobody provisioned. The portal's own log names the writer, one millisecond later on the same
/// pod: <c>MCP github_sync check failed for WhatsNew</c>. An MCP caller asked the sync tool to
/// <c>check</c> the "Space" <c>WhatsNew</c> — which is not a Space at all, it is the root-level
/// declaration node of the built-in <c>WhatsNew</c> NodeType.</para>
///
/// <para><b>Why that reached the store.</b> The sync triggers elevate to the System identity
/// ("the click authorizes, the System executes") on the premise that the target is a GitSynced,
/// system-owned Space — and never checked the premise. <c>check</c> needs only Read, a type
/// declaration is readable, System is exempt from the "no partition, no write" guard, and nothing
/// on the create path provisions a partition. So a System-identity create landed on a schema
/// nobody provisioned, and Postgres refused it the way it is designed to. Provisioning
/// <c>whatsnew</c> would have made a spurious write succeed.</para>
///
/// <para><b>What is measured, and what could falsify it.</b> The trigger's own answer and whether
/// the Activity node ever existed — on the in-memory store, where the pre-fix code does NOT fail:
/// it creates <c>WhatsNew/_Activity/&lt;id&gt;</c> and hands the path back. So against the pre-fix
/// code the first three facts fail on "expected a refusal, got an activity path", which is the
/// defect itself rather than its Postgres symptom. The fourth is the control that keeps the other
/// three honest: a Space that IS synced must still trigger, or the fix would be a switch that
/// refuses every sync.</para>
/// </summary>
public class ASyncTriggerNeedsASyncedSpaceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/sync-trigger-premise";

    private readonly FetchOnlyRepoClient repoClient = new();

    /// <summary>The DevLogin user the test base logs in — a platform admin, the persona the
    /// production trigger came from and the one every authorization check here lets through.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/Octokit transport.
                return services.AddSingleton<IGitHubRepoClient>(repoClient);
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Fact(Timeout = 120_000)]
    public async Task Check_OnABuiltInNodeTypeDeclaration_IsRefused_AndWritesNoActivity()
    {
        // The production call, verbatim: `git_hub_sync space:WhatsNew op:check`.
        var outcome = await Trigger(
            onCreated => Mesh.CheckBranchStateOnGitHub(WhatsNewNodeType.NodeType, UserId, onCreated),
            TestContext.Current.CancellationToken);

        await AssertRefusedAndNothingWritten(outcome, WhatsNewNodeType.NodeType);
    }

    [Fact(Timeout = 120_000)]
    public async Task Update_OnARealSpaceWithNoSyncConfig_IsRefused_AndWritesNoActivity()
    {
        // The same hole with a wider blast radius: the Space exists and belongs to somebody, so an
        // elevated trigger would plant a System-owned node in it on a reader's say-so.
        var space = await CreateSpace("Unsynced", TestContext.Current.CancellationToken);

        var outcome = await Trigger(
            onCreated => Mesh.UpdateToLatestFromGitHub(space, UserId, onCreated),
            TestContext.Current.CancellationToken);

        await AssertRefusedAndNothingWritten(outcome, space);
    }

    [Fact(Timeout = 120_000)]
    public async Task Update_OnASpaceWhoseSyncConfigNamesNoRepository_IsRefused_AndWritesNoActivity()
    {
        // A config NODE is not a configured Space: opening the GitHub Sync settings tab mints
        // `{space}/_GitSync` with an empty RepositoryUrl (EnsureConfigNode) before a repository is
        // chosen. Existence of the node alone must not satisfy the premise.
        var space = await CreateSpace("EmptyConfig", TestContext.Current.CancellationToken);
        await Sync.EnsureConfigNode(space)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var outcome = await Trigger(
            onCreated => Mesh.UpdateToLatestFromGitHub(space, UserId, onCreated),
            TestContext.Current.CancellationToken);

        await AssertRefusedAndNothingWritten(outcome, space);
    }

    [Fact(Timeout = 120_000)]
    public async Task Update_OfASyncSourceTheSpaceDoesNotHave_IsRefused_AndWritesNoActivity()
    {
        var space = await SyncedSpace("OneSource", TestContext.Current.CancellationToken);

        var outcome = await Trigger(
            onCreated => Mesh.UpdateToLatestFromGitHub(space, UserId, onCreated, sourceId: "no-such-source"),
            TestContext.Current.CancellationToken);

        outcome.Error.Should().NotBeNull(
            $"the Space has a primary sync source and no 'no-such-source'; got activity '{outcome.ActivityPath}'");
        outcome.Error.Should().BeOfType<InvalidOperationException>();
        outcome.Error!.Message.Should().Contain("no-such-source");
        outcome.CreatedCallbacks.Should().BeEmpty("the created-callback fires only once an Activity node exists");
    }

    [Fact(Timeout = 120_000)]
    public async Task Update_OfASyncedSpace_StillRunsAsAnActivityUnderThatSpace()
    {
        // The control: the premise HOLDS, so the trigger must behave exactly as before.
        var space = await SyncedSpace("Synced", TestContext.Current.CancellationToken);

        var outcome = await Trigger(
            onCreated => Mesh.UpdateToLatestFromGitHub(space, UserId, onCreated),
            TestContext.Current.CancellationToken);

        outcome.Error.Should().BeNull("a Space with a sync config is exactly what the trigger is for");
        outcome.ActivityPath.Should().StartWith($"{space}/_Activity/");
        outcome.CreatedCallbacks.Should().Equal([outcome.ActivityPath!]);

        // Let the run reach a terminal status so teardown has nothing in flight to wait out.
        var log = await Mesh.GetWorkspace().GetMeshNodeStream(outcome.ActivityPath!)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Status != ActivityStatus.Running)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"control activity ended {log!.Status}");
        repoClient.Requested.Should().NotBeEmpty("the import inside the activity ran and asked GitHub for a ref");
    }

    /// <summary>What one trigger answered: the activity path it handed back, or the fault it
    /// raised — never both — plus every path the created-callback reported.</summary>
    private sealed record TriggerOutcome(
        string? ActivityPath, Exception? Error, ImmutableList<string> CreatedCallbacks);

    /// <summary>Runs one trigger under the signed-in user and MATERIALISES its answer, so a fact can
    /// state what it expected against what it got instead of dying on an unexpected throw.</summary>
    private async Task<TriggerOutcome> Trigger(
        Func<Action<string>, IObservable<string>> operation, CancellationToken cancellationToken)
    {
        var created = ImmutableList<string>.Empty;
        void OnCreated(string path) => ImmutableInterlocked.Update(ref created, list => list.Add(path));

        Task<(string? Path, Exception? Error)> answered;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name }))
            answered = operation(OnCreated)
                .Take(1)
                .Select(path => ((string?)path, (Exception?)null))
                .Catch((Exception ex) => Observable.Return(((string?)null, (Exception?)ex)))
                .Timeout(TestTimeouts.CrossSilo)
                .Await(cancellationToken);
        var (activityPath, error) = await answered;
        Output.WriteLine(error is null
            ? $"activity: {activityPath}"
            : $"refused: {error.GetType().Name}: {error.Message}");
        return new TriggerOutcome(activityPath, error, created);
    }

    private async Task AssertRefusedAndNothingWritten(TriggerOutcome outcome, string target)
    {
        outcome.Error.Should().NotBeNull(
            $"'{target}' has no GitHub sync config, so there is no system-owned Space to run as System in — "
            + $"but the trigger created the activity '{outcome.ActivityPath}' there (MeshWeaver#4933: on "
            + "Postgres that create is the 42P01 on an unprovisioned schema)");
        outcome.Error.Should().BeOfType<InvalidOperationException>();
        outcome.Error!.Message.Should().Contain($"'{target}'",
            "the refusal must name what was asked for — the production log line named only the op");
        outcome.CreatedCallbacks.Should().BeEmpty("the created-callback fires only once an Activity node exists");

        var children = await Storage.ListChildPaths($"{target}/_Activity")
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        children.NodePaths.Should().BeEmpty($"a refused trigger writes nothing under '{target}/_Activity'");
    }

    private async Task<string> CreateSpace(string prefix, CancellationToken cancellationToken)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Sync-trigger premise space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        return space;
    }

    /// <summary>A Space with a primary sync config and a credential for the user and for whoever
    /// the config write attributed itself to.</summary>
    private async Task<string> SyncedSpace(string prefix, CancellationToken cancellationToken)
    {
        var space = await CreateSpace(prefix, cancellationToken);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        foreach (var owner in new[] { configNode.CreatedBy, UserId }
                     .Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await Credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        return space;
    }

    /// <summary>The GitHub transport, reduced to the one call the control's import makes. Everything
    /// else throws.</summary>
    private sealed class FetchOnlyRepoClient : IGitHubRepoClient
    {
        private ImmutableList<string> requested = ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch has asked for, in order.</summary>
        public ImmutableList<string> Requested => requested;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            ImmutableInterlocked.Update(ref requested, list => list.Add(commitish));
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
            new NotSupportedException("ASyncTriggerNeedsASyncedSpaceTest's repo client answers only Fetch."));
    }
}
