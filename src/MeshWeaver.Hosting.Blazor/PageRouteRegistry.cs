using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// The set of SINGLE-SEGMENT Blazor page routes (<c>/login</c>, <c>/privacy</c>, <c>/search</c>,
/// …), derived from the same router assemblies the app's <c>Routes.razor</c> gives the Blazor
/// <c>Router</c>. <see cref="NavigationService"/> short-circuits these BEFORE mesh path
/// resolution.
///
/// <para><b>Why:</b> the navigation service's only page-route heuristic used to be "single
/// segment WITH query args". A bare page URL (<c>/privacy</c>) fell through to
/// <c>PathResolutionService</c>, whose partition-root synthesis treats any single segment as a
/// potential partition root whenever the writable provider's <c>PartitionExists</c> probe is
/// indeterminate (InMemory / monolith). The synthetic resolution then hit the anonymous
/// hard-gate, bouncing the deliberately-PUBLIC page to <c>/login</c>. Registering the app's
/// actual page routes makes the decision exact instead of heuristic.</para>
///
/// <para>Only templates that are exactly ONE fixed segment are registered: multi-segment page
/// routes cannot be mistaken for partition roots (synthesis requires a single segment), and
/// registering their first segments could shadow real mesh paths (e.g. a page under
/// <c>/admin/…</c> must not shadow the <c>Admin</c> partition).</para>
///
/// <para>Register as a DI singleton (app-level opt-in); when absent, the navigation service
/// falls back to the args-only heuristic. Instance state on a mesh-scoped singleton — never
/// static (see NoStaticState.md).</para>
/// </summary>
public sealed class PageRouteRegistry
{
    private readonly ConcurrentDictionary<string, byte> _segments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the registry from the given router assemblies — pass the SAME assemblies
    /// the app's <c>Routes.razor</c> passes to the Blazor <c>Router</c>.</summary>
    public PageRouteRegistry(params Assembly[] routerAssemblies)
    {
        foreach (var assembly in routerAssemblies)
            Register(assembly);
    }

    /// <summary>Registers every single-fixed-segment <see cref="RouteAttribute"/> template
    /// found on the assembly's component types.</summary>
    public void Register(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (!typeof(IComponent).IsAssignableFrom(type))
                continue;
            foreach (var route in type.GetCustomAttributes<RouteAttribute>())
            {
                var template = route.Template.Trim().TrimStart('/');
                if (template.Length == 0 || template.Contains('/') || template.StartsWith('{'))
                    continue;
                _segments.TryAdd(template, 0);
            }
        }
    }

    /// <summary>Registers explicit route segments (tests / routes not discoverable by
    /// reflection).</summary>
    public void Register(params string[] segments)
    {
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim().TrimStart('/');
            if (trimmed.Length > 0 && !trimmed.Contains('/'))
                _segments.TryAdd(trimmed, 0);
        }
    }

    /// <summary>True when <paramref name="route"/> is a registered single-segment page route.</summary>
    public bool IsPageRoute(string? route) =>
        !string.IsNullOrEmpty(route) && _segments.ContainsKey(route);
}
