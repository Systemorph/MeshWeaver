using System.Globalization;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The ONE answer every instance-key-gated endpoint gives when the registry could not FIND OUT
/// whether a presented key is valid (#2695).
///
/// <para>🚨 It is not a 401, and the difference is the caller's entire next move. A 401 says "your
/// key is unknown", which sends an operator hunting for a missing registration or grant — precisely
/// the wrong hunt when the grant was there all along and one read was slow. That is not
/// hypothetical: MeshWeaver.Crm's gate (run 33269921011) read a transient 401 as "this instance
/// needs a whole-source grant" while `Admin/_PluginGrant/ci-crm` sat unchanged, and a re-run minutes
/// later passed with nothing altered anywhere.</para>
///
/// <para>Shared rather than copied into each endpoint on purpose: four endpoints gate on the same
/// authenticator, and a distinction that only three of them make is a distinction callers cannot
/// rely on. Mirrors the identity side's 503 + <c>Retry-After</c> (#637), including the retry
/// budget, so a client sees one convention across both legs.</para>
/// </summary>
internal static class InstanceAuthResponses
{
    /// <summary>503 + <c>Retry-After</c>, logged with the reason and the path.</summary>
    internal static IResult Unavailable(HttpContext http, string? reason, ILogger? logger)
    {
        logger?.LogWarning(
            "Instance-key resolution UNAVAILABLE for {Path} ({Reason}) — answering 503 + Retry-After, "
            + "NOT 401: nothing was established about the presented key",
            http.Request.Path, reason ?? "no reason given");
        http.Response.Headers.RetryAfter =
            InstanceRegistryAuthenticator.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new
            {
                error = "Instance-key resolution is temporarily unavailable — retry shortly. "
                    + "This is NOT a statement about your key or your grant.",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
