using System.Net;

namespace MeshWeaver.GitSync;

/// <summary>
/// GitHub's REST API answered a request with a non-success status. Thrown by
/// <see cref="OctokitGitHubRepoClient"/> (through its <see cref="IGitHubRepoClient"/> observables) in
/// place of the Octokit exception family the client used before it was ported to a plain
/// <see cref="HttpClient"/>.
///
/// <para>Classify by <see cref="StatusCode"/> or by the derived type, never by message text. The
/// <see cref="Exception.Message"/> is GitHub's own <c>message</c> from the error body when it sent one
/// (<c>Not Found</c>, <c>Bad credentials</c>, <c>Resource not accessible by integration</c>,
/// <c>Git Repository is empty.</c>) — the same sentence Octokit surfaced, so a log or a text match
/// written against Octokit's output still reads the same words.</para>
///
/// <para>🚨 Deliberately NOT an <see cref="HttpRequestException"/>. Generic transport classifiers
/// treat that type as a network fault worth retrying; a GitHub 404 or 422 is an ANSWER, and
/// Octokit's <c>ApiException</c> (a plain <see cref="Exception"/>) never reached those classifiers
/// either. A real transport failure still surfaces as the <see cref="HttpRequestException"/>
/// <see cref="HttpClient"/> throws.</para>
/// </summary>
public class GitHubApiException : Exception
{
    /// <summary>Creates the exception for a GitHub answer.</summary>
    /// <param name="statusCode">The HTTP status GitHub answered with.</param>
    /// <param name="message">GitHub's error <c>message</c>, or a description of the status when the body carried none.</param>
    /// <param name="documentationUrl">GitHub's <c>documentation_url</c> from the error body, when present.</param>
    public GitHubApiException(HttpStatusCode statusCode, string message, string? documentationUrl = null)
        : base(message)
    {
        StatusCode = statusCode;
        DocumentationUrl = documentationUrl;
    }

    /// <summary>The HTTP status GitHub answered with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>GitHub's <c>documentation_url</c> from the error body; null when it sent none.</summary>
    public string? DocumentationUrl { get; }
}

/// <summary>HTTP 401 — GitHub rejected the credential (<c>Bad credentials</c>): a token that was
/// presented and judged. A token that could never be MINTED is a
/// <see cref="GitHubAppTokenMintException"/> instead (#4736).</summary>
/// <param name="message">GitHub's error message.</param>
/// <param name="documentationUrl">GitHub's documentation link, when present.</param>
public class GitHubAuthorizationException(string message = "Bad credentials", string? documentationUrl = null)
    : GitHubApiException(HttpStatusCode.Unauthorized, message, documentationUrl);

/// <summary>HTTP 403 — the credential is valid but not allowed this resource
/// (<c>Resource not accessible by integration</c>), and it is not a rate limit.</summary>
/// <param name="message">GitHub's error message.</param>
/// <param name="documentationUrl">GitHub's documentation link, when present.</param>
public class GitHubForbiddenException(string message = "Forbidden", string? documentationUrl = null)
    : GitHubApiException(HttpStatusCode.Forbidden, message, documentationUrl);

/// <summary>HTTP 403 or 429 carrying a rate-limit verdict (an exhausted primary budget —
/// <c>x-ratelimit-remaining: 0</c> — or GitHub's own "rate limit" message for a secondary limit).
/// <see cref="GitHubApiException.StatusCode"/> keeps the status GitHub actually sent.</summary>
/// <param name="statusCode">403 or 429, as GitHub answered.</param>
/// <param name="message">GitHub's error message.</param>
/// <param name="reset">When the primary budget resets (<c>x-ratelimit-reset</c>), when GitHub said.</param>
/// <param name="documentationUrl">GitHub's documentation link, when present.</param>
public class GitHubRateLimitExceededException(
    HttpStatusCode statusCode, string message, DateTimeOffset? reset = null, string? documentationUrl = null)
    : GitHubApiException(statusCode, message, documentationUrl)
{
    /// <summary>When the primary rate-limit budget resets; null when GitHub did not say.</summary>
    public DateTimeOffset? Reset { get; } = reset;
}

/// <summary>HTTP 404 — no such resource, OR one this credential cannot see: GitHub answers 404, not
/// 403, for a private repository the caller has no access to.</summary>
/// <param name="message">GitHub's error message.</param>
/// <param name="documentationUrl">GitHub's documentation link, when present.</param>
public class GitHubNotFoundException(string message = "Not Found", string? documentationUrl = null)
    : GitHubApiException(HttpStatusCode.NotFound, message, documentationUrl);

/// <summary>HTTP 422 — GitHub refused the request body (<c>Validation Failed</c>, e.g. a reference
/// that already exists or a pull request with no commits between head and base).</summary>
/// <param name="message">GitHub's error message.</param>
/// <param name="errors">The <c>errors</c> array of the body, verbatim JSON; null when absent.</param>
/// <param name="documentationUrl">GitHub's documentation link, when present.</param>
public class GitHubValidationException(string message, string? errors = null, string? documentationUrl = null)
    : GitHubApiException(HttpStatusCode.UnprocessableEntity, message, documentationUrl)
{
    /// <summary>GitHub's <c>errors</c> array as raw JSON — the field-level reasons; null when absent.</summary>
    public string? Errors { get; } = errors;
}
