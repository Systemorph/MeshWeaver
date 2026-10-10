using System.Globalization;
using System.Security.Claims;
using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Authentication;

/// <summary>What one Entra step-up <c>id_token</c> must satisfy beyond its signature, audience and lifetime.</summary>
/// <param name="Issuer">The tenant-specific issuer (<c>https://login.microsoftonline.com/{tid}/v2.0</c>).</param>
/// <param name="Nonce">The nonce of THIS pending step-up.</param>
/// <param name="SessionOid">The session's Entra object id, when the session carries one.</param>
/// <param name="SessionAccount">The session's account (email) — the subject check for a session without <c>oid</c>.</param>
/// <param name="Options">The instance's step-up options (context, amr policy, max auth age).</param>
/// <param name="Now">The instant of the check.</param>
public sealed record EntraStepUpExpectation(
    string Issuer, string Nonce, string? SessionOid, string? SessionAccount, StepUpOptions Options, DateTimeOffset Now);

/// <summary>The verdict on one step-up token.</summary>
/// <param name="Ok">True when every check passed.</param>
/// <param name="Reason">The first failed check (<c>issuer</c>, <c>nonce</c>, <c>subject</c>, <c>auth_time</c>, <c>acrs</c>, <c>amr</c>, <c>context</c>), or null.</param>
/// <param name="AuthenticatedAt">The token's <c>auth_time</c>.</param>
/// <param name="Evidence">What was verified, for the receipt — never a secret.</param>
public sealed record EntraStepUpCheck(bool Ok, string? Reason, DateTimeOffset AuthenticatedAt, string? Evidence)
{
    internal static EntraStepUpCheck Fail(string reason) => new(false, reason, default, null);
}

/// <summary>
/// The pure half of the Entra step-up rung: given the claims of an <c>id_token</c> whose signature,
/// audience and lifetime the handler has ALREADY validated, decide whether it proves a fresh,
/// phishing-resistant authentication of THIS session's account for THIS step-up. Every rule is in
/// the table on <c>Doc/Architecture/ApprovalStepUp</c> → "The Entra rung".
/// </summary>
public static class EntraStepUpTokenCheck
{
    /// <summary>Evaluates the token claims against the expectation.</summary>
    /// <param name="claims">The validated token's claims (a multi-valued claim appears once per value).</param>
    /// <param name="expected">What this step-up requires.</param>
    /// <returns>The verdict.</returns>
    public static EntraStepUpCheck Evaluate(IReadOnlyCollection<Claim> claims, EntraStepUpExpectation expected)
    {
        var context = expected.Options.EntraAuthenticationContext;
        if (string.IsNullOrEmpty(context))
            return EntraStepUpCheck.Fail("context");

        string? One(string type) => claims.FirstOrDefault(c => c.Type == type)?.Value;
        IEnumerable<string> Many(string type) => claims.Where(c => c.Type == type)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var iss = One("iss");
        if (!string.Equals(iss, expected.Issuer, StringComparison.Ordinal))
            return EntraStepUpCheck.Fail("issuer");

        if (!string.Equals(One("nonce"), expected.Nonce, StringComparison.Ordinal))
            return EntraStepUpCheck.Fail("nonce");

        var oid = One("oid");
        if (!string.IsNullOrEmpty(expected.SessionOid))
        {
            if (!string.Equals(oid, expected.SessionOid, StringComparison.OrdinalIgnoreCase))
                return EntraStepUpCheck.Fail("subject");
        }
        else
        {
            var account = One("preferred_username") ?? One("email");
            if (string.IsNullOrEmpty(expected.SessionAccount)
                || !string.Equals(account, expected.SessionAccount, StringComparison.OrdinalIgnoreCase))
                return EntraStepUpCheck.Fail("subject");
        }

        if (!long.TryParse(One("auth_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var authSeconds))
            return EntraStepUpCheck.Fail("auth_time");
        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authSeconds);
        var age = expected.Now - authenticatedAt;
        // A small negative age is clock skew between Entra and this host; a large one is a token
        // from the future and as wrong as a stale one.
        if (age > expected.Options.MaxAuthAge || age < -TimeSpan.FromMinutes(2))
            return EntraStepUpCheck.Fail("auth_time");

        var acrs = Many("acrs").ToList();
        if (!acrs.Contains(context, StringComparer.OrdinalIgnoreCase))
            return EntraStepUpCheck.Fail("acrs");

        var amr = Many("amr").ToList();
        if (amr.Count > 0 && !amr.Any(expected.Options.EntraPhishingResistantAmr.Contains))
            return EntraStepUpCheck.Fail("amr");
        if (amr.Count == 0 && expected.Options.EntraRequireAmr)
            return EntraStepUpCheck.Fail("amr");

        var evidence = $"acrs={string.Join(',', acrs)} amr={(amr.Count == 0 ? "-" : string.Join(',', amr))} tid={One("tid")} oid={oid}";
        return new EntraStepUpCheck(true, null, authenticatedAt, evidence);
    }
}
