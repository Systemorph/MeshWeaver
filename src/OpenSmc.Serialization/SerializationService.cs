using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OpenSmc.StringHelpers;

namespace OpenSmc.Serialization;

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
public class SerializationService : ISerializationService
{
    private readonly IServiceProvider serviceProvider;
    private readonly DataBindingRegistry dataBindingRegistry;
    private readonly JsonSerializer serializer;
    public JsonSerializer Serializer => serializer;

    public SerializationService(IServiceProvider serviceProvider, 
                                IEventsRegistry eventRegistry, 
                                DataBindingRegistry dataBindingRegistry)
    {
        this.serviceProvider = serviceProvider;
        this.dataBindingRegistry = dataBindingRegistry;
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

    public async Task<string> SerializeToStringAsync(object obj)
    {
        var context = CreateSerializationContext(obj, null, null, null, 0);
        await SerializeTraverseAsync(context);

        var sb = new StringBuilder();
        await context.ResultToken.WriteToAsync(new JsonTextWriter(new StringWriter(sb)));
        return sb.ToString();
    }

    public async Task<string> SerializePropertyToStringAsync(object value, object obj, PropertyInfo property)
    {
        var context = CreateSerializationContext(value, null, property, obj, 0);
        await SerializeTraverseAsync(context);

        var sb = new StringBuilder();
        if (context.ResultToken == null)
            return null;
        await context.ResultToken.WriteToAsync(new JsonTextWriter(new StringWriter(sb)));
        return sb.ToString();
    }

    public object Deserialize(string content)
    {
        var deserialized = serializer.Deserialize(new StringReader(content), typeof(object));
        return deserialized;
    }

    internal async Task SerializeTraverseAsync(SerializationContext context)
    {
        var originalValue = context.OriginalValue;

        if (originalValue != null)
        {
            var type = originalValue.GetType();
            var transformation = dataBindingRegistry.Transformations.Get(type);
            if (transformation != null)
            {
                var transformedValue = await transformation(originalValue, context);
                context.SetResult(transformedValue);
                return;
            }

            var mutation = dataBindingRegistry.Mutations.Get(type);
            if (mutation != null)
            {
                await mutation(originalValue, context);
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
                        await SerializeTraverseAsync(childContext);
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
                    await SerializeTraverseAsync(childContext);
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