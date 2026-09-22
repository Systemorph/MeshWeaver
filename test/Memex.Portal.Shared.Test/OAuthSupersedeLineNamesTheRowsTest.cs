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
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>/token</c> supersede line names the row it removed and the row that replaced it.
///
/// <para><b>Why a log line is worth a test.</b> One live credential per <c>(user, client_id)</c>
/// is deliberate (#1493): a re-authorization DELETES the client's previous token. The holder of
/// that previous token then gets a 401, which the validator logs under the token's HASH PREFIX
/// — and that prefix is the last segment of the token row's path. The supersede line used to say
/// only <i>"superseding 1 previous token(s) for user U, client label L"</i>: true, and useless
/// for tying it to the 401 it caused. #5074's alternation — two processes of one installation
/// sharing a <c>client_id</c> and evicting each other's credential — is attributable inside a log
/// window only if the supersede names the path whose prefix the 401 names.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the paths leave the line again. The control on the other side is
/// the FIRST exchange, which has nothing to supersede and must log no supersede line at all — an
/// implementation that printed the line on every exchange would fail that half.</para>
/// </summary>
public class OAuthSupersedeLineNamesTheRowsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RedirectUri = "https://claude.ai/cb";
    private const string UserEmail = "dora@example.com";

    /// <summary>The mesh user id the controller resolves from the claims (email local part).</summary>
    private const string UserId = "dora";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // Same registration the portal wires in ConfigureMemexMesh — the OAuthCode
        // NodeType + AuthorizationCode content type the code store persists.
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
        // The controller resolves its two collaborators lazily from the request's provider; the
        // portal registers both as root singletons over the mesh's storage adapter, and so does
        // this.
        var services = new ServiceCollection();
        services.AddSingleton(new OAuthCodeStore(
            Storage, Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<OAuthCodeStore>>()));
        services.AddSingleton(TokenService());
        var provider = services.BuildServiceProvider();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, UserEmail),
                new Claim(ClaimTypes.Name, "Dora"),
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

    /// <summary>One full authorization: <c>/authorize</c> → code → <c>/token</c> → raw token.</summary>
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

    [Fact]
    public async Task TheSupersedeLineNamesTheRowItRemovedAndTheRowThatReplacedIt()
    {
        var controller = Controller();
        var clientId = "one-installation-" + Guid.NewGuid().ToString("N")[..8];
        var label = $"OAuth: {clientId}";

        var first = await AuthorizeAndExchange(controller, clientId);

        Log.Lines(LogLevel.Information).Should().NotContain(
            l => l.Contains("superseding"),
            "the first authorization has nothing to supersede — a line here would be noise, "
            + "and the control that the line is tied to an actual removal");

        // The supersede reads the user's token listing through the synced query, which trails the
        // mint. Wait for the FIRST token to be listed before authorizing again, so the second
        // exchange's listing is guaranteed to see it — waiting on the actual condition, not a delay.
        var listing = TokenService();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => listing.GetTokensForUser(UserId).Take(1))
            .Should().Within(TestTimeouts.Convergence)
            .Match(all => all.Any(t => t.Label == label && t.NodePath == TokenPath(first)),
                cancellationToken: TestContext.Current.CancellationToken);

        var second = await AuthorizeAndExchange(controller, clientId);
        second.Should().NotBe(first, "each authorization mints its own credential");

        // Two lines, two facts. The first is INTENT (the candidate set before any delete ran);
        // the second is the OUTCOME (what each delete actually removed). A reader tying a 401 to
        // a removal reads the second, so that is the one the paths are asserted on.
        var intent = Log.Lines(LogLevel.Information).Where(l => l.Contains("superseding ")).ToArray();
        intent.Should().ContainSingle(
            "the second authorization sets out to retire exactly the first credential, once");
        intent.Single().Should()
            .Contain("superseding 1 previous token(s)")
            .And.Contain(label)
            .And.Contain($"candidates {TokenPath(first)}")
            .And.Contain($"keeping {TokenPath(second)}");

        var outcome = Log.Lines(LogLevel.Information).Where(l => l.Contains("superseded ")).ToArray();
        outcome.Should().ContainSingle(
            "one removal report per supersede, emitted after every delete has answered");
        outcome.Single().Should()
            .Contain("superseded 1 of 1 previous token(s)",
                "the count is what the deletes RETURNED, not what was attempted")
            .And.Contain(label)
            .And.Contain($"removed {TokenPath(first)}",
                "the removed row's path carries the hash prefix the holder's 401 will be logged under — "
                + "without it the supersede and the 401 it caused cannot be tied together")
            .And.Contain($"kept {TokenPath(second)}",
                "the replacement is the other half of the same event");
    }
}
