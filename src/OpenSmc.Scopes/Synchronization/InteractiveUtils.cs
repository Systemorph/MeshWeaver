using System.Reflection;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Utils;

namespace OpenSmc.Scopes.Synchronization;

public static class ScopeUtils
{
    // TODO V10: cache it (2023-02-07, Andrei Sirotenko)
    public static PropertyInfo GetScopePropertyType(IMutableScope rootScope, Guid scopeId, string propertyName)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        var scope = ((IInternalMutableScope)rootScope).GetScopeById(scopeId) as IScope;
        // Note: scope.GetType() is conscious decision. In case Property is defined at the base scope type => result would be null.
        var propertyInfo = scope?.GetScopeType()
                                .GetScopeProperties()
                                .SelectMany(x => x.Properties)
                                .FirstOrDefault(x => x.Name == propertyName || x.Name.ToCamelCase() == propertyName);

        return propertyInfo;
    }
}