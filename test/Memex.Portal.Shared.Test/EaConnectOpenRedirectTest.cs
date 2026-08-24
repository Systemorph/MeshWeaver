using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The consent endpoints must never redirect off-site.
///
/// <para><b>Why this test exists.</b> Both redirect targets come from the request:
/// <c>?returnUrl=</c> on <c>/auth/ea/connect</c>, and the OAuth <c>state</c> on the callback —
/// which is that same value round-tripped through the identity provider. Passing either straight
/// to <c>Redirect</c> is an open redirect, and the already-connected fast path turns it into a
/// SINGLE hop with no dialog in between: <c>/auth/ea/connect?returnUrl=https://phish.example</c>
/// sends an authenticated user off-site behind our own domain. Caught in review on #2101 before
/// it merged.</para>
///
/// <para>The variants below are the ones a prefix check ("does it start with '/'?") lets through:
/// <c>//host</c> is protocol-relative and resolves against the current scheme, and <c>/\host</c>
/// is normalised into an authority by some browsers. The controller implements ASP.NET's own
/// IsLocalUrl rule directly (its <c>Url</c> helper is null for a directly-constructed controller,
/// and a security check that NREs depending on how the type was built is worse than the hole it
/// closes) — so these cases are what make the hand-written rule trustworthy.</para>
/// </summary>
public class EaConnectOpenRedirectTest
{
    private sealed class FakeEaGraphAuth(bool connected) : IEaGraphAuth
    {
        public string? LastState { get; private set; }
        public bool IsConfigured => true;
        public string ConnectPath => EaConsentController.ConnectPath;

        public string BuildConsentUrl(string state, string redirectUri)
        {
            LastState = state;
            return "https://login.microsoftonline.example/consent?state=" + state;
        }

        public Task<bool> ExchangeAndStoreAsync(
            string code, string redirectUri, string userObjectId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct) =>
            Task.FromResult<string?>(connected ? "token" : null);

        public Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct) =>
            Task.FromResult(connected);
    }

    private static EaConsentController Controller(FakeEaGraphAuth ea)
    {
        var access = new AccessService();
        access.SetContext(new AccessContext { ObjectId = "user-1" });
        return new EaConsentController(ea, access, NullLogger<EaConsentController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Scheme = "https", Host = new HostString("memex.example") },
                },
            },
        };
    }

    [Theory]
    [InlineData("https://phish.example")]          // absolute, the headline case
    [InlineData("http://phish.example/steal")]
    [InlineData("//phish.example")]                // protocol-relative — a leading '/' is not enough
    [InlineData("//phish.example/path?a=b")]
    [InlineData("/\\phish.example")]               // backslash variant some browsers normalise
    [InlineData("javascript:alert(1)")]            // not a navigation target at all
    [InlineData("mailto:a@b.example")]
    [InlineData("rbuergi")]                        // not rooted — ambiguous, so refused
    public async Task An_offsite_returnUrl_never_reaches_the_redirect(string hostile)
    {
        // The already-connected path is the dangerous one: no consent dialog stands between the
        // click and the redirect.
        var result = await Controller(new FakeEaGraphAuth(connected: true)).Connect(returnUrl: hostile);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/",
                $"'{hostile}' must collapse to the home page, never be redirected to");
    }

    [Fact]
    public async Task An_offsite_returnUrl_is_not_smuggled_through_the_consent_state_either()
    {
        // The not-connected path hands returnUrl to the IdP as `state` and redirects to it on the
        // way back — sanitising only the visible redirect would leave that route open.
        var ea = new FakeEaGraphAuth(connected: false);

        await Controller(ea).Connect(returnUrl: "https://phish.example");

        ea.LastState.Should().Be("%2F",
            "the state carried to the identity provider is the SANITISED target, url-escaped");
    }

    [Fact]
    public async Task A_hostile_state_coming_back_from_the_provider_is_refused()
    {
        // `state` re-enters as untrusted input: it left our process, so it is re-sanitised rather
        // than trusted on the strength of having been ours once.
        var result = await Controller(new FakeEaGraphAuth(connected: true))
            .Callback(code: null, state: "https%3A%2F%2Fphish.example", error: "access_denied",
                ct: CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/");
    }

    [Theory]
    [InlineData("/rbuergi")]
    [InlineData("/rbuergi/Chat?from=ea#top")]
    [InlineData("/")]
    public async Task A_same_site_returnUrl_is_preserved_exactly(string friendly)
    {
        // The guard must not be so blunt that it breaks the feature it protects.
        var result = await Controller(new FakeEaGraphAuth(connected: true)).Connect(returnUrl: friendly);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be(friendly);
    }
}
