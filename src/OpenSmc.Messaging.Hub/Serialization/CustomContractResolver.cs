using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class CustomContractResolver : CamelCasePropertyNamesContractResolver
{
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (property.DefaultValueHandling == null)
            if (property.PropertyType is { IsEnum: false })
                property.DefaultValueHandling = DefaultValueHandling.Ignore;

        return property;
    }
}