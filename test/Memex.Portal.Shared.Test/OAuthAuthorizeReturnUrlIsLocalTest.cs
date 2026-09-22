using System;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>returnUrl</c> that <c>/authorize</c> hands to <c>/login</c> must be a LOCAL path.
///
/// <para><b>The defect (#5074).</b> Every <c>returnUrl</c> SINK in the portal validates
/// local-only — <see cref="ReturnUrlPolicy.Sanitize"/> here, the GUI's own single copy of the
/// same rule on the login page — because an unvalidated redirect target is an open redirect.
/// Those rules are right. What was wrong was this SOURCE: <c>/authorize</c> minted
/// <c>{Scheme}://{Host}{Path}{QueryString}</c>, an absolute URL that no sink can accept. The
/// login page dropped it silently (its local-or-null helper substitutes nothing rather than a
/// destination), so the provider link carried no <c>returnUrl</c> at all, the user landed on
/// <c>"/"</c> after signing in, and the <c>/authorize</c> request was gone. No authorization
/// code was ever issued, so an MCP client waited forever for an "authentication successful"
/// that could not arrive — and the user, who HAD signed in successfully, read it as the token
/// having expired again.</para>
///
/// <para><b>Why the assertion is stated as a round-trip through the policy</b> rather than as a
/// second hand-written "is it local?" check: there is exactly one such rule per assembly, and a
/// re-implementation is the bug the sink guard exists to prevent — a hand-written copy that was
/// subtly weaker than the shared rule has already shipped once. Asking the real policy whether it
/// would keep this value is the same question every sink downstream asks.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the absolute form comes back:
/// <see cref="TheLoginReturnUrlSurvivesThePolicyEverySinkApplies"/> goes red, because
/// <see cref="TheAbsoluteFormThisEndpointUsedToMint_IsRefusedByTheSamePolicy"/> pins that the
/// policy sends that shape to <c>"/"</c>. The pair is the control on each side of the change: a
/// policy that accepted everything would fail the second, and one that accepted nothing would
/// fail the first, so no constant satisfies both.</para>
/// </summary>
public class OAuthAuthorizeReturnUrlIsLocalTest
{
    /// <summary>
    /// The unauthenticated branch of <c>/authorize</c> resolves neither the code store nor the
    /// token service (both are lazily-read properties reached only after the identity check), so
    /// the action needs no services at all to exercise it.
    /// </summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private const string ClientId = "aB3-derived-client-id";
    private const string RedirectUri = "http://localhost:8765/callback";

    /// <summary>The query an MCP client actually sends, escaped as it arrives on the wire.</summary>
    private const string AuthorizeQuery =
        "?response_type=code"
        + "&client_id=" + ClientId
        + "&redirect_uri=http%3A%2F%2Flocalhost%3A8765%2Fcallback"
        + "&state=6ff1c0de&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        + "&code_challenge_method=S256";

    private static OAuthConnectController Controller() =>
        new(new NoServices(), NullLogger<OAuthConnectController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                // No authenticated identity — DefaultHttpContext's principal has an
                // unauthenticated identity, which is the branch under test.
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Scheme = "https",
                        Host = new HostString("memex.systemorph.com"),
                        Path = "/authorize",
                        QueryString = new QueryString(AuthorizeQuery),
                    },
                },
            },
        };

    private static async Task<string> LoginRedirect()
    {
        var result = await Controller().Authorize(
            response_type: "code",
            client_id: ClientId,
            redirect_uri: RedirectUri,
            state: "6ff1c0de",
            scope: null,
            code_challenge: "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            code_challenge_method: "S256");

        var redirect = Assert.IsType<RedirectResult>(result);
        return redirect.Url;
    }

    /// <summary>The <c>returnUrl</c> value as the login page will read it.</summary>
    private static async Task<string> CapturedReturnUrl()
    {
        var url = await LoginRedirect();
        const string prefix = "/login?returnUrl=";
        Assert.StartsWith(prefix, url, StringComparison.Ordinal);
        return Uri.UnescapeDataString(url[prefix.Length..]);
    }

    [Fact]
    public async Task TheLoginReturnUrlSurvivesThePolicyEverySinkApplies()
    {
        var returnUrl = await CapturedReturnUrl();

        Assert.Equal(returnUrl, ReturnUrlPolicy.Sanitize(returnUrl));
    }

    [Fact]
    public async Task TheLoginReturnUrlCarriesTheWholeAuthorizeRequest()
    {
        var returnUrl = await CapturedReturnUrl();

        // Path AND query: dropping the query would send the user back to a /authorize with no
        // client_id, which 400s — a different way of never issuing a code.
        Assert.Equal("/authorize" + AuthorizeQuery, returnUrl);
    }

    [Theory]
    [InlineData("https://memex.systemorph.com/authorize?response_type=code&client_id=x")]
    [InlineData("http://memex.systemorph.com/authorize")]
    public void TheAbsoluteFormThisEndpointUsedToMint_IsRefusedByTheSamePolicy(string absolute)
        // The control: this is why an absolute returnUrl never reached the login provider.
        // It is not that the policy is wrong — an absolute target IS how an open redirect is
        // spelled — it is that the source had to mint something the policy can keep.
        => Assert.Equal("/", ReturnUrlPolicy.Sanitize(absolute));
}
