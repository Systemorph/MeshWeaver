using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization.Newtonsoft
{
    internal static class NewtonsoftExtensions
    {
        internal static JsonSerializer GetNewtonsoftSerializer(this ITypeRegistry typeRegistry)
        {
            return JsonSerializer.Create(typeRegistry.GetNewtonsoftSettings());
        }

        internal static JsonSerializerSettings GetNewtonsoftSettings(this ITypeRegistry typeRegistry)
        {
            var contractResolver = new CustomContractResolver();
            var converters = new List<JsonConverter>
            {
                new StringEnumConverter(),
                new RawJsonNewtonsoftConverter(),
                new JsonNodeNewtonsoftConverter(),
            };

            return new()
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Error,
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = contractResolver,
                MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead,
                Converters = converters,
                SerializationBinder = new SerializationBinder(typeRegistry)
            };
        }

    }
}
