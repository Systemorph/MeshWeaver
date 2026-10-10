using System.Collections.Immutable;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The portal-held rungs of the approval step-up (Refs #4305, <c>Doc/Architecture/ApprovalStepUp</c>):
/// the ladder's fallback decision (always prefer a passkey; TOTP only with no passkey at all), RFC
/// 6238 TOTP against the RFC's own vectors, and a real WebAuthn registration + assertion verified by
/// the FIDO2 library against a software authenticator. Each refusal is paired with the accepted
/// ceremony it differs from in one property — the negative control.
/// </summary>
public class PortalStepUpRungsTest
{
    private static readonly StepUpOptions On = new() { Enabled = true, EntraAuthenticationContext = "c1" };

    private static PasskeyCredential AKey() => new() { CredentialId = "AAAA", PublicKey = "", CreatedAt = DateTimeOffset.UnixEpoch };

    private static StepUpFactors PasskeyOnly() => new() { UserId = "u", Passkeys = ImmutableDictionary<string, PasskeyCredential>.Empty.Add("AAAA", AKey()) };

    private static StepUpFactors TotpOnly() => new() { UserId = "u", TotpConfirmedAt = DateTimeOffset.UnixEpoch, TotpSecretProtected = "x" };

    private static StepUpFactors Both() => TotpOnly() with { Passkeys = ImmutableDictionary<string, PasskeyCredential>.Empty.Add("AAAA", AKey()) };

    // ───────────── the ladder ─────────────

    [Fact]
    public void ThePasskeyIsAlwaysPreferred_AndTotpIsOnlyForAnAccountWithNoPasskey()
    {
        Assert.Equal(StepUpRung.Passkey, StepUpLadder.Decide("Google", On, true, PasskeyOnly()));
        Assert.Equal(StepUpRung.Passkey, StepUpLadder.Decide("Google", On, true, Both()));   // never TOTP while a passkey exists
        Assert.Equal(StepUpRung.Totp, StepUpLadder.Decide("LinkedIn", On, true, TotpOnly()));
        Assert.Equal(StepUpRung.Enroll, StepUpLadder.Decide("Google", On, true, null));
        Assert.Equal(StepUpRung.Enroll, StepUpLadder.Decide("Google", On with { AllowTotpFallback = false }, true, TotpOnly()));
        // Entra accounts step up with Entra — one prompt, never the portal's own factors as well.
        Assert.Equal(StepUpRung.Entra, StepUpLadder.Decide("Microsoft", On, true, Both()));
        // Off until declared.
        Assert.Equal(StepUpRung.NotRequired, StepUpLadder.Decide("Google", new StepUpOptions(), true, null));
    }

    [Fact]
    public void ATotpCodeCannotDowngradeAnAccountThatHasAPasskey()
    {
        Assert.False(StepUpLadder.AcceptsTotp(On, Both()));
        Assert.False(StepUpLadder.MayEnrollTotp(On, PasskeyOnly()));
        // Negative control: with no passkey, both are allowed.
        Assert.True(StepUpLadder.AcceptsTotp(On, TotpOnly()));
        Assert.True(StepUpLadder.MayEnrollTotp(On, null));
        Assert.False(StepUpLadder.MayEnrollTotp(On with { AllowTotpFallback = false }, null));
    }

    // ───────────── completing a pending step-up: the server-decided rung, re-checked ─────────────

    private static StepUpPending StartedAs(string rung) => new() { Id = "p", UserId = "u", Rung = rung };

    [Fact]
    public void AMicrosoftAccountCannotFinishAnEntraStepUpWithAPortalFactor()
    {
        // A Microsoft account that somehow holds portal factors, with an Entra step-up pending.
        var entraPending = StartedAs(StepUpRung.Entra);
        Assert.False(StepUpLadder.MayComplete(entraPending.Rung, StepUpRung.Passkey, "Microsoft", On, true, Both()));
        Assert.False(StepUpLadder.MayComplete(entraPending.Rung, StepUpRung.Totp, "Microsoft", On, true, TotpOnly()));
        // Even a pending record claiming a portal rung does not help: the ladder decided NOW says Entra.
        Assert.False(StepUpLadder.MayComplete(StepUpRung.Passkey, StepUpRung.Passkey, "Microsoft", On, true, Both()));
        Assert.False(StepUpLadder.MayComplete(StepUpRung.Totp, StepUpRung.Totp, "Microsoft", On, true, TotpOnly()));
        // Negative control: the Entra callback completes the Entra step-up it was started for.
        Assert.True(StepUpLadder.MayComplete(entraPending.Rung, StepUpRung.Entra, "Microsoft", On, true, null));
    }

    [Fact]
    public void APortalStepUpCompletesOnlyWithTheRungItWasStartedFor()
    {
        // Started as a passkey step-up: a TOTP code (or an Entra callback) does not finish it.
        Assert.False(StepUpLadder.MayComplete(StepUpRung.Passkey, StepUpRung.Totp, "Google", On, true, Both()));
        Assert.False(StepUpLadder.MayComplete(StepUpRung.Passkey, StepUpRung.Entra, "Google", On, true, Both()));
        // A record from before the field existed completes nothing.
        Assert.False(StepUpLadder.MayComplete("", StepUpRung.Passkey, "Google", On, true, PasskeyOnly()));
        // A TOTP step-up whose account enrolled a passkey since: the ladder now says passkey.
        Assert.False(StepUpLadder.MayComplete(StepUpRung.Totp, StepUpRung.Totp, "Google", On, true, Both()));
        // Negative controls: each completes with its own rung.
        Assert.True(StepUpLadder.MayComplete(StepUpRung.Passkey, StepUpRung.Passkey, "Google", On, true, PasskeyOnly()));
        Assert.True(StepUpLadder.MayComplete(StepUpRung.Totp, StepUpRung.Totp, "LinkedIn", On, true, TotpOnly()));
    }

    [Fact]
    public void OnlyAKnownNonMicrosoftAccountMayEnrolPortalFactors()
    {
        Assert.False(StepUpLadder.MayEnrollPortalFactors("Microsoft"));
        Assert.False(StepUpLadder.MayEnrollPortalFactors("microsoft"));
        Assert.False(StepUpLadder.MayEnrollPortalFactors(null));   // the provider is never guessed
        Assert.False(StepUpLadder.MayEnrollPortalFactors(""));
        // Negative control.
        Assert.True(StepUpLadder.MayEnrollPortalFactors("Google"));
        Assert.True(StepUpLadder.MayEnrollPortalFactors("LinkedIn"));
    }

    // ───────────── TOTP (RFC 6238 appendix B, SHA-1 secret) ─────────────

    private static readonly DateTimeOffset T59 = DateTimeOffset.FromUnixTimeSeconds(59);

    private static byte[] RfcSecret() => Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void TotpMatchesTheRfcVectors(long unixSeconds, string eightDigits) =>
        Assert.Equal(eightDigits, Totp.Code(RfcSecret(), Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)), digits: 8));

    [Fact]
    public void ACodeIsAcceptedOnce_WithinTheDriftWindow()
    {
        var secret = RfcSecret();
        var step = Totp.StepOf(T59);
        var code = Totp.Code(secret, step);
        Assert.Equal(step, Totp.Verify(secret, code, T59, lastAcceptedStep: 0));
        Assert.Null(Totp.Verify(secret, code, T59, lastAcceptedStep: step));          // replay
        Assert.Equal(step, Totp.Verify(secret, code, T59.AddSeconds(30), 0));          // one step of drift
        Assert.Null(Totp.Verify(secret, code, T59.AddSeconds(90), 0));                 // too far
        Assert.Null(Totp.Verify(secret, "000000" == code ? "111111" : "000000", T59, 0));
        Assert.Null(Totp.Verify(secret, "12a456", T59, 0));
    }

    [Fact]
    public void RecoveryCodesAreHashedAndNormalized()
    {
        var codes = Totp.NewRecoveryCodes();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.Equal(Totp.HashRecoveryCode(codes[0]), Totp.HashRecoveryCode(" " + codes[0].ToUpperInvariant().Replace("-", "") + " "));
        Assert.NotEqual(Totp.HashRecoveryCode(codes[0]), Totp.HashRecoveryCode(codes[1]));
    }

    // ───────────── passkeys, against a software authenticator ─────────────

    private const string RpId = "portal.example.com";
    private const string Origin = "https://portal.example.com";

    private static IReadOnlyList<StepUpTarget> TargetsA => [new StepUpTarget { ActionPath = "Ops/InstanceAction/a", Binding = "h1" }];
    private static IReadOnlyList<StepUpTarget> TargetsB => [new StepUpTarget { ActionPath = "Ops/InstanceAction/b", Binding = "h1" }];

    private static string B64U(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A WebAuthn authenticator in software: one P-256 key, a counter, "none" attestation.</summary>
    private sealed class SoftAuthenticator
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);
        public uint Counter { get; set; }

        private static byte[] RpIdHash() => SHA256.HashData(Encoding.UTF8.GetBytes(RpId));

        private byte[] CoseKey()
        {
            var p = key.ExportParameters(false);
            var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);     // kty: EC2
            w.WriteInt32(3); w.WriteInt32(-7);    // alg: ES256
            w.WriteInt32(-1); w.WriteInt32(1);    // crv: P-256
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
            w.WriteEndMap();
            return w.Encode();
        }

        private static byte[] Counter32(uint c) => [(byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c];

        private static string ClientData(string type, byte[] challenge) =>
            JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = type, ["challenge"] = B64U(challenge), ["origin"] = Origin, ["crossOrigin"] = false });

        public string Create(CredentialCreateOptions options)
        {
            var authData = RpIdHash()
                .Concat(new byte[] { 0x45 })                       // UP | UV | AT
                .Concat(Counter32(Counter))
                .Concat(new byte[16])                              // AAGUID
                .Concat(new[] { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length })
                .Concat(CredentialId)
                .Concat(CoseKey()).ToArray();
            var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
            w.WriteStartMap(3);
            w.WriteTextString("fmt"); w.WriteTextString("none");
            w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
            w.WriteTextString("authData"); w.WriteByteString(authData);
            w.WriteEndMap();
            return JsonSerializer.Serialize(new
            {
                id = B64U(CredentialId),
                rawId = B64U(CredentialId),
                type = "public-key",
                response = new
                {
                    attestationObject = B64U(w.Encode()),
                    clientDataJSON = B64U(Encoding.UTF8.GetBytes(ClientData("webauthn.create", options.Challenge))),
                },
                clientExtensionResults = new { },
            });
        }

        public string Get(byte[] challenge, byte[] userHandle, bool userVerified = true)
        {
            var authData = RpIdHash().Concat(new[] { (byte)(userVerified ? 0x05 : 0x01) }).Concat(Counter32(Counter)).ToArray();
            var clientData = Encoding.UTF8.GetBytes(ClientData("webauthn.get", challenge));
            var signature = key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return JsonSerializer.Serialize(new
            {
                id = B64U(CredentialId),
                rawId = B64U(CredentialId),
                type = "public-key",
                response = new
                {
                    authenticatorData = B64U(authData),
                    clientDataJSON = B64U(clientData),
                    signature = B64U(signature),
                    userHandle = B64U(userHandle),
                },
                clientExtensionResults = new { },
            });
        }
    }

    private static PasskeyStepUp Rp() => new(RpId, Origin, "Portal", IoPool.Unbounded);

    private static async Task<(SoftAuthenticator Device, PasskeyCredential Stored)> Enrolled(CancellationToken ct)
    {
        var device = new SoftAuthenticator();
        var options = Rp().RegistrationOptions("alice", "alice@example.com", []);
        var registered = await Rp().Register(device.Create(options), options, [], DateTimeOffset.UtcNow).Await(ct);
        Assert.True(registered.Ok, registered.Reason);
        return (device, registered.Credential!);
    }

    [Fact(Timeout = 60000)]
    public async Task APasskeyEnrolsAndConfirmsTheStepUpItsChallengeIsBoundTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (device, stored) = await Enrolled(ct);
        Assert.Equal(B64U(device.CredentialId), stored.CredentialId);

        var challenge = PasskeyStepUp.ChallengeFor("alice", TargetsA, "nonce-1");
        device.Counter = 1;
        var assertion = device.Get(challenge, PasskeyStepUp.UserHandle("alice"));
        var accepted = await Rp().Assert(assertion, Rp().AssertionOptions(challenge, [stored]), [stored], DateTimeOffset.UtcNow).Await(ct);
        Assert.True(accepted.Ok, accepted.Reason);
        Assert.Equal(1u, accepted.Credential!.SignCount);

        // The SAME assertion presented for another approval: its challenge is bound to other targets.
        var other = PasskeyStepUp.ChallengeFor("alice", TargetsB, "nonce-1");
        var refused = await Rp().Assert(assertion, Rp().AssertionOptions(other, [stored]), [stored], DateTimeOffset.UtcNow).Await(ct);
        Assert.False(refused.Ok);
        Assert.Equal("passkey", refused.Reason);
    }

    [Fact(Timeout = 60000)]
    public async Task ACounterThatDoesNotMoveForward_IsRefusedAsAClone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (device, stored) = await Enrolled(ct);
        var challenge = PasskeyStepUp.ChallengeFor("alice", TargetsA, "nonce-2");
        var afterFive = stored with { SignCount = 5 };

        device.Counter = 5;   // the stored counter is 5 already
        var replayed = await Rp().Assert(device.Get(challenge, PasskeyStepUp.UserHandle("alice")),
            Rp().AssertionOptions(challenge, [afterFive]), [afterFive], DateTimeOffset.UtcNow).Await(ct);
        Assert.False(replayed.Ok);
        Assert.Equal("counter", replayed.Reason);

        device.Counter = 6;   // negative control: a counter that moved forward passes
        var moved = await Rp().Assert(device.Get(challenge, PasskeyStepUp.UserHandle("alice")),
            Rp().AssertionOptions(challenge, [afterFive]), [afterFive], DateTimeOffset.UtcNow).Await(ct);
        Assert.True(moved.Ok, moved.Reason);
    }

    [Fact(Timeout = 60000)]
    public async Task AnAssertionWithoutUserVerification_IsRefused_AndAnUnknownCredentialToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (device, stored) = await Enrolled(ct);
        var challenge = PasskeyStepUp.ChallengeFor("alice", TargetsA, "nonce-3");
        device.Counter = 1;

        var noUv = await Rp().Assert(device.Get(challenge, PasskeyStepUp.UserHandle("alice"), userVerified: false),
            Rp().AssertionOptions(challenge, [stored]), [stored], DateTimeOffset.UtcNow).Await(ct);
        Assert.False(noUv.Ok);

        var stranger = new SoftAuthenticator { Counter = 1 };
        var unknown = await Rp().Assert(stranger.Get(challenge, PasskeyStepUp.UserHandle("alice")),
            Rp().AssertionOptions(challenge, [stored]), [stored], DateTimeOffset.UtcNow).Await(ct);
        Assert.False(unknown.Ok);
        Assert.Equal("unknownCredential", unknown.Reason);
    }
}
