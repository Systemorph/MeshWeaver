using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The Entra rung of the approval step-up (Refs #4305, <c>Doc/Architecture/ApprovalStepUp</c>):
/// which session gets which rung, and what an <c>id_token</c> must prove. Every refusal test is
/// paired with the accepted baseline token it differs from in exactly one claim, so a refusal can
/// never pass by the check refusing everything.
/// </summary>
public class EntraStepUpTest
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static StepUpOptions Options(bool requireAmr = true) => new()
    {
        Enabled = true,
        EntraAuthenticationContext = "c1",
        EntraRequireAmr = requireAmr,
        MaxAuthAge = TimeSpan.FromSeconds(120),
    };

    private static EntraStepUpExpectation Expect(StepUpOptions? options = null, string? sessionOid = "oid-alice") =>
        new(Issuer, "nonce-1", sessionOid, "alice@acme.com", options ?? Options(), Now);

    private static Dictionary<string, object> Baseline() => new()
    {
        ["iss"] = Issuer,
        ["nonce"] = "nonce-1",
        ["oid"] = "oid-alice",
        ["tid"] = Tenant,
        ["preferred_username"] = "alice@acme.com",
        ["auth_time"] = Now.AddSeconds(-30).ToUnixTimeSeconds(),
        ["acrs"] = new[] { "c1" },
        ["amr"] = new[] { "fido", "mfa" },
    };

    private static IReadOnlyCollection<Claim> ClaimsOf(Dictionary<string, object> values) =>
        values.SelectMany(kv => kv.Value is string[] many
                ? many.Select(v => new Claim(kv.Key, v))
                : [new Claim(kv.Key, Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture)!)])
            .ToList();

    private static EntraStepUpCheck Check(Action<Dictionary<string, object>>? mutate = null, EntraStepUpExpectation? expect = null)
    {
        var claims = Baseline();
        mutate?.Invoke(claims);
        return EntraStepUpTokenCheck.Evaluate(ClaimsOf(claims), expect ?? Expect());
    }

    [Fact]
    public void AFreshPhishingResistantTokenForTheSession_IsAccepted()
    {
        var check = Check();
        Assert.True(check.Ok, check.Reason);
        Assert.Equal(Now.AddSeconds(-30), check.AuthenticatedAt);
        Assert.Contains("acrs=c1", check.Evidence);
    }

    [Theory]
    [InlineData("auth_time")]
    [InlineData("acrs")]
    [InlineData("nonce")]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("amr")]
    public void EachClaimIsLoadBearing(string reason)
    {
        Action<Dictionary<string, object>> mutate = reason switch
        {
            "auth_time" => c => c["auth_time"] = Now.AddSeconds(-121).ToUnixTimeSeconds(),   // one second too old
            "acrs" => c => c["acrs"] = new[] { "c2" },
            "nonce" => c => c["nonce"] = "nonce-of-another-step-up",
            "subject" => c => c["oid"] = "oid-mallory",
            "issuer" => c => c["iss"] = "https://login.microsoftonline.com/other-tenant/v2.0",
            "amr" => c => c["amr"] = new[] { "pwd", "mfa" },                                     // MFA, but not phishing-resistant
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        var refused = Check(mutate);
        Assert.False(refused.Ok);
        Assert.Equal(reason, refused.Reason);
        Assert.True(Check().Ok);   // negative control: the unmutated token passes
    }

    [Fact]
    public void AnAbsentAmr_IsRefusedByDefault_AndPassesOnlyWhenTheRecordWaivesIt()
    {
        var byDefault = Check(c => c.Remove("amr"));
        Assert.False(byDefault.Ok);
        Assert.Equal("amr", byDefault.Reason);
        Assert.True(new StepUpOptions().EntraRequireAmr);   // the shipped default, not just this test's
        Assert.True(Check(c => c.Remove("amr"), Expect(Options(requireAmr: false))).Ok);
    }

    [Theory]
    [InlineData("rsa", "ngcmfa", "mfa")]   // Authenticator push — MFA, but phishable
    [InlineData("pwd")]
    [InlineData("x509")]                    // single-factor certificate
    [InlineData("swk", "rsa", "mfa")]       // phone sign-in
    public void MfaThatIsNotPhishingResistant_IsRefused(params string[] amr)
    {
        var refused = Check(c => c["amr"] = amr);
        Assert.False(refused.Ok);
        Assert.Equal("amr", refused.Reason);
        Assert.True(Check(c => c["amr"] = new[] { "hwk", "mfa", "ngcmfa" }).Ok);   // Windows Hello for Business passes
    }

    [Fact]
    public void TheSessionProvider_ComesFromTheTicket_NeverFromTheRoute()
    {
        var googleSession = new[] { new Claim(StepUpClaims.Idp, "Google") };
        // A signed-in Google user opening /auth/callback/Microsoft: no ticket item, the session says Google.
        Assert.Equal("Google", StepUpClaims.ResolveProvider(null, googleSession));
        // A real sign-in carries the challenged scheme on its ticket.
        Assert.Equal("Microsoft", StepUpClaims.ResolveProvider("Microsoft", []));
        // An Entra guest's own `idp` claim (an STS URI) is not ours and is never read as the provider.
        Assert.Null(StepUpClaims.ResolveProvider(null, [new Claim("idp", "https://sts.windows.net/x/")]));
    }

    [Fact]
    public void ASessionWithoutOid_IsPinnedByItsAccount()
    {
        Assert.True(Check(expect: Expect(sessionOid: null)).Ok);
        var other = Check(c => c["preferred_username"] = "mallory@acme.com", Expect(sessionOid: null));
        Assert.Equal("subject", other.Reason);
    }

    [Fact]
    public void TheLadder_SendsMicrosoftAccountsToEntra_AndNeverWavesThrough()
    {
        var on = Options();
        Assert.Equal(StepUpRung.Entra, StepUpLadder.Decide("Microsoft", on, entraUsable: true, factors: null));
        // Every other account steps up with the portal's own factors — with none yet, it enrols first.
        Assert.Equal(StepUpRung.Enroll, StepUpLadder.Decide("Google", on, entraUsable: true, factors: null));
        Assert.Equal(StepUpRung.Enroll, StepUpLadder.Decide("LinkedIn", on, entraUsable: true, factors: null));
        Assert.Equal(StepUpRung.RefuseUnknownSession, StepUpLadder.Decide(null, on, entraUsable: true, factors: null));
        Assert.Equal(StepUpRung.RefuseNotConfigured, StepUpLadder.Decide("Microsoft", on with { EntraAuthenticationContext = null }, entraUsable: true, factors: null));
        Assert.Equal(StepUpRung.RefuseNotConfigured, StepUpLadder.Decide("Microsoft", on, entraUsable: false, factors: null));
        // Off until declared — only then does nobody step up.
        Assert.Equal(StepUpRung.NotRequired, StepUpLadder.Decide("Google", new StepUpOptions(), entraUsable: true, factors: null));
    }

    [Fact]
    public void TheSessionCarriesTheProviderAndTheEntraSubject()
    {
        var claims = StepUpClaims.ForSession("Microsoft",
            [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "oid-1"), new Claim("tid", Tenant)], Now).ToList();
        Assert.Contains(claims, c => c.Type == StepUpClaims.Idp && c.Value == "Microsoft");
        Assert.Contains(claims, c => c.Type == StepUpClaims.Oid && c.Value == "oid-1");
        Assert.Contains(claims, c => c.Type == StepUpClaims.Tid && c.Value == Tenant);
    }

    [Fact]
    public void TargetsMustPair()
    {
        Assert.NotNull(StepUpController.Pair(["/A/b"], ["h"]));
        Assert.Equal("A/b", StepUpController.Pair(["/A/b"], ["h"])![0].ActionPath);
        Assert.Null(StepUpController.Pair(["A/b", "A/c"], ["h"]));
        Assert.Null(StepUpController.Pair(["A/b"], [" "]));
        Assert.Null(StepUpController.Pair([], []));
    }

    // ---- the IO half, end to end against a fake Entra that signs with a local key ----

    private sealed class FakeEntra(RsaSecurityKey signingKey, Func<string> idToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["jwks_uri"] = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys",
                    ["authorization_endpoint"] = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                    ["token_endpoint"] = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                });
            else if (path.EndsWith("/keys", StringComparison.Ordinal))
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(signingKey);
                body = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } } });
            }
            else if (path.EndsWith("/token", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { id_token = idToken(), token_type = "Bearer" });
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private static string Sign(RsaSecurityKey key, Dictionary<string, object> claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Audience = "client-1",
            Expires = DateTime.UtcNow.AddMinutes(5),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

    private static EntraStepUp Rung(HttpMessageHandler handler) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Microsoft:ClientId"] = "client-1",
            ["Authentication:Microsoft:ClientSecret"] = "secret",
            ["Authentication:Microsoft:TenantId"] = Tenant,
            [StepUpOptions.EnabledKey] = "true",
            [StepUpOptions.EntraContextKey] = "c1",
        }).Build(),
        new HttpClient(handler), IoPool.Unbounded, new EntraMetadataCache(new HttpClient(handler)), NullLogger.Instance);

    private static Dictionary<string, object> LiveClaims()
    {
        var c = Baseline();
        c["auth_time"] = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        return c;
    }

    [Fact(Timeout = 60000)]
    public async Task ATokenSignedByTheTenantsKey_IsRedeemed_AndOneSignedByAnotherKey_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantKey = NewKey("tenant");
        var forger = NewKey("tenant");   // same kid, different key material
        EntraStepUpExpectation Expectation(string issuer) => new(issuer, "nonce-1", "oid-alice", "alice@acme.com", Options(), DateTimeOffset.UtcNow);

        var good = await Rung(new FakeEntra(tenantKey, () => Sign(tenantKey, LiveClaims())))
            .Redeem("code", "https://portal/auth/step-up/callback", Expectation)
            .Timeout(MeshWeaver.Fixture.TestTimeouts.Convergence).Await(ct);
        Assert.True(good.Ok, good.Reason);

        var forged = await Rung(new FakeEntra(tenantKey, () => Sign(forger, LiveClaims())))
            .Redeem("code", "https://portal/auth/step-up/callback", Expectation)
            .Timeout(MeshWeaver.Fixture.TestTimeouts.Convergence).Await(ct);
        Assert.False(forged.Ok);
        Assert.Equal("signature", forged.Reason);
    }

    [Fact]
    public void TheAuthorizeRequestAsksForAFreshLoginAndTheContext()
    {
        var url = Rung(new FakeEntra(NewKey("k"), () => "")).AuthorizeUrl("st", "nn", "https://portal/auth/step-up/callback", "alice@acme.com", "c1");
        Assert.StartsWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?", url);
        Assert.Contains("&prompt=login", url);
        Assert.Contains("&nonce=nn", url);
        Assert.Contains("&login_hint=alice%40acme.com", url);
        Assert.Contains("&claims=" + Uri.EscapeDataString("{\"id_token\":{\"acrs\":{\"essential\":true,\"value\":\"c1\"}}}"), url);
    }
}
