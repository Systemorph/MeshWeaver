using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
/// A <c>/token</c> exchange the client ABANDONED leaves no live credential behind
/// (MeshWeaver.Feedback#13).
///
/// <para><b>Why.</b> <c>ExchangeToken</c> bridges its chain with <c>.ObserveCompletion(…, ct)</c>,
/// which cancels the WAIT and deliberately not the source — so when the client gives up (the
/// request's abort token fires), the response is gone but the exchange keeps running: it minted a
/// year-long <c>mw_</c> token that no client ever received. The one-live-credential supersede
/// removes such a row only on the NEXT successful exchange for the same <c>client_id</c>, which for
/// a client whose id changes per registration never comes. Measured on memex as a late fault
/// ("faulted after the response had already been sent"), raised by the collaborators being
/// resolved lazily from the request's disposed scope.</para>
///
/// <para>Two windows, one test each:</para>
/// <list type="number">
///   <item>abandoned BEFORE the mint — nothing is minted at all;</item>
///   <item>abandoned WHILE the mint is in flight — the minted token is revoked, and the client's
///   previous credential is NOT superseded (it is the one the client still holds).</item>
/// </list>
///
/// <para><b>SHOULD-FAIL-IF</b> the exchange mints (or keeps) a token for a client that is gone:
/// against the unfixed controller both tests observe the <c>Issued OAuth access token</c> line.</para>
/// </summary>
public class OAuthAbandonedExchangeMintsNothingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RedirectUri = "https://claude.ai/cb";
    private const string UserEmail = "erin@example.com";
    private const string UserId = "erin";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddOAuthCodeType();

    private CapturingLogger<OAuthConnectController> Log { get; } = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private ApiTokenService TokenService(ILogger<ApiTokenService>? logger = null) => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Storage,
        logger ?? Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    private OAuthConnectController Controller(ApiTokenService tokens, ILogger<OAuthConnectController>? log = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new OAuthCodeStore(
            Storage, Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<OAuthCodeStore>>()));
        services.AddSingleton(tokens);
        var provider = services.BuildServiceProvider();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, UserEmail),
                new Claim(ClaimTypes.Name, "Erin"),
                new Claim("preferred_username", UserEmail),
            ],
            authenticationType: "Test");

        return new OAuthConnectController(provider, log ?? Log)
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

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (verifier, challenge);
    }

    private static async Task<(string Code, string Verifier)> Authorize(OAuthConnectController controller, string clientId)
    {
        var (verifier, challenge) = Pkce();
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
        return (code, verifier);
    }

    private static TokenRequest Request(string clientId, string code, string verifier) => new()
    {
        grant_type = "authorization_code",
        code = code,
        client_id = clientId,
        redirect_uri = RedirectUri,
        code_verifier = verifier,
    };

    /// <summary>Waits for the exchange's own terminal line — issued, or abandoned — whichever it wrote.</summary>
    private Task WaitForTheExchangeToSettle(string clientId) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Log.Lines(LogLevel.Information))
            .Should().Within(TestTimeouts.Convergence)
            .Match(lines => lines.Any(l => l.Contains(clientId)
                                           && (l.Contains("Issued OAuth access token") || l.Contains("abandoned"))),
                cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnExchangeAbandonedBeforeTheMint_MintsNothing()
    {
        var mints = new CapturingLogger<ApiTokenService>();
        var controller = Controller(TokenService(mints));
        var clientId = "gone-early-" + Guid.NewGuid().ToString("N")[..8];
        var (code, verifier) = await Authorize(controller, clientId);

        // The client has already given up when the exchange starts.
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        _ = controller.ExchangeToken(Request(clientId, code, verifier), aborted.Token);

        await WaitForTheExchangeToSettle(clientId);

        Log.Lines(LogLevel.Information).Should().NotContain(
            l => l.Contains("Issued OAuth access token") && l.Contains(clientId),
            "a client that has gone receives nothing — minting for it leaves a live credential nobody holds");
        Log.Lines(LogLevel.Information).Should().Contain(
            l => l.Contains(clientId) && l.Contains("abandoned the exchange before a token was minted"));
        // The invariant itself, read from the TOKEN SERVICE rather than the controller's own words:
        // it logs "Creating API token {Label} …" for every row it writes, and wrote none.
        mints.Lines(LogLevel.Information).Should().NotContain(
            l => l.Contains("Creating API token") && l.Contains(clientId),
            "no token row may be written for a client that has already gone");
    }

    [Fact]
    public async Task AnExchangeAbandonedDuringTheMint_RevokesTheTokenAndKeepsThePreviousCredential()
    {
        var clientId = "gone-midway-" + Guid.NewGuid().ToString("N")[..8];
        var label = $"OAuth: {clientId}";

        // First: an ordinary, completed authorization — the credential the client holds.
        var held = Controller(TokenService());
        var (code1, verifier1) = await Authorize(held, clientId);
        var first = await held.ExchangeToken(Request(clientId, code1, verifier1), TestContext.Current.CancellationToken)
            .ToObservable()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var firstBody = ((OkObjectResult)first).Value!;
        var firstToken = (string)firstBody.GetType().GetProperty("access_token")!.GetValue(firstBody)!;

        // Wait until the listing (which the supersede reads) carries the held token, so a supersede
        // WOULD find it — otherwise "not superseded" would pass by the listing's lag alone.
        var listing = TokenService();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => listing.GetTokensForUser(UserId).Take(1))
            .Should().Within(TestTimeouts.Convergence)
            .Match(all => all.Any(t => t.Label == label && t.NodePath == TokenPath(firstToken)),
                cancellationToken: TestContext.Current.CancellationToken);

        // Second: the client gives up WHILE the token is being minted — the abort fires the moment
        // the token service reports it is creating the token.
        using var aborted = new CancellationTokenSource();
        var midway = Controller(TokenService(new AbortOnMint(aborted,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>())));
        var (code2, verifier2) = await Authorize(midway, clientId);
        _ = midway.ExchangeToken(Request(clientId, code2, verifier2), aborted.Token);

        // The held exchange already wrote its "Issued" line, so wait on THIS exchange's own terminal
        // line — the undelivered-token report — rather than on "any terminal line".
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Log.Lines(LogLevel.Information))
            .Should().Within(TestTimeouts.Convergence)
            .Match(lines => lines.Any(l => l.Contains(clientId) && l.Contains("undelivered token")),
                cancellationToken: TestContext.Current.CancellationToken);

        Log.Lines(LogLevel.Information).Count(l => l.Contains("Issued OAuth access token") && l.Contains(clientId))
            .Should().Be(1, "only the completed exchange issued a token");
        Log.Lines(LogLevel.Information).Should().Contain(
            l => l.Contains(clientId) && l.Contains("revoked the undelivered token"));
        Log.Lines(LogLevel.Information).Should().NotContain(
            l => l.Contains("superseding") && l.Contains(label),
            "the abandoned exchange must not retire the credential the client still holds");
        await AssertStored(TokenPath(firstToken), present: true, "the credential the client holds is still live");
        await AssertStored(RevokedPath(clientId), present: false, "the undelivered token's row is gone");
    }

    [Fact]
    public async Task AnExchangeAbandonedDuringTheSupersede_DeletesNothingTheClientHolds()
    {
        var clientId = "gone-at-supersede-" + Guid.NewGuid().ToString("N")[..8];
        var label = $"OAuth: {clientId}";

        var held = Controller(TokenService());
        var (code1, verifier1) = await Authorize(held, clientId);
        var first = await held.ExchangeToken(Request(clientId, code1, verifier1), TestContext.Current.CancellationToken)
            .ToObservable()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var firstBody = ((OkObjectResult)first).Value!;
        var firstToken = (string)firstBody.GetType().GetProperty("access_token")!.GetValue(firstBody)!;

        var listing = TokenService();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => listing.GetTokensForUser(UserId).Take(1))
            .Should().Within(TestTimeouts.Convergence)
            .Match(all => all.Any(t => t.Label == label && t.NodePath == TokenPath(firstToken)),
                cancellationToken: TestContext.Current.CancellationToken);

        // The client leaves AFTER the mint and the listing, at the moment the supersede has chosen
        // its candidates — i.e. after the check that precedes the supersede, before any delete.
        using var aborted = new CancellationTokenSource();
        var midway = Controller(TokenService(), new AbortOn("OAuth: superseding", aborted, Log));
        var (code2, verifier2) = await Authorize(midway, clientId);
        _ = midway.ExchangeToken(Request(clientId, code2, verifier2), aborted.Token);

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Log.Lines(LogLevel.Information))
            .Should().Within(TestTimeouts.Convergence)
            .Match(lines => lines.Any(l => l.Contains(clientId) && l.Contains("undelivered token")),
                cancellationToken: TestContext.Current.CancellationToken);

        Log.Lines(LogLevel.Information).Should().Contain(
            l => l.Contains("superseded 0 of 1") && l.Contains(label),
            "the candidate was chosen, and then NOT deleted, because the client had left");
        await AssertStored(TokenPath(firstToken), present: true, "the credential the client holds is still live");
        await AssertStored(RevokedPath(clientId), present: false, "the undelivered token's row is gone");
    }

    /// <summary>The path the revoke line names for this client.</summary>
    private string RevokedPath(string clientId)
    {
        var line = Log.Lines(LogLevel.Information).Single(l => l.Contains(clientId) && l.Contains("undelivered token "));
        var tail = line[(line.IndexOf("undelivered token ", StringComparison.Ordinal) + "undelivered token ".Length)..];
        return tail[..tail.IndexOf(';')];
    }

    /// <summary>Reads the row straight from the store — the authority, not the lagging listing.</summary>
    private async Task AssertStored(string path, bool present, string because)
    {
        var node = await Storage.Read(path, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (node is not null).Should().Be(present, because + $" ({path})");
    }

    /// <summary>A controller logger that records every line and fires the abort on one of them.</summary>
    private sealed class AbortOn(string marker, CancellationTokenSource abort, CapturingLogger<OAuthConnectController> sink)
        : ILogger<OAuthConnectController>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Log(logLevel, eventId, state, exception, formatter);
            if (formatter(state, exception).StartsWith(marker, StringComparison.Ordinal))
                abort.Cancel();
        }
    }

    /// <summary>Forwards to the real logger and fires the abort when the mint is in flight.</summary>
    private sealed class AbortOnMint(CancellationTokenSource abort, ILogger<ApiTokenService> inner) : ILogger<ApiTokenService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.StartsWith("Creating API token", StringComparison.Ordinal))
                abort.Cancel();
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
