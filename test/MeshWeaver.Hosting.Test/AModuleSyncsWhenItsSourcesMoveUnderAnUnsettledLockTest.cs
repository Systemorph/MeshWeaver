using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Measured on a real mesh through the real GitSync import: a module whose sources move in one
/// commit, and whose <c>manifest.lock</c> is settled by the NEXT one, lands its sources. Measured
/// 2026-10-10 on memex.systemorph.com: MeshWeaver.Plugins#3283 merged at <c>f719406d8</c> with
/// <c>Store/Core/Source/SoleMaintainerApproval.cs</c> changed and the Store lock still stating
/// <c>1942f49ee488ee8d</c>; the locks were settled at <c>ae2701acf</c>. The instance's
/// <c>Hosting/InstanceAction</c> — which takes <c>shared=@Store/Core/Source</c> and whose tests call
/// the new members — then failed its compile on every boot with <c>CS0117: 'SoleMaintainerApproval'
/// does not contain a definition for 'SignsAloneKey'</c>.
///
/// <para><b>What is measured.</b> Commit A imports (its lock states its tree). Commit B changes the
/// source and keeps A's lock: the module SYNCS, the source carries B's text, and the outcome says the
/// lock is not settled for these sources. Commit C settles the lock and changes nothing else: the
/// module is UNCHANGED, the source still carries B's text, and the Space holds commit C. The repo
/// client answers the compare between two staged trees as GitHub does — with the default (diff
/// unknown ⇒ full import) the settle commit would re-import everything and hide the defect.</para>
///
/// <para>Against the pre-fix decision the first assertion after B fails: the stated hash is A's, the
/// import is a no-op that records B as held, and at C the diff B..C carries the lock alone — the
/// source keeps A's text for good.</para>
/// </summary>
public class AModuleSyncsWhenItsSourcesMoveUnderAnUnsettledLockTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/unsettled-lock";
    private const string CommitA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string CommitB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private const string CommitC = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    private readonly RecordingRepoClient repoClient = new();

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
                return services.AddSingleton<IGitHubRepoClient>(repoClient);
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 300_000)]
    public async Task SourcesThatMoveBeforeTheirLockIsSettled_Land_AndTheSettleCommitIsThenUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        Space = "Unsettled" + Guid.NewGuid().ToString("N")[..8];
        const string codeA = "class WidgetView { }";
        const string codeB = "class WidgetView { public const string SignsAloneKey = \"k\"; }";
        var settledA = TreeHashOf(codeA);
        var settledB = TreeHashOf(codeB);
        settledB.Should().NotBe(settledA, "the fixture's two sources must hash differently");
        repoClient.Stage(CommitA, Tree(codeA, statedHash: settledA));
        // The merge: the sources moved, the lock is still the previous sources' lock.
        repoClient.Stage(CommitB, Tree(codeB, statedHash: settledA));
        // The settle commit: the lock alone changes.
        repoClient.Stage(CommitC, Tree(codeB, statedHash: settledB));

        await Armed(ct);
        await Sync.ReimportAtCommit(Space, CommitA, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        (await SourceTextWhen(t => t.Contains(codeA), ct)).Should().Contain(codeA);
        var atA = await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitA, ct);
        atA.LastSyncCommitSha.Should().Be(CommitA);
        atA.ModuleVersions!["Floor"].Should().Be(settledA, "a settled lock states the hash its tree has");

        // ── commit B: the sources moved under a lock that still states A's hash ──
        await Sync.ReimportAtCommit(Space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var atB = await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitB, ct);
        Output.WriteLine($"after B: commit={atB.LastSyncCommitSha} outcome={atB.LastSyncOutcome} modules={Describe(atB)}");
        (await SourceText(ct)).Should().Contain("SignsAloneKey",
            "the tree at B carries the moved source, whatever its lock still states — judged by the "
            + "stated hash this import wrote nothing and recorded B as held");
        atB.ModuleOutcomes!.Should().ContainSingle(m => m.Module == "Floor"
                                                      && m.Outcome == ModuleSyncOutcomeKind.Synced
                                                      && m.IncomingVersion == settledB
                                                      && m.Reason.Contains("not settled"));
        atB.LastSyncCommitSha.Should().Be(CommitB, "the Space holds B once B's sources landed");
        atB.ModuleVersions!["Floor"].Should().Be(settledB, "the Space records the hash of what it holds");

        // ── commit C: the lock is settled, nothing else moved — the unchanged case, and it is true ──
        await Sync.ReimportAtCommit(Space, CommitC, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var atC = await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitC, ct);
        Output.WriteLine($"after C: commit={atC.LastSyncCommitSha} outcome={atC.LastSyncOutcome} modules={Describe(atC)}");
        Output.WriteLine("compares: " + string.Join(" | ", repoClient.Compares));
        atC.LastSyncCommitSha.Should().Be(CommitC);
        atC.ModuleOutcomes!.Should().ContainSingle(m => m.Module == "Floor"
                                                      && m.Outcome == ModuleSyncOutcomeKind.Unchanged
                                                      && m.IncomingVersion == settledB);
        (await SourceText(ct)).Should().Contain("SignsAloneKey",
            "the settle commit changes the lock alone, and the Space already holds what it states");
    }

    private static string Describe(GitHubSyncConfig config)
        => string.Join("; ", config.ModuleOutcomes?.Select(m => $"{m.Outcome}: {m.Reason}") ?? []);

    /// <summary>The hash a settled lock states for a tree carrying <paramref name="code"/> — the
    /// production rule, over the tree minus its lock.</summary>
    private static string TreeHashOf(string code)
        => ModuleFloorWitness.TreeVersion(
            Tree(code, statedHash: "unused")
                .Where(f => f.Path != ModuleSyncDecision.ManifestFileName)
                .Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Content))),
            Manifest("unused"))!;

    private static string Manifest(string moduleVersion)
        => $$$"""{"module":"Floor","moduleVersion":"{{{moduleVersion}}}","files":{"Floor/index.json":"recorded"}}""";

    private static IReadOnlyList<RepoFile> Tree(string code, string statedHash) =>
    [
        new("index.json", """{"nodeType":"Space","name":"Unsettled-lock space","content":{}}"""),
        new("manifest.lock", Manifest(statedHash)),
        new("Widget.json",
            """
            {"$type":"MeshNode","id":"Widget","name":"Widget","nodeType":"NodeType","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"unsettled-lock fixture"}}
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
            Name = "Unsettled-lock space",
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
                    $"AModuleSyncsWhenItsSourcesMoveUnderAnUnsettledLockTest staged no tree for '{commitish}'."));

        /// <summary>The compare the production client answers from GitHub: the paths whose content
        /// differs between two staged trees. An unstaged commit answers null — diff unknown.</summary>
        public IObservable<IReadOnlyList<string>?> GetChangedPaths(
            string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        {
            if (!trees.TryGetValue(baseSha, out var before) || !trees.TryGetValue(headSha, out var after))
                return Observable.Return<IReadOnlyList<string>?>(null);
            var was = before.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
            var now = after.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
            var changed = was.Keys.Union(now.Keys)
                .Where(path => was.GetValueOrDefault(path) != now.GetValueOrDefault(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            ImmutableInterlocked.Update(ref compares,
                list => list.Add($"{baseSha[..2]}..{headSha[..2]}: {string.Join(",", changed)}"));
            return Observable.Return<IReadOnlyList<string>?>(changed);
        }

        /// <summary>Every compare this client answered, for the test's own output.</summary>
        public ImmutableList<string> Compares => compares;

        private ImmutableList<string> compares = [];

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
                "AModuleSyncsWhenItsSourcesMoveUnderAnUnsettledLockTest's repo client answers only Fetch and GetChangedPaths."));
    }
}
