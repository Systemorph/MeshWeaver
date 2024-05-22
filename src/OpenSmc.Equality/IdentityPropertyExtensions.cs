using System.ComponentModel.DataAnnotations;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.Domain;
using OpenSmc.Reflection;

namespace OpenSmc.Equality;

public static class IdentityPropertyExtensions
{
    private static readonly CreatableObjectStore<
        Type,
        (PropertyInfo[] identityProperties, PropertyInfo[] keyProperties)
    > PropertyStore = new(GetIdentityPropertiesInner);

    private static (
        PropertyInfo[] identityProperties,
        PropertyInfo[] keyProperties
    ) GetIdentityPropertiesInner(Type type)
    {
        var identityAndSimilarProperties = type.GetProperties()
            .Concat(type.GetInterfaces().SelectMany(i => i.GetProperties())) // here we take implemented interfaces into account
            //SkipKeyForEqualityAttribute must be deleted after internal domain will be kicked out
            .Select(p => new
            {
                Prop = p,
                ipAttr = p.GetSingleCustomAttribute<IdentityPropertyAttribute>(),
                keyAttr = p.HasAttribute<SkipKeyForEqualityAttribute>()
                    ? null
                    : p.GetSingleCustomAttribute<KeyAttribute>()
            })
            .Where(x => x.ipAttr != null || x.keyAttr != null)
            .ToLookup(x => x.ipAttr?.GetType() ?? x.keyAttr?.GetType(), x => x.Prop);

        var ips = identityAndSimilarProperties[typeof(IdentityPropertyAttribute)]
            .DistinctBy(x => x.Name)
            .ToArray();
        var keys = identityAndSimilarProperties[typeof(KeyAttribute)]
            .DistinctBy(x => x.Name)
            .ToArray();

        return (ips, keys);
    }

    public static PropertyInfo[] GetIdentityProperties(this Type type)
    {
        return PropertyStore.GetInstance(type).identityProperties;
    }

    public static bool HasIdentityProperties(
        this Type type,
        Func<PropertyInfo, bool> predicate = null
    )
    {
        var identityProperties = type.GetIdentityProperties();
        return predicate == null ? identityProperties.Any() : identityProperties.Any(predicate);
    }

    public static PropertyInfo[] GetIdentityOrSimilarProperties(this Type type)
    {
        var (identityProperties, keyProperties) = PropertyStore.GetInstance(type);
        var result = identityProperties.Length > 0 ? identityProperties : keyProperties;
        return result;
    }

    public static bool HasIdentityOrSimilarProperties(this Type type)
    {
        return type.GetIdentityOrSimilarProperties().Length > 0;
    }
}
