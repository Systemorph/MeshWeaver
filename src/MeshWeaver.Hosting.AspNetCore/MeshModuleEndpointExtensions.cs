using System.Reflection;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// Applies every installed module's <see cref="MeshEndpointProviderAttribute"/> contributions —
/// the host half of the endpoint-contribution hook (design #1655).
/// </summary>
public static class MeshModuleEndpointExtensions
{
    /// <summary>
    /// Maps the endpoint contributions of every installed module (<c>Modules:Assemblies</c> →
    /// <see cref="InstalledModuleAssembly"/>). Call it with the host's other <c>Map*</c> calls —
    /// after authentication/authorization middleware, before any catch-all fallback.
    ///
    /// <para>Each module's routes map inside a group defaulting to
    /// <c>RequireAuthorization()</c>; a route is anonymous only where the module explicitly opts
    /// out. On <c>ApplicationStarted</c> the whole endpoint table is checked for duplicate
    /// (verb, pattern) registrations — a collision throws and takes the app down, because a
    /// silently shadowed route is indistinguishable from a passing one (the #683 class).</para>
    /// </summary>
    public static WebApplication MapMeshModuleEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(MeshModuleEndpointExtensions));

        var contributed = 0;
        foreach (var module in app.Services.GetServices<InstalledModuleAssembly>())
        foreach (var attribute in module.Assembly.GetCustomAttributes<MeshEndpointProviderAttribute>())
        {
            // Authenticated-by-default: the group policy applies to every route the module maps
            // unless the route itself declares AllowAnonymous — a module cannot accidentally
            // publish an open route.
            var group = app.MapGroup(string.Empty).RequireAuthorization();
            foreach (var configure in attribute.EndpointConfigurations)
            {
                configure(group);
                contributed++;
            }
            logger.LogInformation(
                "Mapped endpoint contributions from module {Module} ({Attribute})",
                module.Assembly.GetName().Name, attribute.GetType().Name);
        }

        if (contributed > 0)
            RegisterCollisionCheck(app, logger);
        return app;
    }

    /// <summary>
    /// LOUD duplicate-route detection, run once at <c>ApplicationStarted</c> when the endpoint
    /// table is fully materialized: two endpoints on the same (verb, pattern) — module vs module
    /// or module vs platform — throw with both display names. ASP.NET alone only surfaces the
    /// ambiguity at REQUEST time, which is exactly the silent-until-hit failure the hook must not
    /// introduce.
    /// </summary>
    private static void RegisterCollisionCheck(WebApplication app, ILogger logger)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var detail = FindRouteCollisions(
                app.Services.GetRequiredService<EndpointDataSource>().Endpoints);
            if (detail is null)
                return;
            logger.LogCritical(
                "Endpoint route collision(s) after module contributions — refusing to serve: {Detail}", detail);
            // Belt and braces: StopApplication() guarantees shutdown even where the runtime
            // swallows a lifetime-callback exception; the throw makes the refusal visible in
            // crash telemetry where it does propagate. Together: logged, stopped, loud.
            app.Lifetime.StopApplication();
            throw new InvalidOperationException(
                $"Endpoint route collision(s) after module endpoint contributions: {detail}. "
                + "Two registrations on one (verb, pattern) mean one of them silently shadows the "
                + "other — remove or re-route the duplicate; never rely on registration order.");
        });
    }

    /// <summary>
    /// The pure collision predicate: duplicate (verb, pattern) pairs across the endpoint table,
    /// or null when clean. Extracted so the refusal logic is unit-testable without a running host.
    /// </summary>
    internal static string? FindRouteCollisions(IEnumerable<Endpoint> endpoints)
    {
        var duplicates = endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                 ?? (IReadOnlyList<string>)["*"])
                .Select(verb => (Verb: verb, Pattern: endpoint.RoutePattern.RawText ?? "", Endpoint: endpoint)))
            .Where(e => e.Pattern.Length > 0)
            .GroupBy(e => (e.Verb, e.Pattern))
            .Where(g => g.Count() > 1)
            .ToList();
        return duplicates.Count == 0
            ? null
            : string.Join("; ", duplicates.Select(g =>
                $"{g.Key.Verb} {g.Key.Pattern} ← [{string.Join(" | ", g.Select(e => e.Endpoint.DisplayName))}]"));
    }
}
