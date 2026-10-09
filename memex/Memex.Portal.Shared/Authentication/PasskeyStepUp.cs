using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Authentication;

/// <summary>What a passkey ceremony produced.</summary>
/// <param name="Ok">True when the server verified it.</param>
/// <param name="Reason">Why not (<c>passkey</c>, <c>unknownCredential</c>, <c>counter</c>), or null.</param>
/// <param name="Credential">The credential as it must be stored now (new, or with its counter moved).</param>
public sealed record PasskeyResult(bool Ok, string? Reason, PasskeyCredential? Credential)
{
    internal static PasskeyResult Fail(string reason) => new(false, reason, null);
}

/// <summary>
/// The passkey rung of the approval step-up (<c>Doc/Architecture/ApprovalStepUp</c>): WebAuthn
/// registration and assertion, verified by the maintained FIDO2 library (<c>Fido2NetLib</c>) —
/// origin, RP id, challenge, signature, user verification and the signature counter. The
/// assertion challenge is DERIVED from the pending step-up (user, targets, nonce), so an assertion
/// made for one approval can never confirm another.
/// </summary>
/// <param name="rpId">The relying-party id — the portal's host name.</param>
/// <param name="origin">The portal's origin (<c>https://host</c>).</param>
/// <param name="rpName">The instance name shown by the authenticator.</param>
internal sealed class PasskeyStepUp(string rpId, string origin, string rpName)
{
    private Fido2Configuration Config => new()
    {
        RPID = rpId,
        RPName = rpName,
        Origins = new HashSet<string>(StringComparer.Ordinal) { origin },
    };

    private Fido2 Fido => new(Config, null!);

    /// <summary>The challenge bound to one pending step-up: SHA-256 over the user, every target and the nonce.</summary>
    /// <param name="userId">The approver.</param>
    /// <param name="targets">The targets.</param>
    /// <param name="nonce">The pending step-up's nonce.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] ChallengeFor(string userId, IEnumerable<StepUpTarget> targets, string nonce)
    {
        var sb = new StringBuilder("MeshWeaver.StepUp.Passkey.v1\n").Append(userId).Append('\n').Append(nonce).Append('\n');
        foreach (var t in targets) sb.Append(t.ActionPath).Append('\u001f').Append(t.Binding).Append('\n');
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>The user handle a user's passkeys are created for: SHA-256 of the mesh id (no personal data on the authenticator's handle).</summary>
    /// <param name="userId">The user.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] UserHandle(string userId) => SHA256.HashData(Encoding.UTF8.GetBytes("MeshWeaver.User\n" + userId));

    /// <summary>Registration options for a new passkey (user verification required, existing ones excluded).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="displayName">How the authenticator labels the account.</param>
    /// <param name="existing">Already enrolled credentials.</param>
    /// <returns>The options (JSON via <c>ToJson()</c>).</returns>
    public CredentialCreateOptions RegistrationOptions(string userId, string displayName, IEnumerable<PasskeyCredential> existing) =>
        Fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = UserHandle(userId), Name = displayName, DisplayName = displayName },
            ExcludeCredentials = existing.Select(c => new PublicKeyCredentialDescriptor(Base64Url.Decode(c.CredentialId))).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

    /// <summary>Verifies a registration response against the options it answers.</summary>
    /// <param name="responseJson">The browser's attestation response.</param>
    /// <param name="options">The options issued for it.</param>
    /// <param name="existing">Already enrolled credentials (an id already present is refused).</param>
    /// <param name="now">The clock.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The credential to store.</returns>
    public async Task<PasskeyResult> Register(string responseJson, CredentialCreateOptions options,
        IReadOnlyCollection<PasskeyCredential> existing, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(responseJson);
            if (response is null) return PasskeyResult.Fail("passkey");
            var known = existing.Select(c => c.CredentialId).ToImmutableHashSet(StringComparer.Ordinal);
            var registered = await Fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = (p, _) => Task.FromResult(!known.Contains(Base64Url.Encode(p.CredentialId))),
            }, ct).ConfigureAwait(false);
            return new PasskeyResult(true, null, new PasskeyCredential
            {
                CredentialId = Base64Url.Encode(registered.Id),
                PublicKey = Convert.ToBase64String(registered.PublicKey),
                UserHandle = Base64Url.Encode(registered.User.Id),
                SignCount = registered.SignCount,
                AaGuid = registered.AaGuid,
                CreatedAt = now,
            });
        }
        catch (Fido2VerificationException)
        {
            return PasskeyResult.Fail("passkey");
        }
        catch (System.Text.Json.JsonException)
        {
            return PasskeyResult.Fail("passkey");
        }
    }

    /// <summary>Assertion options for a pending step-up — the challenge is <see cref="ChallengeFor"/>.</summary>
    /// <param name="challenge">The bound challenge.</param>
    /// <param name="credentials">The user's passkeys.</param>
    /// <returns>The options.</returns>
    public AssertionOptions AssertionOptions(byte[] challenge, IEnumerable<PasskeyCredential> credentials) =>
        Fido2NetLib.AssertionOptions.Create(Config, challenge,
            credentials.Select(c => new PublicKeyCredentialDescriptor(Base64Url.Decode(c.CredentialId))).ToList(),
            UserVerificationRequirement.Required, null);

    /// <summary>Verifies an assertion for the pending step-up.</summary>
    /// <param name="responseJson">The browser's assertion response.</param>
    /// <param name="options">The options (with the bound challenge) it answers.</param>
    /// <param name="credentials">The user's passkeys.</param>
    /// <param name="now">The clock.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The used credential with its counter moved.</returns>
    public async Task<PasskeyResult> Assert(string responseJson, AssertionOptions options,
        IReadOnlyCollection<PasskeyCredential> credentials, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson);
            if (response is null) return PasskeyResult.Fail("passkey");
            var id = Base64Url.Encode(response.RawId);
            var stored = credentials.FirstOrDefault(c => c.CredentialId == id);
            if (stored is null) return PasskeyResult.Fail("unknownCredential");
            var verified = await Fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = Convert.FromBase64String(stored.PublicKey),
                StoredSignatureCounter = stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(
                    p.UserHandle is null || Base64Url.Encode(p.UserHandle) == stored.UserHandle),
            }, ct).ConfigureAwait(false);
            return new PasskeyResult(true, null, stored with { SignCount = verified.SignCount, LastUsedAt = now });
        }
        catch (Fido2VerificationException ex) when (ex.Code == Fido2ErrorCode.InvalidSignCount)
        {
            return PasskeyResult.Fail("counter");
        }
        catch (Fido2VerificationException)
        {
            return PasskeyResult.Fail("passkey");
        }
        catch (System.Text.Json.JsonException)
        {
            return PasskeyResult.Fail("passkey");
        }
    }
}

/// <summary>RFC 4648 §5 base64url without padding.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
