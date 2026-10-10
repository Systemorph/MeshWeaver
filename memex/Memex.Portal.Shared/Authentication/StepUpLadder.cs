using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Authentication;

/// <summary>Which step-up a session gets — the outcome of <see cref="StepUpLadder.Decide"/>.</summary>
public static class StepUpRung
{
    /// <summary>Step-up is off on this instance — go straight back.</summary>
    public const string NotRequired = "notRequired";

    /// <summary>Entra ID step-up (Microsoft accounts).</summary>
    public const string Entra = StepUpMethod.Entra;

    /// <summary>The session predates the <c>idp</c> claim — sign in again; the provider is never guessed.</summary>
    public const string RefuseUnknownSession = "unknownSession";

    /// <summary>A Microsoft account, but no authentication context (or no usable sign-in app / single tenant) is declared.</summary>
    public const string RefuseNotConfigured = "notConfigured";

    /// <summary>An account no rung can step up yet.</summary>
    public const string RefuseProvider = "provider";
}

/// <summary>
/// The pure ladder decision of <c>Doc/Architecture/ApprovalStepUp</c> → "The ladder": Microsoft
/// accounts step up through Entra; every other account through the portal's own passkey (and TOTP
/// where no passkey is possible). This first slice carries the Entra rung only, so a non-Microsoft
/// account is REFUSED with a clear reason — never waved through.
/// </summary>
public static class StepUpLadder
{
    /// <summary>Decides the rung for a session.</summary>
    /// <param name="provider">The session's <c>idp</c> claim, or null for an older session.</param>
    /// <param name="options">The instance's step-up options.</param>
    /// <param name="entraUsable">The Microsoft sign-in app is configured and its step-up tenant is a single tenant.</param>
    /// <returns>A <see cref="StepUpRung"/> value.</returns>
    public static string Decide(string? provider, StepUpOptions options, bool entraUsable)
    {
        if (!options.Enabled)
            return StepUpRung.NotRequired;
        if (string.IsNullOrEmpty(provider))
            return StepUpRung.RefuseUnknownSession;
        if (string.Equals(provider, StepUpClaims.MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(options.EntraAuthenticationContext) || !entraUsable
                ? StepUpRung.RefuseNotConfigured
                : StepUpRung.Entra;
        return StepUpRung.RefuseProvider;
    }
}
