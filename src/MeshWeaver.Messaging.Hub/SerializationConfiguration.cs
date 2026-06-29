using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging
{
    /// <summary>
    /// Mutable-by-record builder for a hub's JSON serialization setup: exposes the
    /// <see cref="JsonSerializerOptions"/> being assembled, registers types with the hub's type
    /// registry, and toggles strict type resolution.
    /// </summary>
    /// <param name="Hub">The message hub this serialization configuration belongs to.</param>
    public record SerializationConfiguration(IMessageHub Hub)
    {
        private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        /// <summary>
        /// Applies the given action to the <see cref="Options"/> and returns this configuration for chaining.
        /// </summary>
        /// <param name="configuration">An action that mutates the serializer options.</param>
        /// <returns>This <see cref="SerializationConfiguration"/> instance.</returns>
        public SerializationConfiguration WithOptions(Action<JsonSerializerOptions> configuration)
        {
            configuration.Invoke(Options);
            return this;
        }

        /// <summary>The serializer options being assembled by this configuration.</summary>
        public JsonSerializerOptions Options { get; init; } = new();

        /// <summary>
        /// Registers <typeparamref name="T"/> with the hub's type registry and returns this configuration for chaining.
        /// </summary>
        /// <typeparam name="T">The type to register.</typeparam>
        /// <returns>This <see cref="SerializationConfiguration"/> instance.</returns>
        public SerializationConfiguration WithType<T>()
        {
            typeRegistry.WithType<T>();
            return this;
        }

        /// <summary>
        /// When <c>true</c>, type resolution is strict and unrecognized type discriminators are not tolerated.
        /// </summary>
        public bool StrictTypeResolution { get; init; }
    };
}
