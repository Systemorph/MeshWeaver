using Newtonsoft.Json;

namespace OpenSmc.Serialization
{
    public class FactoryConverter : JsonConverter
    {
        private readonly IServiceProvider serviceProvider;
        IReadOnlyDictionary<Type, Func<IServiceProvider, object>> TypeFactories;

        public FactoryConverter(IServiceProvider serviceProvider, IReadOnlyDictionary<Type, Func<IServiceProvider, object>> typeFactories)
        {
            this.serviceProvider = serviceProvider;
            TypeFactories = typeFactories;
        }


        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("CustomCreationConverter should only be used while deserializing.");
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            object value = Create(objectType);
            if (value == null)
            {
                throw new JsonSerializationException("No object created.");
            }

            serializer.Populate(reader, value);
            return value;
        }

        public object Create(Type objectType)
        {
            return TypeFactories.TryGetValue(objectType, out var factory) ? factory(serviceProvider) : null;
        }

        public override bool CanConvert(Type objectType)
        {
            return TypeFactories.ContainsKey(objectType);
        }

        public override bool CanWrite => false;
    }

}
