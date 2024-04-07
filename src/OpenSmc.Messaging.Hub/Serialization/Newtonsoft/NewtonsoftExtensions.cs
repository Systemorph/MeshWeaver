using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization.Newtonsoft
{
    internal static class NewtonsoftExtensions
    {
        internal static JsonSerializer GetNewtonsoftSerializer(this IServiceProvider serviceProvider)
        {
            return JsonSerializer.Create(serviceProvider.GetNewtonsoftSettings());
        }

        internal static JsonSerializerSettings GetNewtonsoftSettings(this IServiceProvider serviceProvider)
        {
            var contractResolver = new CustomContractResolver();
            var typeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
            var converters = new List<JsonConverter>
            {
                new StringEnumConverter(),
                new RawJsonNewtonsoftConverter(),
                new JsonNodeNewtonsoftConverter(),
                new ObjectDeserializationConverter(typeRegistry)
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
