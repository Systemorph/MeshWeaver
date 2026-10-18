using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .ConfigureServices(services => services
                .AddGitHubSyncServices()
                // Last registration wins: the App is CONFIGURED (client id + key) — nothing is minted,
                // the identity is decided without a round-trip to GitHub.
                .AddSingleton<IOptions<GitHubAppOptions>>(Options.Create(new GitHubAppOptions
                {
                    ClientId = "Iv23-test",
                    PrivateKey = "-----BEGIN RSA PRIVATE KEY-----\nnot-used\n-----END RSA PRIVATE KEY-----",
                    InstallationId = 162001736,
                    InstallationOwner = "Northwind",
                }))
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

    [Fact]
    public async Task ARepositoryNoSourceDeclares_IsReadAsTheInstancesAppInstallation()
    {
        var identity = await Sync.ReadIdentityFor(Caller, Records).FirstAsync().Timeout(Budget).Await();

        Assert.Equal(GitHubReadIdentityKind.AppInstallation, identity.Kind);
        Assert.Equal(162001736, identity.InstallationId);
        Assert.Equal("Northwind", identity.InstallationOwner);

        var line = identity.ToLogMessage();
        Assert.Equal("activity.gitsync.identity.app", line.MessageKey);
        Assert.Equal(LogLevel.Information, line.LogLevel);
        Assert.Contains("162001736 (Northwind)", line.Message, StringComparison.Ordinal);
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

        Assert.Equal("162001736",
            new GitHubReadIdentity(GitHubReadIdentityKind.AppInstallation, InstallationId: 162001736).InstallationLabel);
        Assert.Equal("on 'Northwind'",
            new GitHubReadIdentity(GitHubReadIdentityKind.AppInstallation, InstallationOwner: "Northwind").InstallationLabel);
    }
}
