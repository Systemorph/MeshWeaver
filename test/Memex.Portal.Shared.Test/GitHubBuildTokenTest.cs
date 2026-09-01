using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The verifier half of the build principal (#2483): what a GitHub Actions OIDC token has to prove
/// before the mesh will even look up a rule for it.
///
/// <para>🚨 <b>Signature-only is the failure this file exists to make impossible.</b> Every workflow
/// run on GitHub carries a token signed by the same keys, so a verifier that checks the signature and
/// stops authenticates the entire public internet's CI. Each refusal below is therefore asserted with
/// an otherwise VALID token — same key, same signature, one claim moved — so a passing test can only
/// mean the claim itself was checked.</para>
///
/// <para>The third state is asserted too: an unknown <c>kid</c> is
/// <see cref="GitHubTokenVerification.KeyUnknown"/>, neither accepted nor refused, because a routine
/// GitHub key rotation must become a re-read rather than a fleet-wide outage — and must never become
/// an admission.</para>
/// </summary>
public class GitHubBuildTokenTest
{
    private const string Audience = "https://registry.example.test";
    private const string Repository = "Systemorph/MeshWeaver.SocialMedia";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Audiences = [Audience];

    private static GitHubTokenVerification Verify(
        GitHubTokenFactory factory, string token, DateTimeOffset? now = null,
        IReadOnlyCollection<string>? audiences = null,
        IReadOnlyDictionary<string, GitHubSigningKey>? keys = null) =>
        GitHubActionsToken.Verify(token, now ?? Now, audiences ?? Audiences, keys ?? factory.Keys());

    [Fact]
    public void AGenuineTokenVerifies_AndCarriesTheClaimsADecisionNeeds()
    {
        using var factory = new GitHubTokenFactory();

        var result = Verify(factory, factory.Mint(Audience, issuedAt: Now));

        Assert.True(result.IsVerified);
        Assert.Equal(Repository, result.Claims!.Repository);
        Assert.Equal("push", result.Claims.EventName);
        Assert.Equal("refs/heads/main", result.Claims.Ref);
        Assert.Equal("123456789", result.Claims.RepositoryId);
        Assert.Equal(Audience, result.Claims.Audience);
    }

    [Fact]
    public void NoConfiguredAudience_RefusesAPerfectlyValidToken()
    {
        // The whole build-principal leg is off until an operator names an audience. "Unconfigured"
        // must never read as "accept anything": every GitHub run in the world verifies here.
        using var factory = new GitHubTokenFactory();

        var result = Verify(factory, factory.Mint(Audience, issuedAt: Now), audiences: []);

        Assert.False(result.IsVerified);
        Assert.False(result.KeyUnknown);
    }

    [Fact]
    public void AnotherServicesAudienceIsRefused()
    {
        // The exact attack the audience exists to stop: a token a workflow legitimately minted for
        // Azure, replayed here.
        using var factory = new GitHubTokenFactory();

        var result = Verify(factory, factory.Mint("api://AzureADTokenExchange", issuedAt: Now));

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void AnAudienceArrayIsHonoured_WhenItContainsOurs()
    {
        using var factory = new GitHubTokenFactory();
        var token = factory.Mint(
            Audience, issuedAt: Now, audienceJson: $"[\"api://other\",\"{Audience}\"]");

        Assert.True(Verify(factory, token).IsVerified);
        // …and not when it does not.
        var other = factory.Mint(Audience, issuedAt: Now, audienceJson: "[\"api://other\"]");
        Assert.False(Verify(factory, other).IsVerified);
    }

    [Fact]
    public void AnotherIssuerIsRefused()
    {
        using var factory = new GitHubTokenFactory();

        var result = Verify(factory, factory.Mint(Audience, issuedAt: Now, issuer: "https://evil.example"));

        Assert.False(result.IsVerified);
        // …and the routing peek agrees it was never ours to verify in the first place.
        Assert.False(GitHubActionsToken.IsGitHubIssued(
            factory.Mint(Audience, issuedAt: Now, issuer: "https://evil.example")));
    }

    [Fact]
    public void AnIssuerThatMerelySTARTSWithGitHubsIsRefused()
    {
        // A prefix match here would trust https://token.actions.githubusercontent.com.evil.example.
        using var factory = new GitHubTokenFactory();
        var token = factory.Mint(
            Audience, issuedAt: Now, issuer: GitHubActionsToken.Issuer + ".evil.example");

        Assert.False(Verify(factory, token).IsVerified);
        Assert.False(GitHubActionsToken.IsGitHubIssued(token));
    }

    [Fact]
    public void AnExpiredTokenIsRefused()
    {
        using var factory = new GitHubTokenFactory();
        var token = factory.Mint(Audience, issuedAt: Now.AddHours(-2), lifetime: TimeSpan.FromMinutes(10));

        Assert.False(Verify(factory, token).IsVerified);
        // …and the same token verified at its own issue instant does, so only the clock moved.
        Assert.True(Verify(factory, token, now: Now.AddHours(-2)).IsVerified);
    }

    [Fact]
    public void ATokenThatIsNotYetValidIsRefused()
    {
        using var factory = new GitHubTokenFactory();
        var token = factory.Mint(Audience, issuedAt: Now.AddHours(1));

        Assert.False(Verify(factory, token).IsVerified);
    }

    [Fact]
    public void TheTokensOwnAlgIsNeverHonoured()
    {
        using var factory = new GitHubTokenFactory();

        // `none` — the classic forgery.
        Assert.False(Verify(factory, factory.Mint(Audience, issuedAt: Now, algorithm: "none")).IsVerified);
        // …and an HMAC claim over the same bytes: the header decides which verifier runs, and this
        // one only ever runs RS256.
        Assert.False(Verify(factory, factory.Mint(Audience, issuedAt: Now, algorithm: "HS256")).IsVerified);
    }

    [Fact]
    public void ASignatureFromAnotherKeyIsRefused()
    {
        using var factory = new GitHubTokenFactory();
        using var stranger = RSA.Create(2048);

        var token = factory.Mint(Audience, issuedAt: Now, signWith: stranger);

        Assert.False(Verify(factory, token).IsVerified);
    }

    [Fact]
    public void AKeySetPublishingSomeoneElsesKeyUnderTheSameKidIsRefused()
    {
        // The JWKS is the trust anchor; a token signed by us must not verify against a key set that
        // merely REUSES our kid.
        using var factory = new GitHubTokenFactory();
        var keys = GitHubActionsToken.ParseJwks(factory.JwksOfAStranger());

        var result = Verify(factory, factory.Mint(Audience, issuedAt: Now), keys: keys);

        Assert.False(result.IsVerified);
        Assert.False(result.KeyUnknown, "the kid WAS published — this is a rejection, not an unknown key");
    }

    [Fact]
    public void ASwappedPayloadIsRefused()
    {
        // A valid signature over DIFFERENT bytes: the only forgery left once `alg` and the key are
        // pinned, and the one that would hand another repository's identity to this one.
        using var factory = new GitHubTokenFactory();

        var token = factory.MintWithSwappedPayload(Audience, Repository, "Systemorph/Evil");

        Assert.False(Verify(factory, token).IsVerified);
    }

    [Fact]
    public void AnUnknownKidIsUNDETERMINED_NeitherAcceptedNorRefused()
    {
        // 🚨 The third state (core #2901). GitHub rotates these keys; folding "I do not hold that
        // key" into "rejected" turns a routine rotation into a fleet-wide outage, and folding it
        // into "accepted" is a hole. It is neither: the caller re-reads the set and asks again.
        using var factory = new GitHubTokenFactory();

        var result = Verify(factory, factory.Mint(Audience, issuedAt: Now, keyId: "rotated-away"));

        Assert.False(result.IsVerified);
        Assert.True(result.KeyUnknown);
    }

    [Fact]
    public void ATokenWithNoRepositoryClaimIsRefused()
    {
        using var factory = new GitHubTokenFactory();

        Assert.False(Verify(factory, factory.Mint(Audience, issuedAt: Now, repository: "")).IsVerified);
    }

    [Fact]
    public void GarbageIsRefusedWithoutThrowing()
    {
        // This runs on an UNAUTHENTICATED request with attacker-controlled input: a throwing parse
        // would let anyone make the registry raise and unwind an exception per request.
        using var factory = new GitHubTokenFactory();

        foreach (var candidate in new[] { null, "", "   ", "a.b", "a.b.c", "...", new string('x', 20_000) })
            Assert.False(GitHubActionsToken.Verify(candidate, Now, Audiences, factory.Keys()).IsVerified);
    }

    // ── the JWKS reader ──────────────────────────────────────────────────────

    [Fact]
    public void TheKeySetSkipsEveryEntryThisVerifierWouldNotUse()
    {
        using var factory = new GitHubTokenFactory();
        var keys = GitHubActionsToken.ParseJwks(factory.Jwks(
            """{"kty":"EC","use":"sig","kid":"ec","crv":"P-256","x":"AA","y":"BB"}""",
            """{"kty":"RSA","use":"enc","alg":"RS256","kid":"enc","n":"AQAB","e":"AQAB"}""",
            """{"kty":"RSA","use":"sig","alg":"RS512","kid":"rs512","n":"AQAB","e":"AQAB"}""",
            // RSA-1024: a modulus short enough to be forgeable is dropped at PARSE time rather
            // than trusted at verify time.
            $$"""{"kty":"RSA","use":"sig","alg":"RS256","kid":"short","n":"{{new string('A', 171)}}","e":"AQAB"}"""));

        Assert.Equal([factory.KeyId], keys.Keys);
    }

    [Fact]
    public void AMalformedKeySetIsEmpty_NeverAThrow()
    {
        foreach (var document in new[] { null, "", "not json", "[]", """{"keys":{}}""", """{"keys":[1,2]}""" })
            Assert.Empty(GitHubActionsToken.ParseJwks(document));
    }

    [Fact]
    public void DiscoveryMayMoveThePath_NeverTheHost()
    {
        Assert.Equal(
            "https://token.actions.githubusercontent.com/somewhere/else",
            GitHubActionsToken.JwksUriFromDiscovery(
                """{"jwks_uri":"https://token.actions.githubusercontent.com/somewhere/else"}"""));

        // 🚨 A discovery document is fetched over the network. It is allowed to move the PATH; it is
        // never allowed to move the trust anchor.
        Assert.Null(GitHubActionsToken.JwksUriFromDiscovery("""{"jwks_uri":"https://evil.example/jwks"}"""));
        Assert.Null(GitHubActionsToken.JwksUriFromDiscovery(
            """{"jwks_uri":"http://token.actions.githubusercontent.com/jwks"}"""));
        Assert.Null(GitHubActionsToken.JwksUriFromDiscovery("""{"jwks_uri":"/relative"}"""));
        Assert.Null(GitHubActionsToken.JwksUriFromDiscovery("not json"));
    }

    // ── repository matching, classic and immutable ───────────────────────────

    [Fact]
    public void TheImmutableSubjectFormatMatchesTheClassicOne()
    {
        // 🚨 The migration hazard: when GitHub moves an org onto immutable ids, a principal written
        // in the classic form must keep matching — or every build principal in the fleet silently
        // stops authenticating on a day nobody changed anything.
        Assert.True(GitHubActionsToken.RepositoryEquals(
            "Systemorph/MeshWeaver.SocialMedia", "Systemorph@12345/MeshWeaver.SocialMedia@67890"));
        Assert.True(GitHubActionsToken.RepositoryEquals(
            "Systemorph@12345/MeshWeaver.SocialMedia@67890", "Systemorph/MeshWeaver.SocialMedia"));
        Assert.Equal(
            "Systemorph/MeshWeaver.SocialMedia",
            GitHubActionsToken.NormalizeRepository("Systemorph@12345/MeshWeaver.SocialMedia@67890"));
    }

    [Fact]
    public void RepositoryMatchingIsExact_NeverAPrefix()
    {
        Assert.False(GitHubActionsToken.RepositoryEquals(
            "Systemorph/MeshWeaver", "Systemorph/MeshWeaver.Evil"));
        Assert.False(GitHubActionsToken.RepositoryEquals(
            "Systemorph/MeshWeaver", "Evil/MeshWeaver"));
        Assert.False(GitHubActionsToken.RepositoryEquals("Systemorph/MeshWeaver", null));
        Assert.False(GitHubActionsToken.RepositoryEquals("", ""));
        // GitHub names are case-insensitive, so an admin's casing must not lock a repo out.
        Assert.True(GitHubActionsToken.RepositoryEquals(
            "systemorph/meshweaver.socialmedia", "Systemorph/MeshWeaver.SocialMedia"));
        // …and a non-numeric suffix after '@' is NOT an immutable id, so it is not stripped.
        Assert.False(GitHubActionsToken.RepositoryEquals(
            "Systemorph/MeshWeaver", "Systemorph/MeshWeaver@evil"));
    }

    [Fact]
    public void ARepositoryResolvesToOneDeterministicNodePath()
    {
        Assert.Equal(
            "Admin/_BuildPrincipal/systemorph--meshweaver.socialmedia",
            BuildPrincipal.PathFor("Systemorph/MeshWeaver.SocialMedia"));
        // Both claim formats route to the SAME node — the path is derived from the normalized form.
        Assert.Equal(
            BuildPrincipal.PathFor("Systemorph/MeshWeaver.SocialMedia"),
            BuildPrincipal.PathFor("Systemorph@1/MeshWeaver.SocialMedia@2"));
        Assert.Equal("", BuildPrincipal.PathFor(""));
    }
}
