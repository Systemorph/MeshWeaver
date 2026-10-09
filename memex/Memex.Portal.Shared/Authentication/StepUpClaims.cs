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
    public const string Idp = "idp";

    /// <summary>The Entra object id of a Microsoft account.</summary>
    public const string Oid = "oid";

    /// <summary>The Entra tenant id of a Microsoft account.</summary>
    public const string Tid = "tid";

    /// <summary>When this session signed in (Unix seconds) — kept through sliding renewal.</summary>
    public const string AuthTime = "auth_time";

    /// <summary>The scheme name of the Microsoft (Entra ID) sign-in.</summary>
    public const string MicrosoftProvider = "Microsoft";

    private const string MappedOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string MappedTid = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>
    /// The step-up claims to add to a freshly issued session for <paramref name="provider"/>, taken
    /// from the provider's own claims (the OIDC handler maps <c>oid</c>/<c>tid</c> to their long URI
    /// forms by default; both spellings are read).
    /// </summary>
    /// <param name="provider">The scheme name the user signed in with.</param>
    /// <param name="external">The provider's claims.</param>
    /// <param name="now">The sign-in instant.</param>
    /// <returns>The claims to add.</returns>
    public static IEnumerable<Claim> ForSession(string provider, IReadOnlyCollection<Claim> external, DateTimeOffset now)
    {
        yield return new Claim(Idp, provider);
        yield return new Claim(AuthTime, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var oid = external.FirstOrDefault(c => c.Type is Oid or MappedOid)?.Value;
        if (!string.IsNullOrEmpty(oid)) yield return new Claim(Oid, oid);
        var tid = external.FirstOrDefault(c => c.Type is Tid or MappedTid)?.Value;
        if (!string.IsNullOrEmpty(tid)) yield return new Claim(Tid, tid);
    }

    /// <summary>The provider the session signed in with, or null for a session issued before the claim existed.</summary>
    /// <param name="user">The session principal.</param>
    /// <returns>The scheme name or null.</returns>
    public static string? ProviderOf(ClaimsPrincipal user) => user.FindFirst(Idp)?.Value;
}
