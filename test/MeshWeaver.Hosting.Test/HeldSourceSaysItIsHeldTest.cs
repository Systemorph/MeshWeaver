using System;
using System.Collections.Generic;
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
using MeshWeaver.Mesh.Security;
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
/// <para><b>What is measured here, and what could falsify it.</b> The first fact drives a real
/// <c>workflow_run</c> delivery at a commit the seal does not cover and reads the config node back:
/// against the pre-fix code the node still reads <c>Imported</c> with an empty note, which is the
/// defect stated in the words the incident used. The second fact is the control that keeps the
/// first honest — a delivery at the SEALED commit must import and CLEAR the note, so the hold
/// record can never be mistaken for a permanent stamp, and "held" stays a statement about one
/// delivery rather than a property of the source.</para>
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
    private void StageSealedPublication()
    {
        var sourceDirectory = Path.Combine(
            publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, SealedSourceName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.RepositoryMarkerFileName), RepoFullName);
        File.WriteAllText(
            Path.Combine(sourceDirectory, SealedPublicationIndex.SourceCommitMarkerFileName), SealedSha);
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
    [Fact(Timeout = 120_000)]
    public async Task SealHeldSource_RecordsTheHoldOnItsConfig_SoItCannotReadAsSettled()
    {
        var space = await ArmedSpace("Held");

        // ── the delivery the seal does not cover ─────────────────────────────
        var neverFetched = repoClient.FetchedRefs.Where(r => r == UnsealedSha)
            .Should().NotEmit(within: TestTimeouts.Quick);

        await Deliver(UnsealedSha);
        await neverFetched;

        // 🚨 Read the node back with a FALLBACK to whatever it currently says, never a bare wait:
        // against the pre-fix code the predicate is never satisfied, and a TimeoutException names no
        // field and reads as a hang. The assertions below must be able to state what the node
        // actually said instead.
        var afterHold = await ConfigWhenOrCurrent(space, c =>
            string.Equals(c.LastSyncOutcome, GitHubSyncService.HeldOutcome, StringComparison.Ordinal));

        Output.WriteLine(
            $"after the held delivery: outcome={afterHold.LastSyncOutcome} "
            + $"note={afterHold.LastSyncNote} attempted={afterHold.LastAttemptedCommitSha ?? "(cleared)"}");

        afterHold.LastSyncOutcome.Should().Be(GitHubSyncService.HeldOutcome,
            "a source the publication seal held is not a source that is up to date, and the node is "
            + "the one artefact an operator reads — on both production portals a held Hosting source "
            + "read 'Imported … final: true' for nine hours while two tagged releases never arrived");
        afterHold.LastSyncNote.Should().NotBeNullOrEmpty(
            "the hold must carry its REASON where the outcome is; a hold whose reason lives only in "
            + "a log line asks the reader to already suspect a hold before they can find one");
        afterHold.LastSyncNote.Should().Contain(SealedSha[..8],
            "the reason names the commit this instance's identity IS sealed at, which is what tells "
            + "an operator whether to wait for a publication or to roll the instance");
        afterHold.LastAttemptedCommitSha.Should().BeNull(
            "a hold means no import ran, so it must never leave a #3945 'already attempted at this "
            + "commit, final' licence standing for the delivery that follows it");
        afterHold.LastAttemptWasFinal.Should().BeFalse(
            "the finality flag is only ever read beside the attempted sha it qualifies");

        // ── the control: the seal DOES cover this one, so it imports and the note clears ──
        var fetched = repoClient.FetchedRefs.Where(r => r == SealedSha)
            .Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a green build AT the sealed commit is exactly what the gate waits for");

        await Deliver(SealedSha);
        (await fetched).Should().Be(SealedSha);

        var afterImport = await ConfigWhen(space, c =>
            string.Equals(c.LastSyncCommitSha, SealedSha, StringComparison.OrdinalIgnoreCase));

        Output.WriteLine(
            $"after the sealed delivery: outcome={afterImport.LastSyncOutcome} "
            + $"note={afterImport.LastSyncNote ?? "(cleared)"} sha={afterImport.LastSyncCommitSha}");

        afterImport.LastSyncOutcome.Should().NotBe(GitHubSyncService.HeldOutcome,
            "the hold describes ONE delivery; an import that ran must replace it, or the record "
            + "would turn a transient condition into a permanent label");
        afterImport.LastSyncNote.Should().BeNullOrEmpty(
            "the note describes the LAST attempt only — a stale hold reason surviving a successful "
            + "import is the same false reading in the opposite direction");
    }

    /// <summary>A Space with a sync config for this repository and a credential for whoever the
    /// config write attributed itself to, so the import path can authenticate.</summary>
    private async Task<string> ArmedSpace(string prefix)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Seal-held space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await();

        // The import authenticates as the sync config's CREATOR — read it off the node rather than
        // assuming which identity the write landed under.
        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await();
        return space;
    }

    /// <summary>One verified green-build delivery, with every ambient identity dropped: the webhook
    /// request is ANONYMOUS — its authorization is the HMAC signature — so the processor's own
    /// System impersonation must be what carries the lookups and the writes.</summary>
    private async Task Deliver(string headSha)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            await Webhooks.Process("workflow_run", GreenBuildPayload(headSha))
                .Timeout(TestTimeouts.Convergence).Await();
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
        string space, Func<GitHubSyncConfig, bool> predicate)
    {
        var configs = Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is not null)
            .Select(n => n!.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!);
        return await configs.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.Convergence, configs.FirstAsync())
            .Timeout(TestTimeouts.CrossSilo)
            .Await();
    }

    /// <summary>The sync config as the authoritative node stream reports it, once it satisfies
    /// <paramref name="predicate"/> — never a query (eventually consistent, and this reads right
    /// after a write), and never a bare first emission (the cache can replay the pre-write value).
    /// </summary>
    private async Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
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

        /// <summary>Every commitish a fetch has asked for, replayed to a late subscriber.</summary>
        public IObservable<string> FetchedRefs => fetched;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
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
