using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
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
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A sync source that cannot converge re-cloned its WHOLE repository on every green build of
/// that repository</b> (Systemorph/MeshWeaver#3945) — and the fix must not stop a source that could
/// have healed itself from ever trying again.
///
/// <para><b>The mechanism.</b> Exactly one thing made a <c>workflow_run</c> delivery free:
/// <c>GitHubWebhookProcessor.SkipReason</c>'s <c>lastSyncCommitSha == headSha</c>. And
/// <c>GitHubSyncService.MayAdvanceBaseline</c> deliberately HOLDS that same field whenever an import
/// did not fully converge — <c>Preserved &gt; 0</c> (#675 / #677), <c>Failed &gt; 0</c> (#2229
/// item C), outcome <c>Failed</c>. So the cheapness gate and the convergence guard were ONE field,
/// and holding it for the second reason necessarily disarmed the first.</para>
///
/// <para><b>Measured, 2026-09-10.</b> <c>Essentials/_GitSync</c> on memex.meshweaver.cloud had been
/// paying a full <c>git fetch --depth 1</c> of MeshWeaver.Plugins plus a full parse on every green
/// Plugins build since 2026-08-10 — 31 days, 436 commits behind — while its 33 siblings on the same
/// repository, the same webhook and the same schedule were skipped for free. On core the same
/// delivery rate is ~10/h (254 green publish-signal runs in 24 h, 87 of them a <c>*/15</c> cron
/// probe that builds nothing and never moves <c>head_sha</c> between merges).</para>
///
/// <para><b>Why the second field is not simply "we already attempted this commit".</b> Today's
/// re-clone-per-delivery is ALSO the retry loop for the failures
/// <c>StaticRepoImporter.IsContentVerdict</c> refuses to call final — a store briefly unreachable,
/// an owner that did not answer. Skipping on "same commit, already attempted" alone would make those
/// wait for the next commit, which on a quiet repository is never: a fix that strands every
/// self-healing source. The two tests below are that distinction, and the second is the one that
/// could falsify the first.</para>
///
/// <para>Two seams are substituted, both IO boundaries and both with precedent: the GitHub transport
/// (<see cref="IGitHubRepoClient"/>, as in <c>BuildTriggeredSyncPinsTheBuiltCommitTest</c>) and the
/// storage adapter for ONE marked path (as in <c>CreateWhenTheStoreIsUnreachableTest</c>, which is
/// where the production incident's own stack traces point). Everything between them is the real
/// mesh: the real webhook processor, the real sync service, the real importer, the real node
/// streams.</para>
/// </summary>
public class GreenBuildSkipsASettledSourceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoFullName = "test/settled-source";
    private const string RepoUrl = $"https://github.com/{RepoFullName}";

    /// <summary>Real-shaped 40-hex shas, so nothing downstream can mistake one for a branch name.</summary>
    private const string BuiltSha = "3945000011112222333344445555666677778888";

    private const string NextSha = "99990000aaaabbbbccccddddeeeeffff11112222";

    /// <summary>A path segment whose storage READ/WRITE faults with the production connect-timeout
    /// shape — the transient fault that must stay RETRYABLE (#3050 / #3051 / #3101).</summary>
    private const string UnreachableMarker = "unreachable";

    private readonly ScriptedRepoClient repoClient = new();

    /// <summary>The DevLogin user the test base logs in — read directly, since
    /// <c>AccessService.Context</c> is circuit-scoped and null on the test-method thread.</summary>
    private static string UserId => TestUsers.Admin.ObjectId!;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the scripted client replaces the git/Octokit transport,
                // so the whole loop runs offline and the CLONE is directly observable.
                services.AddSingleton<IGitHubRepoClient>(repoClient);

                // Wrap the LAST non-keyed IStorageAdapter — the production decorator chain's
                // outermost layer — so the create handler's own read/write is what faults, exactly
                // as in the incident. Path-gated, so it is inert for every test but the second.
                var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
                services.Remove(registered);
                return services.AddSingleton<IStorageAdapter>(sp =>
                    new PathFaultingStorageAdapter(Materialise(registered, sp)));
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    private GitHubWebhookProcessor Webhooks =>
        Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    // ══════════════════════════════════════════════════════════════════════════
    //  1. THE PIN — a content verdict at this commit costs no second clone
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>An import whose every refusal is a verdict about the BYTES must not be re-cloned at the
    /// same commit — and a NEW commit must still be.</b>
    ///
    /// <para>The assertion is on the FETCH, not on a decision function or a log line, because the
    /// whole cost this issue is about is the transfer: <c>FetchAndImport</c> fetches first and diffs
    /// second (it has to — the diff is computed against <c>snapshot.CommitSha</c>), and a configured
    /// subdirectory scopes the PARSE, never the transfer. Against the pre-fix code the second
    /// delivery clones the repository again; against the fix it does not reach GitHub at all.</para>
    ///
    /// <para>The third step is the control that keeps the fix from being "never sync again": the
    /// baseline is deliberately NOT advanced by this outcome, so the source is still behind, and the
    /// next commit must still bring it a full unscoped import.</para>
    /// </summary>
    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, and
    // the inner waits below already carry the adaptive bound. This outer one only stops a WEDGE.
    [Fact(Timeout = 240_000)]
    public async Task AContentVerdictAtThisCommit_CostsNoSecondClone_AndANewCommitStillDoesImport()
    {
        var space = await Arrange("Cs");

        // ── 1. A real import at BuiltSha whose every failure is a verdict about the content: a
        //       nested Space, which the mesh's own rules refuse with InvalidPath ("A 'Space' owns
        //       its partition, so it must be top-level") — one of the exact refusals memex-cloud
        //       counted 425 of, per pass, 19 passes running (#3146).
        repoClient.Sha = BuiltSha;
        repoClient.Files = [Page("Lesson"), NestedSpace("Nested")];

        var first = await Sync.ReimportAtCommit(space, BuiltSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"import 1: outcome={first.Outcome} count={first.Count} failed={first.Failed}");

        first.Outcome.Should().Be(StaticRepoImportResult.ContentErrorsOutcome,
            "every failure in this pass must be a content verdict — if it is not, the source is not "
            + "in the state this test is about and every assertion below measures something else");
        first.VerdictIsFinal.Should().BeTrue(
            "re-reading these same bytes re-derives this same refusal, so attempting the same "
            + "commit again cannot accomplish anything");

        var afterFirst = await ConfigWhen(space, c => c.LastAttemptedCommitSha is { Length: > 0 });
        Output.WriteLine(
            $"config: attempted={afterFirst.LastAttemptedCommitSha} final={afterFirst.LastAttemptWasFinal} "
            + $"baseline={afterFirst.LastSyncCommitSha ?? "(none)"} outcome={afterFirst.LastSyncOutcome}");

        afterFirst.LastAttemptedCommitSha.Should().Be(BuiltSha,
            "the source has now LOOKED at exactly these bytes, and that fact needs a field of its own");
        afterFirst.LastAttemptWasFinal.Should().BeTrue(
            "the verdict is final at this commit, which is what licences the skip below");
        afterFirst.LastSyncCommitSha.Should().BeNull(
            "🚨 the CONSERVATIVE baseline must be untouched: a commit whose nodes did not all land "
            + "is not 'seen', and advancing it here is the #2229 item C hole this fix must not "
            + "reopen. The whole point is that the two questions now have two fields");

        // The processor selects its candidates from an eventually-consistent QUERY, never from the
        // node stream, so delivering the instant the stream shows the new content would race the
        // index rather than measure the decision.
        await QueryShows(space, c => c.LastAttemptedCommitSha == BuiltSha);

        // ── 2. THE MEASURED SHAPE. The same green build arrives again — three workflows go green on
        //       one merge, and a `*/15` cron probe re-delivers the unchanged tip 96 times a day.
        var noSecondClone = repoClient.Fetches.Should().NotEmit(TestTimeouts.Quick,
            "a delivery at a commit this source has already reached a FINAL verdict on must not "
            + "reach GitHub at all — the clone is unconditional (fetch first, diff second) and a "
            + "subdirectory scopes only the parse, so an attempt that can change nothing still "
            + "transfers the whole repository (#3945)");

        var again = await DeliverGreenBuild(BuiltSha);
        again.Should().Be(1, "the build completion is still recorded — only the IMPORT is skipped");
        await noSecondClone;

        // ── 3. THE CONTROL THAT KEEPS THIS FROM BEING 'NEVER SYNC AGAIN'. The repository moves.
        var newCommitClones = repoClient.Fetches.Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a source that is genuinely behind must still import: the skip is scoped to the "
                + "ONE commit already judged, never to the source");

        repoClient.Sha = NextSha;
        await DeliverGreenBuild(NextSha);

        (await newCommitClones).Should().Be(NextSha,
            "and it must ask for the commit THAT BUILD proved, not the branch (MeshWeaver.Plugins#1430)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. THE FALSIFIER — a failure that might not recur is still retried
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>The case that could falsify the one above, and the reason the skip needs TWO conditions
    /// rather than "we already attempted this commit".</b>
    ///
    /// <para>A node whose store was briefly unreachable comes back as
    /// <c>NodeUpsertRejectionReason.Unknown</c> — deliberately NOT in <c>IsContentVerdict</c>'s
    /// allow-list, because calling a transient fault final is #3101: a partition frozen out of the
    /// mesh until its content changes. One such failure among four hundred content verdicts keeps
    /// the outcome <c>ImportedWithErrors</c>, and this source must keep being attempted — today the
    /// only thing that retries it IS the next delivery, and on a quiet repository the next commit
    /// is never.</para>
    ///
    /// <para>So the assertion is the exact opposite of the first test's, on the same instrument: the
    /// delivery at the SAME commit must reach GitHub.</para>
    /// </summary>
    // 240_000 ms — see the note on the first test.
    [Fact(Timeout = 240_000)]
    public async Task AFailureThatMightNotRecur_IsStillAttemptedAtTheSameCommit()
    {
        var space = await Arrange("Rt");

        // ── 1. One node lands; one faults because its store is unreachable. Not a rule about the
        //       bytes — an outage, which the very same bytes may sail through next time.
        repoClient.Sha = BuiltSha;
        repoClient.Files = [Page("Lesson"), Page($"{UnreachableMarker}Blip")];

        var first = await Sync.ReimportAtCommit(space, BuiltSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await();
        Output.WriteLine($"import 1: outcome={first.Outcome} count={first.Count} failed={first.Failed}");

        first.Failed.Should().BeGreaterThan(0,
            "the fault injection must actually have refused a node — if nothing failed, this test "
            + "is measuring a clean import and proves nothing about the retryable arm");
        first.Outcome.Should().NotBe(StaticRepoImportResult.ContentErrorsOutcome,
            "a store that was briefly unreachable is NOT a verdict about the bytes: classifying it "
            + "as one is #3101, a healthy partition frozen out of the mesh until its content changes");
        first.VerdictIsFinal.Should().BeFalse(
            "so re-running MIGHT do better, and the engine must say so rather than settle");

        var afterFirst = await ConfigWhen(space, c => c.LastAttemptedCommitSha is { Length: > 0 });
        Output.WriteLine(
            $"config: attempted={afterFirst.LastAttemptedCommitSha} final={afterFirst.LastAttemptWasFinal} "
            + $"baseline={afterFirst.LastSyncCommitSha ?? "(none)"} outcome={afterFirst.LastSyncOutcome}");

        afterFirst.LastAttemptedCommitSha.Should().Be(BuiltSha,
            "the source DID look at these bytes — recording that is not the same as licencing a skip");
        afterFirst.LastAttemptWasFinal.Should().BeFalse(
            "🚨 and this is the whole difference: the sha alone must never be the skip. A source "
            + "whose last failure might not recur has to keep being attempted, or the fix strands "
            + "every self-healing source until its repository happens to produce a new commit");
        afterFirst.LastSyncCommitSha.Should().BeNull(
            "the baseline is held for the same reason it always was — some node did not land (#2229 item C)");

        await QueryShows(space, c => c.LastAttemptedCommitSha == BuiltSha);

        // ── 2. THE POSITIVE CONTROL. The same green build arrives again and MUST be acted on.
        var retried = repoClient.Fetches.Should().Within(TestTimeouts.Convergence * 2)
            .Emit("a source whose last failure was NOT a verdict about the content must still be "
                + "attempted at the same commit — the next delivery is the only thing that retries "
                + "it, and a skip here would make a transient store fault permanent (#3101)");

        await DeliverGreenBuild(BuiltSha);

        (await retried).Should().Be(BuiltSha,
            "at the commit that build proved, exactly as an ordinary delivery would");
    }

    // ── arrangement ──────────────────────────────────────────────────────────

    /// <summary>A fresh Space with a sync source pointed at the fixture repository, and the
    /// credential seeded for the identity the import will actually resolve — the config's CREATOR
    /// (the activity-owner model), read off the node rather than assumed.</summary>
    private async Task<string> Arrange(string prefix)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Settled source",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await();

        var syncOwner = configNode.CreatedBy is { Length: > 0 } creator ? creator : UserId;
        Output.WriteLine($"sync config {configNode.Path} createdBy={syncOwner}");
        await Credentials
            .Save(syncOwner, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await();
        return space;
    }

    /// <summary>
    /// One verified green build of the default branch, delivered exactly as GitHub delivers it.
    ///
    /// <para>🚨 The webhook request is ANONYMOUS — its authorization is the verified HMAC signature.
    /// Every ambient identity is dropped so the processor's own System impersonation is what carries
    /// the lookups and the write, as it must on an access-gated portal.</para>
    /// </summary>
    private async Task<int> DeliverGreenBuild(string headSha)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            return await Webhooks.Process("workflow_run", GreenBuildPayload(headSha))
                .Timeout(TestTimeouts.Convergence).Await();
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    private static JsonElement GreenBuildPayload(string headSha) => JsonDocument.Parse($$"""
        {
          "action": "completed",
          "repository": { "full_name": "{{RepoFullName}}", "default_branch": "main" },
          "workflow_run": {
            "conclusion": "success", "head_branch": "main", "head_sha": "{{headSha}}",
            "id": 39450000001, "run_number": 3945, "name": "Content CI", "event": "push",
            "path": ".github/workflows/ci.yml",
            "updated_at": "2026-09-10T17:35:00Z"
          }
        }
        """).RootElement;

    /// <summary>A plain content page — it lands.</summary>
    private static RepoFile Page(string id) =>
        new($"{id}.md", $"---\nNodeType: Markdown\nName: {id}\n---\n\nThe {id} page.\n");

    /// <summary>A nested <c>Space</c> — the mesh refuses it with <c>InvalidPath</c> ("A 'Space' owns
    /// its partition, so it must be top-level"), a rule about these bytes that the same bytes break
    /// identically on every later pass.</summary>
    private static RepoFile NestedSpace(string id) =>
        new($"{id}.md", $"---\nNodeType: Space\nName: {id}\n---\n\nA Space cannot live in here.\n");

    // ── reading the source back ──────────────────────────────────────────────

    /// <summary>
    /// The sync config as the authoritative node stream reports it, once it satisfies
    /// <paramref name="predicate"/> — never a query (eventually consistent, and this reads right
    /// after a write), and never a bare first emission (the cache can replay the pre-write value).
    /// </summary>
    private async Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate)
    {
        var path = GitHubSyncService.ConfigPath(space);
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } c
                        && predicate(c))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();
        return node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!;
    }

    /// <summary>
    /// 🚨 Waits until the eventually-consistent QUERY — which is what the webhook processor selects
    /// its candidates from, never the node stream — reports the config the stream already shows.
    /// Without this the delivery would race the index and the test would measure the race rather
    /// than the decision. (In production a stale index costs one extra clone, which is the very
    /// thing this issue is about, so the race is real and is not the subject.)
    /// </summary>
    private Task QueryShows(string space, Func<GitHubSyncConfig, bool> predicate)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{GitHubSyncService.ConfigPath(space)}"))
                .Take(1))
            .Where(c => c.Items.Any(n =>
                n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is { } cfg && predicate(cfg)))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();

    // ── the substituted IO boundaries ────────────────────────────────────────

    /// <summary>
    /// The GitHub transport reduced to the one question this test asks: WAS THE REPOSITORY CLONED,
    /// and for which commit. Everything else throws, so a future caller that starts depending on
    /// another operation is told rather than silently served a fake answer.
    ///
    /// <para>🚨 <see cref="Fetches"/> is HOT, not replaying. The negative control asserts that NO
    /// clone follows a delivery, and a replaying subject would hand it the previous test step's
    /// fetch and fail for the wrong reason.</para>
    /// </summary>
    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        private readonly Subject<string> fetches = new();

        /// <summary>Every commitish a fetch asks for, as it happens.</summary>
        public IObservable<string> Fetches => fetches;

        /// <summary>The commit the next fetch reports. Written by the test between deliveries.</summary>
        public string Sha { get; set; } = BuiltSha;

        /// <summary>The tree the next fetch returns.</summary>
        public ImmutableList<RepoFile> Files { get; set; } = ImmutableList<RepoFile>.Empty;

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            // Defer: the clone is what SUBSCRIBING costs, so the record has to be made there — a
            // fetch composed and never subscribed transfers nothing and must count as nothing.
            => Observable.Defer(() =>
            {
                fetches.OnNext(commitish);
                return Observable.Return(new RepoSnapshot(Sha, Files.ToArray()));
            });

        // The default GetChangedPaths (null → full import) is deliberately inherited: a client with
        // no compare support is the documented safe fallback, and these sources never advance their
        // baseline anyway, so there is no base sha for a diff to be computed against.

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
                "GreenBuildSkipsASettledSourceTest's repo client answers only Fetch."));
    }

    /// <summary>
    /// A driver exception core can construct: every ADO.NET provider derives its faults from
    /// <see cref="DbException"/>. The verbatim incident shape — "Failed to connect …" wrapping a
    /// connect timeout — which the create pipeline classifies as an outage, not a verdict
    /// (#3050 / #3051), so the refusal reaches the importer as
    /// <c>NodeUpsertRejectionReason.Unknown</c> and stays retryable.
    /// </summary>
    private sealed class FakeDbException(string message, Exception? inner = null)
        : DbException(message, inner)
    {
        public override string? SqlState => null;
    }

    private static Exception? FaultFor(string path)
        => path.Contains(UnreachableMarker, StringComparison.Ordinal)
            ? new FakeDbException("Failed to connect to 10.42.18.4:5432",
                new TimeoutException("Timeout during connection attempt"))
            : null;

    private static IStorageAdapter Materialise(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationFactory is { } factory
            ? (IStorageAdapter)factory(sp)
            : descriptor.ImplementationInstance as IStorageAdapter
              ?? throw new InvalidOperationException(
                  "The IStorageAdapter registration is neither a factory nor an instance, so this "
                  + "test cannot wrap it. If the persistence registration lane changed, update this "
                  + "hook — silently falling back to an unwrapped store would make the retryable-arm "
                  + "assertions vacuous.");

    /// <summary>
    /// Makes the READ/WRITE surfaces fault for the ONE marked path; everything else forwards
    /// untouched, so the mesh boots, the partition exists, and every unmarked node still lands.
    ///
    /// <para>Forwarding is exhaustive on purpose: <c>IStorageAdapter</c>'s doc-comments require a
    /// decorator to forward <c>Changes</c>, <c>DeleteIfExists</c>, <c>WriteIfVersion</c>,
    /// <c>ResolvePath</c> and <c>ListDescendantPaths</c>, or the behaviour they carry is silently
    /// lost at the outermost decorator that falls back to the interface default.</para>
    /// </summary>
    private sealed class PathFaultingStorageAdapter(IStorageAdapter inner) : IStorageAdapter
    {
        private static IObservable<T> Fault<T>(Exception ex) => Observable.Throw<T>(ex);

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => FaultFor(path) is { } ex ? Fault<MeshNode?>(ex) : inner.Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => paths.Select(FaultFor).FirstOrDefault(e => e is not null) is { } ex
                ? Fault<MeshNode>(ex)
                : inner.ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => FaultFor(node.Path) is { } ex ? Fault<MeshNode?>(ex) : inner.Write(node, options);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(
            IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
            => nodes.Select(n => FaultFor(n.Path)).FirstOrDefault(e => e is not null) is { } ex
                ? Fault<IReadOnlyList<MeshNode>>(ex)
                : inner.WriteMany(nodes, options);

        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => FaultFor(node.Path) is { } ex
                ? Fault<bool?>(ex)
                : inner.WriteIfVersion(node, expectedVersion, options);

        public IObservable<bool> Exists(string path)
            => FaultFor(path) is { } ex ? Fault<bool>(ex) : inner.Exists(path);

        public IObservable<string> Delete(string path)
            => FaultFor(path) is { } ex ? Fault<string>(ex) : inner.Delete(path);

        public IObservable<bool> DeleteIfExists(string path)
            => FaultFor(path) is { } ex ? Fault<bool>(ex) : inner.DeleteIfExists(path);

        public IObservable<string?> FindDeleteBlockingProvider(string path)
            => inner.FindDeleteBlockingProvider(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options) => inner.FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
            string fullPath, JsonSerializerOptions options) => inner.ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
            => inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
