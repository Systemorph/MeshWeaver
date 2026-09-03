using System.Security.Cryptography;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The one-time token that gates the first-run wizard.
///
/// <para>🚨 <b>The setup surface is UNAUTHENTICATED by construction, and that is a hole unless
/// something closes it.</b> It runs before any storage exists, so there is no user store to
/// authenticate against — and what it collects is a database connection string, provider API keys
/// and the list of ids that become platform administrators. An open form offering all of that to
/// whoever reaches the port first is not a theoretical exposure: a fresh instance is reachable the
/// moment its ingress resolves, and the person who set it up is not necessarily the first person
/// to load it.</para>
///
/// <para>So the instance mints a token at startup and writes it to its own log, where only someone
/// who can already read the instance's output can see it — the same proof-of-access every local
/// notebook server uses. <c>memex-local</c> reads it back out of the pod log and opens the browser
/// with it; an operator on a server reads it from <c>kubectl logs</c> or <c>docker logs</c>.</para>
///
/// <para><b>Per process, and deliberately not persisted.</b> A restart mints a new one, which is
/// the correct behaviour: the token's whole meaning is "you can see this instance's console right
/// now". Persisting it would turn a proof of present access into a durable credential on the same
/// disk as everything it protects.</para>
/// </summary>
public sealed class SetupAccessToken
{
    /// <summary>The token this process minted.</summary>
    public string Value { get; }

    /// <summary>Mints a fresh token. One per process.</summary>
    public SetupAccessToken()
        // URL-safe: it travels as a query parameter when the installer opens the browser for you.
        => Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="candidate"/> is this instance's token.
    ///
    /// <para>Fixed-time comparison: the form can be submitted repeatedly, so an early-exit compare
    /// would leak the token a character at a time to anyone patient enough to measure.</para>
    /// </summary>
    /// <param name="candidate">What the form submitted. Null or blank never matches.</param>
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var expected = System.Text.Encoding.UTF8.GetBytes(Value);
        var actual = System.Text.Encoding.UTF8.GetBytes(candidate.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// The line written to the instance's console so an operator can find the token.
    ///
    /// <para>The URL is included whole because the alternative — "open the portal and paste this" —
    /// is where people paste it into the wrong field. Not a secret in the log's sense: the log is
    /// already the trust boundary this token is defined by.</para>
    /// </summary>
    /// <param name="baseUrl">The address the instance is reachable at, when it is known.</param>
    public string ConsoleBanner(string? baseUrl) =>
        $"""

        ╭──────────────────────────────────────────────────────────────────────────╮
        │  This instance has no database yet and is serving the SETUP wizard only. │
        ╰──────────────────────────────────────────────────────────────────────────╯
          Open:  {(string.IsNullOrWhiteSpace(baseUrl) ? "<this instance>" : baseUrl.TrimEnd('/'))}/setup?token={Value}
          Token: {Value}

        """;
}
