using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Measured on a real mesh through the real GitSync import: a package whose sources moved AFTER its
/// platform floor was last stamped (its <c>mesh-floor.lock</c> vouches for other content) is not
/// written on an instance that knows a newer platform than it runs — the old build keeps serving — and
/// is written as before on an instance that runs the newest platform it knows of. Measured 2026-10-09
/// on memex.systemorph.com (3.0.0-ci.10310, 3.0.0-ci.10319 available): MeshWeaver.Plugins@73e5065d
/// carried the approvals-inbox row selection while <c>Hosting</c> still declared its previous
/// sources' floor <c>3.0.0-ci.10305</c>, GitSync synced it, and the 3.0.0-ci.10310 image had no
/// renderer for the selection.
///
/// <para><b>What is measured.</b> Commit A (witness verified) imports. Commit B changes the source and
/// keeps A's witness (the stamp has not run): with a newer platform known, B is DECLINED — the source
/// keeps A's text, the config keeps commit A and names the reason. NEGATIVE CONTROL: with no newer
/// platform known, the same import of B syncs (policy <c>sources-sync-on-push</c> — nothing waits for
/// a stamp on a current instance). Then commit C carries B's content WITH its witness re-stamped: it
/// syncs even while a newer platform is known. Against the pre-fix code the first hold assertion fails
/// — B's text is written.</para>
///
/// <para>One seam is substituted besides the GitHub transport: the newer-platform reading
/// (<see cref="INewerPlatformReading"/>), because a test process has no self-update poller.</para>
/// </summary>
public class AnUnverifiedFloorHoldsTheImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/unverified-floor";
    private const string CommitA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string CommitB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string CommitC = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string Newer = "3.0.0-ci.10319";

    private readonly RecordingRepoClient repoClient = new();
    private readonly FakeNewerPlatform newer = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    private string Space = "";
    private string TypePath => $"{Space}/Widget";
    private string SourcePath => $"{TypePath}/Source/WidgetView";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services.AddSingleton<INewerPlatformReading>(newer);
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 300_000)]
    public async Task SourcesChangedAfterTheirFloorWasStamped_AreHeldOnALaggingInstance_AndSyncOnACurrentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        Space = "Unverified" + Guid.NewGuid().ToString("N")[..8];
        const string codeA = "class WidgetView { }";
        const string codeB = "class WidgetView { int selection; }";
        var stampA = StampOf(codeA);
        repoClient.Stage(CommitA, Tree(codeA, witnessHash: stampA, moduleVersion: "aaaaaaaaaaaaaaaa"));
        repoClient.Stage(CommitB, Tree(codeB, witnessHash: stampA, moduleVersion: "bbbbbbbbbbbbbbbb"));
        repoClient.Stage(CommitC, Tree(codeB, witnessHash: StampOf(codeB), moduleVersion: "cccccccccccccccc"));
        StampOf(codeB).Should().NotBe(stampA, "the fixture's two sources must hash differently");

        await Armed(ct);
        newer.Set(Newer);
        await Sync.ReimportAtCommit(Space, CommitA, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        (await SourceTextWhen(t => t.Contains(codeA), ct)).Should().Contain(codeA,
            "commit A's floor is stamped for exactly its sources, so a lagging instance takes it");
        (await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitA, ct)).LastSyncCommitSha.Should().Be(CommitA);

        // ── commit B: the sources moved, the witness still vouches for A — and a newer platform exists ──
        await Sync.ReimportAtCommit(Space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var declined = await ConfigWhenOrCurrent(c => c.LastSyncNote?.Contains("stamped") == true, ct);
        Output.WriteLine($"after B (lagging): commit={declined.LastSyncCommitSha} outcome={declined.LastSyncOutcome} note={declined.LastSyncNote}");

        (await SourceText(ct)).Should().Contain(codeA).And.NotContain("selection",
            "B's floor was stamped for A's sources, so it says nothing about B's — and this instance knows "
            + Newer + " exists, which B may need (a renderer only a newer image ships)");
        declined.LastSyncCommitSha.Should().Be(CommitA,
            "a declined module did not land, so the baseline stays and B's files remain in the next diff");
        declined.LastSyncNote.Should().Contain(Newer);
        declined.ModuleOutcomes!.Should().Contain(m => m.Outcome == ModuleSyncOutcomeKind.Declined
                                                     && m.FloorUnverified && m.Floor == Newer);

        // ── NEGATIVE CONTROL: the same import on an instance that runs the newest platform it knows ──
        newer.Set(null);
        await Sync.ReimportAtCommit(Space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        (await SourceTextWhen(t => t.Contains("selection"), ct)).Should().Contain("selection",
            "a current instance takes every push — nothing waits for a stamp (policy sources-sync-on-push)");
        (await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitB, ct)).LastSyncCommitSha.Should().Be(CommitB);

        // ── commit C: the same content with its floor stamped syncs even on a lagging instance ──
        newer.Set(Newer);
        await Sync.ReimportAtCommit(Space, CommitC, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var stamped = await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitC, ct);
        stamped.LastSyncCommitSha.Should().Be(CommitC,
            "C's witness vouches for exactly its sources and its floor is below the running platform");
        stamped.ModuleOutcomes!.Should().NotContain(m => m.FloorUnverified);
    }

    /// <summary>The content hash the stamp would record for a tree carrying <paramref name="code"/> —
    /// computed by the same rule the import reads it with, over the tree MINUS its witness.</summary>
    private static string StampOf(string code)
        => ModuleFloorWitness.ContentHash(
            Tree(code, witnessHash: "unused", moduleVersion: "0000000000000000")
                .Where(f => f.Path != ModuleFloorWitness.FileName)
                .Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Content))),
            Manifest("0000000000000000"))!;

    private static string Manifest(string moduleVersion)
        => $$$"""{"module":"Floor","moduleVersion":"{{{moduleVersion}}}","files":{"Floor/index.json":"recomputed"}}""";

    private static IReadOnlyList<RepoFile> Tree(string code, string witnessHash, string moduleVersion) =>
    [
        new("index.json",
            """{"nodeType":"Space","name":"Unverified-floor space","content":{"minMeshVersion":"3.0.0-ci.10305"}}"""),
        new("manifest.lock", Manifest(moduleVersion)),
        new(ModuleFloorWitness.FileName,
            $$$"""{"schema":"mw-mesh-floor/1","contentHash":"{{{witnessHash}}}","verifiedOn":"3.0.0-ci.10305"}"""),
        new("Widget.json",
            """
            {"$type":"MeshNode","id":"Widget","name":"Widget","nodeType":"NodeType","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"unverified-floor fixture"}}
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

    private async Task Armed(CancellationToken ct)
    {
        await NodeFactory.CreateNode(new MeshNode(Space)
        {
            NodeType = "Space",
            Name = "Unverified-floor space",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        var configNode = await Sync
            .SaveConfig(Space, RepoUrl, "main", null, createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        foreach (var owner in new[] { configNode.CreatedBy, UserId }.Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private async Task<GitHubSyncConfig> ConfigWhenOrCurrent(Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
    {
        var configs = Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(Space))
            .Where(n => n is not null && n.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions) is not null)
            .Select(n => n!.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)!);
        return await configs.Where(predicate).FirstAsync()
            .Timeout(TestTimeouts.Convergence, configs.FirstAsync())
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);
    }

    private Task<string> SourceText(CancellationToken ct) => SourceTextWhen(_ => true, ct);

    private async Task<string> SourceTextWhen(Func<string, bool> predicate, CancellationToken ct)
        => await Mesh.GetWorkspace().GetMeshNodeStream(SourcePath)
            .Select(n => n?.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.Code)
            .Where(code => code is { Length: > 0 } && predicate(code))
            .Select(code => code!)
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);

    /// <summary>The newer-platform reading, set by the test.</summary>
    private sealed class FakeNewerPlatform : INewerPlatformReading
    {
        private readonly BehaviorSubject<string?> current = new(null);

        public void Set(string? version) => current.OnNext(version);

        public IObservable<string?> NewerPlatform(IMessageHub hub) => current.Take(1);
    }

    /// <summary>The GitHub transport, serving a staged tree per commit. Everything else throws, so a
    /// future caller is told rather than served a fake answer.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private ImmutableDictionary<string, IReadOnlyList<RepoFile>> trees =
            ImmutableDictionary<string, IReadOnlyList<RepoFile>>.Empty;

        public void Stage(string commit, IReadOnlyList<RepoFile> files)
            => ImmutableInterlocked.Update(ref trees, map => map.SetItem(commit, files));

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => trees.TryGetValue(commitish, out var files)
                ? Observable.Return(new RepoSnapshot(commitish, [.. files]))
                : Observable.Throw<RepoSnapshot>(new NotSupportedException(
                    $"AnUnverifiedFloorHoldsTheImportTest staged no tree for '{commitish}'."));

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
                "AnUnverifiedFloorHoldsTheImportTest's repo client answers only Fetch."));
    }
}
