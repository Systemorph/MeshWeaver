using System.Reflection;
using OpenSmc.Scopes;
using OpenSmc.ShortGuid;

namespace OpenSmc.Layout.Composition;

public static class DependenciesExtensions
{
    public static ViewDependency[] Convert(this IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> nodeDependencies)
    {
        var ret = nodeDependencies?.Where(n => n.Scope != null).Select(n => new ViewDependency(n.Scope.GetGuid().AsString(), n.Property.Name)).ToArray();
        return ret?.Length == 0 ? null : ret;
    }

}