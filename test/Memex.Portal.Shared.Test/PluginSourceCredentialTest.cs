using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A git plugin source is read with the credential IT declares</b> — and with nothing else
/// when that credential cannot be produced.
///
/// <para>Before this, every URL source was read with the instance's ONE GitHub App installation
/// token. An instance whose App is installed on <c>Systemorph</c> could therefore not read a
/// private repository in <c>Northwind</c>: the token minted for one installation reads nothing
/// in another, and GitHub answers a private repository it will not show as "not found".</para>
///
/// <para>Two declarations, both BY REFERENCE: <c>PluginCatalog:Sources:N:TokenKey</c> names the
/// configuration key the repository's token arrives under (from the vault), and
/// <c>PluginCatalog:Sources:N:InstallationOwner</c> names the account whose installation of the
/// App mints it. The three properties pinned here: the token is chosen PER SOURCE; a declared key
/// that is empty REFUSES by name, with no request to GitHub under any other identity; and two
/// sources on two installations each get their own token, minted once.</para>
/// </summary>
public class PluginSourceCredentialTest
{
    private const string Plugins = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string Extensions = "https://github.com/Northwind/extensions";
    private const string Other = "https://github.com/Fabrikam/modules";

    // Every await is bounded: a wedged token stream must surface as a timeout, never a stuck run.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheTokenIsChosenPerSource_ByTheRepositoryItDeclaresItFor()
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler();
        var credentials = new GitSourceCredentials(
            Config(new()
            {
                ["PluginCatalog:Sources:0:Name"] = "Plugins",
                ["PluginCatalog:Sources:0:RepoPath"] = Plugins,
                ["PluginCatalog:Sources:1:Name"] = "Extensions",
                ["PluginCatalog:Sources:1:RepoPath"] = Extensions,
                ["PluginCatalog:Sources:1:TokenKey"] = "PluginCatalog:SourceTokens:Extensions",
                ["PluginCatalog:Sources:2:Name"] = "Other",
                ["PluginCatalog:Sources:2:RepoPath"] = Other,
                ["PluginCatalog:Sources:2:InstallationOwner"] = "Fabrikam",
                ["PluginCatalog:SourceTokens:Extensions"] = "github_pat_extensions",
            }),
            App(rsa, handler));

        // No declaration → null: the caller keeps its default identity, exactly as before.
        Assert.Null(credentials.TokenFor(Plugins));
        Assert.Null(credentials.TokenFor("https://github.com/Systemorph/NotConfigured"));

        // The vault-delivered token, and only for ITS repository — however the URL is spelled.
        Assert.Equal("github_pat_extensions", await credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("github_pat_extensions",
            await credentials.TokenFor("https://github.com/northwind/EXTENSIONS.git/")!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal(0, handler.Requests);

        // The named installation's token.
        Assert.Equal("ghs_Fabrikam_1", await credentials.TokenFor(Other)!.FirstAsync().Timeout(Budget).Await());
    }

    [Fact]
    public async Task TheEnvironmentSpellingOfATokenKey_NamesTheSameKey()
    {
        var credentials = new GitSourceCredentials(Config(new()
        {
            ["PluginCatalog:Sources:0:Name"] = "Extensions",
            ["PluginCatalog:Sources:0:RepoPath"] = Extensions,
            ["PluginCatalog:Sources:0:TokenKey"] = "PluginCatalog__SourceTokens__Extensions",
            ["PluginCatalog:SourceTokens:Extensions"] = "github_pat_extensions",
        }));

        Assert.Equal("github_pat_extensions", await credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ADeclaredKeyThatIsEmpty_RefusesByName_AndAsksGitHubNothing(string? delivered)
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler();
        var credentials = new GitSourceCredentials(
            Config(new()
            {
                ["PluginCatalog:Sources:0:Name"] = "Extensions",
                ["PluginCatalog:Sources:0:RepoPath"] = Extensions,
                ["PluginCatalog:Sources:0:TokenKey"] = "PluginCatalog:SourceTokens:Extensions",
                ["PluginCatalog:SourceTokens:Extensions"] = delivered,
            }),
            // The App IS configured and would mint: the refusal must not fall back to it.
            App(rsa, handler));

        var declared = credentials.TokenFor(Extensions);
        Assert.NotNull(declared);
        var refusal = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => declared!.FirstAsync().Timeout(Budget).Await());

        Assert.Contains("'Extensions'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Extensions, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'PluginCatalog:SourceTokens:Extensions'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PluginCatalog__SourceTokens__Extensions", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task TwoSourcesOnTwoInstallations_EachGetItsOwnToken_MintedOnce()
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler();
        var app = App(rsa, handler);
        var credentials = new GitSourceCredentials(
            Config(new()
            {
                ["PluginCatalog:Sources:0:Name"] = "Plugins",
                ["PluginCatalog:Sources:0:RepoPath"] = Plugins,
                ["PluginCatalog:Sources:0:InstallationOwner"] = "Systemorph",
                ["PluginCatalog:Sources:1:Name"] = "Extensions",
                ["PluginCatalog:Sources:1:RepoPath"] = Extensions,
                ["PluginCatalog:Sources:1:InstallationOwner"] = "Northwind",
            }),
            app);

        Assert.Equal("ghs_Systemorph_1", await credentials.TokenFor(Plugins)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("ghs_Northwind_1", await credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());

        // Cached PER INSTALLATION: a second read of either mints nothing, and neither answers
        // with the other's token.
        Assert.Equal("ghs_Systemorph_1", await credentials.TokenFor(Plugins)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("ghs_Northwind_1", await credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("ghs_Northwind_1", await app.GetInstallationToken("northwind").FirstAsync().Timeout(Budget).Await());
        Assert.Equal(1, handler.MintsFor("Systemorph"));
        Assert.Equal(1, handler.MintsFor("Northwind"));

        // The instance's OWN installation (GitHub:App:InstallationOwner) is a third promise and
        // is untouched by the named ones.
        Assert.Equal("ghs_Systemorph_2", await app.GetInstallationToken().FirstAsync().Timeout(Budget).Await());
        Assert.Equal(1, handler.MintsFor("Northwind"));
    }

    [Fact]
    public async Task ANamedInstallationTheAppDoesNotHave_FailsByName_NeverWithAnotherInstallationsToken()
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler();
        var app = App(rsa, handler);

        var failure = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => app.GetInstallationToken("Contoso").FirstAsync().Timeout(Budget).Await());

        Assert.Equal(GitHubAppTokenMintStage.InstallationDiscovery, failure.Stage);
        Assert.Contains("'Contoso'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Mints);
    }

    /// <summary>
    /// A source written straight into configuration can state BOTH credentials — the record's own
    /// validation never sees it. Picking a winner would read the repository with an identity
    /// nobody can tell from the declaration, so the reader refuses the pair by name.
    /// </summary>
    [Fact]
    public async Task ASourceDeclaringBothCredentials_IsRefused_NotResolvedToAWinner()
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler();
        var credentials = new GitSourceCredentials(
            Config(new()
            {
                ["PluginCatalog:Sources:0:Name"] = "Extensions",
                ["PluginCatalog:Sources:0:RepoPath"] = Extensions,
                ["PluginCatalog:Sources:0:TokenKey"] = "PluginCatalog:SourceTokens:Extensions",
                ["PluginCatalog:Sources:0:InstallationOwner"] = "Northwind",
                ["PluginCatalog:SourceTokens:Extensions"] = "github_pat_extensions",
            }),
            App(rsa, handler));

        var refusal = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());

        Assert.Equal("gitsync.source.twoCredentials", refusal.Refusal.Key);
        Assert.Contains("'Extensions'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'Northwind'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>
    /// The refusals are raised server-side with no viewer in scope, so each carries a catalog key
    /// and named arguments and is rendered where it is shown — in German for a German viewer.
    /// </summary>
    [Fact]
    public async Task ARefusal_IsRenderedForItsViewer()
    {
        var credentials = new GitSourceCredentials(Config(new()
        {
            ["PluginCatalog:Sources:0:Name"] = "Extensions",
            ["PluginCatalog:Sources:0:RepoPath"] = Extensions,
            ["PluginCatalog:Sources:0:TokenKey"] = "PluginCatalog:SourceTokens:Extensions",
        }));

        var thrown = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());

        Assert.True(GitSyncRefusalException.TryGet(thrown, out var refusal));
        Assert.Equal("gitsync.source.tokenKeyEmpty", refusal.Key);
        var german = refusal.Render("de");
        Assert.Contains("Plugin-Quelle", german, StringComparison.Ordinal);
        Assert.Contains("PluginCatalog__SourceTokens__Extensions", german, StringComparison.Ordinal);
        Assert.DoesNotContain("{", german, StringComparison.Ordinal);
    }

    /// <summary>
    /// The App is installed on more accounts than one page of the listing holds: the named owner
    /// on a later page is still found, rather than reported as an installation that does not exist.
    /// </summary>
    [Fact]
    public async Task ANamedInstallationOnALaterPage_IsFound()
    {
        using var rsa = RSA.Create(2048);
        var handler = new InstallationsHandler { Padding = 100 };
        var app = App(rsa, handler);

        Assert.Equal("ghs_Fabrikam_1", await app.GetInstallationToken("Fabrikam").FirstAsync().Timeout(Budget).Await());
    }

    [Fact]
    public async Task ANamedInstallationWithoutAnApp_RefusesByName()
    {
        var credentials = new GitSourceCredentials(Config(new()
        {
            ["PluginCatalog:Sources:0:Name"] = "Extensions",
            ["PluginCatalog:Sources:0:RepoPath"] = Extensions,
            ["PluginCatalog:Sources:0:InstallationOwner"] = "Northwind",
        }));

        var refusal = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => credentials.TokenFor(Extensions)!.FirstAsync().Timeout(Budget).Await());

        Assert.Contains("'Extensions'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'Northwind'", refusal.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Config(Dictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static GitHubAppTokenService App(RSA rsa, HttpMessageHandler handler) =>
        new(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = rsa.ExportRSAPrivateKeyPem(),
                InstallationOwner = "Systemorph",
            }),
            httpClient: new HttpClient(handler));

    /// <summary>
    /// GitHub's App endpoints for ONE App installed on three accounts. A mint answers
    /// <c>ghs_{owner}_{n}</c>, n counted per installation, so a token names both the installation
    /// it was minted for and whether it was minted again.
    /// </summary>
    private sealed class InstallationsHandler : HttpMessageHandler
    {
        private static readonly ImmutableArray<(long Id, string Owner)> Installations =
            [(77, "Systemorph"), (88, "Northwind"), (99, "Fabrikam")];

        private readonly ConcurrentDictionary<string, int> mintsByOwner = new(StringComparer.Ordinal);
        private int requests;
        private int mints;

        /// <summary>Unrelated installations listed ahead of the real ones.</summary>
        public int Padding { get; init; }

        public int Requests => Volatile.Read(ref requests);

        public int Mints => Volatile.Read(ref mints);

        public int MintsFor(string owner) => mintsByOwner.GetValueOrDefault(owner);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
            {
                // Padding puts that many unrelated installations AHEAD of the real ones, so the
                // real ones fall onto a later page of the 100-per-page listing.
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var perPage = int.TryParse(query["per_page"], out var size) ? size : 30;
                var page = int.TryParse(query["page"], out var number) ? number : 1;
                var all = Enumerable.Range(0, Padding).Select(i => (Id: 1000L + i, Owner: $"filler-{i}"))
                    .Concat(Installations);
                return Json(HttpStatusCode.OK, "[" + string.Join(",", all.Skip((page - 1) * perPage).Take(perPage).Select(i =>
                    $$"""{"id": {{i.Id}}, "account": {"login": "{{i.Owner}}"} }""")) + "]");
            }

            foreach (var (id, owner) in Installations)
            {
                if (request.Method != HttpMethod.Post || path != $"/app/installations/{id}/access_tokens")
                    continue;
                Interlocked.Increment(ref mints);
                var n = mintsByOwner.AddOrUpdate(owner, 1, (_, count) => count + 1);
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                return Json(HttpStatusCode.Created,
                    $$"""{"token": "ghs_{{owner}}_{{n}}", "expires_at": "{{expires}}"}""");
            }

            return Json(HttpStatusCode.NotFound, $$"""{"message": "unexpected {{request.Method}} {{path}}"}""");
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

/// <summary>
/// The same rule where it is APPLIED: the package sources <c>PackageSources.FromConfiguration</c>
/// builds — the list the registry endpoint serves and the default install reads — hand each
/// repository's fetch the token its source declared. A configured source with no declaration keeps
/// the instance's default identity (anonymous here: this mesh has no App), and a declared key
/// that is empty never reaches the repository at all.
/// </summary>
public class PluginSourceCredentialFetchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugins = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string Extensions = "https://github.com/Northwind/extensions";
    private const string Unfunded = "https://github.com/Northwind/unfunded";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly RecordingRepoClient client = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services
                .AddGitHubSyncServices()
                // Last registration wins — for the configuration and for the client below.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PluginCatalog:Sources:0:Name"] = "Plugins",
                        ["PluginCatalog:Sources:0:RepoPath"] = Plugins,
                        ["PluginCatalog:Sources:1:Name"] = "Extensions",
                        ["PluginCatalog:Sources:1:RepoPath"] = Extensions,
                        ["PluginCatalog:Sources:1:TokenKey"] = "PluginCatalog:SourceTokens:Extensions",
                        ["PluginCatalog:Sources:2:Name"] = "Unfunded",
                        ["PluginCatalog:Sources:2:RepoPath"] = Unfunded,
                        ["PluginCatalog:Sources:2:TokenKey"] = "PluginCatalog:SourceTokens:Unfunded",
                        ["PluginCatalog:SourceTokens:Extensions"] = "github_pat_extensions",
                    })
                    .Build())
                .AddSingleton<IGitHubRepoClient>(client));

    [Fact]
    public async Task EachConfiguredSource_FetchesItsRepositoryWithItsOwnToken()
    {
        var sources = PackageSources.FromConfiguration(Mesh, Mesh.ServiceProvider.GetRequiredService<IConfiguration>());
        Assert.Equal(["Plugins", "Extensions", "Unfunded"], sources.Select(s => s.Name));

        await sources[0].Source.ListPackages("main").FirstAsync().Timeout(Budget).Await();
        await sources[1].Source.ListPackages("main").FirstAsync().Timeout(Budget).Await();

        Assert.Equal([""], client.TokensFor(Plugins));
        Assert.Equal(["github_pat_extensions"], client.TokensFor(Extensions));
    }

    [Fact]
    public async Task ASourceWhoseDeclaredKeyIsEmpty_IsNeverFetched()
    {
        var sources = PackageSources.FromConfiguration(Mesh, Mesh.ServiceProvider.GetRequiredService<IConfiguration>());

        var refusal = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => sources[2].Source.ListPackages("main").FirstAsync().Timeout(Budget).Await());

        Assert.Contains("'Unfunded'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'PluginCatalog:SourceTokens:Unfunded'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(client.TokensFor(Unfunded));
    }

    /// <summary>
    /// 🚨 The SYNC route reads the same repository with the same credential. A package installed
    /// out of a source is kept current by GitSync (import, branch reconcile), which resolved its
    /// token on its own — the user's, else the instance's App. Had only the package source learned
    /// the declaration, the catalog would list the repository and every sync of it would fail.
    /// </summary>
    [Fact]
    public async Task GitSync_ReadsADeclaredRepositoryWithTheSameToken_AndRefusesTheSameWay()
    {
        var sync = Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
        var user = MeshWeaver.Mesh.Security.WellKnownUsers.System;

        await sync.GetBranchHead(Extensions, "main", user).FirstAsync().Timeout(Budget).Await();
        Assert.Equal(["github_pat_extensions"], client.TokensFor(Extensions));

        var refusal = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => sync.GetBranchHead(Unfunded, "main", user).FirstAsync().Timeout(Budget).Await());
        Assert.Contains("'Unfunded'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(client.TokensFor(Unfunded));
    }

    /// <summary>Records the token each repository was fetched with and answers an empty repository.</summary>
    private sealed class RecordingRepoClient : IGitHubRepoClient
    {
        private readonly ConcurrentQueue<(string Repo, string Token)> fetches = new();

        public IReadOnlyList<string> TokensFor(string repo) =>
            fetches.Where(f => f.Repo == repo).Select(f => f.Token).Distinct().ToList();

        private static IObservable<TResult> Never<TResult>() => Observable.Never<TResult>();

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        {
            fetches.Enqueue((repositoryUrl, accessToken));
            return Observable.Return(new RepoSnapshot("0000000000000000000000000000000000000000", []));
        }

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => Never<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => Never<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => Never<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => Never<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken) => Never<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => Never<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => Never<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken) => Never<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken) => Never<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken) => Never<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => Never<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken) => Never<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => Never<GitHubMergeResult>();
    }
}
