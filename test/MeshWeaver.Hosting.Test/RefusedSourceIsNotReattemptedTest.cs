using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A source whose subdirectory matches nothing refused on EVERY publication announcement, at
/// ONE unchanged sealed commit, forever</b> (Systemorph/MeshWeaver#4499).
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-16</b> through the control instance's
/// <c>Logs</c> action: 64 refusal lines in one hour, not truncated — ~32 refusals/hour — for two
/// Spaces, <c>DeepSign</c> (a folder MeshWeaver.Plugins renamed to <c>Signature</c>) and
/// <c>UWDeepfield</c> (a folder MeshWeaver.Reinsurance deleted), always at the commit the instance's
/// seal named (<c>061976bc</c>, <c>a225cbe9</c>). Every one was a full repository fetch plus an
/// Activity node, and nothing it did could change: the same commit under the same subdirectory lists
/// the same nothing.</para>
///
/// <para><b>The mechanism.</b> #3945 gave the green-build webhook a skip for a source holding a FINAL
/// verdict at the built commit. The seal reconciler (<see cref="SealedSyncReconcile"/>), which runs on
/// every <c>Hosting/PlatformBuilds/&lt;source&gt;</c> announcement, never asked — so a source that
/// cannot converge at the sealed commit was re-imported on every announcement. And the refusal
/// itself cleared the attempt pair, so even the webhook could not have skipped it.</para>
///
/// <para>🚨 <b>The control that keeps this from being "never sync again".</b> A refusal is a
/// CONFIGURATION fault, and the operator's remedy is to edit the source — at the same commit. So the
/// verdict is recorded against the configuration it was read under, and an edit must re-attempt on
/// the very next announcement without waiting for the repository to move. That is the last step.</para>
///
/// <para>Scaffolding mirrors <see cref="SealArrivalReleasesHeldSourceTest"/>: a REAL published-bundle
/// root carrying the seal markers, a REAL announcement node write reaching the logical change feed,
/// and one substituted seam — the GitHub transport, which returns an empty listing (what a
/// subdirectory matching nothing produces) and reports every fetch HOT, never replayed. The
/// negative ("nothing is dispatched") is read off the reconciler's own dispatch count rather than
/// off a silence window, so it costs no budget. The green-build webhook asks the same predicate
/// (<c>GitHubSyncService.HasFinalVerdictAt</c>); <see cref="GreenBuildSkipsASettledSourceTest"/>
/// measures that trigger's skip on the clone itself.</para>
/// </summary>
public class RefusedSourceIsNotReattemptedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/refused-source";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";
    private const string SealedSourceName = "plugins";
    private const string SealedSha = "061976bc88d3cf28811959cd112da3704c735f70";
    private const string MissingSubdirectory = "DeepSign";
    private const string CorrectedSubdirectory = "Signature";
    private const string AnnouncementNodeType = "Hosting/PlatformBuild";

    private readonly FetchCountingRepoClient repoClient = new();

    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-refused-source-" + Guid.NewGuid().ToString("N")[..12]);

    private bool announced;

    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <summary>What the publishing lane POSTs when it seals. Only the node's PATH matters to the
    /// watcher; this exists so the create is a real, typed node write.</summary>
    public record PlatformBuildAnnouncement(string Identity, string Version, string Sha);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        StageSeal(SealedSha);
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
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
                // Layer the published-bundle root ONTO the host configuration (replacing it takes
                // every other key with it — see HeldSourceSaysItIsHeldTest for the measurement).
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

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// 🚨 Policy <c>module-sync-per-manifest-hash</c> re-expresses this case. The seal's announcement
    /// no longer imports ANY source by itself — it used to import a source with no last-sync commit at
    /// the sealed commit, which is where #4499's loop lived — so the loop cannot recur on this lane at
    /// all, whatever the attempt pair says and whether or not the source was edited. The refusal is
    /// still a conclusion that records itself (final, scoped to its configuration), which the
    /// green-build webhook's skip reads (<see cref="GreenBuildSkipsASettledSourceTest"/>).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ASealAnnouncement_NeverRefetchesARefusedSource_EditedOrNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArmedSpace(ct);

        // ── 1. the announcement alone imports nothing, even for a source with no commit ──────────
        await AnnouncePublication(ct);
        (await Reconcile(ct)).Should().Be(0,
            "the seal does not choose a source's commit; a source with no commit is brought by its "
            + "first import or its next green build");

        // ── 2. the source refuses at the sealed commit when a person imports it there ────────────
        var refusal = await Sync.ReimportAtCommit(space, SealedSha, UserId)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        refusal.Should().BeOfType<SyncSubdirectoryEmptyException>("the subdirectory matches nothing");
        var refused = await ConfigWhen(space,
            c => string.Equals(c.LastSyncOutcome, GitHubSyncService.RefusedOutcome, StringComparison.Ordinal)
                 && c.LastAttemptWasFinal, ct);
        refused.LastAttemptedCommitSha.Should().Be(SealedSha,
            "the refusal IS an attempt, and a verdict about exactly the bytes at this commit");
        refused.LastAttemptedConfigFingerprint.Should().NotBeNullOrEmpty();
        refused.LastSyncCommitSha.Should().BeNull("nothing landed, so the SEEN pointer must not claim this commit");
        await QueryShows(space, c => c.LastAttemptedCommitSha == SealedSha && c.LastAttemptWasFinal, ct);

        // ── 3. THE PIN: no reconcile at this seal dispatches anything — refused, or edited ───────
        (await Reconcile(ct)).Should().Be(0, "the #4499 loop was this dispatch; it no longer exists");

        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Update(node => node with
            {
                Content = node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)! with
                {
                    Subdirectory = CorrectedSubdirectory,
                },
            })
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        await QueryShows(space, c => c.Subdirectory == CorrectedSubdirectory, ct);
        (await Reconcile(ct)).Should().Be(0,
            "a corrected source is re-attempted by its next green build or a person's import, never by the seal");
    }

    /// <summary>One reconcile of this identity's sealed publications — exactly what a publication
    /// announcement runs — answering the number of imports it dispatched.</summary>
    private Task<int> Reconcile(CancellationToken ct)
    {
        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        var sealedForThisIdentity = SealedPublicationIndex.ReadFor(publishedRoot, identity);
        sealedForThisIdentity.Should().ContainSingle(s => s.IsSealed && s.SourceCommit == SealedSha,
            "the reconcile must be asked about the seal this test staged, or a zero proves nothing");
        return Mesh.ServiceProvider.GetRequiredService<IPublicationSyncReconciler>()
            .Reconcile(identity, sealedForThisIdentity, [])
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
    }

    // ── arrangement ──────────────────────────────────────────────────────────

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

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

    private async Task<string> ArmedSpace(CancellationToken ct)
    {
        var space = "Refused" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Refused-source space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        // The subdirectory is the point: the refusal fires only when one is configured.
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", MissingSubdirectory,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    /// <summary>The publication announcement, as a REAL node write reaching the logical change feed
    /// (see <see cref="SealArrivalReleasesHeldSourceTest"/> for why that seam is the load-bearing
    /// one). The first call creates it; every later call re-writes it, which is what a re-announced
    /// publication looks like to the watcher.</summary>
    private async Task AnnouncePublication(CancellationToken ct)
    {
        const string path = "Hosting/PlatformBuilds";
        if (announced)
        {
            await Mesh.GetWorkspace().GetMeshNodeStream($"{path}/{SealedSourceName}")
                .Update(node => node with { Name = "Publication " + Guid.NewGuid().ToString("N")[..8] })
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            return;
        }
        await NodeFactory.CreateNode(new MeshNode(SealedSourceName, path)
        {
            NodeType = AnnouncementNodeType,
            Name = "Publication",
            State = MeshNodeState.Active,
            Content = new PlatformBuildAnnouncement(
                PrebuiltAssemblySeeder.LiveFrameworkMvid, "3.0.0-ci.8692", SealedSha),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        announced = true;
    }

    /// <summary>The sync config as the authoritative node stream reports it, once it satisfies
    /// <paramref name="predicate"/> — never a query, and never a bare first emission.</summary>
    private async Task<GitHubSyncConfig> ConfigWhen(
        string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    /// <summary>Waits until the eventually-consistent QUERY — what both triggers select their
    /// candidates from — reports what the stream already shows, so a trigger measures the decision
    /// rather than the index lag.</summary>
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
    /// The GitHub transport reduced to one answer — an EMPTY listing at the requested commit, which is
    /// what a subdirectory matching nothing produces — and one measurement: every fetch, HOT. A
    /// replaying subject would hand the negative controls the previous step's fetch and fail them
    /// for the wrong reason. The record is made on SUBSCRIBE, because that is when a fetch costs.
    /// </summary>
    private sealed class FetchCountingRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetches = new();

        public IObservable<string> Fetches => fetches;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Defer(() =>
            {
                fetches.OnNext(commitish);
                return Observable.Return(new RepoSnapshot(commitish, Array.Empty<RepoFile>()));
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
            new NotSupportedException("RefusedSourceIsNotReattemptedTest's repo client answers only Fetch."));
    }
}
