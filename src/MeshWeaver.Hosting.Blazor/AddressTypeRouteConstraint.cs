using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Route constraint that validates address types against registered mesh namespaces.
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
            // Check if this address type matches any registered namespace prefix
            return meshCatalog.GetNamespace(addressType) != null;
        }
        return false;
    }
}
