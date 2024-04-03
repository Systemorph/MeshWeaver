using System;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OpenSmc.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace OpenSmc.Messaging.Serialization;

public static class SerializationExtensions
{
    public static MessageHubConfiguration WithSerialization(this MessageHubConfiguration hubConf,
        Func<SerializationConfiguration, SerializationConfiguration> configure)
    {
        var conf = hubConf.Get<ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>>() ?? ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        return hubConf.Set(conf.Add(configure));
    }

    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, IEnumerable<Type> types)
        => configuration.WithInitialization(hub =>
            hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithTypes(types));
    public static MessageHubConfiguration WithTypes(this MessageHubConfiguration configuration, params Type[] types)
        => configuration.WithTypes((IEnumerable<Type>)types);


    internal static Newtonsoft.Json.JsonSerializer GetNewtonsoftSerializer(this IServiceProvider serviceProvider)
    {
        return Newtonsoft.Json.JsonSerializer.Create(serviceProvider.GetNewtonsoftSettings());
    }

    internal static JsonSerializerSettings GetNewtonsoftSettings( this IServiceProvider serviceProvider)
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
};