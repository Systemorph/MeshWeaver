using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Route constraint that validates address types against registered mesh nodes.
/// </summary>
public class AddressTypeRouteConstraint : IRouteConstraint
{
    private readonly IMeshCatalog meshCatalog;

    public AddressTypeRouteConstraint(IMeshCatalog meshCatalog)
    {
        this.meshCatalog = meshCatalog;
    }

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        if (values.TryGetValue(routeKey, out var value) && value is string addressType)
        {
            // Check if this address type matches any registered node (single segment nodes act as prefixes)
            return meshCatalog.Configuration.Nodes.Values
                .Any(n => n.Segments.Length == 1 &&
                          n.Segments[0].Equals(addressType, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }
}
