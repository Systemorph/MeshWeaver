using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using OpenSmc.Utils;

namespace OpenSmc.Serialization;

public class SerializationService : ISerializationService
{
    private readonly IServiceProvider serviceProvider;
    private readonly CustomSerializationRegistry customSerializationRegistry;
    private readonly JsonSerializer serializer;
    public JsonSerializer Serializer => serializer;

    public SerializationService(IServiceProvider serviceProvider, 
                                IEventsRegistry eventRegistry, 
                                CustomSerializationRegistry customSerializationRegistry)
    {
        this.serviceProvider = serviceProvider;
        this.customSerializationRegistry = customSerializationRegistry;
        var contractResolver = new CustomContractResolver();
        serializer = JsonSerializer.Create(new()
                                           {
                                               ReferenceLoopHandling = ReferenceLoopHandling.Error,
                                               // TypeNameHandling = TypeNameHandling.Auto,
                                               TypeNameHandling = TypeNameHandling.Objects,
                                               NullValueHandling = NullValueHandling.Ignore,
                                               ContractResolver = contractResolver,
                                               MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead,
                                               Converters = new List<JsonConverter> { new StringEnumConverter(), new RawJsonNewtonsoftConverter(), new ObjectDeserializationConverter(eventRegistry) },
                                               SerializationBinder = new SerializationBinder(eventRegistry)
                                           });
    }

    public string SerializeToString(object obj)
    {
        var context = CreateSerializationContext(obj, null, null, null, 0);
        SerializeTraverse(context);

        var sb = new StringBuilder();
        context.ResultToken.WriteTo(new JsonTextWriter(new StringWriter(sb)));
        return sb.ToString();
    }

    public string SerializePropertyToString(object value, object obj, PropertyInfo property)
    {
        var context = CreateSerializationContext(value, null, property, obj, 0);
        SerializeTraverse(context);

        var sb = new StringBuilder();
        if (context.ResultToken == null)
            return null;
        context.ResultToken.WriteTo(new JsonTextWriter(new StringWriter(sb)));
        return sb.ToString();
    }

    public object Deserialize(string content)
    {
        var deserialized = serializer.Deserialize(new StringReader(content), typeof(object));
        return deserialized;
    }

    internal void SerializeTraverse(SerializationContext context)
    {
        var originalValue = context.OriginalValue;

        if (originalValue != null)
        {
            var type = originalValue.GetType();
            var transformation = customSerializationRegistry.Transformations.Get(type);
            if (transformation != null)
            {
                var transformedValue = transformation(originalValue, context);
                context.SetResult(transformedValue);
                return;
            }

            var mutation = customSerializationRegistry.Mutations.Get(type);
            if (mutation != null)
            {
                mutation(originalValue, context);
            }
        }


        var jToken = context.ResultToken;
        switch (jToken?.Type)
        {
            case JTokenType.Object:
                var jObject = (JObject)jToken;

                foreach (var p in originalValue!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var propertyName = p.Name.ToCamelCase();
                    var jProperty = jObject.Property(propertyName);
                    if (jProperty != null)
                    {
                        var propertyValue = p.GetValue(originalValue);
                        var childContext = CreateSerializationContext(propertyValue, jProperty.Value, p, originalValue, context.Depth);
                        SerializeTraverse(childContext);
                        if (childContext.ResultToken != null && childContext.ResultToken.Type != JTokenType.Null)
                            jObject[propertyName] = childContext.ResultToken;
                        else
                            jObject.Remove(propertyName);
                    }
                }

                return;
            case JTokenType.Array:
                var jArray = (JArray)jToken;

                int index = 0;
                if (originalValue is not IEnumerable enumerable)
                    return;

                foreach (var item in enumerable)
                {
                    var childContext = CreateSerializationContext(item, jArray[index], null, enumerable, context.Depth);
                    SerializeTraverse(childContext);
                    jArray[index] = childContext.ResultToken;
                    index++;
                }

                return;
        }
    }

    internal SerializationContext CreateSerializationContext(object originalValue, JToken resultToken, PropertyInfo parentProperty, object parent, int depth)
    {
        return new SerializationContext(this, serviceProvider, serializer, originalValue, resultToken, parentProperty, parent, depth);
    }
}