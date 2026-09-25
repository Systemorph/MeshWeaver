using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #5074 defect 2: several processes of ONE installation share a <c>client_id</c> (Claude Code keeps
/// one registration per machine), so a one-live-credential-per-client rule made every new sign-in
/// revoke every sibling session's token. The exchange now keeps a BOUNDED number of live credentials
/// per client — the newest N — and evicts only the oldest beyond that, logging each eviction.
///
/// <para>Driven end to end through the real controller, token service and mesh store, with a bound of
/// TWO so three authorizations cross it.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the bound reverts to one (the second authorization would already evict
/// the first — the half asserted between the second and third exchange), or the eviction takes
/// anything but the oldest, or it is not logged by path.</para>
/// </summary>
public class OAuthBoundedLiveCredentialsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RedirectUri = "https://claude.ai/cb";
    private const string UserEmail = "erin@example.com";
    private const string UserId = "erin";
    private const int Bound = 2;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddOAuthCodeType();

    private CapturingLogger<OAuthConnectController> Log { get; } = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private ApiTokenService TokenService() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Storage,
        Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    private OAuthConnectController Controller()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new OAuthCodeStore(
            Storage, Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<OAuthCodeStore>>()));
        services.AddSingleton(TokenService());
        services.AddSingleton(Options.Create(new OAuthServerOptions { MaxLiveCredentialsPerClient = Bound }));
        var provider = services.BuildServiceProvider();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, UserEmail),
                new Claim(ClaimTypes.Name, "Erin"),
                new Claim("preferred_username", UserEmail),
            ],
            authenticationType: "Test");

        return new OAuthConnectController(provider, Log)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                    Request = { Scheme = "https", Host = new HostString("memex.test") },
                },
            },
        };
    }

    private static string TokenPath(string rawToken) =>
        $"{UserId}/ApiToken/{ApiTokenService.HashToken(rawToken)[..12]}";

    private static async Task<string> AuthorizeAndExchange(OAuthConnectController controller, string clientId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var redirect = await controller.Authorize(
                response_type: "code",
                client_id: clientId,
                redirect_uri: RedirectUri,
                state: "s",
                scope: "mcp",
                code_challenge: challenge,
                code_challenge_method: "S256",
                ct: TestContext.Current.CancellationToken)
            .ToObservable()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var url = ((RedirectResult)redirect).Url;
        var code = Uri.UnescapeDataString(url[(RedirectUri + "?code=").Length..url.IndexOf("&state=", StringComparison.Ordinal)]);

        var result = await controller.ExchangeToken(new TokenRequest
            {
                grant_type = "authorization_code",
                code = code,
                client_id = clientId,
                redirect_uri = RedirectUri,
                code_verifier = verifier,
            }, TestContext.Current.CancellationToken)
            .ToObservable()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var body = ((OkObjectResult)result).Value!;
        return (string)body.GetType().GetProperty("access_token")!.GetValue(body)!;
    }

    /// <summary>Waits until the listing the eviction reads carries <paramref name="rawToken"/>, so the
    /// next exchange is guaranteed to see it — the actual condition, never a delay.</summary>
    private async Task WaitListed(string label, string rawToken)
    {
        var listing = TokenService();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => listing.GetTokensForUser(UserId).Take(1))
            .Should().Within(TestTimeouts.Convergence)
            .Match(all => all.Any(t => t.Label == label && t.NodePath == TokenPath(rawToken)),
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the row straight from the store — the authority, not the lagging listing.</summary>
    private async Task AssertStored(string path, bool present, string because)
    {
        var node = await Storage.Read(path, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (node is not null).Should().Be(present, because + $" ({path})");
    }

    [Fact]
    public async Task SiblingSessionsKeepTheirCredentials_UpToTheBound_ThenTheOldestIsEvicted()
    {
        var controller = Controller();
        var clientId = "one-machine-" + Guid.NewGuid().ToString("N")[..8];
        var label = $"OAuth: {clientId}";

        // Session A signs in, then session B of the same installation signs in.
        var a = await AuthorizeAndExchange(controller, clientId);
        await WaitListed(label, a);
        var b = await AuthorizeAndExchange(controller, clientId);
        await WaitListed(label, b);

        Log.Lines(LogLevel.Information).Should().NotContain(
            l => l.Contains("superseding") && l.Contains(label),
            "two credentials are within a bound of two — B's sign-in must not cost A its session "
            + "(the one-per-client rule evicted A right here)");
        await AssertStored(TokenPath(a), present: true, "session A still holds a live credential");

        // Session C crosses the bound: the OLDEST (A) goes, B and C stay.
        var c = await AuthorizeAndExchange(controller, clientId);

        var evicted = Log.Lines(LogLevel.Information)
            .Where(l => l.Contains("evicted credential") && l.Contains(label)).ToArray();
        evicted.Should().ContainSingle("exactly one credential falls below a bound of two");
        evicted.Single().Should().Contain(TokenPath(a),
            "the eviction line names the removed row, whose last segment is the hash prefix A's 401 is logged under");

        Log.Lines(LogLevel.Information).Should().Contain(
            l => l.Contains("superseded 1 of 1 previous token(s)") && l.Contains($"removed {TokenPath(a)}"),
            "the outcome line reports what the delete actually removed");

        await AssertStored(TokenPath(a), present: false, "the oldest credential was evicted");
        await AssertStored(TokenPath(b), present: true, "session B keeps its credential across C's sign-in");
        await AssertStored(TokenPath(c), present: true, "the newly minted credential is live");
    }
}
