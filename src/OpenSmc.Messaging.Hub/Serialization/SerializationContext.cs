using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenSmc.Serialization;
using OpenSmc.Utils;

namespace OpenSmc.Messaging.Serialization;

public class SerializationContext : ISerializationContext
{
    private readonly SerializationService serializationService;
    private readonly JsonSerializer serializer;
    private JToken resultToken;
    private const int MaxDepth = 500;

    public SerializationContext(SerializationService serializationService, 
                                IServiceProvider serviceProvider, 
                                JsonSerializer serializer, 
                                object originalValue, 
                                JToken resultToken, 
                                PropertyInfo parentProperty, 
                                object parent,
                                int depth)
    {
        this.serializationService = serializationService;
        this.serializer = serializer;
        ServiceProvider = serviceProvider;
        OriginalValue = originalValue;
        this.resultToken = resultToken ?? (originalValue != null ? JToken.FromObject(originalValue, serializer) : null);
        ParentProperty = parentProperty;
        Parent = parent;
        Depth = depth + 1;

        if (Depth > MaxDepth)
            throw new SerializationException($"Depth of serialization exceeds {MaxDepth}");
    }

    public IServiceProvider ServiceProvider { get; }
    public object OriginalValue { get; }

    public JToken ResultToken => resultToken;

    public PropertyInfo ParentProperty { get; }
    public object Parent { get; }
    public int Depth { get; }
    public void SetResult(object value)
    {
        resultToken = value != null ? JToken.FromObject(value, serializer) : null;
    }

    public object TraverseProperty(object propertyValue, object parent, PropertyInfo propertyInfo)
    {
        var context = serializationService.CreateSerializationContext(propertyValue, null, propertyInfo, parent, Depth);
        serializationService.SerializeTraverse(context);
        return context.ResultToken;
    }

    public object TraverseValue(object value)
    {
        var context = serializationService.CreateSerializationContext(value, null, ParentProperty, Parent, Depth);
        serializationService.SerializeTraverse(context);
        return context.ResultToken;
    }

    public object SetProperty(string propName, object propValue)
    {
        if (ResultToken is not JObject jObject)
            throw new InvalidOperationException("Result is not an object");

        jObject[propName.ToCamelCase()] = JToken.FromObject(propValue, serializer);
        return propValue;
    }

    public void DeleteProperty(string propName)
    {
        if (ResultToken is JObject jObject)
            jObject.Property(propName.ToCamelCase())?.Remove();
    }
}