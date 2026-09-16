#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A sync source that REFUSES must say so on its own node.</b> The refusal itself is correct
/// and protective — a configured subdirectory matching nothing yields an empty snapshot, and
/// importing that under <c>FullReplace</c> would mirror the whole Space away (#1326) — but until
/// this, the refusal logged, threw, and wrote NOTHING to the config. From outside, a source
/// refusing on <b>every single pass</b> was indistinguishable from one that is working.
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-16 (#4499).</b> Two Spaces — <c>DeepSign</c>
/// and <c>UWDeepfield</c> — refusing at <b>~32 passes/hour</b>, one every two minutes, on both
/// replicas, for an unbounded duration. <c>DeepSign</c> was verified absent rather than merely
/// reported absent: <c>061976bc</c> is a valid commit in MeshWeaver.Plugins and no <c>DeepSign</c>
/// path exists in that tree nor anywhere on its <c>main</c>. The sync was following the publication
/// seal correctly; the <b>subdirectory</b> was the wrong half. Nothing escalated: the only signal
/// was a <c>Warning</c>-shaped log line, and because the refusal is BY DESIGN it can never resolve
/// on its own — no retry creates a directory that does not exist.</para>
///
/// <para><b>This is not new policy.</b> #3581 already established that EVERY conclusion records
/// when it happened and what it was — including branches that deliberately advance nothing. This
/// refusal was the one branch that escaped that rule.</para>
///
/// <para>🚨 <b>What would falsify the fix:</b> the recording must not change the error contract.
/// The refusal still throws, and <see cref="SyncSubdirectoryEmptyException"/> derives from
/// <see cref="InvalidOperationException"/> — what this path threw before — so every existing catch
/// behaves identically. Both halves are asserted below.</para>
/// </summary>
public class RefusedSourceSaysItIsRefusedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string MissingSubdirectory = "DeepSign";
    private const string HeadSha = "061976bc0000000000000000000000000000abcd";

    private readonly EmptySnapshotRepoClient repoClient = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins — the fetch never reaches the network, and returns the
                // one shape this test is about: a snapshot with zero files.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private GitHubCredentialService Credentials =>
        Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private static string UserId => TestUsers.Admin.ObjectId!;

    [Fact(Timeout = 120_000)]
    public async Task ARefusedImport_RecordsTheRefusalOnItsConfig_AndStillThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArmedSpace(ct);

        // ── the import that must refuse ──────────────────────────────────────
        var refusal = await Sync.ReimportAtCommit(space, HeadSha, UserId, sourceId: null)
            .Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        // 🚨 HALF ONE: the error contract is unchanged. A caller catching InvalidOperationException
        // — which is what this path threw before the refusal became a type — still catches it.
        refusal.Exception.Should().BeOfType<SyncSubdirectoryEmptyException>();
        refusal.Exception.Should().BeAssignableTo<InvalidOperationException>(
            "the typed refusal exists so the CALLER can record it without matching message text; "
            + "it must not change what anything already catching this path sees");
        refusal.Exception!.Message.Should().Contain(MissingSubdirectory,
            "the message names the subdirectory, which is the half that is wrong");

        // 🚨 HALF TWO: the conclusion is on the NODE, not only in a log line. Read with a fallback
        // to whatever the node currently says — against the pre-fix code the predicate is never
        // satisfied, and a bare wait would report a hang for a value that is simply absent.
        var after = await ConfigWhenOrCurrent(space,
            c => string.Equals(c.LastSyncOutcome, GitHubSyncService.RefusedOutcome, StringComparison.Ordinal), ct);

        Output.WriteLine($"after the refusal: outcome={after.LastSyncOutcome} note={after.LastSyncNote} "
                         + $"attempted={after.LastAttemptedCommitSha ?? "(cleared)"}");

        after.LastSyncOutcome.Should().Be(GitHubSyncService.RefusedOutcome,
            "a source refusing on every pass is a source that is NOT syncing, and the config node is "
            + "the one artefact an operator reads — two Spaces refused ~32x/hour for an unbounded "
            + "duration with nothing but a log line to show for it (#4499)");
        after.LastSyncNote.Should().NotBeNullOrEmpty(
            "the refusal must carry its REASON where the outcome is; a reason that lives only in a "
            + "log line asks the reader to already suspect a refusal before they can find one");
        after.LastSyncNote.Should().Contain(MissingSubdirectory,
            "the note names the subdirectory, so the fix is readable without opening the logs — this "
            + "is a CONFIGURATION fault and the configuration is what has to change");
        after.LastAttemptedCommitSha.Should().BeNull(
            "the refusal happens BEFORE any import, so it must never leave a #3945 'already "
            + "attempted at this commit' pointer that would licence a later pass to skip");
        after.LastSyncCommitSha.Should().NotBe(HeadSha,
            "nothing was read and nothing landed, so the SEEN pointer must not advance to a commit "
            + "this refusal never imported");
    }

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

        // The subdirectory is the point: the guard fires only when one is configured, because a
        // genuinely empty repo with no subdirectory is a legitimate first-sync state.
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

    private async Task<GitHubSyncConfig> ConfigWhenOrCurrent(
        string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
    {
        var configs = Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n is not null
                        && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is not null)
            .Select(n => n!.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!);
        return await configs.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.Convergence, configs.FirstAsync())
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);
    }

    /// <summary>The transport reduced to the one answer this test needs: a snapshot with NO files,
    /// which is what a subdirectory matching nothing produces. Everything else throws, so a future
    /// caller depending on another operation is told rather than served a fake.</summary>
    private sealed class EmptySnapshotRepoClient : IGitHubRepoClient
    {
        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(new RepoSnapshot(commitish, Array.Empty<RepoFile>()));

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
            string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubPullRequestDetail>();
        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();
        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException("this test's transport answers only Fetch"));
    }
}
