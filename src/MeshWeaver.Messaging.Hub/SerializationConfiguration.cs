using System.Text.Json;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging
{
    public record SerializationConfiguration(IMessageHub Hub)
    {
        private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        public SerializationConfiguration WithOptions(Action<JsonSerializerOptions> configuration)
        {
            configuration.Invoke(Options);
            return this;
        }

        public JsonSerializerOptions Options { get; init; } = new();

        public SerializationConfiguration WithType<T>()
        {
            typeRegistry.WithType<T>();
            return this;
        }

        public bool StrictTypeResolution { get; init; }
    };
}
