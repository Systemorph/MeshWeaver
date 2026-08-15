using System;
using System.Text;
using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins how an API token is read out of an <c>Authorization</c> header.
///
/// <para><b>Why Basic is accepted at all.</b> A NuGet client cannot send <c>Bearer</c> —
/// nuget.config's <c>packageSourceCredentials</c> speaks Basic, and the only alternative is
/// shipping a credential-provider plugin. Accepting both lets <c>dotnet restore</c> read the
/// access-controlled <c>/api/content</c> route with the SAME personal token every other API caller
/// uses, so the route's per-node Read check remains the authorization gate rather than a second
/// scheme growing its own rules.</para>
///
/// <para>This runs on an UNAUTHENTICATED request path, so the parse must never throw: a malformed
/// header is an anonymous caller, not a 500 anyone can trigger.</para>
/// </summary>
public class ApiTokenBasicAuthTest
{
    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Theory]
    [InlineData("Bearer mw_abc", "mw_abc")]
    [InlineData("bearer mw_abc", "mw_abc")]                  // scheme is case-insensitive
    [InlineData("  Bearer   mw_abc  ", "mw_abc")]            // stray whitespace tolerated
    public void BearerStillWorks(string header, string expected)
        => Assert.Equal(expected, ApiTokenAuthenticationHandler.ExtractToken(header));

    [Theory]
    [InlineData("nuget")]
    [InlineData("anything")]
    [InlineData("")]
    public void BasicYieldsThePasswordHalf(string username)
        // The username is ignored — the token is the whole secret — but NuGet requires one to be
        // present, so any value must work.
        => Assert.Equal("mw_abc", ApiTokenAuthenticationHandler.ExtractToken(Basic(username, "mw_abc")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mw_abc")]                                   // no scheme
    [InlineData("Bearer ")]
    [InlineData("Basic ")]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Negotiate abc")]                            // unrelated scheme
    public void MalformedHeadersYieldNoTokenAndDoNotThrow(string? header)
        => Assert.Null(ApiTokenAuthenticationHandler.ExtractToken(header));

    [Fact]
    public void BasicWithoutAColonYieldsNoToken()
        // No colon means no password half; reading the whole blob would accept a bare username.
        => Assert.Null(ApiTokenAuthenticationHandler.ExtractToken(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("mw_abc"))));

    [Fact]
    public void OverlongBasicPayloadYieldsNoToken()
        // Unauthenticated, attacker-controlled input: the reject path must not decode an unbounded
        // blob, and must not throw doing it.
        => Assert.Null(ApiTokenAuthenticationHandler.ExtractToken("Basic " + new string('A', 5000)));

    [Fact]
    public void ATokenContainingAColonSurvives()
    {
        // Only the FIRST colon separates user from password, per RFC 7617 — a token that itself
        // contains one must come back whole, or such tokens would silently fail to authenticate.
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:mw_a:b:c"));

        Assert.Equal("mw_a:b:c", ApiTokenAuthenticationHandler.ExtractToken(header));
    }
}
