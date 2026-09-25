using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Since policy <c>module-sync-per-manifest-hash</c> the seal HOLDS nothing, so its arrival
/// releases nothing — and must move nothing.</b> The history below is why the arrival was listened
/// to at all; the test now pins that listening to it never rolls a source back.
///
/// 🚨 <b>"It advances on the next green build" was FALSE, and that was MeshWeaver#4063's
/// convergence defect.</b> The <c>workflow_run</c> hook fires when a repository's build goes green — BEFORE its
/// publish-bake job seals the bundles for this instance's framework identity — so
/// <see cref="SealedSyncGate"/> holds, correctly. The seal lands minutes later and nothing
/// re-evaluates it. And the NEXT green build does not rescue the source either: that delivery asks
/// whether the seal is at the NEW head sha, which it is not.
///
/// <para>So a repository whose bake seals after its webhook <b>never advances inside one process
/// lifetime</b>. The reconciler designed for exactly this ordering
/// (<see cref="IPublicationSyncReconciler"/>) had ONE caller —
/// <c>ShippedPrebuiltBundles.SeedPublishedRoot</c>, at boot. That is why nine hours of tagged
/// releases sat undelivered on two production portals on 2026-09-12, and why the only thing that
/// ever cleared it was a restart.</para>
///
/// <para><b>What is measured here.</b> A green build the seal does not cover is held (the
/// precondition — asserted, so nothing below can hold vacuously). Then the publication is SEALED at
/// that commit and announced in the mesh the way the publishing lane announces it, as
/// <c>Hosting/PlatformBuilds/&lt;source&gt;</c>. The source must import at that commit with NO
/// further webhook. Against the pre-fix code nothing listens to that announcement and the fetch
/// never happens — the fact this test states is precisely the fact the incident needed.</para>
///
/// <para>🚨 <b>And the negative control that keeps it from being a resubscribe loop in disguise:</b>
/// a publication announced while the seal is still at the OLD commit must move nothing. If it did,
/// this would be a watcher that imports on any stimulus rather than on the seal — a band-aid
/// wearing the fix's name.</para>
///
/// <para>One seam is substituted, the GitHub transport, exactly as in
/// <c>HeldSourceSaysItIsHeldTest</c>, whose scaffolding this mirrors. The published bundle root is a
/// REAL directory carrying the markers <c>publish-bake-bundles.sh</c> writes, and the announcement
/// is a REAL node write, so the post-commit LOGICAL feed the watcher subscribes to
/// (<see cref="IMeshChangeFeed"/>, not <c>IMeshInvalidationFeed</c> — see
/// <see cref="AnnouncePublication"/> for why that distinction is the load-bearing one) is the
/// production one rather than a stand-in.</para>
/// </summary>
public class SealArrivalReleasesHeldSourceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/seal-arrival";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>The bake source directory the seal lives under — the name the Plugins lane uses.</summary>
    private const string SealedSourceName = "plugins";

    /// <summary>The commit the seal covers when the green build arrives.</summary>
    private const string SealedSha = "24c2d024b1f25e84a9c98525cfb543d3cbb79fda";

    /// <summary>The green build the seal does not cover yet — the one that is held, and the one the
    /// publication is later sealed at.</summary>
    private const string LaterSha = "222853d4aabbccddeeff00112233445566778899";

    private readonly RecordingRepoClient repoClient = new();

    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-seal-arrival-" + Guid.NewGuid().ToString("N")[..12]);

    private static string UserId => TestUsers.Admin.ObjectId!;

    private const string AnnouncementNodeType = "Hosting/PlatformBuild";

    /// <summary>What the publishing lane POSTs when it seals: the identity it sealed under, the
    /// version and the content commit. Only the node's PATH matters to the watcher; this exists so
    /// the create is a real, typed node write rather than a bare one.</summary>
    public record PlatformBuildAnnouncement(string Identity, string Version, string Sha);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        StageSeal(SealedSha);
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            // The publishing lane's announcement node type. Declared here rather than taken from
            // the Hosting plugin (MeshWeaver.Plugins), which a core mesh does not load: the watcher
            // keys on the PATH and never on the type, so all this has to do is let CreateNode
            // accept a node at Hosting/PlatformBuilds/<source> the way the webhook inbox does.
            .AddMeshNodes(new MeshNode(AnnouncementNodeType)
            {
                Name = "Platform Build",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<PlatformBuildAnnouncement>()),
            })
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);

                // Layer the published-bundle root ONTO the host configuration; replacing it takes
                // every other key with it (see HeldSourceSaysItIsHeldTest for the measurement).
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

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

    /// <summary><c>&lt;root&gt;/&lt;this process's identity&gt;/plugins/</c> with the three markers a
    /// sealed publication carries. Re-callable: re-sealing at a later commit is what the publishing
    /// lane does, and it is the event under test.</summary>
    private void StageSeal(string commit)
    {
        var sourceDirectory = Path.Combine(
            publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, SealedSourceName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), RepoFullName);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), commit);
        File.WriteAllText(
            Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Empty);
    }

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private GitHubWebhookProcessor Webhooks =>
        Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    /// <summary>
    /// 🚨 Policy <c>module-sync-per-manifest-hash</c> re-expresses this test. There is no hold for
    /// the seal's arrival to release any more: the green build lands at once, and the census never
    /// records the repository frozen. What the arrival must STILL never do is move a source — neither
    /// back to an OLDER sealed commit (the source is ahead of it) nor anywhere once the seal catches
    /// up to the commit the source already holds.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task WithNothingHeld_TheBuildLandsAtOnce_AndASealArrivalNeverMovesTheSource()
    {
        await ArmedSpace("SealArrival");

        var census = Mesh.ServiceProvider.GetRequiredService<SealedSyncCensus>();

        // ── a green build the seal does not cover LANDS, with no seal involved ──────────────
        var landed = repoClient.FetchedRefs.Where(r => r == LaterSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the green build is the proof; the seal decides adoption only",
                TestContext.Current.CancellationToken);
        await Deliver(LaterSha);
        (await landed).Should().Be(LaterSha);
        census.Holds().Should().BeEmpty("the seal holds no source, so nothing is ever reported frozen");

        // ── an announcement while the seal is at an OLDER commit must not roll the source back ──
        var neverBack = repoClient.FetchedRefs.Where(r => r == SealedSha)
            .Should().NotEmit(within: TestTimeouts.Quick, cancellationToken: TestContext.Current.CancellationToken);
        await AnnouncePublication();
        await neverBack;

        // ── the seal catches up to the commit the source holds: the steady state, nothing moves ──
        var fetchedSoFar = repoClient.Count;
        var nothingMore = repoClient.FetchedRefs.Skip(fetchedSoFar)
            .Should().NotEmit(within: TestTimeouts.Quick, cancellationToken: TestContext.Current.CancellationToken);
        StageSeal(LaterSha);
        await AnnouncePublication();
        await nothingMore;
        census.Holds().Should().BeEmpty();
    }

    /// <summary>
    /// The publication announcement the publishing lane writes — a REAL node write through the mesh,
    /// never a poke at the storage adapter or at a feed.
    ///
    /// <para>🚨 That distinction is the load-bearing one, and it is why this helper is shaped like
    /// this. The watcher listens on <see cref="IMeshChangeFeed"/> — the LOGICAL feed, which delivers
    /// once, in the process that performed the write — rather than on
    /// <c>IMeshInvalidationFeed</c>, which deliberately delivers in every replica and would have
    /// every pod dispatch the same GitHub import. A test that published on a feed directly, or that
    /// wrote through <c>IStorageAdapter</c> (whose durable echo reaches the invalidation feed
    /// ONLY — <c>StorageChangeFeedRelayTest</c> pins exactly that), would have asserted the
    /// watcher's plumbing while leaving the seam choice unmeasured. A real node write is what the
    /// webhook inbox performs, so this is what has to reach the logical feed.</para>
    /// </summary>
    private async Task AnnouncePublication()
    {
        const string path = "Hosting/PlatformBuilds";
        if (announced)
        {
            await Mesh.GetWorkspace().GetMeshNodeStream($"{path}/{SealedSourceName}")
                .Update(node => node with { Name = "Publication " + Guid.NewGuid().ToString("N")[..8] })
                .Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken);
            return;
        }
        await NodeFactory.CreateNode(new MeshNode(SealedSourceName, path)
        {
            NodeType = AnnouncementNodeType,
            Name = "Publication",
            State = MeshNodeState.Active,
            Content = new PlatformBuildAnnouncement(
                PrebuiltAssemblySeeder.LiveFrameworkMvid, "3.0.0-ci.8392", LaterSha),
        }).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        announced = true;
    }

    private bool announced;

    private async Task<string> ArmedSpace(string prefix)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Seal-arrival space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return space;
    }

    private async Task Deliver(string headSha)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            await Webhooks.Process("workflow_run", GreenBuildPayload(headSha))
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        }
        finally
        {
            accessService.SetHostIdentity(
                new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    private static JsonElement GreenBuildPayload(string headSha) => JsonDocument.Parse($$"""
        {
          "action": "completed",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "workflow_run": {
            "conclusion": "success", "head_branch": "main", "head_sha": "{{headSha}}",
            "id": 34668111508, "run_number": 5957, "name": "Content CI", "event": "push",
            "path": ".github/workflows/ci.yml",
            "updated_at": "2026-09-12T03:44:46Z"
          }
        }
        """).RootElement;

    /// <inheritdoc />
    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(publishedRoot))
                Directory.Delete(publishedRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run costs nothing and must never fail a test.
        }
        base.Dispose();
    }

    /// <summary>
    /// The GitHub transport, reduced to the one question this test asks: which ref did the import
    /// request? Everything else throws, so a future caller that starts depending on another
    /// operation is told rather than silently served a fake answer.
    /// </summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private readonly ReplaySubject<string> fetched = new();

        private int count;

        /// <summary>Every commitish a fetch has asked for, replayed to a late subscriber.</summary>
        public IObservable<string> FetchedRefs => fetched;

        /// <summary>How many fetches have been asked for so far.</summary>
        public int Count => System.Threading.Volatile.Read(ref count);

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            System.Threading.Interlocked.Increment(ref count);
            fetched.OnNext(commitish);
            // An empty snapshot at the requested sha: the import then has nothing to write, which is
            // exactly what this test wants — the RECORD is the measurement, the content is not.
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
            new NotSupportedException("SealArrivalReleasesHeldSourceTest's repo client answers only Fetch."));
    }
}
