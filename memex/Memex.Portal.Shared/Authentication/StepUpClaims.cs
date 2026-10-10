using System.Globalization;
using System.Security.Claims;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The claims the session cookie carries so an approval step-up knows WHICH ladder rung applies and
/// WHOSE token to accept (<c>Doc/Architecture/ApprovalStepUp</c>). Before these existed the cookie
/// recorded nothing about the provider — every sign-in was re-issued with name and email only — so a
/// Microsoft account and a Google account were indistinguishable after sign-in.
/// </summary>
public static class StepUpClaims
{
    /// <summary>The sign-in provider's scheme name (<c>Microsoft</c>, <c>Google</c>, <c>LinkedIn</c>, <c>Apple</c>, <c>GitHub</c>, <c>Dev</c>).</summary>
    public const string Idp = "mw_idp";

    /// <summary>The Entra object id of a Microsoft account.</summary>
    public const string Oid = "mw_oid";

    /// <summary>The Entra tenant id of a Microsoft account.</summary>
    public const string Tid = "mw_tid";

    /// <summary>When this session signed in (Unix seconds) — kept through sliding renewal.</summary>
    public const string AuthTime = "mw_auth_time";

    /// <summary>The scheme name of the Microsoft (Entra ID) sign-in.</summary>
    public const string MicrosoftProvider = "Microsoft";

    // The provider's own spellings: Entra's short names, and the long URIs the OIDC handler maps
    // them to by default. Our session claims carry an `mw_` prefix so a provider claim of the same
    // short name (Entra sends `idp` for guests, `auth_time` on request) can never pass for ours.
    private const string EntraOid = "oid";
    private const string EntraTid = "tid";
    private const string MappedOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string MappedTid = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>
    /// The step-up claims to add to a session being issued: the provider (when known), the sign-in
    /// time (when this IS a sign-in), and the Entra object/tenant ids taken from the provider's own
    /// claims (the OIDC handler maps <c>oid</c>/<c>tid</c> to their long URI forms by default; both
    /// spellings are read).
    /// </summary>
    /// <param name="provider">The scheme the user signed in with (<see cref="ResolveProvider"/>), or null when unknown.</param>
    /// <param name="external">The provider's (or the existing session's) claims.</param>
    /// <param name="authTime">When the user authenticated, or null to stamp none.</param>
    /// <returns>The claims to add.</returns>
    public static IEnumerable<Claim> ForSession(string? provider, IReadOnlyCollection<Claim> external, DateTimeOffset? authTime)
    {
        if (!string.IsNullOrEmpty(provider)) yield return new Claim(Idp, provider);
        if (authTime is { } at) yield return new Claim(AuthTime, at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var oid = external.FirstOrDefault(c => c.Type is Oid or EntraOid or MappedOid)?.Value;
        if (!string.IsNullOrEmpty(oid)) yield return new Claim(Oid, oid);
        var tid = external.FirstOrDefault(c => c.Type is Tid or EntraTid or MappedTid)?.Value;
        if (!string.IsNullOrEmpty(tid)) yield return new Claim(Tid, tid);
    }

    /// <summary>
    /// The provider a session REALLY signed in with — from the authentication ticket the challenged
    /// scheme produced (the <c>provider</c> item the sign-in challenge set), else from the provider
    /// the existing session already records. NEVER from the callback route: a signed-in Google user
    /// who opens <c>/auth/callback/Microsoft</c> must not become a Microsoft session.
    /// </summary>
    /// <param name="ticketProvider">The ticket's <c>provider</c> item, present right after a remote sign-in.</param>
    /// <param name="sessionClaims">The claims of the principal being re-issued.</param>
    /// <returns>The provider, or null when nothing trustworthy names one.</returns>
    public static string? ResolveProvider(string? ticketProvider, IEnumerable<Claim> sessionClaims) =>
        !string.IsNullOrEmpty(ticketProvider) ? ticketProvider
            : sessionClaims.FirstOrDefault(c => c.Type == Idp)?.Value;

    /// <summary>The provider the session signed in with, or null for a session issued before the claim existed.</summary>
    /// <param name="user">The session principal.</param>
    /// <returns>The scheme name or null.</returns>
    public static string? ProviderOf(ClaimsPrincipal user) => user.FindFirst(Idp)?.Value;
}
