using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OpenSmc.Messaging.Serialization.Newtonsoft;

public class CustomContractResolver : CamelCasePropertyNamesContractResolver
{
    public CustomContractResolver()
    {
        NamingStrategy = new CamelCaseNamingStrategy(false, true);
    }
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (property.DefaultValueHandling == null)
            if (property.PropertyType is { IsEnum: false })
                property.DefaultValueHandling = DefaultValueHandling.Ignore;

        return property;
    }
}