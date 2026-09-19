using System.Security.Cryptography;
using System.Text;

namespace Memex.Portal.Shared.Authentication;

/// <summary>What the bootstrap gate decided about one caller.</summary>
public enum BootstrapAuthResult
{
    /// <summary>No <c>Bootstrap:Secret</c> is configured — the endpoints do not exist.</summary>
    Disabled,

    /// <summary>A secret is configured and the caller presented none.</summary>
    Missing,

    /// <summary>A secret is configured and the caller presented a different one.</summary>
    Invalid,

    /// <summary>The caller presented the configured secret.</summary>
    Accepted,
}

/// <summary>
/// The <c>Bootstrap:Secret</c> check, as a PURE function — the one authority in front of
/// <see cref="BootstrapController"/>, which materialises the first platform administrator of an
/// instance that has none.
///
/// <para><b>Why it is extracted.</b> The comparison was written twice in the controller, once per
/// endpoint, and both copies compared with <c>string.Equals</c>. Two copies of a security decision
/// is the shape that lets one of them get fixed alone — the reason
/// <c>OnboardingGate.Decide</c> exists as a pure function too — and this one now has properties
/// worth asserting without an HTTP context.</para>
///
/// <para>🚨 <b>Constant time, deliberately.</b> <c>string.Equals</c> returns at the first differing
/// character, so the time it takes is a function of how much of the secret the caller got right.
/// This endpoint is anonymous-reachable by design (there is no admin yet to authorise it), so that
/// oracle is offered to the whole internet, on the one door that grants platform admin.
/// <see cref="FixedTimeEquals"/> compares the full length either way.</para>
///
/// <para>🚨 <b>The secret belongs in a HEADER, not a query string.</b> Every proxy on the path logs
/// a URL: this fleet's ingress ships its access logs to Loki, and a secret in a query parameter is
/// therefore a secret in a log store, readable by everyone who may read logs. The query form still
/// works — the e2e stack and scripted scaffolds use it — and the caller is warned. See
/// <c>Doc/Architecture/FirstRunSetupOnAProvisionedInstance</c>.</para>
/// </summary>
public static class BootstrapSecretGate
{
    /// <summary>The header the secret should be presented in.</summary>
    public const string HeaderName = "X-Bootstrap-Secret";

    /// <summary>
    /// Decides whether a caller may use the bootstrap endpoints.
    /// </summary>
    /// <param name="configured">The <c>Bootstrap:Secret</c> value. Null, empty or whitespace ⇒ <see cref="BootstrapAuthResult.Disabled"/>.</param>
    /// <param name="presented">What the caller presented, from the header or the query. Null or empty ⇒ <see cref="BootstrapAuthResult.Missing"/>.</param>
    public static BootstrapAuthResult Decide(string? configured, string? presented)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return BootstrapAuthResult.Disabled;
        if (string.IsNullOrEmpty(presented))
            return BootstrapAuthResult.Missing;
        return FixedTimeEquals(configured, presented)
            ? BootstrapAuthResult.Accepted
            : BootstrapAuthResult.Invalid;
    }

    /// <summary>
    /// Which of the two forms the caller used, for the warning. The header wins when both are
    /// present, so a caller that has moved to the header is not scolded for a stale script that
    /// still appends the query parameter.
    /// </summary>
    /// <param name="header">The header value, or null.</param>
    /// <param name="query">The query value, or null.</param>
    /// <returns>The value to check, and whether it came from the query string.</returns>
    public static (string? Presented, bool FromQuery) Present(string? header, string? query) =>
        !string.IsNullOrEmpty(header) ? (header, false) : (query, !string.IsNullOrEmpty(query));

    /// <summary>
    /// Ordinal equality in time independent of WHERE the two differ.
    ///
    /// <para>Length is not secret here — it leaks through the comparison of any implementation, and
    /// a length mismatch is answered without a content comparison. What must not leak is the
    /// position of the first differing byte, which is what a short-circuiting compare reveals.</para>
    /// </summary>
    public static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
