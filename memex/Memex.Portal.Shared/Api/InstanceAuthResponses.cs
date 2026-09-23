using System.Globalization;
using System.Reactive.Linq;
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

    /// <summary>
    /// 🚨 The answer for "the key was fine, but the mesh READ behind the route could not be
    /// answered" — a stalled query provider (<see cref="MeshWeaver.Mesh.QueryProviderStalledException"/>,
    /// policy <c>query-fanin-stall-terminal</c>) or a transient store connect fault. The same
    /// 503 + <c>Retry-After</c> convention as <see cref="Unavailable"/>, and for the same reason: the
    /// fault says, in its own text, that it is an availability failure and retryable, so the wire
    /// has to say so too.
    ///
    /// <para>Before this, such a fault escaped the route as an UNHANDLED exception: ASP.NET's
    /// exception middleware answered a bare 500 with no <c>Retry-After</c> and logged the stall as
    /// "An unhandled exception has occurred while executing the request" (MeshWeaver#5345) — a
    /// defect report aimed at the endpoint for a read the endpoint had no part in stalling. The
    /// cause stays logged here, with the exception, naming the route; it is the stall's own
    /// reporters (the fan-in's probe, the silo's health monitor) that attribute it.</para>
    /// </summary>
    /// <param name="http">The request — the 503 carries <c>Retry-After</c> on its response.</param>
    /// <param name="cause">The availability fault the route's read terminated with.</param>
    /// <param name="logger">Diagnostic sink, resolved while the request scope was alive.</param>
    internal static IResult ReadUnavailable(HttpContext http, Exception cause, ILogger? logger)
    {
        logger?.LogWarning(cause,
            "Mesh read behind {Path} is UNAVAILABLE — answering 503 + Retry-After rather than an "
            + "unhandled 500: the read produced no answer, which says nothing about the request",
            http.Request.Path);
        http.Response.Headers.RetryAfter =
            InstanceRegistryAuthenticator.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new { error = "The registry could not read its catalogue just now — retry shortly." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Maps an availability fault on a route's answer to <see cref="ReadUnavailable"/>, and lets
    /// every OTHER fault through unchanged — a real defect keeps surfacing as one. The rule is
    /// <see cref="MeshWeaver.Layout.AreaErrorClassifier.IsStorageUnavailable"/>, the one definition
    /// the GUI already classifies the same faults by.
    /// </summary>
    /// <param name="answer">The route's answer.</param>
    /// <param name="http">The request.</param>
    /// <param name="logger">Diagnostic sink, resolved while the request scope was alive.</param>
    internal static IObservable<IResult> UnavailableOnAStalledRead(
        this IObservable<IResult> answer, HttpContext http, ILogger? logger) =>
        answer.Catch<IResult, Exception>(ex =>
            MeshWeaver.Layout.AreaErrorClassifier.IsStorageUnavailable(ex)
                ? Observable.Return(ReadUnavailable(http, ex, logger))
                : Observable.Throw<IResult>(ex));
}
