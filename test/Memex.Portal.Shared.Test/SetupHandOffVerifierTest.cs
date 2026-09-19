using System.Text;
using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The door in front of the one endpoint that carries a client's secrets.
///
/// <para>A signature alone would be enough for an inbox event and is not enough here: it signs the
/// body and nothing about WHEN, so a captured request stays valid forever. The three checks are
/// asserted together — authentic, fresh, and not seen before — along with the property that a
/// replay cannot be spent by an unauthentic caller.</para>
/// </summary>
[Collection("handoff")]
public class SetupHandOffVerifierTest
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"Deployment":"pearl"}""");
    private const string Secret = "shared-secret";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static string Stamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    public SetupHandOffVerifierTest() => SetupHandOffVerifier.Reset();

    [Fact]
    public void AnAuthenticFreshUnseenRequestIsAccepted()
    {
        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, Secret), Stamp(Now), "nonce-1", Now));
    }

    [Fact]
    public void WithNoSecretConfiguredTheEndpointDoesNotExist()
    {
        // NotConfigured answers 404, so a control instance that does not offer this never advertises it.
        Assert.Equal(HandOffRefusal.NotConfigured, SetupHandOffVerifier.Verify(
            null, Body, SetupHandOffVerifier.Sign(Body, Secret), Stamp(Now), "n", Now));
    }

    [Fact]
    public void AWrongSignatureAndAMissingOneAreDistinguished()
    {
        Assert.Equal(HandOffRefusal.Unsigned, SetupHandOffVerifier.Verify(
            Secret, Body, null, Stamp(Now), "n", Now));
        Assert.Equal(HandOffRefusal.Unsigned, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, Secret), Stamp(Now), nonce: null, Now));
        Assert.Equal(HandOffRefusal.BadSignature, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, "another-secret"), Stamp(Now), "n", Now));
    }

    [Fact]
    public void ATamperedBodyIsRefused()
    {
        var signature = SetupHandOffVerifier.Sign(Body, Secret);
        var tampered = Encoding.UTF8.GetBytes("""{"Deployment":"memex"}""");
        Assert.Equal(HandOffRefusal.BadSignature, SetupHandOffVerifier.Verify(
            Secret, tampered, signature, Stamp(Now), "n", Now));
    }

    [Fact]
    public void AStaleOrFutureTimestampIsRefused()
    {
        var signature = SetupHandOffVerifier.Sign(Body, Secret);

        // Too old: this is what bounds how long a captured request stays useful.
        Assert.Equal(HandOffRefusal.Stale, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now.AddMinutes(-6)), "n1", Now));

        // From the future: otherwise a captured request's life could simply be extended.
        Assert.Equal(HandOffRefusal.Stale, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now.AddMinutes(6)), "n2", Now));

        // Unparseable is stale, not "unsigned": there is no timestamp to judge.
        Assert.Equal(HandOffRefusal.Stale, SetupHandOffVerifier.Verify(
            Secret, Body, signature, "not-a-number", "n3", Now));

        // Ordinary clock skew still passes.
        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now.AddMinutes(-4)), "n4", Now));
    }

    [Fact]
    public void TheSameRequestIsAcceptedOnceAndRefusedAfterwards()
    {
        var signature = SetupHandOffVerifier.Sign(Body, Secret);
        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now), "nonce-replay", Now));
        Assert.Equal(HandOffRefusal.Replayed, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now), "nonce-replay", Now));
    }

    [Fact]
    public void AnUnauthenticCallerCannotBurnANonce()
    {
        // The replay check runs LAST. Otherwise anybody who can reach the endpoint could spend the
        // nonces of requests they do not own, and the real instance's hand-off would be refused.
        Assert.Equal(HandOffRefusal.BadSignature, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, "wrong"), Stamp(Now), "victim-nonce", Now));

        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, Secret), Stamp(Now), "victim-nonce", Now));
    }

    [Fact]
    public void ANonceOutsideTheWindowIsForgotten()
    {
        var signature = SetupHandOffVerifier.Sign(Body, Secret);
        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, signature, Stamp(Now), "old-nonce", Now));

        // Well past the window the nonce is dropped — a request signed then is Stale anyway, so the
        // cache never has to grow without bound to stay correct.
        var later = Now.AddMinutes(30);
        Assert.Equal(HandOffRefusal.None, SetupHandOffVerifier.Verify(
            Secret, Body, SetupHandOffVerifier.Sign(Body, Secret), Stamp(later), "old-nonce", later));
    }

    [Fact]
    public void ASecretNeverPrintsItselfOnTheReceivingSideEither()
    {
        var secret = new SetupHandOffSecret("Authentication__Microsoft__ClientSecret", "s3cret", "pearl-Authentication-Microsoft-ClientSecret");
        Assert.DoesNotContain("s3cret", secret.ToString());
        Assert.Contains("redacted", secret.ToString());
    }
}
