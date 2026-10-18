using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Cryptography;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A GitSync read NAMES the identity it authenticates as — the instance's GitHub App installation,
/// a token a source declared (by its configuration key), or an installation a source named — and
/// never the token. Asked of an operator: "show that the records sync runs on the App, not on a
/// personal access token"; the activity said "Imported" and nothing about its credential.
///
/// <para>The name comes from the SAME decision the token resolution takes
/// (<see cref="GitHubSyncService.ReadIdentityFor"/>), so a repository read as the App is named as
/// the App, and a repository whose source declared a token is named by that token's key.</para>
/// </summary>
public class GitSyncNamesItsReadIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Records = "https://github.com/Northwind/records";
    private const string Tokened = "https://github.com/Northwind/tokened";
    private const string Elsewhere = "https://github.com/Fabrikam/modules";
    private const string TokenValue = "github_pat_never_named_on_an_activity";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // A real key, so the App JWT signs and the mint reaches the fake GitHub below.
    private readonly RSA appKey = RSA.Create(2048);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services
                .AddGitHubSyncServices()
                // Last registration wins. The App is configured with an OWNER but no installation id,
                // and the fake GitHub has NO installation on that owner: the mint falls back to the
                // first installation (Contoso, 11) — the case where naming the configured owner
                // would attribute the read to an account that did not perform it.
                .AddSingleton<IOptions<GitHubAppOptions>>(Options.Create(new GitHubAppOptions
                {
                    ClientId = "Iv23-test",
                    PrivateKey = appKey.ExportRSAPrivateKeyPem(),
                    InstallationOwner = "Northwind",
                }))
                .AddSingleton(sp => new GitHubAppTokenService(
                    sp.GetRequiredService<IoPoolRegistry>(),
                    sp.GetRequiredService<IOptions<GitHubAppOptions>>(),
                    httpClient: new HttpClient(new InstallationsHandler(
                        """[{"id": 11, "account": {"login": "Contoso"}}, {"id": 33, "account": {"login": "Fabrikam"}}]"""))))
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PluginCatalog:Sources:0:Name"] = "Records",
                        ["PluginCatalog:Sources:0:RepoPath"] = Records,
                        ["PluginCatalog:Sources:1:Name"] = "Tokened",
                        ["PluginCatalog:Sources:1:RepoPath"] = Tokened,
                        ["PluginCatalog:Sources:1:TokenKey"] = "PluginCatalog__SourceTokens__Tokened",
                        ["PluginCatalog:SourceTokens:Tokened"] = TokenValue,
                        ["PluginCatalog:Sources:2:Name"] = "Elsewhere",
                        ["PluginCatalog:Sources:2:RepoPath"] = Elsewhere,
                        ["PluginCatalog:Sources:2:InstallationOwner"] = "Fabrikam",
                    })
                    .Build()));

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private static string Caller => MeshWeaver.Mesh.Security.WellKnownUsers.System;

    /// <summary>
    /// The App installation is named from the installation the token was MINTED for, not from
    /// configuration: no installation matches the configured owner (Northwind), so the mint falls
    /// back to the first (Contoso, 11) — and that is what the activity must name.
    /// </summary>
    [Fact]
    public async Task ARepositoryNoSourceDeclares_IsNamedByTheInstallationTheMintSelected()
    {
        var identity = await Sync.ReadIdentityFor(Caller, Records).FirstAsync().Timeout(Budget).Await();

        Assert.Equal(GitHubReadIdentityKind.AppInstallation, identity.Kind);
        Assert.Equal(11, identity.InstallationId);
        Assert.Equal("Contoso", identity.InstallationOwner);

        var line = identity.ToLogMessage();
        Assert.Equal("activity.gitsync.identity.app", line.MessageKey);
        Assert.Equal(LogLevel.Information, line.LogLevel);
        Assert.Contains("11 (Contoso)", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Northwind", line.Message, StringComparison.Ordinal);
    }

    /// <summary>A repository with no source at all (a Space's own sync config) is the App too.</summary>
    [Fact]
    public async Task ARepositoryOutsideTheCatalog_IsReadAsTheApp()
    {
        var identity = await Sync.ReadIdentityFor(Caller, "https://github.com/Northwind/space")
            .FirstAsync().Timeout(Budget).Await();
        Assert.Equal(GitHubReadIdentityKind.AppInstallation, identity.Kind);
    }

    [Fact]
    public async Task ATokenDeclaredSource_IsNamedByItsKey_NeverByItsValue()
    {
        var identity = await Sync.ReadIdentityFor(Caller, Tokened).FirstAsync().Timeout(Budget).Await();

        Assert.Equal(GitHubReadIdentityKind.SourceToken, identity.Kind);
        Assert.Equal("PluginCatalog:SourceTokens:Tokened", identity.TokenKey);

        var line = identity.ToLogMessage();
        Assert.Equal("activity.gitsync.identity.sourceToken", line.MessageKey);
        Assert.Contains("PluginCatalog:SourceTokens:Tokened", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstallationNamingSource_IsNamedByItsOwner()
    {
        var identity = await Sync.ReadIdentityFor(Caller, Elsewhere).FirstAsync().Timeout(Budget).Await();

        Assert.Equal(GitHubReadIdentityKind.SourceInstallation, identity.Kind);
        Assert.Equal("Fabrikam", identity.InstallationOwner);
        Assert.Equal("activity.gitsync.identity.sourceInstallation", identity.ToLogMessage().MessageKey);
    }

    /// <summary>The line for each kind is keyed; "none" is a Warning; the person is named by login.</summary>
    [Fact]
    public void EveryKind_WritesAKeyedLine()
    {
        var person = new GitHubReadIdentity(GitHubReadIdentityKind.PersonConnected, Login: "octocat").ToLogMessage();
        Assert.Equal("activity.gitsync.identity.person", person.MessageKey);
        Assert.Contains("@octocat", person.Message, StringComparison.Ordinal);

        var none = new GitHubReadIdentity(GitHubReadIdentityKind.None).ToLogMessage();
        Assert.Equal("activity.gitsync.identity.none", none.MessageKey);
        Assert.Equal(LogLevel.Warning, none.LogLevel);

        // An installation pinned by id alone is named by its id under its own key — never an
        // English fragment passed as an argument, which the catalog would not translate.
        var pinned = new GitHubReadIdentity(GitHubReadIdentityKind.AppInstallation, InstallationId: 162001736)
            .ToLogMessage();
        Assert.Equal("activity.gitsync.identity.appId", pinned.MessageKey);
        Assert.Contains("162001736", pinned.Message, StringComparison.Ordinal);
    }

    /// <summary>Fake GitHub: lists the App's installations and mints a token for any of them.</summary>
    internal sealed class InstallationsHandler(string installationsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(installationsJson),
                });
            if (request.Method == HttpMethod.Post && path.StartsWith("/app/installations/", StringComparison.Ordinal)
                && path.EndsWith("/access_tokens", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{"token": "ghs_test", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):o}}"}"""),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
