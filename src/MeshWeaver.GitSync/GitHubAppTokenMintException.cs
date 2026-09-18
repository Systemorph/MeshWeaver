namespace MeshWeaver.GitSync;

/// <summary>
/// How far a GitHub App installation-token mint got before it stopped — the part of a failed
/// GitHub read that <see cref="GitHubAppTokenMintException"/> exists to name.
///
/// <para>🚨 The distinction the stages draw is the one #4736 could not draw: "the App installation
/// is gone" (<see cref="InstallationDiscovery"/>, <see cref="TokenExchange"/>) and "the mint itself
/// is broken" (<see cref="Signing"/>, <see cref="Response"/>, <see cref="Transport"/>) are
/// different operator actions, and both used to arrive as the same untyped
/// <see cref="InvalidOperationException"/>.</para>
/// </summary>
public enum GitHubAppTokenMintStage
{
    /// <summary>No App identity is configured at all (<c>GitHub:App:ClientId</c> +
    /// <c>GitHub:App:PrivateKey</c>). A deployment CHOICE on a consumer instance, a misconfiguration
    /// on the registry — never a credential GitHub rejected, because none was presented.</summary>
    NotConfigured,

    /// <summary>The App JWT could not be signed: the configured private key is absent, truncated or
    /// not a usable RSA key. Nothing reached GitHub.</summary>
    Signing,

    /// <summary>Discovering the installation (<c>GET /app/installations</c>) failed, or the App has
    /// no installation at all — it was uninstalled from the organization, or its credentials no
    /// longer identify it.</summary>
    InstallationDiscovery,

    /// <summary>The exchange (<c>POST /app/installations/{id}/access_tokens</c>) was refused: the
    /// App's own credentials are rejected, or the installation is suspended or removed. This is the
    /// nearest stage to "revoked" — and it is still NOT the same event as a minted token being
    /// rejected by a repository read.</summary>
    TokenExchange,

    /// <summary>GitHub accepted the exchange but the body carried no usable token — a response
    /// shape this code cannot read.</summary>
    Response,

    /// <summary>The mint never reached a verdict: a transport fault, a DNS failure, a malformed
    /// response body. Nothing is known about the credential itself.</summary>
    Transport,
}

/// <summary>
/// A GitHub App installation token could NOT BE MINTED. The credential was never obtained, so it
/// was never presented and GitHub never judged it.
///
/// <para>🚨 It exists as a TYPE so a caller can tell a FAILED MINT from a REJECTED CREDENTIAL
/// without matching on message text (#4736). Those are the two halves of "GitHub said no" and they
/// look identical from a log: a mint failure that degrades to an anonymous fetch surfaces one layer
/// later as Octokit's <c>AuthorizationException: Bad credentials</c> — a sentence that describes a
/// credential GitHub REJECTED and therefore sends the reader to the App's permissions, when the
/// real event was upstream and one layer earlier. A rejected credential is an
/// <c>Octokit.AuthorizationException</c>; a credential that never existed is this.</para>
///
/// <para>Derived from <see cref="InvalidOperationException"/>, which is what every one of these
/// paths threw before, so each existing catch behaves identically.</para>
///
/// <para>🚨 It NEVER carries a secret: not the App private key, not the signed JWT, not the minted
/// token. <see cref="Stage"/>, <see cref="StatusCode"/> and GitHub's own error body are what a
/// reader gets — verdicts and lengths, never values.</para>
/// </summary>
/// <param name="stage">How far the mint got — see <see cref="GitHubAppTokenMintStage"/>.</param>
/// <param name="message">What could not be done, in terms an operator can act on.</param>
/// <param name="innerException">The underlying fault, when the mint failed on one.</param>
public sealed class GitHubAppTokenMintException(
    GitHubAppTokenMintStage stage,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>How far the mint got before it stopped.</summary>
    public GitHubAppTokenMintStage Stage { get; } = stage;

    /// <summary>GitHub's HTTP status for the refusal, when the stage reached GitHub at all
    /// (<see cref="GitHubAppTokenMintStage.InstallationDiscovery"/>,
    /// <see cref="GitHubAppTokenMintStage.TokenExchange"/>); null otherwise.</summary>
    public int? StatusCode { get; init; }
}
