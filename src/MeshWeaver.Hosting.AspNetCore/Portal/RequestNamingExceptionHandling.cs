using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// How a request is NAMED in a log line: <c>{Method} {Route}</c>, where the route is the matched
/// endpoint's route TEMPLATE (<c>POST /api/mesh/get</c>, <c>GET /{*path:nonfile}</c>) — never the
/// concrete path, never the query string. A concrete path carries user data (node paths, partition
/// names, ids); the template names the code that ran, which is what a reader of an incident needs.
/// </summary>
public static class RequestName
{
    /// <summary>What a request that matched no endpoint is named — an exception thrown by middleware
    /// that runs before (or without) endpoint routing.</summary>
    public const string NoEndpoint = "(no endpoint matched)";

    /// <summary>The request's method and the route template of the endpoint it matched.</summary>
    public static string Of(HttpContext http) => Of(http.Request.Method, http.GetEndpoint());

    /// <summary>The method and the route template of <paramref name="endpoint"/>.</summary>
    public static string Of(string method, Endpoint? endpoint) => $"{method} {Route(endpoint)}";

    /// <summary>
    /// The route template of <paramref name="endpoint"/>; its display name when it is not a route
    /// endpoint (both are fixed at mapping time, so neither carries request data);
    /// <see cref="NoEndpoint"/> when there is none.
    /// </summary>
    public static string Route(Endpoint? endpoint) => endpoint switch
    {
        RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template } => template,
        { DisplayName: { Length: > 0 } name } => name,
        _ => NoEndpoint,
    };
}

/// <summary>
/// The portal's production exception handler, with the one thing the framework's own line lacks:
/// WHICH request failed (MeshWeaver#6790).
///
/// <para><b>Why.</b> <c>ExceptionHandlerMiddleware</c> logs <i>"An unhandled exception has occurred
/// while executing the request."</i> with the exception and nothing else. When the exception carries
/// no frames of its own — an Rx <c>.Timeout(…)</c> creates its <see cref="TimeoutException"/>
/// without throwing it, so the stack starts at the minimal-API bridge — that line cannot name the
/// endpoint, and with request logging off nothing else on the pod does either. #6790 stayed
/// undecidable for exactly that reason.</para>
///
/// <para><b>How, once.</b> <see cref="ExceptionHandlerOptions.SuppressDiagnosticsCallback"/> is the
/// framework's designated point for REPLACING the middleware's diagnostics: it runs exactly where the
/// middleware would log, for every exception it handled. <see cref="Report"/> writes the line there —
/// same level, same event id, the exception attached, plus <c>{RequestMethod} {RequestRoute}</c> —
/// tags the request's activity as the middleware would, and returns <c>true</c> so the middleware's
/// own pathless copy is not written as well. Exceptions the middleware did NOT handle (the response
/// had already started, or the error page itself failed) never reach the callback; the middleware
/// still logs those itself, so nothing goes unlogged.</para>
/// </summary>
public static class RequestNamingExceptionHandling
{
    /// <summary>The log category the request-naming line is written under.</summary>
    public const string LogCategory = "MeshWeaver.Hosting.AspNetCore.UnhandledRequestException";

    /// <summary>The event id — the same one <c>ExceptionHandlerMiddleware</c> uses for its line.</summary>
    public static readonly EventId UnhandledException = new(1, "UnhandledException");

    /// <summary>
    /// <c>app.UseExceptionHandler(errorHandlingPath, createScopeForErrors)</c>, re-executing onto the
    /// same error page, with the unhandled-exception line naming the request.
    /// </summary>
    public static IApplicationBuilder UseRequestNamingExceptionHandler(
        this IApplicationBuilder app, string errorHandlingPath, bool createScopeForErrors = true)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        return app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandlingPath = new PathString(errorHandlingPath),
            CreateScopeForErrors = createScopeForErrors,
            SuppressDiagnosticsCallback = context => Report(logger, context),
        });
    }

    /// <summary>
    /// Writes the unhandled-exception line naming the request and returns <c>true</c> (the
    /// middleware's own line is replaced, not duplicated).
    /// <para>By the time this runs the middleware has re-executed onto the error page, so
    /// <c>HttpContext.GetEndpoint()</c> is the ERROR page's endpoint; the endpoint that threw is
    /// the one <see cref="IExceptionHandlerFeature.Endpoint"/> captured before the re-execution.</para>
    /// </summary>
    internal static bool Report(ILogger logger, ExceptionHandlerSuppressDiagnosticsContext context)
    {
        var http = context.HttpContext;
        var failed = http.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        logger.LogError(UnhandledException, context.Exception,
            "An unhandled exception has occurred while executing {RequestMethod} {RequestRoute}.",
            http.Request.Method, RequestName.Route(failed));

        if (http.Features.Get<IHttpActivityFeature>()?.Activity is { } activity)
        {
            activity.SetTag("error.type", context.Exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error);
        }
        return true;
    }
}
