using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 follow-up, measured on a real mesh through the real GitSync import: a package
/// whose tree declares <c>requires: ["AI@^1.21.0"]</c> while this process has AI 1.20.4 LOADED must
/// not move its NodeType sources (which would compile them against a build that lacks what they
/// call). Measured 2026-10-04 on the control instance: Hosting 1.56's sources were imported and
/// compiled at 20:00:50Z against the loaded AI 1.20.4, and every thread start threw
/// <c>MissingMethodException</c> (<c>ThreadPreparation.set_Group</c>).
///
/// <para><b>What is measured.</b> Commit A (no requirement) imports and the type compiles. Commit B
/// changes the source AND declares <c>AI@^1.21.0</c>; with AI 1.20.4 loaded the import is DECLINED:
/// the source keeps A's text, the type keeps its build, the config keeps commit A and names the
/// requirement and both versions. Then a satisfying AI 1.21.0 is loaded and the same import runs
/// again: B's sources land. Against the pre-fix code the first assertion fails — B's text is written.</para>
///
/// <para>One seam is substituted besides the GitHub transport: the loaded-module reading
/// (<see cref="ILoadedPackageModules"/>), because a test process cannot load two builds of one
/// module. Its production implementation is pinned on its own (<c>LoadedPackageModuleReaderTest</c>).</para>
/// </summary>
public class ADependencyFloorHoldsTheImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/dependency-floor";
    private const string CommitA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string CommitB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";

    private readonly RecordingRepoClient repoClient = new();
    private readonly FakeLoadedModules loaded = new();

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
                return services.AddSingleton<ILoadedPackageModules>(loaded);
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 300_000)]
    public async Task APackageRequiringANewerDependencyThanIsLoaded_KeepsItsSources_UntilTheDependencyIsLoaded()
    {
        var ct = TestContext.Current.CancellationToken;
        Space = "Floor" + Guid.NewGuid().ToString("N")[..8];
        loaded.Set("AI", "MeshWeaver.AI", "1.20.4");
        repoClient.Stage(CommitA, Tree("class WidgetView { }", requires: null, moduleVersion: "aaaaaaaaaaaaaaaa"));
        repoClient.Stage(CommitB, Tree("class WidgetView { int group; }", requires: "AI@^1.21.0",
            moduleVersion: "bbbbbbbbbbbbbbbb"));

        await Armed(ct);
        await Sync.ReimportAtCommit(Space, CommitA, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        (await SourceTextWhen(t => t.Contains("class WidgetView { }"), ct)).Should().Contain("class WidgetView { }");
        (await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitA, ct)).LastSyncCommitSha.Should().Be(CommitA);

        // ── commit B requires AI@^1.21.0, and AI 1.20.4 is what is loaded ──
        await Sync.ReimportAtCommit(Space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        var declined = await ConfigWhenOrCurrent(c => c.LastSyncNote?.Contains("AI@^1.21.0") == true, ct);
        Output.WriteLine($"after B: commit={declined.LastSyncCommitSha} outcome={declined.LastSyncOutcome} note={declined.LastSyncNote}");

        (await SourceText(ct)).Should().Contain("class WidgetView { }").And.NotContain("int group",
            "the package requires AI@^1.21.0 and AI 1.20.4 is loaded — moving its sources would compile them "
            + "against a build that lacks what they call (MissingMethodException at run time)");
        declined.LastSyncCommitSha.Should().Be(CommitA,
            "a declined module did not land, so the baseline stays and B's files remain in the next diff");
        declined.LastSyncNote.Should().Contain("AI@^1.21.0").And.Contain("1.20.4");
        declined.ModuleOutcomes.Should().NotBeNull();
        declined.ModuleOutcomes!.Should().Contain(m => m.Outcome == ModuleSyncOutcomeKind.Declined
                                                     && m.UnmetRequirement == "AI@^1.21.0");

        // ── the dependency lands and is LOADED: the same import now syncs B ──
        loaded.Set("AI", "MeshWeaver.AI", "1.21.0");
        await Sync.ReimportAtCommit(Space, CommitB, UserId).Timeout(TestTimeouts.CrossSilo).Await(ct);
        (await SourceTextWhen(t => t.Contains("int group"), ct)).Should().Contain("int group",
            "a satisfying dependency is loaded, so the held sources arrive");
        (await ConfigWhenOrCurrent(c => c.LastSyncCommitSha == CommitB, ct)).LastSyncCommitSha.Should().Be(CommitB);
    }

    private static IReadOnlyList<RepoFile> Tree(string code, string? requires, string moduleVersion) =>
    [
        new("index.json",
            requires is null
                ? """{"nodeType":"Space","name":"Dependency-floor space"}"""
                : $$$"""{"nodeType":"Space","name":"Dependency-floor space","content":{"requires":["{{{requires}}}"]}}"""),
        new("manifest.lock", $$$"""{"module":"Floor","moduleVersion":"{{{moduleVersion}}}","files":{}}"""),
        new("Widget.json",
            """
            {"$type":"MeshNode","id":"Widget","name":"Widget","nodeType":"NodeType","state":"Active",
             "content":{"$type":"NodeTypeDefinition","description":"dependency-floor fixture"}}
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
            Name = "Dependency-floor space",
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

    /// <summary>The loaded-module reading, set by the test.</summary>
    private sealed class FakeLoadedModules : ILoadedPackageModules
    {
        private readonly BehaviorSubject<ImmutableDictionary<string, LoadedPackageModule>> current =
            new(ImmutableDictionary<string, LoadedPackageModule>.Empty);

        public void Set(string package, string module, string version)
            => current.OnNext(current.Value.SetItem(package, new LoadedPackageModule(module, version)));

        public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() => current.Take(1);
    }

    /// <summary>The GitHub transport, serving a staged tree per commit and recording what was asked
    /// for. Everything else throws, so a future caller is told rather than served a fake answer.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private ImmutableDictionary<string, IReadOnlyList<RepoFile>> trees =
            ImmutableDictionary<string, IReadOnlyList<RepoFile>>.Empty;
        private ImmutableList<string> requested = ImmutableList<string>.Empty;

        /// <summary>Every commitish a fetch asked for, in order.</summary>
        public ImmutableList<string> Requested => requested;

        public void Stage(string commit, IReadOnlyList<RepoFile> files)
            => ImmutableInterlocked.Update(ref trees, map => map.SetItem(commit, files));

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            ImmutableInterlocked.Update(ref requested, list => list.Add(commitish));
            return trees.TryGetValue(commitish, out var files)
                ? Observable.Return(new RepoSnapshot(commitish, [.. files]))
                : Observable.Throw<RepoSnapshot>(new NotSupportedException(
                    $"ADependencyFloorHoldsTheImportTest staged no tree for '{commitish}'."));
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
            new NotSupportedException(
                "ADependencyFloorHoldsTheImportTest's repo client answers only Fetch."));
    }
}
