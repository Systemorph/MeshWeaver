using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The build principal end to end (#2483): a GitHub Actions OIDC token, verified against a JWKS this
/// test controls, resolved to a <c>BuildPrincipal</c> node in the Admin partition, and finally
/// decided at the prebuilt-publication routes.
///
/// <para>🚨 <b>The refusal matrix is the point.</b> Every negative below starts from a token that
/// WOULD be accepted and moves exactly one thing — the repository, the audience, the expiry, the
/// node, the scope — so a passing assertion can only mean that one thing was checked. A verifier
/// that checked the signature and stopped would authenticate every workflow run on GitHub.</para>
///
/// <para>🚨 <b>And the third state.</b> An unreadable JWKS answers <c>503</c>, never <c>401</c>:
/// "I could not find out" is not a denial and certainly not an admission (core #2901). The test
/// asserts the STATUS, because a build told "your identity is unknown" goes looking for a
/// credential that was never the problem.</para>
/// </summary>
public class BuildPrincipalAuthenticationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Audience = "https://registry.build-principal.test";
    private const string Repository = "Systemorph/MeshWeaver.SocialMedia";
    private const string Identity = "s0123456789abcdef0123456789abcdef";
    private const string Source = "plugins";

    private readonly GitHubTokenFactory tokens = new();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        tokens.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>What the JWKS leaf answers next. A test swaps it to stage a rotation or an outage.
    /// Instance state, so it dies with the test class — never a static.</summary>
    private volatile Func<string> jwks = () => "";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // The audience is the one deployment knob the build-principal leg has, and an
                // unconfigured one refuses everything — so a test that wants the leg live has to
                // name it, exactly as an operator does.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [BuildPrincipalConfiguration.AudienceConfigKey] = Audience,
                    })
                    .Build())
                // 🚨 TRANSIENT here, singleton in production. The service caches a key set for an
                // hour on purpose; a per-request instance lets each fact stage its own JWKS (a
                // rotation, an outage) without a Clear() for test isolation, which would be the tell
                // of an unfixed root cause. The cache and refresh-floor rules are pinned separately
                // below, against one instance driven directly.
                .AddTransient(sp => new GitHubOidcKeyService(
                    sp.GetRequiredService<IMessageHub>(),
                    sp.GetRequiredService<ILogger<GitHubOidcKeyService>>())
                {
                    FetchOverride = _ => Task.FromResult(jwks()),
                }));

    private InstanceRegistryAuthenticator Authenticator() => new(
        Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());

    private Task<InstanceAuthResult> Authenticate(string token) =>
        Authenticator().AuthenticateOutcome($"Bearer {token}")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

    /// <summary>Writes a build principal into the Admin partition — the create a global admin
    /// makes. As System, because the Admin partition is exactly what an ordinary identity cannot
    /// write.</summary>
    private Task<MeshNode> Grant(BuildPrincipal principal, string? repository = null)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = BuildPrincipal.NodeId(repository ?? principal.Repository);
        var node = new MeshNode(id, BuildPrincipal.Namespace)
        {
            Name = $"Build principal: {principal.Repository}",
            NodeType = MeshWeaverInstanceNodeType.BuildPrincipalNodeType,
            State = MeshNodeState.Active,
            Content = principal,
        };
        return access.RunAsSystem(() => meshService.CreateOrUpdateNode(node))
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private static BuildPrincipal Principal(string repository = Repository) => new()
    {
        Repository = repository,
        Scopes = [$"{BuildVerbs.Fetch}:{Source}", $"{BuildVerbs.Publish}:socialmedia"],
        Events = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["push"] = [BuildVerbs.Fetch, BuildVerbs.Publish],
            ["pull_request"] = [BuildVerbs.Fetch],
        },
        IssuedBy = "admin",
        IssuedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    // ── the authenticator ────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task AVerifiedToken_ResolvesToItsPrincipal_AndOnlyWhenOneExists()
    {
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.NoPrincipalYet";

        // 1. No node for the repository → a DEFINITIVE negative. A verified signature is not an
        //    authorization: this is the "no node = 401" the design names.
        var stranger = await Authenticate(tokens.Mint(Audience, repository: repository));
        Assert.False(stranger.IsUnavailable);
        Assert.Null(stranger.Instance);
        Assert.Null(stranger.Build);

        // 2. Same token, once a global admin has written the rule → authenticated.
        await Grant(Principal(repository));
        var trusted = await Authenticate(tokens.Mint(Audience, repository: repository));
        Assert.False(trusted.IsUnavailable);
        Assert.NotNull(trusted.Build);
        Assert.Equal(repository, trusted.Build!.Repository);
        // 🚨 …and it is NOT an instance. Every surface that needs an installation keeps refusing it.
        Assert.Null(trusted.Instance);
    }

    [Fact(Timeout = 240_000)]
    public async Task ANodeThatNamesAnotherRepository_DoesNotAuthenticateTheToken()
    {
        // The path is a routing hint; the RECORD is the authority. A node hand-edited (or migrated)
        // so its path and its declared repository disagree must authenticate nobody.
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.PathDrift";
        await Grant(Principal("Systemorph/SomethingElse"), repository);

        var result = await Authenticate(tokens.Mint(Audience, repository: repository));

        Assert.False(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    [Fact(Timeout = 240_000)]
    public async Task ARevokedPrincipal_StopsAuthenticatingAtOnce()
    {
        jwks = () => tokens.Jwks();
        var repository = "Systemorph/MeshWeaver.Revoked";
        await Grant(Principal(repository));
        Assert.NotNull((await Authenticate(tokens.Mint(Audience, repository: repository))).Build);

        // The control-plane verb — no watcher stands between writing it and the refusal.
        await Grant(Principal(repository) with { RequestedAction = BuildPrincipalActions.Revoke });

        var result = await Authenticate(tokens.Mint(Audience, repository: repository));
        Assert.False(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    [Fact(Timeout = 240_000)]
    public async Task AWrongAudienceOrAnExpiredToken_IsRefused_NotUnavailable()
    {
        jwks = () => tokens.Jwks();
        await Grant(Principal());

        foreach (var token in new[]
                 {
                     tokens.Mint("api://AzureADTokenExchange"),
                     tokens.Mint(Audience, issuedAt: DateTimeOffset.UtcNow.AddHours(-2)),
                     tokens.Mint(Audience, issuer: "https://evil.example"),
                 })
        {
            var result = await Authenticate(token);
            Assert.False(result.IsUnavailable, "a bad token is a VERDICT, not an outage");
            Assert.Null(result.Build);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task AnUnreachableKeySet_IsUNDETERMINED_NeverUnknownToken()
    {
        // 🚨 Fail closed AND distinguishable. The build is told "retry", not "your identity is
        // unknown" — the latter sends an operator hunting a credential that was never the problem
        // (the #2695 shape, on the leg that did not exist yet).
        jwks = () => throw new HttpRequestException("JWKS unreachable");
        await Grant(Principal());

        var result = await Authenticate(tokens.Mint(Audience));

        Assert.True(result.IsUnavailable);
        Assert.Null(result.Build);
        Assert.Null(result.Instance);
    }

    [Fact(Timeout = 240_000)]
    public async Task AKeySetThatPublishesNothingUsable_IsUNDETERMINED_NotAnAdmission()
    {
        // An empty key set would refuse everything, which is correct — but it must not be REMEMBERED
        // for an hour as if it were a valid answer, so the read throws and stays retryable.
        jwks = () => """{"keys":[]}""";
        await Grant(Principal());

        var result = await Authenticate(tokens.Mint(Audience));

        Assert.True(result.IsUnavailable);
        Assert.Null(result.Build);
    }

    // ── the key service's own cache ──────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task TheKeySetIsReadOnce_AFaultIsNeverCached_AndTheRefreshFloorBoundsRe_Reads()
    {
        var reads = 0;
        var document = tokens.Jwks();
        var fail = true;
        var service = new GitHubOidcKeyService(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<GitHubOidcKeyService>>())
        {
            FetchOverride = _ =>
            {
                Interlocked.Increment(ref reads);
                return fail
                    ? Task.FromException<string>(new HttpRequestException("down"))
                    : Task.FromResult(document);
            },
        };
        var now = DateTimeOffset.UtcNow;

        // A failure propagates AND is not cached — the next caller starts a genuinely new attempt
        // rather than replaying a latched OnError (the ReplaySubject trap, #1369).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await());
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await());
        Assert.Equal(2, reads);

        // 🚨 A ROTATION ARRIVING WHILE THE CACHE IS EMPTY. The fault above evicted the promise, so
        // this refresh has nothing to replace — and must still start a real read rather than letting
        // a null observable escape into the authenticator's SelectMany, where the failure would
        // surface far from its cause (Copilot review, PR #2988).
        fail = false;
        var afterFault = await service
            .Refresh(new GitHubSigningKeys(
                new Dictionary<string, GitHubSigningKey>(), now - TimeSpan.FromHours(1)), now)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Single(afterFault.ByKeyId);
        Assert.Equal(3, reads);

        var first = await service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Single(first.ByKeyId);
        // …and it was SHARED with the refresh above rather than starting a round trip of its own.
        Assert.Equal(3, reads);

        // A success is shared: a second caller inside the window costs no round trip.
        await service.Keys(now).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(3, reads);

        // 🚨 The refresh floor. An unknown kid may force ONE early re-read; inside the floor it
        // returns the set unchanged, so a caller inventing key ids cannot amplify into a fetch per
        // request.
        var suppressed = await service.Refresh(first, now).FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(3, reads);
        Assert.Equal(first.FetchedAt, suppressed.FetchedAt);

        // Past the floor it really does re-read — a rotation is recoverable without a restart.
        await service.Refresh(first, now + GitHubOidcKeyService.MinimumRefreshInterval + TimeSpan.FromSeconds(1))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(4, reads);

        // And a stale set is re-read on the ordinary path too.
        await service.Keys(now + GitHubOidcKeyService.CacheDuration + TimeSpan.FromMinutes(1))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(5, reads);
    }

    // ── the endpoint ─────────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task ThePrebuiltRoutesServeATrustedBuild_AndRefuseEveryoneElse()
    {
        jwks = () => tokens.Jwks();
        var root = Path.Combine(Path.GetTempPath(), "mw-buildprincipal-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(dir);
        try
        {
            WriteBundle(Path.Combine(dir, "Store.zip"));
            File.WriteAllText(
                Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");

            await Grant(Principal());
            await using var app = await StartHost(root);
            var route = $"/api/plugins/bundles/prebuilt/{Identity}/{Source}";

            // 1. the designed case — a push from the trusted repo, holding fetch:plugins
            var ok = await Get(app, route, tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("Store.zip", await ok.Content.ReadAsStringAsync());
            var bytes = await Get(app, route + "/Store.zip", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);

            // 2. 🚨 a valid signature from ANOTHER repository — 401, because no rule names it
            var other = await Get(app, route, tokens.Mint(Audience, repository: "Systemorph/Evil"));
            Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

            // 3. the right repository on an event its rule does not carry → 403, never the bytes
            var wrongEvent = await Get(app, route, tokens.Mint(Audience, eventName: "workflow_dispatch"));
            Assert.Equal(HttpStatusCode.Forbidden, wrongEvent.StatusCode);

            // 4. a source the principal holds no fetch scope for → 403
            var otherSource = await Get(
                app, $"/api/plugins/bundles/prebuilt/{Identity}/education", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.Forbidden, otherSource.StatusCode);

            // 5. 🚨 a build principal is NOT an installation: every other bundle route still 401s,
            //    because it decides per package against a grant and a plan a build does not have.
            var index = await Get(app, "/api/plugins/bundles/index.json", tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.Unauthorized, index.StatusCode);

            // 6. an unreadable key set is 503 + Retry-After, never the 401 a bad token gets
            jwks = () => throw new HttpRequestException("JWKS unreachable");
            var unavailable = await Get(app, route, tokens.Mint(Audience));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void WriteBundle(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open());
        writer.Write("""{"plugin":"Store"}""");
    }

    private async Task<WebApplication> StartHost(string publishedRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublishedBundleCatalogue.PublishedRootConfigKey] = publishedRoot,
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Get(WebApplication app, string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return await app.GetTestClient().SendAsync(request);
    }
}
