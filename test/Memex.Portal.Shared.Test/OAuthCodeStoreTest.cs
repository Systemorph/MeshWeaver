using System;
using System.Security.Cryptography;
using System.Text;
using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// OAuthCodeStore exchange semantics: single-use consume-first codes, and the exact
/// failure reason for every rejection branch (unknown/consumed, expired, client_id,
/// redirect_uri, PKCE). The reason string is what the /token endpoint logs at Warning —
/// the bare "invalid or expired" line made real-world MCP-login failures unattributable
/// (2026-07-02: a failed exchange on memex-cloud could not be told apart from a
/// duplicate-callback burn or a PKCE mismatch).
/// </summary>
public class OAuthCodeStoreTest
{
    private const string ClientId = "client-1";
    private const string RedirectUri = "http://localhost:12345/callback";

    private static OAuthCodeStore NewStore() => new();

    private static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string Issue(OAuthCodeStore store, string? challenge = null, string? method = null)
        => store.GenerateCode("rbuergi", "Roland", "rbuergi@systemorph.com",
            ClientId, RedirectUri, challenge, method);

    [Fact]
    public void Exchange_WithMatchingParameters_ReturnsEntry_NoReason()
    {
        var store = NewStore();
        var code = Issue(store);

        var entry = store.ExchangeCode(code, ClientId, RedirectUri, null, out var reason);

        Assert.NotNull(entry);
        Assert.Null(reason);
        Assert.Equal("rbuergi", entry!.UserId);
    }

    [Fact]
    public void Exchange_UnknownCode_ReportsUnknown()
    {
        var store = NewStore();

        var entry = store.ExchangeCode("no-such-code", ClientId, RedirectUri, null, out var reason);

        Assert.Null(entry);
        Assert.Contains("unknown or already consumed", reason);
    }

    [Fact]
    public void Exchange_SecondAttempt_IsConsumed()
    {
        var store = NewStore();
        var code = Issue(store);
        Assert.NotNull(store.ExchangeCode(code, ClientId, RedirectUri, null, out _));

        var second = store.ExchangeCode(code, ClientId, RedirectUri, null, out var reason);

        Assert.Null(second);
        Assert.Contains("unknown or already consumed", reason);
    }

    [Fact]
    public void Exchange_ClientIdMismatch_ReportsClientId()
    {
        var store = NewStore();
        var code = Issue(store);

        var entry = store.ExchangeCode(code, "other-client", RedirectUri, null, out var reason);

        Assert.Null(entry);
        Assert.Contains("client_id mismatch", reason);
    }

    [Fact]
    public void Exchange_RedirectUriMismatch_ReportsRedirectUri()
    {
        var store = NewStore();
        var code = Issue(store);

        var entry = store.ExchangeCode(code, ClientId, "http://localhost:9/other", null, out var reason);

        Assert.Null(entry);
        Assert.Contains("redirect_uri mismatch", reason);
    }

    [Fact]
    public void Exchange_S256Pkce_RoundTrips()
    {
        var store = NewStore();
        const string verifier = "the-verifier-string-with-enough-entropy";
        var code = Issue(store, Challenge(verifier), "S256");

        var entry = store.ExchangeCode(code, ClientId, RedirectUri, verifier, out var reason);

        Assert.NotNull(entry);
        Assert.Null(reason);
    }

    [Fact]
    public void Exchange_MissingVerifier_ReportsMissing()
    {
        var store = NewStore();
        var code = Issue(store, Challenge("v"), "S256");

        var entry = store.ExchangeCode(code, ClientId, RedirectUri, null, out var reason);

        Assert.Null(entry);
        Assert.Contains("code_verifier missing", reason);
    }

    [Fact]
    public void Exchange_WrongVerifier_ReportsPkceFailure()
    {
        var store = NewStore();
        var code = Issue(store, Challenge("right-verifier"), "S256");

        var entry = store.ExchangeCode(code, ClientId, RedirectUri, "wrong-verifier", out var reason);

        Assert.Null(entry);
        Assert.Contains("PKCE verification failed", reason);
    }
}
