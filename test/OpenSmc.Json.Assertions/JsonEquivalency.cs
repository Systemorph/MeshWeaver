using FluentAssertions.Equivalency;
using FluentAssertions.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Json.Assertions;

public class JsonEquivalency : IEquivalencyStep
{
    private readonly JsonEquivalencyOptions options;

    public JsonEquivalency(JsonEquivalencyOptions options)
    {
        this.options = options;
    }

    public EquivalencyResult Handle(
        Comparands comparands,
        IEquivalencyValidationContext context,
        IEquivalencyValidator nestedValidator
    )
    {
        if (!TryGetJToken(comparands.Subject, out var actual))
            return EquivalencyResult.ContinueWithNext;

        RemoveRedundantProperties(actual);

        if (TryGetJToken(comparands.Expectation, out var expected))
        {
            RemoveRedundantProperties(expected);

            actual
                .Should()
                .BeEquivalentTo(
                    expected,
                    context.Reason.FormattedMessage,
                    context.Reason.Arguments
                );
            return EquivalencyResult.AssertionCompleted;
        }

        var settings = GetSerializerSettings();
        var json = comparands.Expectation switch
        {
            string s => s,
            null => string.Empty,
            _ => JsonConvert.SerializeObject(comparands.Expectation, typeof(object), settings)
        };
        var expectedJObject = string.IsNullOrEmpty(json) ? JValue.CreateNull() : JToken.Parse(json);

        RemoveRedundantProperties(expectedJObject);

        actual
            .Should()
            .BeEquivalentTo(
                expectedJObject,
                context.Reason.FormattedMessage,
                context.Reason.Arguments
            );
        return EquivalencyResult.AssertionCompleted;
    }

    private static readonly Dictionary<string, Type> TypeNames = new();

    private static Type GetType(string typeName)
    {
        if (typeName == null)
            return null;

        if (TypeNames.TryGetValue(typeName, out var type))
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = assembly.GetType(typeName, false);
            if (t != null)
                return TypeNames[typeName] = t;
        }

        return TypeNames[typeName] = null;
    }

    private void RemoveRedundantProperties(JToken jToken)
    {
        if (jToken is not JObject jObject)
            return;

        if (options.ExcludedTypeDiscriminator)
            jObject.Remove("$type");

        foreach (var node in jObject.DescendantsAndSelf().OfType<JObject>())
        {
            var typeId = node.Value<string>("$type");
            var type = GetType(typeId);
            if (type != null)
            {
                var allTypes = GetBaseTypes(type, true);
                foreach (var t in allTypes)
                {
                    if (options.ExcludedProperties.TryGetValue(t, out var propertyNames))
                    {
                        foreach (var propertyName in propertyNames)
                        {
                            node.Remove(propertyName);
                        }
                    }
                }
            }
        }
    }

    public static IEnumerable<Type> GetBaseTypes(Type type, bool includeSelf = false)
    {
        if (type == null)
            throw new ArgumentNullException(nameof(type));

        var start = includeSelf ? type : type.BaseType;
        for (var t = start; t != null; t = t.BaseType)
            yield return t;
    }

    bool TryGetJToken(object obj, out JToken ret)
    {
        switch (obj)
        {
            case JToken jObject:
                ret = jObject.DeepClone();
                return true;
            case RawJson subject:
                ret = JToken.Parse(subject.Content);
                return true;
            default:
                ret = null;
                return false;
        }
    }

    private static JsonSerializerSettings GetSerializerSettings()
    {
        return new()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
            // TypeNameHandling = TypeNameHandling.Auto,
            TypeNameHandling = TypeNameHandling.Objects,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead,
            Converters = new List<JsonConverter>
            {
                new StringEnumConverter(),
                // TODO V10: Understand the idea of this and replace by something else. (08.04.2024, Roland Bürgi)
                //new RawJsonNewtonsoftConverter(),
                //new JsonNodeNewtonsoftConverter()
            },
            SerializationBinder = new BenchmarkSerializationBinder()
        };
    }

    public class BenchmarkSerializationBinder : DefaultSerializationBinder
    {
        public override void BindToName(
            Type serializedType,
            out string assemblyName,
            out string typeName
        )
        {
            typeName = serializedType.FullName ?? serializedType.Name;
            assemblyName = null;
        }
    }
}
