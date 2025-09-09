using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MeshWeaver.Hosting.Blazor;

public class AddressTypeRouteConstraint : IRouteConstraint
{
    public AddressTypeRouteConstraint(IMessageHub hub)
    {
        validAddressTypes = hub.TypeRegistry.Types
            .Where(kvp => kvp.Value.Type.IsAssignableTo(typeof(Address)))
            .Select(kvp => kvp.Key)
            .ToHashSet();
    }

    private readonly HashSet<string> validAddressTypes;


    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        if (values.TryGetValue(routeKey, out var value) && value is string addressType)
        {
            return validAddressTypes.Contains(addressType);
        }
        return false;
    }

}
