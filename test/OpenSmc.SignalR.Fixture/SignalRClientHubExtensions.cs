using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.SignalR.Fixture;

public static class SignalRClientHubExtensions
{
    public static MessageHubConfiguration AddSignalRClient(this MessageHubConfiguration config, Func<SignalRClientConfiguration, SignalRClientConfiguration> clientConfiguration)
        => config
            .Set(clientConfiguration)
            .WithTypes(typeof(RawJson))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is RawJsonConverter))
                        options.Converters.Insert(0, new RawJsonConverter(serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()));
                })
            )
            .AddPlugin<SignalRClientPlugin>();
}

public record SignalRClientConfiguration
{
    internal Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig;
    public SignalRClientConfiguration WithHubConnectionConfiguration(Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig)
        => this with { hubConnectionBuilderConfig = hubConnectionBuilderConfig, };
}
