using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Authentication;

/// <summary>Which step-up a session gets — the outcome of <see cref="StepUpLadder.Decide"/>.</summary>
public static class StepUpRung
{
    /// <summary>Step-up is off on this instance — go straight back.</summary>
    public const string NotRequired = "notRequired";

    /// <summary>Entra ID step-up (Microsoft accounts).</summary>
    public const string Entra = StepUpMethod.Entra;

    /// <summary>The portal's own passkey (every other account, once one is enrolled).</summary>
    public const string Passkey = StepUpMethod.Passkey;

    /// <summary>The portal's own TOTP — only for an account with NO passkey and an authenticator app enrolled.</summary>
    public const string Totp = StepUpMethod.Totp;

    /// <summary>No factor enrolled yet — enrol one (a passkey preferred) before approving.</summary>
    public const string Enroll = "enroll";

    /// <summary>The session predates the <c>idp</c> claim — sign in again; the provider is never guessed.</summary>
    public const string RefuseUnknownSession = "unknownSession";

    /// <summary>A Microsoft account, but no authentication context (or no usable sign-in app / single tenant) is declared.</summary>
    public const string RefuseNotConfigured = "notConfigured";

    /// <summary>An account no rung can step up.</summary>
    public const string RefuseProvider = "provider";
}

/// <summary>
/// The pure ladder decision of <c>Doc/Architecture/ApprovalStepUp</c> → "The ladder", policy
/// <c>approval-step-up</c>: <i>always prefer a passkey, use a second factor only where no passkey is
/// available</i>. Microsoft accounts step up through Entra (whose authentication strength requires a
/// phishing-resistant passkey) and never through the portal's own factors as well — one prompt, not
/// two. Every other account steps up with its portal passkey; TOTP is the rung ONLY for an account
/// that has no passkey at all — so a TOTP code can never be used to downgrade an account that has one.
/// No rung ever waves an approval through.
/// </summary>
public static class StepUpLadder
{
    /// <summary>Decides the rung for a session.</summary>
    /// <param name="provider">The session's <c>idp</c> claim, or null for an older session.</param>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="entraUsable">The Microsoft sign-in app is configured and its step-up tenant is a single tenant.</param>
    /// <param name="factors">The account's portal-held factors, or null when it has none.</param>
    /// <returns>A <see cref="StepUpRung"/> value.</returns>
    public static string Decide(string? provider, StepUpOptions options, bool entraUsable, StepUpFactors? factors)
    {
        if (!options.Enabled)
            return StepUpRung.NotRequired;
        if (string.IsNullOrEmpty(provider))
            return StepUpRung.RefuseUnknownSession;
        if (IsMicrosoft(provider))
            return string.IsNullOrEmpty(options.EntraAuthenticationContext) || !entraUsable
                ? StepUpRung.RefuseNotConfigured
                : StepUpRung.Entra;
        return PortalRung(options, factors);
    }

    /// <summary>The rung among the portal's own factors — also what enrolling another factor steps up with.</summary>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="factors">The account's factors, or null.</param>
    /// <returns><see cref="StepUpRung.Passkey"/>, <see cref="StepUpRung.Totp"/> or <see cref="StepUpRung.Enroll"/>.</returns>
    public static string PortalRung(StepUpOptions options, StepUpFactors? factors)
    {
        if (factors is { Passkeys.Count: > 0 })
            return StepUpRung.Passkey;
        if (factors is { TotpConfirmedAt: not null } && options.AllowTotpFallback)
            return StepUpRung.Totp;
        return StepUpRung.Enroll;
    }

    /// <summary>
    /// Whether a TOTP code may confirm a step-up for this account — re-asked at verification, never
    /// trusted from the page: only with no passkey, the rung allowed, and an authenticator enrolled.
    /// </summary>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="factors">The account's factors.</param>
    /// <returns>True when TOTP is this account's rung.</returns>
    public static bool AcceptsTotp(StepUpOptions options, StepUpFactors? factors) =>
        PortalRung(options, factors) == StepUpRung.Totp;

    /// <summary>
    /// Whether TOTP may be ENROLLED: the rung exists and the account has no passkey. (Whether the
    /// device can do a passkey is the browser's to report; the page offers TOTP only when it cannot.)
    /// </summary>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="factors">The account's factors.</param>
    /// <returns>True when an authenticator app may be set up.</returns>
    public static bool MayEnrollTotp(StepUpOptions options, StepUpFactors? factors) =>
        options.AllowTotpFallback && factors is not { Passkeys.Count: > 0 };

    /// <summary>
    /// Whether this session may enrol a PORTAL factor (passkey or TOTP) at all. A Microsoft account
    /// steps up through Entra and nothing else, so a portal factor would be dead weight at best and,
    /// at worst, a second way in that bypasses Entra's phishing-resistant prompt. A session that
    /// predates the provider claim is refused too: the provider is never guessed.
    /// </summary>
    /// <param name="provider">The session's <c>idp</c> claim, or null for an older session.</param>
    /// <returns>True only for a known, non-Microsoft provider.</returns>
    public static bool MayEnrollPortalFactors(string? provider) =>
        !string.IsNullOrEmpty(provider) && !IsMicrosoft(provider);

    /// <summary>
    /// Whether <paramref name="method"/> may COMPLETE a pending step-up: the rung the server decided
    /// when the step-up started must be this method, AND the ladder decided again now — on the
    /// session's provider and the account's factors as they are now — must still land on it. Every
    /// completing endpoint asks this; none trusts that the page it served is the page that called.
    /// </summary>
    /// <param name="pendingRung">The rung recorded on the pending step-up.</param>
    /// <param name="method">The method the calling endpoint verifies (a <see cref="StepUpMethod"/> value).</param>
    /// <param name="provider">The session's <c>idp</c> claim.</param>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="entraUsable">The Microsoft sign-in app is configured for a single tenant.</param>
    /// <param name="factors">The account's portal factors as read now.</param>
    /// <returns>True only when both agree on <paramref name="method"/>.</returns>
    public static bool MayComplete(string? pendingRung, string method, string? provider, StepUpOptions options,
        bool entraUsable, StepUpFactors? factors) =>
        string.Equals(pendingRung, method, StringComparison.Ordinal)
        && string.Equals(Decide(provider, options, entraUsable, factors), method, StringComparison.Ordinal);

    /// <summary>True when <paramref name="provider"/> is the Microsoft (Entra ID) sign-in.</summary>
    /// <param name="provider">The session's <c>idp</c> claim.</param>
    /// <returns>True for a Microsoft account.</returns>
    public static bool IsMicrosoft(string? provider) =>
        string.Equals(provider, StepUpClaims.MicrosoftProvider, StringComparison.OrdinalIgnoreCase);
}
