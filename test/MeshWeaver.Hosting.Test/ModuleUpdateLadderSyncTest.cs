using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The source-sync rows of the module update ladder</b> (<c>Doc/Architecture/ModuleUpdateLadder</c>,
/// rows E3, F1 and F4; policies <c>sources-sync-on-push</c> and <c>prune-requires-provenance</c>),
/// driven end to end through the real webhook processor, the real sync service and the real importer.
///
/// <para>Rows the sibling classes already pin are not repeated here: F2 ("a red build holds nothing")
/// is <see cref="BuildTriggeredSyncPinsTheBuiltCommitTest.APush_ImportsAtThePushedCommit_EvenThoughTheBranchsBuildIsRed"/>,
/// and F3 ("an incompatible module is declined alone") is
/// <see cref="BuildTriggeredSyncPinsTheBuiltCommitTest.AnIncompatibleModuleIsDeclinedAlone_WhileItsSiblingSyncs"/>.
/// What this class adds is the SEAL half (E3): a published-bundle root that holds no seal for the
/// running identity, or one for an OLDER commit of the same repository, must neither hold nor
/// redirect a push. It also adds F4 through the push path, not only through the importer.</para>
///
/// <para>One seam is substituted: the GitHub transport (<see cref="IGitHubRepoClient"/>), which is
/// the IO boundary and not a mesh interface. The published root is a real directory carrying the
/// markers the publishing lane writes.</para>
/// </summary>
public class ModuleUpdateLadderSyncTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/ladder-sync";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SealedSourceName = "plugins";

    /// <summary>An identity this process does NOT run — a newer line's seal.</summary>
    private const string OtherIdentity = "c999e999";

    private const string OlderSha = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string PushedSha = "1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b";
    private const string NextSha = "2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c";

    private readonly TreeRepoClient repoClient = new();

    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-ladder-sync-" + Guid.NewGuid().ToString("N")[..12]);

    private static string UserId => TestUsers.Admin.ObjectId
        ?? throw new InvalidOperationException("the fixture contract: TestUsers.Admin carries an ObjectId");

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private GitHubWebhookProcessor Webhooks => Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(publishedRoot);
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                // Layer the published-bundle root ONTO the host configuration (replacing it would take
                // every other key with it — the measurement is in HeldSourceSaysItIsHeldTest).
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                    }).Build();
                });
            });
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(publishedRoot))
                Directory.Delete(publishedRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is harmless.
        }
    }

    /// <summary>
    /// 🚨 <b>Row F1 + E3 — no seal for the running identity, no green build: a push still imports AT
    /// the pushed commit.</b> The published root carries a sealed publication of THIS repository at
    /// the very pushed commit — but under another identity (a newer line this process does not run).
    /// The precondition is asserted through the production reader, so the test cannot pass on a root
    /// it never read: this identity has nothing sealed, and the other identity's seal is really
    /// there. No <c>workflow_run</c> is delivered at all.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task APush_ImportsAtThePushedCommit_WithNoSealForTheRunningIdentity_AndNoGreenBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        StageSeal(OtherIdentity, PushedSha);
        var space = await Arrange("NoSeal", ct);

        var (forThisIdentity, outcome) =
            SealedPublicationIndex.ReadingFor(publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid);
        outcome.Should().NotBe(SealedReadOutcome.Unreadable, "the precondition: the root is readable");
        forThisIdentity.Where(s => s.IsSealed).Should().BeEmpty(
            "the premise: NOTHING is sealed for the identity this process runs");
        SealedPublicationIndex.ReadingFor(publishedRoot, OtherIdentity).Sources
            .Should().Contain(s => s.IsSealed && s.SourceCommit == PushedSha,
                "the control: the seal under the other identity IS there, so 'nothing for this "
                + "identity' is a measured reading and not an unread root");

        repoClient.Trees = repoClient.Trees.SetItem(PushedSha, [Page("Page")]);
        var fetched = repoClient.FetchedRefs.Where(r => r == PushedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a push imports at once — no seal for this identity and no green build hold it");
        (await Anonymously(() => Webhooks.Process("push", PushPayload(PushedSha)), ct))
            .Should().Be(1, "the one sync source of this repository is selected by the push");
        (await fetched).Should().Be(PushedSha);

        await SourceAt(space, PushedSha, ct);
    }

    /// <summary>
    /// 🚨 <b>Row E3 — a seal for this identity at an OLDER commit neither holds nor redirects a
    /// push.</b> The seal exists and is attributable to this repository (asserted through the
    /// production reader). The push names a newer commit; the import must fetch THAT commit and
    /// never the sealed one. The seal decides only whether prebuilt bytes are adopted.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ASealForAnOlderCommit_NeitherHoldsNorRedirectsThePush()
    {
        var ct = TestContext.Current.CancellationToken;
        StageSeal(PrebuiltAssemblySeeder.LiveFrameworkMvid, OlderSha);
        var space = await Arrange("OlderSeal", ct);

        SealedPublicationIndex.ReadingFor(publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid).Sources
            .Should().Contain(s => s.IsSealed && s.SourceCommit == OlderSha && s.Repository == RepoFullName,
                "the premise: this identity HAS a seal for this repository — at an older commit");

        repoClient.Trees = repoClient.Trees
            .SetItem(OlderSha, [Page("Page")])
            .SetItem(PushedSha, [Page("Page"), Page("Added")]);
        var neverTheSeal = repoClient.FetchedRefs.Where(r => r == OlderSha)
            .Should().NotEmit(within: TestTimeouts.Convergence, cancellationToken: ct);
        var fetched = repoClient.FetchedRefs.Where(r => r == PushedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the pushed commit lands; an older seal is no reason to hold or redirect it");
        (await Anonymously(() => Webhooks.Process("push", PushPayload(PushedSha)), ct)).Should().Be(1);
        (await fetched).Should().Be(PushedSha);
        await neverTheSeal;

        await SourceAt(space, PushedSha, ct);
    }

    /// <summary>
    /// 🚨 <b>Row F4 through the PUSH path — a node created at runtime survives a push import</b>
    /// (policy <c>prune-requires-provenance</c>). Push 1 imports two pages; a runtime writer creates a
    /// third node in the synced partition; push 2's tree deletes one page. The deleted page is
    /// pruned — the positive control, without which "the runtime node survived" could mean the prune
    /// never ran — and the runtime node stays.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARuntimeNode_SurvivesAPushImport_WhileARetiredSourceNodeIsPruned()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await Arrange("Runtime", ct);
        var retired = $"{space}/Retired";
        var runtime = $"{space}/Babysitter";

        repoClient.Trees = repoClient.Trees
            .SetItem(PushedSha, [Page("Kept"), Page("Retired")])
            .SetItem(NextSha, [Page("Kept")]);

        (await Anonymously(() => Webhooks.Process("push", PushPayload(PushedSha)), ct)).Should().Be(1);
        await SourceAt(space, PushedSha, ct);
        await Children(space, paths => paths.Contains(retired, StringComparer.OrdinalIgnoreCase), ct);

        // The running portal writes state of its own into the synced partition.
        IObservable<MeshNode> write;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = "pr-babysitter", Name = "PR babysitter" }))
            write = MeshService.CreateNode(new MeshNode("Babysitter", space)
            {
                Name = "Babysitter",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "# runtime state" },
            });
        await write.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        (await Anonymously(() => Webhooks.Process("push", PushPayload(NextSha)), ct)).Should().Be(1);
        await SourceAt(space, NextSha, ct);

        var after = await Children(space,
            paths => !paths.Contains(retired, StringComparer.OrdinalIgnoreCase), ct);
        after.Should().NotContain(retired,
            "the POSITIVE control: the repository put Retired there and then deleted it");
        after.Should().Contain(runtime,
            "no import manifest records the runtime node, so a push import must never prune it");
    }

    // ── arrangement ──────────────────────────────────────────────────────────

    private static RepoFile Page(string id) =>
        new($"{id}.md", $"---\nNodeType: Markdown\nName: {id}\n---\n\n# {id}\n");

    /// <summary><c>&lt;root&gt;/&lt;identity&gt;/plugins/</c> with the three markers a sealed
    /// publication carries.</summary>
    private void StageSeal(string identity, string commit)
    {
        var sourceDirectory = Path.Combine(publishedRoot, identity, SealedSourceName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), RepoFullName);
        File.WriteAllText(Path.Combine(sourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), commit);
        File.WriteAllText(Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Empty);
    }

    private async Task<string> Arrange(string prefix, CancellationToken ct)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = prefix,
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

        // The processor selects from an eventually-consistent QUERY of the configs.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Count > 0)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return space;
    }

    /// <summary>
    /// The import at <paramref name="sha"/> CONCLUDED (<see cref="GitHubSyncConfig.LastAttemptedCommitSha"/>
    /// is written on every conclusion) and LANDED — the source records it as held content. A
    /// conclusion that did not land fails here naming the importer's own outcome, never as a bare
    /// timeout.
    /// </summary>
    private async Task SourceAt(string space, string sha, CancellationToken ct)
    {
        var config = await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .SelectMany(c => c.Items.Select(n => n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)))
            .OfType<GitHubSyncConfig>()
            .Where(cfg => cfg.LastSyncCommitSha == sha || cfg.LastAttemptedCommitSha == sha)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence * 4)
            .Await(ct);
        config.LastSyncCommitSha.Should().Be(sha,
            $"the import at {sha} must land — it concluded '{config.LastSyncOutcome}' "
            + $"(note: {config.LastSyncNote ?? "none"})");
    }

    private Task<string[]> Children(string space, Func<string[], bool> until, CancellationToken ct) =>
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{space} scope:children"))
                .Take(1))
            .Select(c => c.Items.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray())
            .Where(until)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence * 4)
            .Await(ct);

    /// <summary>The webhook request is ANONYMOUS — its authorization is the HMAC signature — so every
    /// ambient identity is dropped and the processor's own System impersonation carries the work.</summary>
    private async Task<int> Anonymously(Func<IObservable<int>> work, CancellationToken ct)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            return await work().Timeout(TestTimeouts.Convergence).Await(ct);
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

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

    /// <summary>
    /// The GitHub transport: serves a tree per commit and records every commitish fetched. HOT, not
    /// replaying, so a <c>NotEmit</c> cannot be handed an earlier step's fetch.
    /// </summary>
    private sealed class TreeRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetched = new();
        private ImmutableDictionary<string, ImmutableList<RepoFile>> trees =
            ImmutableDictionary<string, ImmutableList<RepoFile>>.Empty;

        public IObservable<string> FetchedRefs => fetched;

        public ImmutableDictionary<string, ImmutableList<RepoFile>> Trees
        {
            get => Volatile.Read(ref trees);
            set => Volatile.Write(ref trees, value);
        }

        public IObservable<RepoSnapshot> Fetch(string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Defer(() =>
            {
                fetched.OnNext(commitish);
                return Trees.TryGetValue(commitish, out var files)
                    ? Observable.Return(new RepoSnapshot(commitish, files))
                    : Observable.Throw<RepoSnapshot>(new InvalidOperationException(
                        $"the test repository has no tree at {commitish}"));
            });

        /// <summary>The configured branch's head is <see cref="PushedSha"/>; any other commitish is a
        /// dependency these tests did not declare, so it fails loudly instead of answering.</summary>
        public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
            => commitish == "main"
                ? Observable.Return(PushedSha)
                : Observable.Throw<string>(new InvalidOperationException(
                    $"the test repository resolves only the configured branch 'main', not '{commitish}'"));

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotUsed<GitHubPushResult>();
        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => NotUsed<GitHubBranchResult>();
        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => NotUsed<GitHubPullRequestInfo>();
        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();
        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(string repositoryUrl, GitHubIssueState? state, string accessToken) => NotUsed<IReadOnlyList<GitHubIssue>>();
        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubIssue>();
        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();
        public IObservable<GitHubIssueComment> CommentIssue(string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();
        public IObservable<GitHubIssue> SetIssueState(string repositoryUrl, int number, GitHubIssueState state, string accessToken) => NotUsed<GitHubIssue>();
        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(string repositoryUrl, PullRequestStatus? state, string accessToken) => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();
        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();
        public IObservable<GitHubIssueComment> CommentPullRequest(string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("ModuleUpdateLadderSyncTest's repo client answers only Fetch and GetHeadSha."));
    }
}
