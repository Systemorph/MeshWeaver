using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the short-lived access token an instance exchanges its durable <c>mwi_</c> key for. Every
/// case here is a forgery or a lifetime question, so the instants are explicit and the keys are
/// fixed — a token test that depends on the wall clock proves nothing repeatably.
/// </summary>
public class SyncAccessTokenTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Encoding.UTF8.GetBytes(new string('k', 32));
    private static readonly byte[] OtherKey = Encoding.UTF8.GetBytes(new string('x', 32));

    private const string KeyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Mint(
        string instanceId = "manufacturing-ci",
        string[]? scope = null,
        TimeSpan? lifetime = null,
        DateTimeOffset? issuedAt = null,
        string keyHash = KeyHash) =>
        SyncAccessToken.Mint(instanceId, keyHash, scope ?? [], issuedAt ?? Now,
            lifetime ?? SyncAccessToken.DefaultLifetime, Key);

    [Fact]
    public void AMintedTokenVerifiesAndCarriesItsInstance()
    {
        var claims = SyncAccessToken.Verify(Mint(), Now, Key);
        Assert.NotNull(claims);
        Assert.Equal("manufacturing-ci", claims!.InstanceId);
    }

    [Fact]
    public void TheTokenIsAStandardJwt()
    {
        // header.payload.signature, HS256 declared in the header, the key named by `kid` — so any
        // JWT tooling can read one and a second issuer can share the verifier (#2483).
        var parts = Mint().Split('.');
        Assert.Equal(3, parts.Length);
        var header = System.Text.Json.JsonDocument.Parse(Base64Url(parts[0])).RootElement;
        Assert.Equal(SyncAccessToken.Algorithm, header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(SyncAccessToken.KeyId(Key), header.GetProperty("kid").GetString());
        var payload = System.Text.Json.JsonDocument.Parse(Base64Url(parts[1])).RootElement;
        Assert.Equal("manufacturing-ci", payload.GetProperty("sub").GetString());
        Assert.Equal(SyncAccessToken.Audience, payload.GetProperty("aud").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());
    }

    [Fact]
    public void AlgNoneIsRefused_TheHeaderNeverChoosesTheAlgorithm()
    {
        // The classic JWT forgery: strip the signature and declare `none`. The verifier honours
        // only the algorithm THIS registry mints with, never the one the token asks for.
        var parts = Mint().Split('.');
        var noneHeader = Encode(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        Assert.Null(SyncAccessToken.Verify($"{noneHeader}.{parts[1]}.", Now, Key));
        Assert.Null(SyncAccessToken.Verify($"{noneHeader}.{parts[1]}.{parts[2]}", Now, Key));
    }

    private static byte[] Base64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    [Fact]
    public void PrefixesAreDisjoint_SoNoValidatorAcceptsAnothersCredential()
    {
        // A JWT (three segments) vs mwi_ (instance) vs mwr_ (registration) vs mw_ (personal). The
        // instance extractor must not see a token, and the token extractor must not see an
        // instance key — recognition is by shape, and the shapes are disjoint.
        var token = Mint();
        var instanceKey = InstanceKeys.Generate();

        Assert.Null(InstanceKeys.ExtractKey($"Bearer {token}"));
        Assert.Null(SyncAccessToken.ExtractToken($"Bearer {instanceKey}"));
        Assert.Equal(token, SyncAccessToken.ExtractToken($"Bearer {token}"));
    }

    [Fact]
    public void AnExpiredTokenDoesNotVerify()
    {
        var token = Mint(lifetime: TimeSpan.FromMinutes(15));
        Assert.NotNull(SyncAccessToken.Verify(token, Now.AddMinutes(14), Key));
        Assert.Null(SyncAccessToken.Verify(token, Now.AddMinutes(15), Key));
        Assert.Null(SyncAccessToken.Verify(token, Now.AddMinutes(16), Key));
    }

    [Fact]
    public void ATokenSignedWithAnotherKeyDoesNotVerify()
        => Assert.Null(SyncAccessToken.Verify(Mint(), Now, OtherKey));

    [Fact]
    public void TamperingWithThePayloadInvalidatesTheSignature()
    {
        // The whole point of signing: the scope and the instance id cannot be edited by the holder.
        var token = Mint(scope: ["Plugins/Publish"]);
        var parts = token.Split('.');
        var forged = $"{parts[0]}.{parts[1].Replace('a', 'b')}.{parts[2]}";

        Assert.NotEqual(token, forged);
        Assert.Null(SyncAccessToken.Verify(forged, Now, Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodot")]
    [InlineData("one.dot")]
    [InlineData("..")]
    [InlineData("a..c")]
    [InlineData("not-a-token-at-all")]
    [InlineData("!!!.@@@.###")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.notasignature")]
    public void MalformedTokensAreRejectedRatherThanThrowing(string candidate)
        => Assert.Null(SyncAccessToken.Verify(candidate, Now, Key));

    [Fact]
    public void AShortSigningKeyIsRefusedAtMint()
    {
        // A forgeable signature must fail loudly where it is created, not quietly where it is used.
        var tooShort = Encoding.UTF8.GetBytes("short");
        Assert.Throws<ArgumentException>(() =>
            SyncAccessToken.Mint("ci", KeyHash, [], Now, SyncAccessToken.DefaultLifetime, tooShort));
        Assert.False(SyncAccessToken.IsUsableSigningKey(tooShort));
        Assert.True(SyncAccessToken.IsUsableSigningKey(Key));
    }

    [Fact]
    public void VerifyingUnderAnUnusableKeyFailsClosed()
        => Assert.Null(SyncAccessToken.Verify(Mint(), Now, Encoding.UTF8.GetBytes("short")));

    [Fact]
    public void ATokenMustNameItsInstance()
        => Assert.Throws<ArgumentException>(() =>
            SyncAccessToken.Mint("  ", KeyHash, [], Now, SyncAccessToken.DefaultLifetime, Key));

    [Fact]
    public void ATokenMustCarryTheKeyItWasExchangedFrom()
        => Assert.Throws<ArgumentException>(() =>
            SyncAccessToken.Mint("ci", "  ", [], Now, SyncAccessToken.DefaultLifetime, Key));

    [Fact]
    public void TheTokenIsBoundToTheKeyThatMintedIt()
    {
        // Re-issuing an instance key must invalidate every outstanding token. The binding that
        // achieves it is the key hash the token carries: the instance record then holds a different
        // one, so the token no longer resolves to it.
        var claims = SyncAccessToken.Verify(Mint(keyHash: KeyHash), Now, Key);
        Assert.NotNull(claims);
        Assert.Equal(KeyHash, claims!.KeyHash);
    }

    [Fact]
    public void LifetimeIsClampedToTheMaximum_NotRefused()
    {
        // A consumer written against a more generous registry should still work, not fail at the
        // exchange — so an over-long request is clamped.
        var token = Mint(lifetime: TimeSpan.FromDays(30));
        var claims = SyncAccessToken.Verify(token, Now, Key);
        Assert.NotNull(claims);
        Assert.Equal(Now.Add(SyncAccessToken.MaximumLifetime), claims!.ExpiresAt);
        Assert.Null(SyncAccessToken.Verify(token, Now.Add(SyncAccessToken.MaximumLifetime), Key));
    }

    [Fact]
    public void ANonPositiveLifetimeFallsBackToTheDefault_NeverToAnAlreadyExpiredToken()
    {
        var claims = SyncAccessToken.Verify(Mint(lifetime: TimeSpan.Zero), Now, Key);
        Assert.NotNull(claims);
        Assert.Equal(Now.Add(SyncAccessToken.DefaultLifetime), claims!.ExpiresAt);
    }

    [Fact]
    public void ScopeNarrowsWhatTheHolderMayAskAbout()
    {
        var claims = SyncAccessToken.Verify(Mint(scope: ["Plugins/Publish"]), Now, Key);
        Assert.NotNull(claims);
        Assert.True(claims!.Covers("Plugins", "Publish"));
        Assert.False(claims.Covers("Plugins", "Store"));
        Assert.False(claims.Covers("Education", "Publish"));
    }

    [Fact]
    public void AnUnnarrowedTokenCoversEverything_TheGrantStillDecides()
    {
        // Empty scope means "not narrowed", NOT "nothing" — the token shortens the lifetime and the
        // PluginGrant remains the authority.
        var claims = SyncAccessToken.Verify(Mint(scope: []), Now, Key);
        Assert.NotNull(claims);
        Assert.True(claims!.Covers("Plugins", "Publish"));
        Assert.True(claims.Covers("Education", "DataModeling"));
    }

    [Fact]
    public void ScopeUnderstandsTheWholeSourceWildcard()
    {
        var claims = SyncAccessToken.Verify(Mint(scope: ["Plugins/*"]), Now, Key);
        Assert.NotNull(claims);
        Assert.True(claims!.Covers("Plugins", "Publish"));
        Assert.True(claims.Covers("Plugins", "AnythingElse"));
        Assert.False(claims.Covers("Education", "DataModeling"));
    }

    [Fact]
    public void TwoTokensMintedTogetherAreDistinguishable()
    {
        // Same instance, same scope, same second — the nonce keeps them individually recognisable
        // in a log rather than being one indistinguishable string.
        Assert.NotEqual(Mint(), Mint());
    }

    [Fact]
    public void ABasicAuthHeaderIsAccepted_ForClientsThatCannotSendBearer()
    {
        var token = Mint();
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"instance:{token}"));
        Assert.Equal(token, SyncAccessToken.ExtractToken(header));
    }

    [Fact]
    public void AMalformedBasicHeaderIsRejectedRatherThanThrowing()
        => Assert.Null(SyncAccessToken.ExtractToken("Basic !!!not-base64!!!"));

    [Fact]
    public void SignaturesUseTheFullKey_ARandomKeyRoundTrips()
    {
        // Guards against a mint that silently truncates or ignores the key material.
        var random = RandomNumberGenerator.GetBytes(64);
        var token = SyncAccessToken.Mint(
            "ci", KeyHash, ["Plugins/Publish"], Now, TimeSpan.FromMinutes(5), random);
        Assert.NotNull(SyncAccessToken.Verify(token, Now, random));
        Assert.Null(SyncAccessToken.Verify(token, Now, RandomNumberGenerator.GetBytes(64)));
    }

    [Fact]
    public void AnOversizedBasicPayloadIsRejectedWithoutDecoding()
    {
        // The reject path runs on UNAUTHENTICATED, attacker-controlled input, so it must not throw
        // and must not allocate an unbounded buffer — a throwing parse lets anyone make the registry
        // raise and unwind an exception per request. Same discipline as InstanceKeys.
        var oversized = "Basic " + new string('A', 4096);
        Assert.Null(SyncAccessToken.ExtractToken(oversized));
    }

    [Theory]
    [InlineData("Basic ")]
    [InlineData("Basic !!!!")]
    [InlineData("Basic ====")]
    [InlineData("Basic bm9jb2xvbg==")]
    public void AMalformedBasicPayloadIsNullRatherThanAThrow(string header)
        => Assert.Null(SyncAccessToken.ExtractToken(header));

    [Fact]
    public void ABasicPayloadWithNoTokenInThePasswordHalfIsRejected()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:not-a-token"));
        Assert.Null(SyncAccessToken.ExtractToken(header));
    }

    [Fact]
    public void NonUtf8BasicBytesDoNotThrow()
    {
        // Replacement fallback turns invalid bytes into U+FFFD, which then simply fails the prefix
        // check — rather than throwing on the decode.
        var header = "Basic " + Convert.ToBase64String([0xFF, 0xFE, (byte)':', 0xFF]);
        Assert.Null(SyncAccessToken.ExtractToken(header));
    }
}
