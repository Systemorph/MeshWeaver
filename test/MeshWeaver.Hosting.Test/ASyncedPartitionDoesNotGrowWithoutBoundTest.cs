using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
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
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A synced Space's partition must not grow on attempts that change nothing</b>
/// (Systemorph/MeshWeaver#8020).
///
/// <para><b>Measured on memex-cloud.</b> The <c>Hosting</c> partition grew 26,436 → 26,576 nodes in
/// 7.6 h (~440/day), which blocked the declared retirement of <c>Plugins/Hosting</c> at the
/// uninstall's 20,000-node verification bound. Three defects, one per test:</para>
/// <list type="number">
///   <item>Every unattended attempt minted a permanent Activity — including the ones that changed
///   nothing (<c>Essentials/_Activity/833b2845</c> "Re-imported Skipped").</item>
///   <item>A commit DECLINED by the platform floor was never final, so the branch reconcile re-fetched
///   it and minted a new Activity on every pass (<c>b99c8fd6</c> 14×, <c>4dc523a1</c> 9× on 11-02).</item>
///   <item>A Space whose package the record declares retired
///   (<c>features.fleetops: {enabled:false, packages:[Plugins/Hosting, …]}</c>) was still imported on
///   every push.</item>
/// </list>
///
/// <para>The real mesh, the real webhook processor, the real sync service and importer; the GitHub
/// transport (<see cref="IGitHubRepoClient"/>) and the newer-platform reading
/// (<see cref="INewerPlatformReading"/>) are the only substituted seams, both with precedent
/// (<c>GreenBuildSkipsASettledSourceTest</c>, <c>AnUnverifiedFloorHoldsTheImportTest</c>). The flags are
/// the platform's own <c>ConfigurationFeatureFlags</c> over a reloadable configuration.</para>
/// </summary>
public class ASyncedPartitionDoesNotGrowWithoutBoundTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/bounded-partition";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SourceName = "Plugins";
    private const string Flag = "fleetops";
    private const string CommitA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string CommitB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string CommitC = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string Newer = "3.0.0-ci.10319";
    private const string NewerStill = "3.0.0-ci.10400";

    private readonly ScriptedRepoClient repoClient = new();
    private readonly FakeNewerPlatform newer = new();

    /// <summary>Fixed per test instance, so the flag declaration can name it before the mesh starts.</summary>
    private readonly string retiredSpace = "Retired" + Guid.NewGuid().ToString("N")[..8];

    private IConfigurationRoot? layered;

    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                services.AddSingleton<INewerPlatformReading>(newer);
                // Layer the declaration ONTO the host configuration (replacing it takes every other
                // key with it — see HeldSourceSaysItIsHeldTest), and keep the root so the test can
                // flip the flag and RELOAD, exactly as a reloaded values file does.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    layered = configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PluginCatalog:Sources:0:Name"] = SourceName,
                        ["PluginCatalog:Sources:0:RepoPath"] = RepoUrl,
                        [$"Features:Flags:{Flag}:Enabled"] = "false",
                        [$"Features:Flags:{Flag}:Packages:0"] = $"{SourceName}/{retiredSpace}",
                    }).Build();
                    return layered;
                });
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubWebhookProcessor Webhooks => Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>Production runs every unattended trigger as System (the webhook and the reconcile are
    /// anonymous); the test calls the same surface the same way.</summary>
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  1. An attempt that changes nothing leaves no permanent Activity
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The pushed-commit surface — what every push and reconcile pass runs — imports commit A (it
    /// WRITES, so its Activity is the record a person reads and must stay), then commit B carrying the
    /// same tree (nothing to do). B's Activity is gone once the attempt has finished; what B concluded
    /// is on the sync source's own node. NEGATIVE CONTROL: A's Activity, read with the same instrument
    /// at the same point of its run, exists. Against the pre-fix code B's Activity exists too.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnAttemptThatChangesNothing_LeavesNoActivity_AndOneThatWrote_KeepsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, owner) = await Arrange("Noop", ct);
        repoClient.Stage(CommitA, [Page("Lesson")]);
        repoClient.Stage(CommitB, [Page("Lesson")]);

        var wrote = await Access.RunAsSystem(() => Mesh.UpdateToPushedCommitFromGitHub(space, owner, CommitA, "push"))
            .Timeout(TestTimeouts.CrossSilo).Await(ct);
        Output.WriteLine($"A: activity {wrote}");
        (await ConfigWhen(space, c => c.LastSyncCommitSha == CommitA, ct)).LastSyncOutcome.Should().NotBe("Skipped",
            "commit A must really import, or the control below measures nothing");
        (await ActivityExists(wrote, ct)).Should().BeTrue(
            "an attempt that WROTE keeps its Activity — and this read is the control: the same "
            + "instrument at the same point of a run sees an Activity that exists");

        var noop = await Access.RunAsSystem(() => Mesh.UpdateToPushedCommitFromGitHub(space, owner, CommitB, "push"))
            .Timeout(TestTimeouts.CrossSilo).Await(ct);
        Output.WriteLine($"B: activity {noop}");
        var recorded = await ConfigWhen(space, c => c.LastAttemptedCommitSha == CommitB, ct);
        Output.WriteLine($"B on the source: outcome={recorded.LastSyncOutcome} at={recorded.LastSyncAttemptAt}");

        (await ActivityExists(noop, ct)).Should().BeFalse(
            "an unattended attempt that changed nothing must not leave a permanent Activity — every "
            + "push fans out to every Space of the repository, and the partition grew ~440 records/day "
            + "this way (MeshWeaver#8020)");
        recorded.LastSyncCommitSha.Should().Be(CommitB,
            "the attempt is recorded on the sync source instead: the mesh holds B's content");
        recorded.LastSyncAttemptAt.Should().NotBeNull("the source says WHEN it was last attempted");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. A floor decline is retried only when what blocks it changes
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Commit B is declined (its floor was stamped for other sources, and a newer platform exists).
    /// The next push of B is NOT re-attempted — no fetch, no Activity — because the condition the
    /// decline waits for still holds. NEGATIVE CONTROL: once that condition changes (a different newer
    /// platform is known) the same delivery re-attempts. Against the pre-fix code the first re-delivery
    /// already re-attempts: a decline was merely "not final".
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFloorDecline_IsNotReattempted_UntilItsConditionChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, _) = await Arrange("Decline", ct);
        const string codeA = "class WidgetView { }";
        const string codeB = "class WidgetView { int selection; }";
        var stampA = StampOf(codeA);
        repoClient.Stage(CommitA, FloorTree(codeA, stampA, "aaaaaaaaaaaaaaaa"));
        repoClient.Stage(CommitB, FloorTree(codeB, stampA, "bbbbbbbbbbbbbbbb"));

        await Sync.ReimportAtCommit(space, CommitA, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == CommitA, ct);

        newer.Set(Newer);
        await Sync.ReimportAtCommit(space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var declined = await ConfigWhen(space, c => c.LastAttemptDeclinedUnder is { Length: > 0 }, ct);
        Output.WriteLine($"declined: outcome={declined.LastSyncOutcome} under={declined.LastAttemptDeclinedUnder}");
        declined.LastSyncCommitSha.Should().Be(CommitA, "a decline did not land; the baseline stays");
        declined.LastAttemptDeclinedUnder.Should().Contain(Newer, "the decline names what it waits for");
        await QueryShows(space, c => c.LastAttemptDeclinedUnder is { Length: > 0 }, ct);

        var fetchesBefore = repoClient.FetchCount(CommitB);
        (await DeliverPush(CommitB, ct)).Should().Be(0,
            "the same commit under the same condition re-derives the same decline — re-fetching it on "
            + "every reconcile pass minted an Activity each time (MeshWeaver#8020)");
        repoClient.FetchCount(CommitB).Should().Be(fetchesBefore, "nothing was fetched for the skipped delivery");

        // ── NEGATIVE CONTROL: the blocking condition changes → the same delivery re-attempts ──
        newer.Set(NewerStill);
        (await DeliverPush(CommitB, ct)).Should().Be(1,
            "a changed condition (here: the newer platform this instance knows of) can change the verdict, "
            + "so the decline is re-attempted at the SAME commit");
        (await ConfigWhen(space, c => c.LastAttemptDeclinedUnder?.Contains(NewerStill) == true, ct))
            .LastSyncCommitSha.Should().Be(CommitA);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. A Space whose package is declared retired is not synced
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two Spaces sync the same repository; a flag declared off names one of them as a package of that
    /// source. A push imports only the other. NEGATIVE CONTROL: the flag turns on (a configuration
    /// reload, nothing recycled) and the next push imports both. Against the pre-fix code the first
    /// push already imports both.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASpaceWhosePackageIsRetired_IsNotSynced_AndResumesWhenTheFlagTurnsOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await ArrangeSpace(retiredSpace, ct);
        var (kept, _) = await Arrange("Kept", ct);
        repoClient.Stage(CommitB, [Page("Lesson")]);
        repoClient.Stage(CommitC, [Page("Lesson"), Page("Other")]);
        await QueryShows(retiredSpace, c => c.RepositoryUrl == RepoUrl, ct);
        await QueryShows(kept, c => c.RepositoryUrl == RepoUrl, ct);

        (await DeliverPush(CommitB, ct)).Should().Be(1,
            $"only '{kept}' imports — '{retiredSpace}' is package {SourceName}/{retiredSpace}, which "
            + $"feature flag '{Flag}' declares retired (MeshWeaver#8020)");
        await ConfigWhen(kept, c => c.LastAttemptedCommitSha == CommitB, ct);
        repoClient.FetchCount(CommitB).Should().Be(1, "the retired Space was never fetched");

        // ── NEGATIVE CONTROL: the flag turns on → the retired Space syncs again ──
        layered!.Providers.OfType<MemoryConfigurationProvider>().Last().Set($"Features:Flags:{Flag}:Enabled", "true");
        layered.Reload();
        (await DeliverPush(CommitC, ct)).Should().Be(2,
            "the flag is read live: turned on, the retired Space resumes on the next push");
        await ConfigWhen(retiredSpace, c => c.LastAttemptedCommitSha == CommitC, ct);
    }

    // ── arrangement ──────────────────────────────────────────────────────────

    private async Task<(string Space, string Owner)> Arrange(string prefix, CancellationToken ct)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        return (space, await ArrangeSpace(space, ct));
    }

    private async Task<string> ArrangeSpace(string space, CancellationToken ct)
    {
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Bounded partition",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var owner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        foreach (var identity in new[] { owner, UserId }.Distinct())
            await credentials
                .Save(identity, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);
        return owner;
    }

    /// <summary>One verified push to <c>main</c>, delivered anonymously as GitHub delivers it.</summary>
    private async Task<int> DeliverPush(string headSha, CancellationToken ct)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            return await Webhooks.Process("push", JsonDocument.Parse($$"""
                {
                  "ref": "refs/heads/main",
                  "before": "0123456789abcdef0123456789abcdef01234567",
                  "after": "{{headSha}}",
                  "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
                  "commits": [],
                  "size": 0
                }
                """).RootElement).Timeout(TestTimeouts.Convergence).Await(ct);
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    // ── reading back ─────────────────────────────────────────────────────────

    /// <summary>Whether a node exists at <paramref name="path"/> — an exact-path query, which answers
    /// zero rows for an absent path rather than a routing NotFound.</summary>
    private async Task<bool> ActivityExists(string path, CancellationToken ct)
        => await MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => c.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private async Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    /// <summary>Waits until the eventually-consistent QUERY — which the webhook processor selects its
    /// candidates from — reports what the node stream already shows.</summary>
    private Task QueryShows(string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Any(n =>
                n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } cfg && predicate(cfg)))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static RepoFile Page(string id) =>
        new($"{id}.md", $"---\nNodeType: Markdown\nName: {id}\n---\n\nThe {id} page.\n");

    private static string StampOf(string code)
        => ModuleFloorWitness.ContentHash(
            FloorTree(code, "unused", "0000000000000000")
                .Where(f => f.Path != ModuleFloorWitness.FileName)
                .Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Content))),
            Manifest("0000000000000000"))!;

    private static string Manifest(string moduleVersion)
        => $$$"""{"module":"Floor","moduleVersion":"{{{moduleVersion}}}","files":{"Floor/index.json":"recomputed"}}""";

    /// <summary>The unverified-floor tree of <c>AnUnverifiedFloorHoldsTheImportTest</c>.</summary>
    private static IReadOnlyList<RepoFile> FloorTree(string code, string witnessHash, string moduleVersion) =>
    [
        new("index.json",
            """{"nodeType":"Space","name":"Decline space","content":{"minMeshVersion":"3.0.0-ci.10305"}}"""),
        new("manifest.lock", Manifest(moduleVersion)),
        new(ModuleFloorWitness.FileName,
            $$$"""{"schema":"mw-mesh-floor/1","contentHash":"{{{witnessHash}}}","verifiedOn":"3.0.0-ci.10305"}"""),
        new("Widget.json",
            """
            {"$type":"MeshNode","id":"Widget","name":"Widget","nodeType":"NodeType","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"decline fixture"}}
            """),
        new("Widget/Source/WidgetView.cs",
            $"""
            // <meshweaver>
            // Id: WidgetView
            // DisplayName: Widget View
            // </meshweaver>

            {code}
            """),
    ];

    /// <summary>The newer-platform reading, set by the test.</summary>
    private sealed class FakeNewerPlatform : INewerPlatformReading
    {
        private readonly BehaviorSubject<string?> current = new(null);

        public void Set(string? version) => current.OnNext(version);

        public IObservable<string?> NewerPlatform(IMessageHub hub) => current.Take(1);
    }

    /// <summary>The GitHub transport: a staged tree per commit, and how often each commit was fetched.
    /// Everything else throws, so a future caller is told rather than served a fake answer.</summary>
    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        private ImmutableDictionary<string, IReadOnlyList<RepoFile>> trees =
            ImmutableDictionary<string, IReadOnlyList<RepoFile>>.Empty;

        private ImmutableDictionary<string, int> fetches = ImmutableDictionary<string, int>.Empty;

        public void Stage(string commit, IReadOnlyList<RepoFile> files)
            => ImmutableInterlocked.Update(ref trees, map => map.SetItem(commit, files));

        public int FetchCount(string commit) => fetches.TryGetValue(commit, out var n) ? n : 0;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            // Deferred: the clone is what SUBSCRIBING costs, so that is where it is counted.
            => Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref fetches, map => map.SetItem(commitish, FetchCount(commitish) + 1));
                return trees.TryGetValue(commitish, out var files)
                    ? Observable.Return(new RepoSnapshot(commitish, [.. files]))
                    : Observable.Throw<RepoSnapshot>(new NotSupportedException(
                        $"ASyncedPartitionDoesNotGrowWithoutBoundTest staged no tree for '{commitish}'."));
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
            new NotSupportedException("ASyncedPartitionDoesNotGrowWithoutBoundTest's repo client answers only Fetch."));
    }
}
