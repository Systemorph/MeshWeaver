namespace Memex.Portal.Shared.Pages;

/// <summary>
/// The single definition of the production error route.
///
/// <para><b>Why this constant exists.</b> The portal wires
/// <c>app.UseExceptionHandler(ErrorRoutes.Path)</c> in production only, and for a long time the
/// path it named — <c>"/Error"</c>, written as a string literal — was served by <i>nothing</i>:
/// no page, no controller, no endpoint anywhere in the solution. So every unhandled server-side
/// exception in production was re-executed onto a route the Blazor router could not match, and the
/// user got a BLACK SCREEN with no message, no status and no request id. Nothing failed to compile
/// and nothing went red, because a route expressed as a literal in one file and as a page in
/// another has no link between the two. It never showed up in development either — the handler is
/// registered only when <c>!IsDevelopment()</c>, so locally you get the developer exception page.</para>
///
/// <para>Both sides now read this constant (<see cref="Path"/> in the pipeline,
/// <c>@attribute [Route(ErrorRoutes.Path)]</c> on the page), so a rename breaks compilation rather
/// than silently shipping a blank screen. <c>ErrorPageRouteTest</c> pins that a component actually
/// claims it.</para>
/// </summary>
public static class ErrorRoutes
{
    /// <summary>The path the production exception handler re-executes onto.</summary>
    public const string Path = "/Error";
}
