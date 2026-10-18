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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The pure half of the instance's own repository access (Doc/Architecture/InstanceRepositoryAccess):
/// the manifest an instance posts to GitHub, the state check that guards the callback, the
/// conversion reader, which App identity mints a token, and which identity reads an instance-local
/// package source. No mesh: every rule here is a function of its inputs.
/// </summary>
public class InstanceRepositoryAccessUnitTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    // ───────────────────────────── the manifest ─────────────────────────────

    [Fact]
    public void TheManifest_AsksForAPrivateReadOnlyApp_ThatReturnsToThisInstanceOnly()
    {
        var manifest = JsonNode.Parse(InstanceGitHubApp.BuildManifest("https://portal.example.com/"))!;

        Assert.Equal("https://portal.example.com/admin/github-app/callback", (string?)manifest["redirect_url"]);
        Assert.Equal("https://portal.example.com", (string?)manifest["url"]);
        Assert.False((bool)manifest["public"]!);
        var permissions = manifest["default_permissions"]!.AsObject();
        Assert.Equal(["contents", "metadata"], permissions.Select(p => p.Key).OrderBy(k => k));
        Assert.All(permissions, p => Assert.Equal("read", (string?)p.Value));
        Assert.Equal("https://portal.example.com/webhooks/github", (string?)manifest["hook_attributes"]!["url"]);
        Assert.True((bool)manifest["hook_attributes"]!["active"]!);
        Assert.Equal(["push"], manifest["default_events"]!.AsArray().Select(e => (string?)e));
        Assert.Equal("MeshWeaver portal.example.com", (string?)manifest["name"]);
    }

    [Fact]
    public void TheAppName_FitsGitHubsLimit()
    {
        var name = InstanceGitHubApp.AppName("a-very-long-instance-host-name.example.com");
        Assert.True(name.Length <= 34, name);
        Assert.StartsWith("MeshWeaver ", name);
    }

    [Theory]
    [InlineData("acme", "https://github.com/organizations/acme/settings/apps/new?state=s%2Bt")]
    [InlineData("", "https://github.com/settings/apps/new?state=s%2Bt")]
    [InlineData(null, "https://github.com/settings/apps/new?state=s%2Bt")]
    [InlineData("acme/../evil", null)]
    [InlineData("-acme", null)]
    public void TheCreationPage_IsTheOrganizationsOrTheUsersOwn_AndRefusesAnOddName(string? org, string? expected) =>
        Assert.Equal(expected, InstanceGitHubApp.CreationUrl(org, "s+t"));

    [Fact]
    public void TheInstallPage_IsAddressedBySlug()
    {
        Assert.Equal("https://github.com/apps/meshweaver-portal/installations/new",
            InstanceGitHubApp.InstallUrl(new InstanceGitHubAppContent { Slug = "meshweaver-portal" }));
        Assert.Null(InstanceGitHubApp.InstallUrl(new InstanceGitHubAppContent()));
        Assert.Null(InstanceGitHubApp.InstallUrl(null));
    }

    // ───────────────────────────── the state ─────────────────────────────

    private static InstanceGitHubAppContent Pending(string state, string by = "boss", DateTimeOffset? expires = null) => new()
    {
        PendingStateHash = InstanceGitHubApp.HashState(state),
        PendingExpiresAt = expires ?? Now.AddMinutes(5),
        PendingBy = by,
    };

    [Fact]
    public void AStateIsRandom_AndOnlyItsHashIsKept()
    {
        var a = InstanceGitHubApp.NewState();
        var b = InstanceGitHubApp.NewState();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40, "32 random bytes");
        Assert.DoesNotContain(a, InstanceGitHubApp.HashState(a));
        Assert.Matches("^[0-9a-f]{64}$", InstanceGitHubApp.HashState(a));
    }

    [Fact]
    public void TheCallback_PassesOnlyWhenCookieStatePendingHashExpiryAndAdminAllAgree()
    {
        var state = InstanceGitHubApp.NewState();
        Assert.Null(InstanceGitHubApp.CheckState(Pending(state), state, state, "boss", Now));

        // A code from a flow this browser did not start: GitHub's state is not the cookie's.
        Assert.Equal("instanceApp.error.stateMismatch",
            InstanceGitHubApp.CheckState(Pending(state), state, "other", "boss", Now)!.Key);
        Assert.Equal("instanceApp.error.stateMismatch",
            InstanceGitHubApp.CheckState(Pending(state), state, null, "boss", Now)!.Key);
        Assert.Equal("instanceApp.error.stateMismatch",
            InstanceGitHubApp.CheckState(Pending(state), null, null, "boss", Now)!.Key);
        // A replay, after the pending flow was cleared — or a state nobody recorded.
        Assert.Equal("instanceApp.error.noPendingFlow",
            InstanceGitHubApp.CheckState(new InstanceGitHubAppContent(), state, state, "boss", Now)!.Key);
        Assert.Equal("instanceApp.error.noPendingFlow",
            InstanceGitHubApp.CheckState(null, state, state, "boss", Now)!.Key);
        Assert.Equal("instanceApp.error.noPendingFlow",
            InstanceGitHubApp.CheckState(Pending("another"), state, state, "boss", Now)!.Key);
        // Too late.
        Assert.Equal("instanceApp.error.flowExpired",
            InstanceGitHubApp.CheckState(Pending(state, expires: Now), state, state, "boss", Now)!.Key);
        // Another administrator cannot finish somebody else's flow.
        Assert.Equal("instanceApp.error.otherAdmin",
            InstanceGitHubApp.CheckState(Pending(state), state, state, "colleague", Now)!.Key);
    }

    // ───────────────────────────── the conversion ─────────────────────────────

    [Fact]
    public void TheConversion_IsReadAndNeverPrinted()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----\n";
        var json = new JsonObject
        {
            ["id"] = 4242,
            ["slug"] = "meshweaver-portal",
            ["name"] = "MeshWeaver portal",
            ["client_id"] = "Iv23liInstance",
            ["client_secret"] = "never-kept",
            ["pem"] = pem,
            ["webhook_secret"] = "hook-secret",
            ["html_url"] = "https://github.com/apps/meshweaver-portal",
            ["owner"] = new JsonObject { ["login"] = "acme" },
        }.ToJsonString();

        var conversion = GitHubAppManifestConversion.Parse(json);
        Assert.Equal(4242, conversion.AppId);
        Assert.Equal("meshweaver-portal", conversion.Slug);
        Assert.Equal("Iv23liInstance", conversion.ClientId);
        Assert.Equal(pem, conversion.PrivateKey);
        Assert.Equal("hook-secret", conversion.WebhookSecret);
        Assert.Equal("acme", conversion.Owner);
        Assert.DoesNotContain("BEGIN", conversion.ToString());
        Assert.DoesNotContain("hook-secret", conversion.ToString());
    }

    [Theory]
    [InlineData("""{"id": 1, "slug": "s", "client_id": "c"}""", "instanceApp.error.conversionIncomplete")]
    [InlineData("""{"slug": "s", "client_id": "c", "pem": "p"}""", "instanceApp.error.conversionIncomplete")]
    [InlineData("not json", "instanceApp.error.conversionUnreadable")]
    public void AnIncompleteConversion_IsRefusedByName(string json, string key)
    {
        var refused = Assert.ThrowsAny<Exception>(() => GitHubAppManifestConversion.Parse(json));
        Assert.True(GitSyncRefusalException.TryGet(refused, out var refusal));
        Assert.Equal(key, refusal.Key);
    }

    // ───────────────────────────── which App mints ─────────────────────────────

    [Fact]
    public async Task WithoutADeploymentApp_TheInstancesOwnAppMints_AndAConnectOrDisconnectIsLive()
    {
        using var instanceKey = RSA.Create(2048);
        GitHubAppOptions? connected = null;
        var handler = new AppHandler();
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(), Options.Create(new GitHubAppOptions()), () => connected,
            httpClient: new HttpClient(handler), clock: () => Now);

        Assert.False(service.IsConfigured);
        Assert.False(service.HasInstanceApp);
        Assert.False(service.IsDeploymentConfigured);

        // Connected on some replica: the next mint uses it — no restart, no new service.
        connected = new GitHubAppOptions { ClientId = "Iv23liInstance", PrivateKey = instanceKey.ExportRSAPrivateKeyPem() };
        Assert.True(service.IsConfigured);
        Assert.True(service.HasInstanceApp);
        Assert.False(service.IsDeploymentConfigured);
        Assert.Equal("ghs_Iv23liInstance_1", await service.GetInstallationToken().FirstAsync().Timeout(Budget).Await());
        Assert.Equal("ghs_Iv23liInstance_1", await service.GetInstanceAppInstallationToken().FirstAsync().Timeout(Budget).Await());

        // Disconnected: nothing falls back — the mint is refused as NOT CONFIGURED.
        connected = null;
        Assert.False(service.IsConfigured);
        var refused = await Assert.ThrowsAsync<GitHubAppTokenMintException>(
            () => service.GetInstanceAppInstallationToken().FirstAsync().Timeout(Budget).Await());
        Assert.Equal(GitHubAppTokenMintStage.NotConfigured, refused.Stage);
    }

    [Fact]
    public async Task TheDeploymentsApp_StillWinsTheDefault_WhileAnInstanceSourceAsksForTheInstanceApp()
    {
        using var deploymentKey = RSA.Create(2048);
        using var instanceKey = RSA.Create(2048);
        var handler = new AppHandler();
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(),
            Options.Create(new GitHubAppOptions { ClientId = "Iv23liPlatform", PrivateKey = deploymentKey.ExportRSAPrivateKeyPem() }),
            () => new GitHubAppOptions { ClientId = "Iv23liInstance", PrivateKey = instanceKey.ExportRSAPrivateKeyPem() },
            httpClient: new HttpClient(handler), clock: () => Now);

        Assert.True(service.IsDeploymentConfigured);
        Assert.True(service.HasInstanceApp);
        Assert.StartsWith("ghs_Iv23liPlatform_", await service.GetInstallationToken().FirstAsync().Timeout(Budget).Await());
        Assert.StartsWith("ghs_Iv23liInstance_", await service.GetInstanceAppInstallationToken().FirstAsync().Timeout(Budget).Await());
        // Two identities, two tokens: neither is ever served the other's.
        Assert.Equal(2, handler.Mints);
    }

    // ───────────────────────────── which identity reads a source ─────────────────────────────

    private const string Configured = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string Local = "https://github.com/acme/reporting";

    [Fact]
    public async Task AnInstanceSource_IsReadWithTheInstanceApp_AConfiguredSourceAsTheRecordSays()
    {
        using var instanceKey = RSA.Create(2048);
        GitHubAppOptions? connected = new() { ClientId = "Iv23liInstance", PrivateKey = instanceKey.ExportRSAPrivateKeyPem() };
        var handler = new AppHandler();
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(), Options.Create(new GitHubAppOptions()), () => connected,
            httpClient: new HttpClient(handler), clock: () => Now);
        var local = new InstancePackageSourceContent { Repo = Local };
        var credentials = new GitSourceCredentials(
            Config(new()
            {
                ["PluginCatalog:Sources:0:Name"] = "Plugins",
                ["PluginCatalog:Sources:0:RepoPath"] = Configured,
                ["PluginCatalog:Sources:1:Name"] = "AcmeConfigured",
                ["PluginCatalog:Sources:1:RepoPath"] = Local,
                ["PluginCatalog:Sources:1:TokenKey"] = "PluginCatalog:SourceTokens:Acme",
                ["PluginCatalog:SourceTokens:Acme"] = "github_pat_acme",
            }),
            service,
            repo => IsLocal(repo) ? local : null);

        // The record declares a token for that repository: the record's declaration wins.
        Assert.Equal("github_pat_acme", await credentials.TokenFor(Local)!.FirstAsync().Timeout(Budget).Await());
        // A configured source declaring nothing keeps the caller's default.
        Assert.Null(credentials.TokenFor(Configured));

        // Without the record's entry, the instance source is read with the instance App.
        var instanceOnly = new GitSourceCredentials(Config(new()), service,
            repo => IsLocal(repo) ? local : null);
        var declared = instanceOnly.For("https://github.com/ACME/reporting.git/");
        Assert.NotNull(declared);
        Assert.True(declared!.InstanceApp);
        Assert.Equal("reporting", declared.SourceName);
        Assert.StartsWith("ghs_Iv23liInstance_", await instanceOnly.TokenFor(Local)!.FirstAsync().Timeout(Budget).Await());

        // Not connected: an instance source declares nothing, and the default identity applies.
        connected = null;
        Assert.Null(instanceOnly.For(Local));
    }

    [Fact]
    public async Task AConfiguredSourceAskingForTheInstanceApp_IsRefusedByName_WhenNoneIsConnected()
    {
        var service = new GitHubAppTokenService(
            new IoPoolRegistry(), Options.Create(new GitHubAppOptions()), () => null);
        var credentials = new GitSourceCredentials(Config(new()
        {
            ["PluginCatalog:Sources:0:Name"] = "Acme",
            ["PluginCatalog:Sources:0:RepoPath"] = Local,
            ["PluginCatalog:Sources:0:Identity"] = "instance",
        }), service);

        var refused = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => credentials.TokenFor(Local)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("gitsync.source.instanceAppNotConnected", refused.Refusal.Key);

        var both = new GitSourceCredentials(Config(new()
        {
            ["PluginCatalog:Sources:0:Name"] = "Acme",
            ["PluginCatalog:Sources:0:RepoPath"] = Local,
            ["PluginCatalog:Sources:0:Identity"] = "instance",
            ["PluginCatalog:Sources:0:InstallationOwner"] = "acme",
        }), service);
        var twice = await Assert.ThrowsAsync<GitSourceCredentialException>(
            () => both.TokenFor(Local)!.FirstAsync().Timeout(Budget).Await());
        Assert.Equal("gitsync.source.instanceAndOther", twice.Refusal.Key);
    }

    // ───────────────────────────── the source record ─────────────────────────────

    [Theory]
    [InlineData("https://github.com/acme/reporting", "https://github.com/acme/reporting")]
    [InlineData("  https://github.com/acme/reporting.git/ ", "https://github.com/acme/reporting")]
    [InlineData("https://github.com/acme/reporting/tree/main", null)]
    [InlineData("https://gitlab.com/acme/reporting", null)]
    [InlineData("http://github.com/acme/reporting", null)]
    [InlineData("https://github.com/acme/..", null)]
    [InlineData("", null)]
    public void OnlyAGitHubRepositoryUrl_IsASource(string repo, string? normalized) =>
        Assert.Equal(normalized, InstancePackageSources.NormalizeRepo(repo));

    [Fact]
    public void TwoSpellingsOfOneRepository_AddressOneNode()
    {
        Assert.Equal("PackageSource-acme--reporting", InstancePackageSources.IdOf("https://github.com/Acme/Reporting.git"));
        Assert.Equal("Admin/PackageSource-acme--reporting", InstancePackageSources.PathOf("https://github.com/acme/reporting/"));
    }

    [Fact]
    public void InstallByDefault_BecomesSourcePackageEntries()
    {
        var entries = InstancePackageSources.InstallEntries(
        [
            new InstancePackageSourceContent { Repo = Local, InstallByDefault = ["Reporting", " ", "reporting"] },
            new InstancePackageSourceContent { Repo = "https://github.com/acme/other", Name = "Other", InstallByDefault = ["Pack"] },
            new InstancePackageSourceContent { Repo = "https://github.com/acme/none" },
        ]);
        Assert.Equal(["reporting/Reporting", "Other/Pack"], entries);
        Assert.Equal("main", InstancePackageSources.RefOf(new InstancePackageSourceContent { Repo = Local }));
    }

    // The catalog's own lookup is by node path (case-folded); the fake mirrors it.
    private static bool IsLocal(string? repo) =>
        string.Equals(InstancePackageSources.PathOf(repo), InstancePackageSources.PathOf(Local), StringComparison.OrdinalIgnoreCase);

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// A GitHub fake for two Apps: discovery answers one installation per App, and every token is
    /// named after the App whose JWT asked for it — so a test can see WHICH identity minted.
    /// </summary>
    private sealed class AppHandler : HttpMessageHandler
    {
        private int mints;
        private readonly ConcurrentDictionary<string, int> perApp = new();
        public int Mints => Volatile.Read(ref mints);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var issuer = Issuer(request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return Answer(HttpStatusCode.OK, """[{"id": 91, "account": {"login": "acme"}}]""");
            if (request.Method == HttpMethod.Post && path == "/app/installations/91/access_tokens")
            {
                Interlocked.Increment(ref mints);
                var n = perApp.AddOrUpdate(issuer, 1, (_, c) => c + 1);
                return Answer(HttpStatusCode.Created,
                    $$"""{"token": "ghs_{{issuer}}_{{n}}", "expires_at": "{{Now.AddHours(1):o}}"}""");
            }
            return Answer(HttpStatusCode.NotFound, $"unexpected {request.Method} {path}");
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        private static string Issuer(string? jwt)
        {
            var payload = (jwt ?? "..").Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.GetProperty("iss").GetString() ?? "";
        }
    }
}
