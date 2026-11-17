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
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A sync source reads the branch of THIS instance's platform line (policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/PluginRegistryServesEachLine → "GitSync syncs
/// each line's ref"), declared once on the record as the registry's <c>lineRefs</c>.
///
/// <para>Measured 2026-11-17 on memex.meshweaver.cloud (3.1.13482): every <c>_GitSync</c> of
/// MeshWeaver.Plugins and MeshWeaver.SocialMedia stored <c>branch: main</c>, the record declared
/// <c>lineRefs {"3":"main3"}</c> for both repositories, and GitSync imported the v4 tree from
/// <c>main</c> — <c>Marketing/Event</c> was refused at load by its 4.0 floor and
/// <c>Collaboration/Review</c> compiled into CS0103.</para>
///
/// <para><b>What is measured</b>, on a real mesh through the real reconcile and push fan-out, with the
/// production reader (<see cref="DeclaredLineGitSyncRefs"/> over <see cref="PackageSources.FromConfiguration"/>)
/// and only the GitHub transport substituted. Line 3: the reconcile resolves and imports <c>main3</c>'s
/// head, a push to <c>main</c> selects nothing, a push to <c>main3</c> imports. Then the record rolls to
/// line 4 — the SAME stored config, untouched — and the reconcile imports <c>main</c>'s head. The stored
/// branch stays <c>main</c> throughout. Against the pre-fix code the first assertion fails: the reconcile
/// asks for <c>main</c> and imports the v4 head.</para>
/// </summary>
public class GitSyncReadsItsPlatformLinesBranchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/line-aware";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SourceName = "LineAware";

    private const string V4Head = "4444444444444444444444444444444444444444";
    private const string V3Head = "3333333333333333333333333333333333333333";
    private const string V3Pushed = "3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a";
    private const string V4Pushed = "4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b";

    private readonly RecordingRepoClient repoClient = new();

    /// <summary>The record as the portal renders it: one registry source declaring <c>3 → main3</c>,
    /// and the instance's line. Mutable, so the roll is the record changing — nothing else.</summary>
    private readonly IConfigurationRoot record = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginCatalog:Sources:0:Name"] = SourceName,
            ["PluginCatalog:Sources:0:RepoPath"] = RepoUrl,
            ["PluginCatalog:Sources:0:Ref"] = "main",
            [$"PluginCatalog:LineRefs:{SourceName}:3"] = "main3",
            [PlatformLine.ConfigKey] = "3",
        })
        .Build();

    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                // The production reader, over the record above instead of the mesh's own configuration.
                return services.AddSingleton<IGitSyncLineRefs>(sp => new DeclaredLineGitSyncRefs(
                    record, () => PackageSources.FromConfiguration(sp.GetRequiredService<IMessageHub>(), record)));
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubWebhookProcessor Webhooks => Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 240_000)]
    public async Task ASourceStoredAsMain_SyncsMain3OnLine3_AndMainAfterTheRollToLine4()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await Arrange(ct);

        // ── line 3: the reconcile resolves main3, never main ──
        var v3FetchedTask = repoClient.FetchedRefs.Where(r => r == V3Head)
            .Should().Within(TestTimeouts.Convergence * 2).Emit("a line-3 reconcile imports main3's head");
        var triggered = await Anonymously(() => Webhooks.ReconcileBranches());
        Output.WriteLine($"line 3 reconcile: triggered={triggered}, heads asked={string.Join(",", repoClient.HeadsAsked)}");
        repoClient.HeadsAsked.Should().Contain("main3").And.NotContain("main",
            "a line-3 instance syncs the ref the record declares for line 3 (lineRefs 3 → main3)");
        triggered.Should().Be(1);
        (await v3FetchedTask).Should().Be(V3Head);
        await LastSyncCommit(space, V3Head, ct);

        // ── line 3: a push to the OTHER line's branch selects nothing; one to main3 imports ──
        (await Anonymously(() => Webhooks.Process("push", PushPayload("main", V4Pushed))))
            .Should().Be(0, "main is line 4's branch; a line-3 instance takes nothing from it");
        var pushFetched = repoClient.FetchedRefs.Where(r => r == V3Pushed)
            .Should().Within(TestTimeouts.Convergence * 2).Emit("a push to this line's branch imports");
        (await Anonymously(() => Webhooks.Process("push", PushPayload("main3", V3Pushed))))
            .Should().Be(1, "main3 is this line's branch");
        (await pushFetched).Should().Be(V3Pushed);
        await LastSyncCommit(space, V3Pushed, ct);

        // ── the roll: the record's line moves to 4; the stored config is not touched ──
        record[PlatformLine.ConfigKey] = "4";
        repoClient.ClearHeadsAsked();
        var v4FetchedTask = repoClient.FetchedRefs.Where(r => r == V4Head)
            .Should().Within(TestTimeouts.Convergence * 2).Emit("after the roll the reconcile imports main's head");
        var afterRoll = await Anonymously(() => Webhooks.ReconcileBranches());
        repoClient.HeadsAsked.Should().Contain("main").And.NotContain("main3",
            "after the roll the same source reads line 4's ref, with no edit to its config");
        afterRoll.Should().Be(1);
        (await v4FetchedTask).Should().Be(V4Head);
        var stored = await LastSyncCommit(space, V4Head, ct);
        stored.Branch.Should().Be("main", "the line moves what is READ, never what is stored");
    }

    // ── arrangement ──────────────────────────────────────────────────────────

    private async Task<string> Arrange(CancellationToken ct)
    {
        var space = "LineAware" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Line-aware sync",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        var owner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await credentials
            .Save(owner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigQuery(space).Where(items => items.Count > 0).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    /// <summary>The processor selects from an eventually-consistent QUERY of the configs, so a landed
    /// import is awaited on the query too, not just the node.</summary>
    private async Task<GitHubSyncConfig> LastSyncCommit(string space, string sha, CancellationToken ct)
        => await ConfigQuery(space)
            .Select(items => items
                .Select(n => n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions))
                .FirstOrDefault(cfg => cfg?.LastSyncCommitSha == sha))
            .Where(cfg => cfg is not null)
            .Select(cfg => cfg!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence * 2)
            .Await(ct);

    private IObservable<IReadOnlyList<MeshNode>> ConfigQuery(string space)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Select(change => (IReadOnlyList<MeshNode>)change.Items.ToList());

    /// <summary>The webhook and the reconcile run with no user: their own System impersonation carries
    /// the reads and the writes.</summary>
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

    private static JsonElement PushPayload(string branch, string afterSha) => JsonDocument.Parse($$"""
        {
          "ref": "refs/heads/{{branch}}",
          "before": "0123456789abcdef0123456789abcdef01234567",
          "after": "{{afterSha}}",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "commits": [ { "added": ["Page.md"], "modified": [], "removed": [] } ],
          "size": 1
        }
        """).RootElement;

    /// <summary>The GitHub transport: each branch's head, and which commit an import fetched.
    /// Everything else throws.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetched = new();
        private ImmutableList<string> headsAsked = ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch asks for, as it happens (hot).</summary>
        public IObservable<string> FetchedRefs => fetched;

        /// <summary>Every branch whose head was asked for.</summary>
        public ImmutableList<string> HeadsAsked => headsAsked;

        public void ClearHeadsAsked() => ImmutableInterlocked.Update(ref headsAsked, _ => ImmutableList<string>.Empty);

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Defer(() =>
            {
                fetched.OnNext(commitish);
                return Observable.Return(new RepoSnapshot(commitish, ImmutableList.Create(
                    new RepoFile("Page.md", $"---\nNodeType: Markdown\nName: Page\n---\n\nAt {commitish}.\n"))));
            });

        public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
            => Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref headsAsked, l => l.Add(commitish));
                return commitish switch
                {
                    "main" => Observable.Return(V4Head),
                    "main3" => Observable.Return(V3Head),
                    _ => Observable.Throw<string>(new NotSupportedException($"no branch '{commitish}' is staged")),
                };
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

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(new NotSupportedException(
            "GitSyncReadsItsPlatformLinesBranchTest's repo client answers only Fetch and GetHeadSha."));
    }
}

/// <summary>
/// The pure rule (<see cref="GitSyncLine.Resolve"/>) and its controls: which stored branches the
/// declaration moves, and which it never touches.
/// </summary>
public class GitSyncLineRuleTest
{
    private static readonly IReadOnlyDictionary<string, string> Main3 = new Dictionary<string, string> { ["3"] = "main3" };

    [Theory]
    [InlineData("main", "main", "3", "main3")]   // the configured ref, on the declared line
    [InlineData("main", "main", "4", "main")]    // the configured ref, on a line nobody declared
    [InlineData("main3", "main", "4", "main")]   // provisioned on v3 (main3), now rolled to v4
    [InlineData("main3", "main", "3", "main3")]
    [InlineData("MAIN", "main", "3", "main3")]   // GitHub names branches case-insensitively
    [InlineData("main", "HEAD", "3", "main3")]   // a catalog ref of HEAD is the default branch
    [InlineData("main", "main", null, "main")]   // no line known: the configured ref
    public void AStoredBranchTheDeclarationNames_ReadsItsLinesRef(string stored, string configured, string? line, string expected)
        => GitSyncLine.Resolve(stored, configured, Main3, line).Should().Be(expected);

    [Fact]
    public void ABranchSomebodyChose_IsNeverMoved()
        => GitSyncLine.Resolve("feature/x", "main", Main3, "3").Should().BeNull(
            "a branch the declaration does not name is a deliberate choice, not a line's ref");

    [Fact]
    public void ARepositoryDeclaringNoLineRefs_KeepsItsBranch()
        => GitSyncLine.Resolve("main", "main", new Dictionary<string, string>(), "3").Should().BeNull();
}
