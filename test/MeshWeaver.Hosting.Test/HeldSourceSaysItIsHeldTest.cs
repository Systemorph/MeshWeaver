using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A sync source the publication seal HELD must say so on its own node.</b> Until it did, a
/// held source and a settled one were BYTE-IDENTICAL on the one artefact an operator reads, and the
/// difference lived only in a log line (Systemorph/MeshWeaver#4063).
///
/// <para><b>Measured on both production portals, 2026-09-12 ~04:00Z.</b>
/// <c>Hosting/_GitSync</c> read <c>lastSyncOutcome: Imported</c>, <c>lastSyncCommitSha ==
/// lastAttemptedCommitSha == 24c2d024</c> and <c>lastAttemptWasFinal: true</c> — the exact
/// signature of "settled, nothing to do" — while <c>Hosting/v1.17.1</c> and <c>v1.18.0</c> had been
/// tagged by a green <c>main</c> run nine hours earlier and the Space had never seen either. Seven
/// green builds of MeshWeaver.Plugins arrived in between; every one reached
/// <c>GitHubWebhookProcessor.MatchingBuildTargets</c>, was held by <see cref="SealedSyncGate"/>
/// because the publication sealed for this instance's framework identity had stopped advancing, and
/// left NOTHING on the node. The positive controls that the rest of the chain was alive —
/// <c>Admin/_Build</c> rewritten at 03:44:49Z on both portals, an issue fan-out across 34 spaces at
/// 02:07:07Z — were what finally located the hold, one branch at a time.</para>
///
/// <para><b>What is measured here, and what could falsify it.</b> Since MeshWeaver#3845 a green build
/// the seal does not cover LANDS the source on the sealed commit rather than only holding it — the
/// first fact pins that. The second drives a delivery the seal does not cover to a source ALREADY on
/// the seal, where there is nothing to import, and reads the config node back: against the pre-#4065
/// code the node still reads <c>Imported</c> with an empty note, which is the defect stated in the
/// words the incident used. Its control keeps it honest — once the seal advances to cover a build, the
/// delivery must import and CLEAR the note, so the hold record can never be mistaken for a permanent
/// stamp, and "held" stays a statement about one delivery rather than a property of the
/// source.</para>
///
/// <para>One seam is substituted, the GitHub transport (<see cref="IGitHubRepoClient"/>), exactly as
/// in <c>BuildTriggeredSyncPinsTheBuiltCommitTest</c>. The published bundle root is a REAL directory
/// on disk carrying the markers <c>publish-bake-bundles.sh</c> writes, because
/// <see cref="SealedPublicationIndex"/> is pure over the file system and staging it is cheaper and
/// truer than substituting it. Everything between is the real mesh: the real webhook processor, the
/// real gate, the real sync service, the real node streams.</para>
/// </summary>
public class HeldSourceSaysItIsHeldTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/held-source";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>The bake source directory the seal lives under — the name the Plugins lane uses.</summary>
    private const string SealedSourceName = "plugins";

    /// <summary>Real-shaped 40-hex shas, so nothing downstream can mistake one for a branch name.</summary>
    private const string SealedSha = "24c2d024b1f25e84a9c98525cfb543d3cbb79fda";

    /// <summary>A later green build of the same branch — the commit the seal does NOT cover.</summary>
    private const string UnsealedSha = "222853d4aabbccddeeff00112233445566778899";

    /// <summary>A still later green build — delivered to a source already on the seal, and the commit
    /// the seal then advances to for the control.</summary>
    private const string LaterUnsealedSha = "3a2556b0ffeeddccbbaa99887766554433221100";

    private readonly RecordingRepoClient repoClient = new();

    /// <summary>The published bundle root this test stages; removed on dispose.</summary>
    private readonly string publishedRoot = Path.Combine(
        Path.GetTempPath(), "mw-held-source-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The DevLogin user the test base logs in — read directly, since
    /// <c>AccessService.Context</c> is circuit-scoped and null on the test-method thread.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        StageSealedPublication();
        return base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/Octokit transport,
                // so the whole loop runs offline and the FETCH is directly observable.
                services.AddSingleton<IGitHubRepoClient>(repoClient);

                // 🚨 LAYER the published-bundle root ONTO the host's configuration, never replace
                // it. A bare AddSingleton<IConfiguration> wins the resolve and takes every other key
                // with it — measured here: the credential save then refused with "no master key is
                // configured (Ai:KeyProtection:MasterKey)", a failure about a key this test never
                // mentions.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var builder = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        builder.AddConfiguration(host);
                    return builder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.PublishedRootConfigKey] = publishedRoot,
                    }).Build();
                });
            });
    }

    /// <summary>The host's own <c>IConfiguration</c>, from whichever registration shape it used, or
    /// null when the host registered none. Never silently empty on an unrecognised shape: a test
    /// whose mesh lost every configuration key fails somewhere else entirely.</summary>
    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it. If the host's configuration lane changed, update this hook — "
                + "replacing it outright drops every key the mesh needs to boot.");

    /// <summary>
    /// <c>&lt;root&gt;/&lt;this process's identity&gt;/plugins/</c> with the three markers a sealed
    /// publication carries: the producing repository, the commit it was baked from, and the
    /// completion sentinel. The sentinel lists no bundles, which
    /// <see cref="SealedPublicationIndex"/> reads as sealed-and-complete — the seal is what this
    /// test needs, not the bytes.
    /// </summary>
    private void StageSealedPublication(string commit = SealedSha)
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

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a
    // constant, and the inner waits below already carry the adaptive bound. This outer one
    // only has to stop a WEDGE.
    /// <summary>
    /// 🚨 <b>A green build lands its OWN commit — the seal never redirects or holds it</b> (policy
    /// <c>module-sync-per-manifest-hash</c>). This used to pin the redirect onto the sealed commit
    /// (MeshWeaver#3845); against that code this fetches the SEALED commit and the config never
    /// reaches the built one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AGreenBuildNotSealedForThisInstance_ImportsTheBuiltCommit_NeverTheSealedOne()
    {
        var space = await ArmedSpace("Lands", TestContext.Current.CancellationToken);

        var builtFetched = repoClient.FetchedRefs.Where(r => r == UnsealedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a green build the seal does not cover still lands its own commit",
                TestContext.Current.CancellationToken);

        await Deliver(UnsealedSha, TestContext.Current.CancellationToken);
        (await builtFetched).Should().Be(UnsealedSha);

        var landed = await ConfigWhenOrCurrent(space,
            c => string.Equals(c.LastSyncCommitSha, UnsealedSha, StringComparison.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);
        landed.LastSyncCommitSha.Should().Be(UnsealedSha, "the Space sits on the commit the build proved");
        landed.LastSyncOutcome.Should().NotBe(GitHubSyncService.HeldOutcome);
        repoClient.Requested.Should().NotContain(SealedSha,
            "the seal decides adoption only — it never chooses which commit the sources land on");
    }

    /// <summary>
    /// 🚨 <b>A source on a commit the seal does not cover is NEVER rolled back to the seal</b> (policy
    /// <c>module-sync-per-manifest-hash</c>). This used to pin the rollback (review on #4576): a build
    /// of the commit the Space already held re-imported the older sealed commit. Now the delivery is
    /// "already at this commit" and nothing is fetched at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASourceOnAnUnsealedCommit_StaysThere_AndIsNeverRolledBackToTheSeal()
    {
        var space = await ArmedSpace("Stays", TestContext.Current.CancellationToken);

        await Sync.ReimportAtCommit(space, UnsealedSha, UserId)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        var onIt = await ConfigWhen(space,
            c => string.Equals(c.LastSyncCommitSha, UnsealedSha, StringComparison.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);
        onIt.LastSyncCommitSha.Should().Be(UnsealedSha, "the premise: the Space holds a tree the seal does not cover");

        // A negative with no positive signal to wait on — a bounded window is the sanctioned shape.
        var neverRolledBack = repoClient.FetchedRefs.Where(r => r == SealedSha)
            .Should().NotEmit(within: TestTimeouts.Convergence,
                cancellationToken: TestContext.Current.CancellationToken);
        await Deliver(UnsealedSha, TestContext.Current.CancellationToken);
        await neverRolledBack;

        var after = await ConfigWhenOrCurrent(space, _ => true, TestContext.Current.CancellationToken);
        after.LastSyncCommitSha.Should().Be(UnsealedSha, "moving it to the older sealed commit would go backwards");
        after.LastSyncOutcome.Should().NotBe(GitHubSyncService.HeldOutcome, "nothing is held any more");
    }

    /// <summary>
    /// 🚨 <b>Successive green builds the seal does not cover each LAND, and none records a hold</b>
    /// (policy <c>module-sync-per-manifest-hash</c>). This used to pin the per-delivery HOLD record
    /// (#4063/#4065) on a source already on the seal; with no hold to take, the node carries each
    /// import's own outcome and an empty note — so "held" can never be read off a source that is
    /// simply up to date, and a source that is behind is behind only until its next green build.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GreenBuildsTheSealDoesNotCover_EachLand_AndNoneRecordsAHold()
    {
        var space = await ArmedSpace("NoHold", TestContext.Current.CancellationToken);

        await Deliver(UnsealedSha, TestContext.Current.CancellationToken);
        await ConfigWhen(space,
            c => string.Equals(c.LastSyncCommitSha, UnsealedSha, StringComparison.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);

        var fetched = repoClient.FetchedRefs.Where(r => r == LaterUnsealedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("the next green build is imported at its own commit, sealed or not",
                TestContext.Current.CancellationToken);
        await Deliver(LaterUnsealedSha, TestContext.Current.CancellationToken);
        (await fetched).Should().Be(LaterUnsealedSha);

        var afterImport = await ConfigWhenOrCurrent(space, c =>
            string.Equals(c.LastSyncCommitSha, LaterUnsealedSha, StringComparison.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);
        Output.WriteLine(
            $"after the second delivery: outcome={afterImport.LastSyncOutcome} "
            + $"note={afterImport.LastSyncNote ?? "(cleared)"} sha={afterImport.LastSyncCommitSha}");
        afterImport.LastSyncCommitSha.Should().Be(LaterUnsealedSha);
        afterImport.LastSyncOutcome.Should().NotBe(GitHubSyncService.HeldOutcome,
            "the seal holds no source, so no delivery may record a hold");
        afterImport.LastSyncNote.Should().BeNullOrEmpty("nothing was held, declined or preserved");
        repoClient.Requested.Should().NotContain(SealedSha);
    }

    /// <summary>
    /// The Space's first activity satisfying <paramref name="predicate"/>. An activity is created
    /// mid-import, so the listing is re-asked until one matches — through the sanctioned
    /// re-query shape (a query source has no stream to wait on), never a delay.
    /// </summary>
    private async Task<ActivityLog> ActivityWhen(
        string space, Func<ActivityLog, bool> predicate, CancellationToken cancellationToken)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{space}/_Activity scope:descendants").Complete().AsSystem())
                .Take(1))
            .SelectMany(change => change.Items
                .Select(n => n.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
                .Where(log => log is not null && predicate(log)))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken)
            ?? throw new InvalidOperationException("the predicate matched a null activity log");
    }

    /// <summary>A Space with a sync config for this repository and a credential for whoever the
    /// config write attributed itself to, so the import path can authenticate.</summary>
    private async Task<string> ArmedSpace(string prefix, CancellationToken cancellationToken)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Seal-held space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);

        // The import authenticates as the sync config's CREATOR — read it off the node rather than
        // assuming which identity the write landed under.
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        return space;
    }

    /// <summary>One verified green-build delivery, with every ambient identity dropped: the webhook
    /// request is ANONYMOUS — its authorization is the HMAC signature — so the processor's own
    /// System impersonation must be what carries the lookups and the writes.</summary>
    private async Task Deliver(string headSha, CancellationToken cancellationToken)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            await Webhooks.Process("workflow_run", GreenBuildPayload(headSha))
                .Timeout(TestTimeouts.Convergence).Await(cancellationToken);
        }
        finally
        {
            accessService.SetHostIdentity(
                new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    /// <summary>The sync config once it satisfies <paramref name="predicate"/>, or — when it never
    /// does within the bound — whatever the node says RIGHT NOW, so the caller's assertion is what
    /// fails and names the field. A bare timeout would report a hang for a value that is simply
    /// wrong.</summary>
    private async Task<GitHubSyncConfig> ConfigWhenOrCurrent(
        string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken cancellationToken)
    {
        var configs = Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is not null)
            .Select(n => n!.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!);
        return await configs.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.Convergence, configs.FirstAsync())
            .Timeout(TestTimeouts.CrossSilo)
            .Await(cancellationToken);
    }

    /// <summary>The sync config as the authoritative node stream reports it, once it satisfies
    /// <paramref name="predicate"/> — never a query (eventually consistent, and this reads right
    /// after a write), and never a bare first emission (the cache can replay the pre-write value).
    /// </summary>
    private async Task<GitHubSyncConfig> ConfigWhen(
        string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken cancellationToken)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
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
        private System.Collections.Immutable.ImmutableList<string> requested =
            System.Collections.Immutable.ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch has asked for, replayed to a late subscriber.</summary>
        public IObservable<string> FetchedRefs => fetched;

        /// <summary>The same record as a snapshot — complete once the imports that made it have landed.</summary>
        public System.Collections.Immutable.ImmutableList<string> Requested => requested;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref requested, list => list.Add(commitish));
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
            new NotSupportedException("HeldSourceSaysItIsHeldTest's repo client answers only Fetch."));
    }
}
