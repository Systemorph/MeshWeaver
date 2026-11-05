using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The NodeType page's Code area shows the file FROM ITS REPOSITORY, read as the viewer</b>
/// (maintainer, 2026-11-05: "it's just about getting the file from github and displaying on gui";
/// "if i am not authorized, ask for login"; "repo is private and requires my github credential").
///
/// <para>The module's provenance — here a <c>{Space}/_GitSync</c> entry — names the repository, the
/// commit and the folder; <see cref="GitHubCodeSourceFiles"/> reads the file with the VIEWER's own
/// connected GitHub credential and nothing else. Without one the page asks for a GitHub sign-in;
/// GitHub refusing it (a private repository answers 404) asks again; with one the file's text is
/// shown. The repository client is a recording stand-in: it proves which token, path and ref the
/// read went out with, which is the security half of the contract.</para>
/// </summary>
public class CodePageReadsTheFileFromGitHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/acme/private-modules";
    private const string SyncedSha = "8d4920c93a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string FileText = "public static class WidgetTests { }\n";
    private const string ViewerToken = "ghp_viewer_token";

    private readonly FileReadingRepoClient repoClient = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // Last registration wins: the recording client replaces the git/REST transport.
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                services.AddSingleton<ICodeSourceFiles, GitHubCodeSourceFiles>();
                return services;
            });

    private ICodeSourceFiles Files => Mesh.ServiceProvider.GetRequiredService<ICodeSourceFiles>();

    private GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();

    /// <summary>A Space whose module syncs from <see cref="RepoUrl"/> at <see cref="SyncedSha"/>, folder <c>modules/{space}</c>.</summary>
    private async Task<string> ArrangeSyncedSpace(Func<string, Task>? beforeTheSyncEntry = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var space = "CodeSrc" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = space,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        // Anything authored in the Space goes in BEFORE the sync entry: from then on the Space is
        // owned by its repository and nobody holds a write grant on it.
        if (beforeTheSyncEntry is not null)
            await beforeTheSyncEntry(space);
        await NodeFactory.CreateNode(new MeshNode(GitHubSyncService.ConfigId, space)
        {
            NodeType = GitHubSyncService.ConfigNodeType,
            Name = "GitHub Sync",
            State = MeshNodeState.Active,
            Content = new GitHubSyncConfig
            {
                RepositoryUrl = RepoUrl,
                Branch = "main",
                Subdirectory = $"modules/{space}",
                LastSyncCommitSha = SyncedSha,
            },
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    private Task SaveViewerCredential()
        => Credentials.Save(UserId, new GitHubToken(ViewerToken, null, "bearer", "repo", null), "octocat")
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// Every state of the read, in order: signed out and signed in without a GitHub account are
    /// asked to sign in; with the viewer's account the file is read AS THEM — their token, the
    /// module's folder, the synced commit — and its text comes back; GitHub refusing that token
    /// (404 for a private repository the account cannot see) is a refusal, not an empty file.
    /// </summary>
    [Fact]
    public async Task TheFileIsReadWithTheViewersOwnCredential_OrTheViewerIsAskedToSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArrangeSyncedSpace();
        var codePath = $"{space}/Widget/Test/WidgetTests";
        var expectedFile = $"modules/{space}/Widget/Test/WidgetTests.cs";

        var signedOut = await Files.Read(codePath, viewer: null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(CodeSourceOutcomes.SignInRequired, signedOut.Outcome);
        Assert.Equal(new CodeSourceOrigin(RepoUrl, SyncedSha, expectedFile), signedOut.Origin);

        var noAccount = await Files.Read(codePath, TestUsers.Admin).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(CodeSourceOutcomes.SignInRequired, noAccount.Outcome);
        Assert.Empty(repoClient.Reads);

        // ONE live read, opened BEFORE the account is connected: it answers SignInRequired, then —
        // with no re-open and no reload — the file once the credential lands.
        var live = Files.Read(codePath, TestUsers.Admin).Replay();
        using var liveConnection = live.Connect();
        await live.Where(r => r.Outcome == CodeSourceOutcomes.SignInRequired)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await SaveViewerCredential();
        var read = await live.Where(r => r.Outcome != CodeSourceOutcomes.SignInRequired)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(CodeSourceOutcomes.Read, read.Outcome);
        Assert.Equal(FileText, read.Text);
        var call = Assert.Single(repoClient.Reads);
        Assert.Equal((RepoUrl, expectedFile, SyncedSha, ViewerToken), call);

        // Another person never reads with this viewer's account.
        var someoneElse = await Files.Read(codePath, TestUsers.TestUser).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(CodeSourceOutcomes.SignInRequired, someoneElse.Outcome);
        Assert.Single(repoClient.Reads);

        repoClient.Refuse = true;
        var refused = await Files.Read(codePath, TestUsers.Admin).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(CodeSourceOutcomes.Refused, refused.Outcome);
        Assert.Equal(404, refused.StatusCode);
        Assert.Null(refused.Text);
    }

    /// <summary>A node whose partition records no repository is answered NoOrigin — the mesh copy.</summary>
    [Fact]
    public async Task ANodeWithoutARepositoryOrigin_ShowsTheMeshCopy()
    {
        var read = await Files.Read($"{TestPartition}/Widget/Test/WidgetTests", TestUsers.Admin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Equal(CodeSourceOutcomes.NoOrigin, read.Outcome);
        Assert.Empty(repoClient.Reads);
    }

    /// <summary>
    /// The PAGE, through the layout client, opened with the id exactly as the side menu's link
    /// carries it (still encoded — the measured failure). Without a GitHub account: a message bar
    /// naming the repository and the mesh copy embedded at the DECODED node address. With one: the
    /// file's text in a read-only editor.
    /// </summary>
    [Fact]
    public async Task TheCodePage_AsksForAGitHubSignIn_ThenShowsTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await ArrangeSyncedSpace(s => NodeFactory.CreateNode(new MeshNode("Widget", s)
        {
            Name = "Widget",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
        }).Timeout(TestTimeouts.Convergence).Await(ct));
        var typePath = $"{space}/Widget";
        var codePath = $"{typePath}/Test/WidgetTests";
        // Exactly as the connect flow returns to the page: the encoded id plus the callback's query.
        var encodedId = (string)WorkspaceReference.Encode(codePath) + "?connect=github-ok";

        IObservable<UiControl?> Page() => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(typePath), new LayoutAreaReference(NodeTypeLayoutAreas.CodeArea) { Id = encodedId })
            .WalkTree(NodeTypeLayoutAreas.CodeArea);

        var page = Page().Replay();
        using var connection = page.Connect();
        await page.OfType<MessageBarControl>()
            .Where(bar => bar.Body?.ToString()?.Contains(RepoUrl, StringComparison.Ordinal) == true)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var meshCopy = await page.OfType<LayoutAreaControl>()
            .Where(c => c.Reference.Area == CodeLayoutAreas.ContentArea)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(codePath, meshCopy.Address.ToString());

        // Connect GitHub while the page is OPEN: it follows, no reload.
        await SaveViewerCredential();
        var editor = await page.OfType<CodeEditorControl>()
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.True(editor.Readonly is true or JsonElement { ValueKind: JsonValueKind.True });
        Assert.Contains(repoClient.Reads, r => r.Token == ViewerToken && r.Path.EndsWith("Widget/Test/WidgetTests.cs"));
    }

    // ── pure: the coordinate from an install record ───────────────────────────────────────────

    [Fact]
    public void AnInstallRecord_NamesTheInstalledFileAtTheInstalledCommit()
    {
        var record = new PackageManifest
        {
            Id = "Acme",
            TargetPartition = "Acme",
            RepositoryUrl = RepoUrl,
            InstalledFromRef = SyncedSha,
            InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
            {
                ["Acme/Widget.json"] = "h1",
                ["Acme/Widget/Test/WidgetTests.cs"] = "h2",
            }),
        };

        var parsers = Parsers();
        Assert.Equal(new CodeSourceOrigin(RepoUrl, SyncedSha, "Acme/Widget/Test/WidgetTests.cs"),
            GitHubCodeSourceFiles.FromInstallRecord(record, "Acme/Widget/Test/WidgetTests", parsers));
        Assert.Null(GitHubCodeSourceFiles.FromInstallRecord(record, "Acme/Widget/Test/Missing", parsers));
        Assert.Null(GitHubCodeSourceFiles.FromInstallRecord(record with { RepositoryUrl = null }, "Acme/Widget/Test/WidgetTests", parsers));

        // A node with children installs from `X/index.*` — matched the way the INSTALLER maps it,
        // and requested from GitHub under its own key.
        var withIndex = record with
        {
            InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
            {
                ["Acme/Widget/Source/Model/index.cs"] = "h3",
            }),
        };
        Assert.Equal(new CodeSourceOrigin(RepoUrl, SyncedSha, "Acme/Widget/Source/Model/index.cs"),
            GitHubCodeSourceFiles.FromInstallRecord(withIndex, "Acme/Widget/Source/Model", parsers));
    }

    private FileFormatParserRegistry Parsers()
        => new(Mesh.JsonSerializerOptions, Mesh.ServiceProvider.GetServices<IFileFormatParser>());

    /// <summary>The exporter's naming: a node with children is <c>X/index.ext</c>, the serializer picks the extension.</summary>
    [Fact]
    public void ASyncEntry_NamesTheFileTheWayTheExporterDoes()
    {
        var config = new GitHubSyncConfig { RepositoryUrl = RepoUrl, Branch = "main", Subdirectory = "/modules/Acme/" };

        Assert.Equal("modules/Acme/Widget/Test/T.cs",
            GitHubCodeSourceFiles.FromSyncEntry(config, "Acme", "Acme/Widget/Test/T", ".cs", hasChildren: false)!.FilePath);
        Assert.Equal("modules/Acme/Widget/Cell/index.json",
            GitHubCodeSourceFiles.FromSyncEntry(config, "Acme", "Acme/Widget/Cell", ".json", hasChildren: true)!.FilePath);
        Assert.Equal("main", GitHubCodeSourceFiles.FromSyncEntry(config, "Acme", "Acme/X", ".cs", false)!.Ref);
        Assert.Null(GitHubCodeSourceFiles.FromSyncEntry(
            config with { RepositoryUrl = "https://dev.azure.com/acme/_git/modules" }, "Acme", "Acme/X", ".cs", false));
    }

    /// <summary>A failure keeps the origin; only an ACCESS refusal offers the sign-in.</summary>
    [Fact]
    public void AFailedRead_KeepsItsOrigin()
    {
        var origin = new CodeSourceOrigin(RepoUrl, SyncedSha, "a.cs");
        Assert.Equal(CodeSourceOutcomes.Refused, GitHubCodeSourceFiles.Classify(origin, new GitHubNotFoundException()).Outcome);
        var network = GitHubCodeSourceFiles.Classify(origin, new System.Net.Http.HttpRequestException("down"));
        Assert.Equal(CodeSourceOutcomes.Failed, network.Outcome);
        Assert.Equal(origin, network.Origin);
    }

    [Theory]
    [InlineData("https://user:ghp_secret@github.com/acme/modules.git", "https://github.com/acme/modules")]
    [InlineData("https://github.com/acme/modules?token=x#frag", "https://github.com/acme/modules")]
    [InlineData("https://github.com/acme/modules.git", "https://github.com/acme/modules")]
    [InlineData("https://github.com/acme/modules/", "https://github.com/acme/modules")]
    [InlineData("/srv/checkout/modules", null)]
    [InlineData("", null)]
    public void AListedPackage_RecordsOnlyAHostedRepository(string configured, string? recorded)
        => Assert.Equal(recorded, NodeRepoPackageSource.HostedRepositoryUrl(configured));

    /// <summary>A recording stand-in: answers <see cref="GetFileText"/> and records every read.</summary>
    private sealed class FileReadingRepoClient : IGitHubRepoClient
    {
        private readonly List<(string Url, string Path, string Ref, string Token)> reads = [];

        public IReadOnlyList<(string Url, string Path, string Ref, string Token)> Reads
        {
            get { lock (reads) return reads.ToList(); }
        }

        public bool Refuse { get; set; }

        public IObservable<string> GetFileText(string repositoryUrl, string filePath, string commitish, string accessToken)
            => Observable.Defer(() =>
            {
                lock (reads) reads.Add((repositoryUrl, filePath, commitish, accessToken));
                return Refuse
                    ? Observable.Throw<string>(new GitHubNotFoundException())
                    : Observable.Return(FileText);
            });

        public IObservable<RepoSnapshot> Fetch(string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(new RepoSnapshot(SyncedSha, ImmutableList<RepoFile>.Empty));

        public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
            => Observable.Return(SyncedSha);

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
            new NotSupportedException("CodePageReadsTheFileFromGitHubTest's repo client answers only file reads."));
    }
}
